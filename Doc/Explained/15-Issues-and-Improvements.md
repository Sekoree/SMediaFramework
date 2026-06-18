# 15 · Issues & Improvements

An honest review from reading the whole codebase. **First, the headline: the framework
is genuinely high quality.** The 2026-06-14 inventory has ~108.5k production lines
under `MediaFramework`, `UI/HaPlay`, and `UI/HaPlay.Desktop`, exactly **1** TODO
marker (`ControlWorkspaceViewModel` script-template text), **0**
`NotImplementedException`s in shipping paths, a clean obsolete-shim migration policy,
careful lock-free hot paths, real cooperative shutdown, and a strong probe/test suite.
The hot-path review measured the engine **allocation-free with zero GC at 4K60** — so
*don't* go hunting for allocation wins; that work is done.

So this is mostly a list of **structural / maintainability** improvements and a few
**optional robustness** enhancements, not a bug pile. Severity is my estimate of
impact × likelihood, not a defect count.

Legend: 🔴 worth doing · 🟡 nice-to-have · 🟢 housekeeping · ✅ already fixed (noted for
accuracy).

---

## 🔴 1. The HaPlay view-models are God-objects

The single clearest improvement. Four view-models and two engine classes dominate the
line count:

| File | Lines |
|------|------:|
| `UI/HaPlay/ViewModels/MediaPlayerViewModel.cs` | 3,157 |
| `UI/HaPlay/ViewModels/ControlWorkspaceViewModel.cs` | 2,908 |
| `UI/HaPlay/Playback/CuePlaybackEngine.cs` | 2,360 |
| `UI/HaPlay/ViewModels/CuePlayerViewModel.cs` | 2,011 |
| `UI/HaPlay/Playback/HaPlayPlaybackSession.cs` | 1,829 |
| `UI/HaPlay/ViewModels/MainViewModel.cs` | 1,527 |

These are much smaller than the earliest HaPlay snapshots, but they are still large
enough to make testing and review harder than necessary. Concrete, low-risk
decompositions:

* Split each big VM by **feature area** into partials or child VMs: e.g.
  `CuePlayerViewModel` → cue-list/tree, transport, composition/layers, output-mapping,
  pre-roll/standby. The Cue *engine* already separates concerns; the VM can mirror it.
* Extract **command handlers** into small command objects or services (the
  `[RelayCommand]` bodies are where most lines live). This also shrinks the AOT
  reflection surface.
* `ControlWorkspaceViewModel` mixes device management, script editing, the live
  monitor, and learn-MIDI — those are four screens' worth of state in one class.

This is purely a refactor (no API break, no behaviour change) and would make the UI
contributable. **Highest leverage item here.**

## 🔴 2. Multi-output sync is "one master + everyone drifts" (by design — but the top feature gap)

Documented thoroughly in [04](04-Core-Audio-Engine.md)/[06](06-Clocks-and-AV-Sync.md)
and `Doc/MediaFramework-Architecture.md`: only the `SlaveTo` output paces the router;
all other outputs run off their own crystal and drift ~±50 ppm, eventually
dropping/starving. The framework deliberately provides **no** coordinated master-PPM
policy and **no** lock-step drop/repeat across outputs.

For a tool whose whole point is multi-output (NDI + local + monitors simultaneously),
this is the most impactful *feature* to consider, even though it's not a bug:

* **Option A (least invasive):** auto-wrap every non-primary output in
  `AdaptiveRateAudioOutput` when >1 output is attached, driven by per-output
  `PumpPressurePlaybackHintMonitor`. The pieces already exist; today it's opt-in and
  host-wired. Defaulting it for multi-output graphs would make drift self-correcting
  out of the box.
* **Option B (bigger):** a real coordinated clock policy (a graph-level PPM controller
  that nudges all outputs toward the master). This is the "what's not implemented"
  section of the architecture doc; it's a design project, not a patch.

Either would also help the **cross-cue compositor alignment** caveat (two cues on two
PortAudio devices in one composition drift relative to each other —
[06](06-Clocks-and-AV-Sync.md)).

