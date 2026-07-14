# MFPlayer / HaPlay Code Review - 2026-07-13 (branch `test-enhancements`)

## Scope and method

Reviewed the full `master...test-enhancements` diff (~22,700 insertions / 3,660 deletions across 194
files: the 2026-07-12 review's fix batches plus the "Bigger fixes" / "Experiments" commits), with the
new subsystems (S.Media.Encode.FFmpeg, S.Media.Stream.Http, S.Media.Visualizer.ProjectM, ProjectMLib,
buses/effects, ABI effect capabilities) read line by line, and a general style pass over every
first-party framework library and the HaPlay UI. `External/`, `Reference/`, and generated output were
excluded. The two NEW UI-hang dumps from 2026-07-13 (`haplay-uihang-20260713-044457.dmp`,
`haplay-uihang-20260713-184012.dmp`) were analyzed with `dotnet-dump` as incident evidence for the
still-open hang.

Verification baseline on this branch: `dotnet build` Debug and Release both clean (0 warnings,
0 errors), full test suite green (all assemblies passed; ~1,400+ tests visible in the summary tail,
exit code 0).

Severity: **H** = correctness/availability issue to fix before relying on the feature in a show;
**M** = material robustness/performance issue; **L** = cleanup or smaller edge case. Items marked
**[carried]** were reported on 2026-07-12 and are still open in this tree.

## Executive summary

The fix batches from the 2026-07-12 review landed well: OverlayPopups, encode pixel-format/CRF
validation, visualizer settings persistence (`AppSettings.Update` is now the single serialized write
path and every writer uses it), stream-key masking + redacted sink names, watchdog dump retention +
ptrace reset, capability-preserving audio-effect wrapping with correct lease ownership,
retire-on-processing-thread effect hot-swap, the ABI audio/video effect extension, event-driven deck
advancement, and jump/visualizer cues. The new code is consistently well documented and the test
suite grew substantially.

The three operator-visible problems from the latest commit message all have identifiable causes in
this tree:

1. **UI still hangs** - the two new dumps show the OverlayPopups fix worked (no tooltip surface), but
   the *same GLX-swap stall* now surfaces through a real dialog close
   (`CompositionOutputLayoutDialog` → `Window.Close` → `SyncWaitCompositorBatch` → `Task.Wait()`
   while the render thread sits in `GlxGlPlatformSurface…Session.Dispose`/swap). A second dump shows
   a *new* signature: the UI thread inside SkiaSharp font-fallback matching during a `TextBlock`
   measure. Details in H1/H2 below.
2. **After-load cue output not output** - the fixed-FPS encode scheduler mixes two PTS domains
   (keepalive wall clock vs. clip media time) through one baseline and drops every frame whose tick
   is behind the cursor: a clip fired after a stream has been live N seconds produces **black video
   for N seconds** while audio flows (H3). The cue reload also silently skips bindings whose runtime
   isn't ready, with no retry (M1).
3. **Remove/re-add output → composition layout doesn't work** - a four-step mechanism in the cue
   coordinator: stale binding Guid after re-add, no topology subscription so nothing reattaches,
   live layout updates are deliberately not reload triggers and are *silently dropped* when they
   miss, and the debounced reload defers indefinitely while any cue is active (H4).

## UI-hang dump analysis (2026-07-13)

### Dump `haplay-uihang-20260713-044457.dmp` - window close, same stall as 07-12

- **UI thread**: `CompositionOutputLayoutDialog.CancelClick` → `Window.Close` →
  `X11Window.Cleanup` → `PresentationSource.Dispose` → `MediaContext.SyncDisposeCompositionTarget`
  → `SyncWaitCompositorBatch` → `Task.Wait()`.
- **Render thread**: `ServerCompositionTarget.Render` → `DrawingContextImpl.Dispose` →
  `GlRenderTarget.GlGpuSession.Dispose` → `GlxGlPlatformSurface.RenderTarget.Session.Dispose` - the
  exact frame the 07-12 review's JIT analysis mapped to `glXSwapBuffers`.
- The projectM renderer thread was idle in its frame pacer; SDL video output idle.

**Conclusion**: identical mechanism to 07-12 - a driver-level GLX swap stall wedges Avalonia's render
thread, and any synchronous UI→render-thread wait freezes the UI. OverlayPopups removed the *tooltip*
instance of that wait; a real window/dialog close performs the same synchronous wait and cannot be
configured away. The 07-12 remediations stand unchanged: a show-safe Linux mode with the main UI on
the software renderer, and moving projectM (and ideally all non-UI GL) out of the UI process/driver
failure domain. Until then, **every dialog close during a driver stall is a freeze**.

### Dump `haplay-uihang-20260713-184012.dmp` - NEW signature: font fallback on the UI thread

- **UI thread**: `TextBlock.MeasureOverride` → `TextLayout.CreateTextLines` → shaping →
  `FontManager.TryMatchCharacter` → `SystemFontCollection.TryMatchCharacterFromPlatform` →
  SkiaSharp `sk_fontmgr_match_family_style_character` (native fontconfig match).
- **Render thread**: inside `sk_surface_new_backend_render_target` (Skia GL surface creation).
- projectM renderer idle in its pacer; no popup/close frames anywhere.

**Interpretation**: the UI thread stalled (≥8 s heartbeat gap) inside per-character font-fallback
matching. Milkdrop preset names are full of exotic codepoints (math symbols, box drawing, CJK), and
the VIZ status text / drawer re-renders them; every unmatched codepoint costs a native fontconfig
query, serialized against whatever Skia/driver state the render thread holds. Recommendations:
sanitize or elide preset names rendered in per-tick UI text, avoid re-measuring them every update
(cache the formatted string; only invalidate on preset change), and consider configuring an explicit
Avalonia font-fallback list so matching short-circuits. If it recurs, capture with
`HAPLAY_OVERLAY_POPUPS=0` ruled out and compare - this signature is not the GLX-swap stall.

### Logging gap

The rolling log covering the 18:40 hang was already rotated away (retain 10 files, but each run
writes two, so only ~5 sessions of history survive). When the watchdog captures a dump, **pin the
current rolling log** (copy it next to the `.dmp`) so dump and log never separate. The retention
sweep (M11) itself is implemented correctly now.

## High-severity findings

### H1. Synchronous window-close wait on a stalled render thread (the 04:44 hang)

Files: Avalonia internals via `UI/HaPlay.Desktop/Program.cs`, `CompositionOutputLayoutDialog`.

See dump analysis above. Nothing in this repo *causes* the GLX stall, but the app's exposure is a
product decision this repo controls: (1) offer the software-renderer UI mode on Linux
(`X11PlatformOptions.RenderingMode = [X11RenderingMode.Software]`) as a show-safe toggle, and
(2) isolate projectM/SDL GL work from the UI process (the 07-12 H1 helper-process plan). Also
consider deferring dialog `Close()` while the compositor batch is overdue (detectable via the
watchdog's heartbeat) to at least keep the main window interactive.

### H2. UI-thread font-fallback stall rendering visualizer/preset text (the 18:40 hang)

Files: `MediaPlayerViewModel.ShowSession.cs` (status text with preset names),
`CuePlayerView.axaml` VIZ drawer, `PipelineStatsView.axaml` (per-second text refresh).

See dump analysis above. Cache formatted status strings, strip non-ASCII (or non-BMP at minimum)
from preset names shown in ticking UI, and set an explicit fallback font list. A 552-preset pack
makes this a routine operator scenario, not an edge case.

### H3. Fixed-FPS encode scheduler drops all frames behind the tick cursor - black stream video after keepalive, dead segments across track changes

Files: `MediaFramework/Media/S.Media.Encode.FFmpeg/FFmpegEncodeSession.cs:474-522`,
`MediaFramework/Media/S.Media.Stream.Http/StreamKeepAlive.cs:67-107`,
`UI/HaPlay/OutputPreview/LiveStreamOutputRuntime.cs`, `UI/HaPlay/OutputPreview/FileOutputRuntime.cs`.

`EncodeAtTargetRate` computes each frame's tick from `frame.PresentationTime` relative to a single
session-lifetime baseline (`_videoBaseTicks`, anchored by the *first* frame) and **drops any frame
whose tick ≤ `_lastVideoTick`**. Live streams *always* run this path (validation demands `Fps > 0`).
Two PTS domains flow through it:

- `StreamKeepAlive` stamps black frames on its own wall clock starting at 0 and keeps advancing it
  even while yielded.
- Real playback frames arrive stamped in clip/session media time (restarting near 0 per fire).

Consequences, all reproducible from the code:

1. Stream live for N seconds, then fire a clip: every real frame's tick is behind the keepalive's
   cursor until the clip's media time exceeds N → **video freezes on black for N seconds while the
   clip's audio streams** (audio is sample-counted, append-only). This matches "after-load Cue
   output doesn't seem to be respected/output".
2. Clip ends, keepalive resumes: if the clip advanced the cursor beyond the keepalive's wall clock,
   keepalive frames drop → dead stream between tracks, defeating the keepalive's purpose.
3. Fixed-FPS *file* recording across a track change: track 2's frames (media time ≈ 0) drop until
   they pass track 1's cursor. A backwards seek mid-recording likewise freezes output for the seek
   distance (the non-fixed path clamps forward one frame instead - inconsistent policies).

Fix: make the scheduler *monotonic like the non-fixed path*. On a backward jump (tick ≤ lastTick by
more than one tick), re-anchor `_videoBaseTicks` so the incoming frame lands at `lastTick + 1`
instead of being dropped, and keep the ≤ check only for genuine duplicate ticks (fast input). The
keepalive should also stop stamping its own clock and instead continue from the session's cursor
(ask the session for "next tick time") so hand-offs are seamless in both directions. Add tests:
keepalive→clip handoff after 10 s live, clip→keepalive, fixed-FPS two-track recording, backwards
seek during fixed-FPS record.

### H4. Cue output remove/re-add leaves the composition without that output; layout edits are silently dropped

Files: `UI/HaPlay/ViewModels/CueShowSessionCoordinator.cs` (DetachCueOutputLineAsync /
AttachCueOutputLineAsync / OnCueReloadDebounceTick / ApplyCueShowOutputMappingAsync),
`UI/HaPlay/ViewModels/OutputManagementViewModel.cs` (RemoveLineAsync, add commands),
`MediaFramework/Media/S.Media.Session/ShowSession.cs:640-660` (preservation compatibility).

The mechanism has four cooperating parts:

1. **Remove** works: `RemoveLineAsync` awaits the detach hook, the live composition drops the
   output. But the cue list's `CueVideoOutputBinding.OutputLineId` still holds the removed Guid.
2. **Re-add creates a new Guid**, and the coordinator subscribes only to *cue-model* changes - it
   has no `RoutingTopologyChanged` subscription, and `Outputs.Add` raises no reconfigure event, so
   nothing ever re-acquires/attaches the new line. (The reattach path `AttachCueOutputLineAsync`
   exists but only fires from the reconfigure/arm events.)
3. **Live layout edits don't reload by design**: `OnCueOutputBindingPropertyChanged` deliberately
   ignores `Mapping`/`MappingEnabled` changes (they're applied live via
   `UpdateOutputMappingCallback`); when the live apply misses - because the output isn't attached -
   it only logs "missed" and the edit is lost with **no operator feedback and no dirty-mark**.
4. **While any cue is active the debounced reload defers forever** (`CuePlayer.HasActiveCues`
   restarts the timer), so even edits that do mark the graph dirty (rebinding the new line) don't
   apply until the next GO/stop - during a running show the layout appears simply broken.

Additionally, `ShowSession` preservation compatibility still checks only id+raster+rate and skips
the video-output factory for preserved compositions (07-12 M10, **[carried]**), so a preserve-true
reload can never fix output topology either.

Fix package: subscribe the coordinator to output topology changes and run
`AttachCueOutputLineAsync` when an added line matches an existing binding; make a missed live
mapping/placement update mark the graph dirty (and surface a status message); include the output
lease set in preservation compatibility (or force preserve=false whenever `_cueVideoOutputs`
changed); and surface "binding skipped - output not available" from the reload instead of the
silent `continue`.

### H5. `ProjectMModule.Register(ICompositorRegistryBuilder)` leaks a continuous renderer per layer-surface creation

File: `MediaFramework/Media/S.Media.Visualizer.ProjectM/ProjectMModule.cs:28-37`.

The compositor-registry path creates a `new ProjectMVisualSource(1920, 1080, 30)` per
`CreateLayerSurface` call and never disposes it. With `OffscreenGlContextFactory` set (it always is
in HaPlay once projectM is available), **every such source immediately spawns a dedicated render
thread + offscreen GL context that runs until process exit**; `ProjectMFrameBlitSurface.Dispose` is
a flag-only op and does not reach the source. Every composition rebuild that instantiates a
config-blob projectm layer therefore accumulates one orphan renderer (and that source receives no
PCM, so it renders idle visuals). HaPlay's deck/cue paths use `SetCompositionVisualizerAsync` with
owned sources and don't hit this today - but the registry path is the documented plugin-parity seam
and is armed. Fix: make the surface own (and dispose) a source it created, or remove the
compositor-registry registration until a real caller exists.

### H6. In-place video effects corrupt sibling outputs sharing fan-out frame backing

Files: `MediaFramework/Media/S.Media.Core/Buses/BusContracts.cs:32-37`,
`MediaFramework/Media/S.Media.Routing/VideoEffectBusOutput.cs:94-106`,
`MediaFramework/Interop/S.Abi/include/mfp_plugin.h` (video-effect v1 contract),
`MediaFramework/Media/S.Media.Routing/Video/VideoRouter.cs:33-39`.

`IVideoBusEffect` (and the native plugin contract) explicitly permit mutating CPU pixels in place.
But the video router's CPU NV12 fan-out deliberately **shares one plane backing across all outputs**
(`VideoFrame.TryCreateNv12CpuFanOutViews`), and per-output effect chains
(`OutputManagementViewModel.WrapVideoEffects`) run on those shared views. An in-place effect
(grayscale, a native invert plugin) on output A then rewrites the pixels output B displays - or
races B's consumption mid-frame. Fix: when a branch has a non-empty video-effect chain, force that
branch onto the copying path (router CPU converter / `DuplicateCpuBacking`) or copy-on-write in
`VideoEffectBusOutput.Submit` before the first mutating effect.

## Medium-severity findings

### M1. Cue reload silently skips bindings whose output can't be acquired

File: `UI/HaPlay/ViewModels/CueShowSessionCoordinator.cs` (`ReloadCueShowSessionOnceAsync`).

`if (output is null) continue;` - a disarmed encode line, a not-yet-started preview runtime, or a
line held elsewhere yields a composition with fewer (or zero) outputs, a log line, and no operator
feedback or retry. Combined with H4.4 this is the likeliest "after load, output missing" report
besides H3. Surface it (status message / line health) and re-run attach when the runtime arms.

### M2. `MuxPacketSink.BytesWritten` races Dispose (native use-after-free window)

File: `MediaFramework/Media/S.Media.Encode.FFmpeg/Sinks/MuxPacketSink.cs:51-63,183-224`.

`BytesWritten` dereferences `_fmt->pb` with no synchronization; `GetMetrics()` (UI health poll, 1 s
cadence) can hit it while `Dispose` frees the format context on the stop path. Null out `_fmt`
before freeing under a small lock, or snapshot bytes into a `long` field on each write.

### M3. `LiveStreamSession.Start` leaks the encode session when keepalive construction fails; zero-sink config only fails late

File: `MediaFramework/Media/S.Media.Stream.Http/LiveStreamSession.cs:151-256`. **[carried]**

The session is created inside the `try`; if `StreamKeepAlive` construction/`Start` throws, the
`catch` disposes sinks directly (the session worker/codecs leak, and sinks get double-disposed -
idempotent today, but only by luck). Hoist the session local out and dispose it on rollback.
Validation still accepts `LocalServer` enabled with both TS and HLS off and no push targets - it
reaches `CreateWithSinks` and throws "at least one packet sink", an unfriendly late error.

### M4. Keepalive↔playback handoff race on the combined audio sink

Files: `StreamKeepAlive.cs:75-106`, `FFmpegEncodeSinks.cs:103-129`. **[carried]**

`SetPlaybackActive` is volatile-flag based; one keepalive silence chunk can interleave with the
first real submit, and `FFmpegEncodeCombinedAudioSink` reuses `_legScratch` with no synchronization,
so a straddling submit can corrupt/split a chunk. Serialize check+submit against acquire/release.

### M5. TS history join discontinuity (carried M9) and other LAN-server gaps (carried M8)

Files: `TsFanOutBuffer.cs:99-114`, `HttpMediaServer.cs`. **[carried]**

Still open from 07-12: history-overflow clients get head-of-history + live tail (decoder-corrupting
discontinuity; the comment still claims "slightly later"); server pooling ignores `bindAddress` when
a port is reused; request reads have no deadline (slowloris); the client cap is check-then-spawn;
HEAD `/status` sends a body and the 405 body says "GET only"; HLS HEAD counts BytesServed;
`HttpMount.Refs` unused; `LiveStreamStatus.LocalServerBytesServed` hard-coded 0 while
`HttpMount.BytesServed` is tracked; no port-range validation. Also new: `ReleaseMount` holds the
global `ServersGate` through a 2 s accept-loop join, and `FormatStreamUrls`
(OutputManagementViewModel) prints `Dns.GetHostName()` as the URL host, which LAN clients often
can't resolve - print the bound interface IP(s).

### M6. Output reconfigure still commits UI state off the dispatcher (carried M6)

File: `UI/HaPlay/ViewModels/OutputManagementViewModel.cs:660-735`. **[carried]**

`ReconfigureLineAsync` flows through `ConfigureAwait(false)` and then calls
`line.ReplaceDefinition(...)` (a burst of `PropertyChanged`), `RaiseTopologyChanged()` and the
`OutputLineReconfiguredAsync` handlers - which re-acquire outputs documented as UI-thread-only - on
whatever thread the awaits resumed on. Marshal the commit + events to `Dispatcher.UIThread`.

### M7. Removing one effect insert removes value-equal duplicates (carried M12)

File: `UI/HaPlay/ViewModels/OutputLineViewModel.cs:167-174`. **[carried]**

`Where(e => !ReferenceEquals(e, effect) && e != effect)` - `OutputEffectDefinition` is a record, so
two identical gain inserts are both removed when either is deleted (and the `ReferenceEquals` clause
is redundant given record equality). Remove exactly one occurrence by index.

### M8. Continuous-renderer transfer/pacing costs (carried M1, partially addressed)

Files: `ProjectMOffscreenRenderer.cs:198-236`, `ProjectMFrameBlitSurface.cs:83-96`,
`StreamKeepAlive.cs:101-105`. **[carried]**

Still per frame: synchronous `glReadPixels` (untimed - only the projectM render call is timed, so a
readback stall produces no SLOW log), full-frame copy under `_frameGate` on both producer and
consumer sides, and the absolute-schedule pacer that "catches up" with an unpaced burst after a
stall (the same pattern was newly copied into `StreamKeepAlive.Run`). `CurrentPresetName` is still
assigned *after* the native load returns, so a never-returning load cannot be named in a dump/log -
set an "attempting" marker before `projectm_load_preset_file`.

### M9. Legacy `ProjectMGlLayerSurface` never destroys its projectM instance (carried M3)

File: `ProjectMGlLayerSurface.cs`. **[carried]**

`projectm_destroy` is called only by the offscreen renderer's teardown. The legacy in-composition
surface (still the fallback when no offscreen factory is set) leaks the native instance on dispose.

### M10. Opus/sample-rate (and similar) codec constraints validated only at `avcodec_open2`

Files: `EncodeOptions.cs` (Validate), `FfmpegAudioEncoderCore.cs:186-249`.

Opus accepts only 48/24/16/12/8 kHz; a 44.1 kHz mix rate with default leg settings fails deep inside
session construction with a raw FFmpeg error. Same class of issue: x265 10-bit depends on the build.
Validate rate/codec compatibility in `Validate()` (the encoder's `supported_samplerates` is
available from `avcodec_find_encoder`).

### M11. `FireCueIndependentAsync` maps caller cancellation to `Failed`

File: `MediaFramework/Media/S.Media.Session/ShowSession.cs:1586-1620`.

`catch (OperationCanceledException) { return CueExecutionStatus.Failed; }` conflates "the operator
cancelled" with "the fire failed", so the UI reports an error for a deliberate cancel. Rethrow when
`cancellationToken.IsCancellationRequested`, or add a distinct status.

## Low-severity findings and cleanup

- **L1.** Both projectM blit shaders compute `vec4(rgb * uOpacity, uOpacity)` and then blend with
  `SrcAlpha` - opacity is applied twice (a squared fade curve). Consistent across legacy and
  continuous surfaces, but wrong; use premultiplied output with `One` src factor, or don't
  pre-multiply. Also `GetUniformLocation` is called per frame in both `Render` paths - cache the
  locations at `ConfigureGl`.
- **L2.** `FFmpegEncodeSession.Dispose` stuck-branch reports success via `_finished.TrySetResult()`
  for a wedged worker; callers awaiting `Completion` can't tell. `GetMetrics` also takes `_gate`
  twice back-to-back. The `WorkerLoop` comment "best-effort trailer below in Dispose" is drift - the
  trailer comes from `MuxPacketSink.Dispose`. `FileOutputRuntime`'s "Dispose flushes + writes the
  trailer" comment overstates the same way (no encoder flush happens).
- **L3.** `av_opt_set` returns are still unchecked for `crf`/`preset` on codecs that *do* support
  them - a typo'd preset silently encodes with defaults (carried H8 sub-item).
- **L4.** `BusRegistry.TryCreate*` still lets factory exceptions escape a `Try` API (carried L4);
  `GainAudioEffect.GainDb` getter returns ≈-180 dB (not -∞) after a mute set.
- **L5.** `VideoEffectBusOutput.SetEffects` XML doc still says removed effects are "disposed after
  the swap", contradicting the retire-queue implementation five lines below.
- **L6.** `build-projectm.sh` (carried L2): unpinned `git clone`s, `rid` hard-codes `linux-x64`
  (the resolver probes `linux-arm64`), the vendored `Reference/projectm-4.1.6` tree is patched in
  place, and a stale `presets.tmp` makes later runs silently fall back to the test presets.
- **L7.** `MediaRuntime` comment says a factory returning null "falls back to in-composition
  render" - it doesn't; the renderer marks `Failed` and the blit surface renders nothing (M2
  residue). `VisualizerSettingsSeed` is still a static launch-time `AppSettings.Load()` snapshot, so
  a deck created later inherits stale values (persistence itself is now correct).
- **L8.** `OutputManagementViewModel._videoEffectWrappers` is an unsynchronized dictionary and a
  double-acquire without release overwrites (leaks) a wrapper; today's callers are UI-thread and
  single-holder, so this is a fragility note, not a live bug.
- **L9.** Style nits: `OutputLineViewModel.Summary`'s switch body is mis-indented; leftover double
  blank lines where the local `TryUpdateThrottle` helpers were removed
  (`VideoOutputPump.cs`, `VideoRouter.cs`); `FileOutputRuntime.ResolveFilePath` duplicates
  `FfmpegEncodeMaps.ContainerFileExtension`.
- **L10.** Watchdog dump+log pairing: pin/copy the current rolling log next to a captured `.dmp`
  (see the logging-gap note above) and consider counting `haplay-crash-*.log` files separately from
  run logs in retention so hang-adjacent history survives longer.

## What's notably good in this change set

- **AppSettings.Update** - the serialized read-modify-write closes the whole stale-snapshot class;
  all writers verified converted, title-bar-X persistence included.
- The **natural-end generation/claim state machine** in `MediaPlayerViewModel.ShowSession.cs`
  (packed generation+claim CAS shared by event and poll paths) is exactly the right shape, and the
  event hook's one-shot flag is safe because the session is only nulled at VM teardown.
- **AudioEffectOutput.Wrap** capability subclasses, `disposeInner` ownership split, and the
  retire-on-processing-thread queue in all three effect hosts match the review contract; the gain
  ramp is per frame with NaN/∞ rejection.
- The **ABI audio/video effect extension** is clean end-to-end (header contract, vtable validation,
  pinned zero-copy process calls, lease-based lifetime), with a real compiled-C-plugin test.
- `ChannelMap.TryAccumulateAnyInterleaved` removes the SIMD/scalar drift between the two mix paths;
  the **abandoned-vs-dropped** pump counters and the hint monitor's decay kill the latched
  adaptive-rate bias after pause/seek.
- `SwsFrameEmitter`/`SwrPooledAudioFrames` deduplicate genuinely delicate pin/rollback/release code
  out of the demux and file decoders.
- `ControlEventQueue`'s `CoalesceKey` struct removes per-event string allocation under MIDI floods.
- The `ControlWorkspaceViewModel` split into five topical partials, jump/visualizer cue models with
  stable-ID targets, `ElfNeededReader` (GLES-build veto before any native call), and the offscreen
  renderer's non-blocking dispose are all quality work.
- Style across the tree is remarkably consistent: file-scoped namespaces, operational rationale in
  XML docs, one TODO in the entire first-party tree, records for options/DTOs, `Lock` type usage,
  structured logging with named parameters.

## Simplification opportunities

1. **Unify the encode PTS policy** (H3): one monotonic tick cursor with re-anchoring, used by both
   the fixed and free-rate paths, and a keepalive that asks the session for the next tick instead of
   keeping its own clock. This deletes the second PTS domain rather than patching around it.
2. **A cue output-topology reconciler**: one place that owns "binding ↔ line ↔ live lease" and
   reacts to line add/remove/reconfigure/arm and binding edits. Today that logic is spread across
   `DetachCueOutputLineAsync`, `AttachCueOutputLineAsync`, the reload's acquire loop, and the
   arm/disarm event flow - H4/M1 are gaps between those copies.
3. The **big VMs keep growing**: `CuePlayerViewModel` 3,013 lines (plus five partials),
   `MediaPlayerViewModel` family ~2,4k+, `OutputManagementViewModel` 1,702. The 07-12
   recommendations (headless playback controller per deck; output runtime registry service) still
   stand and would have absorbed most of H4/M1/M6 structurally.
4. **Delete or gate the compositor-registry projectm registration** until something consumes it
   (H5) - the deck/cue paths don't.

## Verification and test gaps

Completed here: Debug + Release builds (0 warnings), full suite green, managed-stack analysis of
both 2026-07-13 dumps, line-level review of the new subsystems and all carried-fix sites.

Highest-value tests to add:

- Keepalive→clip and clip→keepalive PTS handoff at fixed FPS (assert frames are *encoded*, not
  dropped, within one tick of the swap); two-track fixed-FPS recording; backwards seek during
  fixed-FPS recording (H3).
- Cue coordinator: remove line → re-add line → rebind → assert composition output attached and a
  layout edit applies while a cue is active (H4).
- A fan-out branch with an in-place video effect: assert sibling outputs receive unmutated pixels
  (H6).
- Compositor-registry projectm layer surface: assert no renderer thread outlives surface disposal
  (H5).
- LAN server: history-larger-than-queue join continuity, per-request deadline, HEAD semantics (M5).
- `MuxPacketSink.BytesWritten` under concurrent stop (M2, a stress test with metrics polling).

## Suggested order

1. H3 (encode PTS unification) and H4+M1 (cue output reconciliation + operator feedback) - these are
   the two commit-message bugs that are fully fixable in this repo.
2. H2 mitigations (cache/sanitize preset text; fallback font list) - cheap, and removes the newest
   hang signature; then continue the H1 track (software-UI mode, projectM helper process).
3. H5/H6 (visualizer leak, effect-on-fanout corruption) before effects/visual layers see real show
   use.
4. M2-M7, then the L items opportunistically.

---

*Method note: dialog/control review was done at code level (XAML + code-behind + VMs); no visual
screengrab pass was made this round. The two new dialogs (Add File Output / Add Live Stream) are
structurally sound - masked stream key, per-kind validation via the runtime's own `Validate()`, and
dialog-size persistence including title-bar close.*
