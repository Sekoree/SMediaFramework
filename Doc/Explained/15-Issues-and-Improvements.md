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
| `UI/HaPlay/ViewModels/CuePlayerViewModel.cs` | 4,862 |
| `UI/HaPlay/ViewModels/MediaPlayerViewModel.cs` | 4,449 |
| `UI/HaPlay/ViewModels/ControlWorkspaceViewModel.cs` | 4,165 |
| `UI/HaPlay/Playback/CuePlaybackEngine.cs` | 2,395 |
| `UI/HaPlay/Playback/HaPlayPlaybackSession.cs` | 2,196 |
| `UI/HaPlay/ViewModels/MainViewModel.cs` | 2,091 |

A 4,800-line view-model is very hard to test, reason about, or change safely. The
author already flagged the UI as the rough part. Concrete, low-risk decompositions:

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

## 🟢 8. Headless-vs-GL verification gaps

* The **warp mesh is GL-only**; the CPU chained stage silently falls back to the affine
  transform and ignores mesh points ([10](10-Effects-and-Compositing.md)). A host that
  runs the CPU compositor headless and expects warp gets affine instead — worth a
  one-line warning when a mesh is set on a non-warp-capable backend.
* GL composite/flip correctness can't be caught by headless CPU tests (project memory:
  composition orientation GL vs CPU). `CompositorSmoke --pattern` under `xvfb` in CI is
  the mitigation — make sure it's actually wired into CI, not just available.

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

---

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