> **Status (2026-06-15):** Option A is now **on by default in HaPlay** — startup registers
> the FFmpeg adaptive-rate plugin and the playback session enables
> `EnableAdaptiveRateOnNonMasterOutputs`, so secondary-output drift self-corrects out of the
> box. The A-vs-B decision and the long-term design for **stitched multi-output compositions**
> (an `OutputSyncGroup` / genlock domain that reuses A as its audio actuator and adds lock-step
> video present) are written up in [`Doc/HaPlay-MultiOutput-Sync.md`](../HaPlay-MultiOutput-Sync.md).
> Short version: A and B are not either/or — A is a building block of B; ship A now, build B
> when a genuinely stitched surface across separate physical outputs becomes a hard requirement.
>
> **Update (2026-06-15):** Option B's **Phase-1 foundation is built** — `OutputSyncGroup`
> (`S.Media.Core.Clock`) is the coordinated master-ppm PI controller that disciplines member clocks to a
> reference and emits a per-member ppm correction for Option A's `AdaptiveRateAudioOutput` to apply.
> Unit-tested (locks a +40 ppm member to sub-ms phase; resets on pause/seek).
>
> **Update (2026-06-15, Phase 2b):** the **video present scheduler is now built** — `VideoPresentSyncGroup`
> + `ISyncPresentableVideoOutput` + `SyncPresentVideoOutput` (`S.Media.Core.Video`). This is the
> "synchronized drop/repeat across outputs" the architecture doc listed as not-implemented: grouped video
> outputs present the frame for one reference timestamp on a shared tick, holding (bounded) when a member
> falls behind so a stitched canvas never tears, then degrading to keep the wall live if a member wedges.
> Unit-tested (12 cases: lock-step advance, hold-on-lag, degrade-after-budget, coordinated drop, two real
> members present the same frame). **Remaining:** HaPlay host wiring to declare sync groups and route each
> member's device through the controllers — deliberately deferred until validated on real multi-output
> hardware (a wrong drift *direction* won't show up in the unit suite), and pending the genlock-*scope*
> product decision (per-composition vs engine-wide) noted in `Doc/HaPlay-MultiOutput-Sync.md`.
>
> **Update (2026-06-15, audio host wiring):** scope decided — **engine-wide** (one reference device, all
> other active audio devices disciplined to it; fits the per-device pooled `ClipAudioOutputRuntime` model).
> Wired into HaPlay's cue engine via `EngineAudioGenlock` (`UI/HaPlay/Playback/`): the reference device is
> left unwrapped (so the master clock is never resampled — `AdaptiveRateAudioOutput` resamples even at
> 0 ppm), members are wrapped via the runtime's `ratePpmProvider`, with reference handoff on release.
> **Opt-in, off by default** (`HAPLAY_MULTIOUTPUT_GENLOCK=1`) so the default audio path is byte-identical;
> unit-tested (`EngineAudioGenlockTests`, 5 cases).
>
> **Update (2026-06-15, multi-output layout UI):** the operator can now *define* a multi-output composition
> in the cue player — Output setup → **Layout…** opens a draggable editor (`CompositionOutputLayoutDialog` /
> `OutputLayoutCanvas`) placing each bound output over the canvas (overlaps blend, gaps stay black), writing
> each output's source slice back to its `CueOutputMapping`. Unit-tested (`CompositionOutputLayoutViewModelTests`).
> **Video present-sync for compositions — resolved (not needed):** investigating the `ClipCompositionRuntime`
> hook showed the composition fan-out is **already frame-locked** — `PumpOneFrame` composites once per canvas
> tick and submits that one frame to every output (zero-copy, same backing), it runs freerun for video-only
> walls too, the guarantee is already covered by `ClipCompositionRuntime_MultiOutputPump_SharesCanvasBackingAcrossOutputs`,
> and NDI egress is already timecoded. A `VideoPresentSyncGroup` here would buffer already-synchronized frames
> for no lock gain; the residual cross-output skew is device vsync / NDI network timing = **hardware genlock**,
> outside software scope. The primitive stays valid for independent-per-output architectures. See
> `Doc/HaPlay-MultiOutput-Sync.md`.

## 🟡 3. A few framework files are large enough to split

These are well-factored *internally*, so this is lower priority than the VMs, but they
raise the bar to contribute:

* `MediaContainerSharedDemux.cs` (2,560) — the reader thread, the seek/prime logic, the
  swr-rebuild logic, and the hardware-backing dispatch could each be a partial or
  collaborator. It's the most intricate file in the framework.
* `ChannelMap.SimdAccumulate.cs` (1,831) — a long cascade of `TryAccumulate…` fast
  paths. Consider grouping by family (stereo / wide-source / packed-permutation) into
  separate partials with a table-driven dispatcher, so adding a path is local. Watch
  for diminishing returns — these are deliberately hand-tuned.
* `AudioRouter.cs` (1,961) — already split into `.Matrix`/`.Playback`/`Events` partials;
  the `OutputPump` nested class could move to its own file.
* `YuvVideoRenderer.cs` (1,525) — format-specific shader/upload setup could split by
  pixel-format family.

> **Status (2026-06-15):** the two clean nested-type extractions are done. `AudioRouter`'s
> `OutputPump` moved to `AudioRouter.OutputPump.cs` (1,961 → 1,694); `MediaContainerSharedDemux`'s
> `AudioTrack`/`VideoTrack` source classes moved to `MediaContainerSharedDemux.Tracks.cs` (2,560 →
> 2,296), making that class `partial`. Build clean; 213 Core-audio + 181 FFmpeg tests green. The
> remaining two (`ChannelMap.SimdAccumulate`, `YuvVideoRenderer`) are deliberately hand-tuned method
> cascades the doc itself flags as diminishing-returns — left intact, lower value.

## 🟡 4. `VideoOutputPump.Dispose` can deliberately leak pump state

`MediaFramework/Media/S.Media.Core/Video/VideoOutputPump.cs` (~414–423): if the drainer
is still blocked inside a slow inner `Submit` after the 2 s join cap, `Dispose`
*intentionally leaks* `_pending`/`_cts`/the inner output rather than risk a
use-after-dispose on the running thread. This is the **correct** safe choice and is
documented in-code — but it's a known residual window. The same file notes a related
race: a frame already in flight on the drainer can outlive a format change because the
queues aren't format-versioned. If bulletproofing is wanted:

* **Format-versioned queues** would close the "stale-format frame already on the drain
  thread" window mentioned in `Configure`.
* A cooperative **hard-abort** signal the inner output observes mid-`Submit` would let
  the drainer exit promptly so `Dispose` never needs to leak.

Low frequency (needs a genuinely wedged output) — track, don't rush.

> **Status (2026-06-15): both done.** (1) `VideoOutputPump._queue` now carries a per-frame
> `_formatVersion` (bumped under the gate on a real `Configure` format change); the drain thread drops a
> frame that turned stale before `_inner.Submit`, closing the "frame already dequeued before a format
> change" window. (2) New optional capability `IVideoOutputCooperativeAbort` — `Dispose` calls the
> inner's `RequestSubmitAbort()` before the join, so a cooperative output (e.g. a future NDI sender)
> abandons its in-flight `Submit` promptly and the pump takes the clean teardown path instead of the
> leak fallback. A new `VideoOutputPumpTests` case proves `Dispose` returns well under the 2 s cap when
> the inner supports abort. Build clean; pump/router/Core-video/HaPlay tests green across reruns.

## 🟡 5. Sync-over-async in `Dispose()`

Several `IDisposable.Dispose()` bodies call `DisposeAsync().AsTask().GetAwaiter().GetResult()`:
`MediaSession.cs:119`, `CuePlaybackEngine.cs:1102/2129–2131`,
`ControlEventQueue.cs:231`. This is the standard compromise when a type is both
`IDisposable` and `IAsyncDisposable`, and these are teardown paths, so the deadlock risk
is low — but it's worth: (a) preferring `await using` / `IAsyncDisposable` at call sites
so the sync path is rarely taken, and (b) confirming no captured `SynchronizationContext`
on the async path (they look context-free, but an audit is cheap insurance). *No actual
`Task.Result`/`.Wait()` deadlock candidates were found — the `.Result` hits in the VMs
are record-property reads, not tasks.*

## 🟢 6. HaPlay best-effort cleanup should be centralized

The framework consistently uses `MediaDiagnostics.SwallowDisposeErrors(action, context)`
(which logs in DEBUG). HaPlay and `HaPlay.Desktop` have many short best-effort cleanup
catches around cancellation, `Dispose`, listener shutdown, file deletion, and output
unwinding. Most are intentional and commented, but a few remain truly empty
(`PlaylistDecoderCache.cs:94/116/129`, `CuePlaybackEngine.cs:2142/2143`), and the
pattern is now spread across enough files that it is hard to audit.

This does **not** look like a functional bug. The cleanup paths are correctly
best-effort. The improvement is to add one or two HaPlay-local helpers, e.g.
`TryDispose(context, action)` / `TryCancel(context, action)`, that delegate to
`MediaDiagnostics.SwallowDisposeErrors` or log through the app logger. That keeps the
same behaviour while making future teardown failures visible in DEBUG/log files.

> **Status (2026-06-15):** `UI/HaPlay/HaPlayCleanup.cs` adds `TryDispose` / `TryRun` /
> `TryCancel` delegating to `MediaDiagnostics.SwallowDisposeErrors`. The truly-empty catches
> called out above (`PlaylistDecoderCache` 94/116/129, `CuePlaybackEngine` 2142/2143) now route
> through it, so teardown failures log in DEBUG instead of vanishing. Prefer these helpers for
> any new best-effort cleanup.

## 🟢 7. Schedule the obsolete-API removal

The deprecation policy (`Doc/MediaFramework-PublicAPI.md`) is well-managed, but the
`[Obsolete]` surface is now sizable and ready to cut at the next major:

* `MediaPlayer.TryOpen*` family (8 overloads, `MediaPlayer.cs:430–763`) — builders
  fully replace them; HaPlay still references some, so migrate those first.
* `IPlaybackTimeline` / `IPlaybackPlayhead` aliases → `IPlayhead`/`IReadOnlyPlayhead`.
* `AudioRouterAutoResample` type alias, the wrong-assembly `IVideoCpuFrameConverter`
  members (now via `MediaFrameworkPlugins`), `IDeinterlacer`/converter obsolete statics.

A `[Obsolete(..., error: true)]` pass next major, then deletion, keeps the surface lean
(it also trims the AOT/reflection surface slightly).

> **Status (2026-06-15):** The headline item is done — the **`MediaPlayer.TryOpen*` family (9
> overloads) was demoted from `[Obsolete] public` to `internal` builder cores**, and the builders'
> `#pragma warning disable CS0618` suppressions were removed. HaPlay/tools already used the public
> builders, so there were no public callers to migrate; the full solution + 99 Playback + 524 HaPlay
> tests stayed green. The remaining items (`IPlaybackTimeline`/`IPlaybackPlayhead` aliases,
> `AudioRouterAutoResample`, the wrong-assembly `IVideoCpuFrameConverter` members, the `IDeinterlacer`
> static, the `MediaContainerPlaybackBundle` member) were all confirmed unused and **deleted** — the
> two interface-alias files + `AudioRouterAutoResample.cs` removed, the registry-property/enum shims
> edited out, and 4 dangling doc crefs repointed to `MediaFrameworkPlugins`. Full solution + 508 Core
> + 99 Playback + 524 HaPlay tests stayed green. **#7 done.**

## 🟢 8. Headless-vs-GL verification gaps

* The **warp mesh is GL-only**; the CPU chained stage silently falls back to the affine
  transform and ignores mesh points ([10](10-Effects-and-Compositing.md)). A host that
  runs the CPU compositor headless and expects warp gets affine instead — worth a
  one-line warning when a mesh is set on a non-warp-capable backend.
  *(Status 2026-06-15: this warning already exists — `ClipCompositionRuntime.Composite`
  logs it once per stage when a mesh section hits the CPU fallback. The CI item below is now
  wired.)*
* GL composite/flip correctness can't be caught by headless CPU tests (project memory:
  composition orientation GL vs CPU). `CompositorSmoke --pattern` under `xvfb` in CI is
  the mitigation — make sure it's actually wired into CI, not just available.
  *(Status 2026-06-15: now wired — `.github/workflows/build.yml` has a `gl-smoke` job that runs
  `CompositorSmoke --pattern` under `xvfb` + Mesa software GL; SDL3/Skia natives come from NuGet,
  so no FFmpeg is needed for the media-free `--pattern` path. Windows publishes now also pull
  FFmpeg natives from the `FFmpeg.GPL` NuGet instead of hand-adding them.)*

## ✅ Already fixed (verified during this review — noted so the record is current)

* **`VideoRouter.TryAddRoute` graph poisoning** — an earlier finding (a throw out of
  branch negotiation left the input half-configured) is **resolved**: `VideoRouter.cs`
  (~280–322) now rolls the route back completely and restores the prior configured
  state, returning `out errorMessage` instead of poisoning the graph.
* **AOT periodic audio drops / progressive desync** — fixed by the primary-output
  backpressure in `OutputPump.Commit` ([04](04-Core-Audio-Engine.md)); GC tuning did
  *not* fix it and shouldn't be revisited.
* **Pause/resume desync** — fixed by the `MediaClock.Start` master-drift fold
  ([06](06-Clocks-and-AV-Sync.md)), contingent on outputs honoring the freeze contract
  (which `PortAudioOutput` does).
* **HW-frame PTS / seek desync** — fixed (`av_frame_copy_props` on hardware frames);
  guarded by `TransportSyncProbe --verify-content`.
* **GL compositor "composite_layer program missing required uniforms" (2026-06-15)** —
  `SharedGlProgramCache` was keyed by shader-pair string only, so a program linked in one
  GL context was handed to a compositor in another (every `glGetUniformLocation` → -1,
  crashing `GlVideoCompositor.BuildPipeline`). Surfaced when a multi-output composition
  created a second compositor (probe / mapping stage) alongside the canvas compositor.
  Fixed by scoping the cache **per `GL` instance** (`ConditionalWeakTable<GL, …>`), so
  programs are only shared within one context (their own remark already required this).

## Optimization notes (mostly "leave it alone")

* **Audio mixing** is already SIMD-saturated and allocation-free. AVX-512 lanes are the
  only headroom and the ROI is marginal vs the AVX2/SSE paths already present.
* **Pass-through descriptor arena** already has lock-free Treiber stacks *plus* opt-in
  profiling and an opt-in per-arena mutex — the contention question is already
  instrumented; only flip the serialize knob if a real workload shows CAS churn.
* **Zero-copy fan-out** (CPU views + dma-buf/D3D11 sharing) is already in place; the
  remaining copies are the genuinely unavoidable mixed-capability fan-outs.

## Suggested order of attack

1. **#1 (VM decomposition)** — biggest maintainability win, zero behaviour risk.
2. **#2 Option A (auto adaptive-rate for multi-output)** — biggest user-visible
   robustness win, reuses existing pieces.
3. **#7 (obsolete cleanup)** + **#6 (cleanup helper)** — quick housekeeping.
4. **#3/#4** — opportunistic, when you're already in those files.
5. **#2 Option B (coordinated clock policy)** — only if multi-output pixel-lock becomes
   a hard requirement; it's a design project.

---

That's the tour. For the why-it-works narrative, start back at
[01 · Overview](01-Overview-and-Data-Flow.md); for the concept that ties it all
together, [06 · Clocks & A/V Sync](06-Clocks-and-AV-Sync.md). For "where is this
class covered?", use [16 · Type Coverage Appendix](16-Type-Coverage-Appendix.md).
