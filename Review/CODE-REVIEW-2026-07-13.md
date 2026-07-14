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

---

## Verification addendum — 2026-07-14

This addendum independently rechecked the review against `HEAD 6cba5cce` (`test-enhancements`). It
preserves the original findings above, but separates directly observed evidence from likely causal
interpretations. No production code was changed during this pass.

### Reproduction and validation

- `dotnet build MFPlayer.sln -c Debug --no-restore --nologo`: **0 warnings, 0 errors**.
- `dotnet test MFPlayer.sln -c Debug --no-restore --nologo`: **1,865 passed, 9 skipped, 0 failed**
  (1,874 total).
- `dotnet build MFPlayer.sln -c Release --no-restore --nologo`: **0 warnings, 0 errors**.
- Both dumps named in the review exist and were reopened with `dotnet-dump`:
  - `haplay-uihang-20260713-044457.dmp`: the UI thread is synchronously closing the composition
    target from `CompositionOutputLayoutDialog.CancelClick`; the render thread is in GLX surface
    disposal.
  - `haplay-uihang-20260713-184012.dmp`: the UI thread is in Skia font-family/character matching
    while Avalonia is measuring a `TextBlock`; the render thread is creating a Skia backend render
    target.
- This was a code, test, build, and managed-stack verification. It did not include an interactive UI
  pass, hardware matrix, real network broadcast, or long-running native leak measurement.

### Disposition of the existing findings

| ID | Disposition | Verification note |
|---|---|---|
| H1 | Confirmed, with qualification | The deadlock signature and application call site are confirmed. The exact defect inside the graphics driver/native stack, and whether each proposed mitigation prevents it, remain inferences rather than dump-proven facts. |
| H2 | Confirmed, with qualification | The Skia/Avalonia font-fallback stack is confirmed. The dump does not retain enough managed heap data to identify the measured string, so attribution to projectM preset names is a plausible hypothesis, not a verified trigger. |
| H3 | Confirmed | `FFmpegEncodeSession.EncodeAtTargetRate` and `StreamKeepAlive` maintain independent clocks; keepalive can advance the shared video base and make resumed media frames fail the monotonic-tick test. The code defect is deterministic, although its match to a particular field incident remains circumstantial without a captured packet timeline. |
| H4 | Confirmed | The cue coordinator listens for line reconfiguration but not `RoutingTopologyChanged`; bindings retain removed line IDs, newly added IDs are not reconciled, and the active-cue reload loop can keep retrying without repairing the mapping. |
| H5 | Confirmed | The compositor-registry factory creates a `ProjectMVisualSource`, returns only its surface, and never disposes the source/renderer thread. |
| H6 | Confirmed | CPU frames are shared across fan-out branches while the native effect path is permitted to mutate the supplied frame in place. No branch-local copy isolates sibling consumers. |
| M1 | **Partly confirmed / correction** | The silent `output is null` skip and lack of operator feedback are real. The statement that there is no arm-time retry is too broad: arm/disarm raises the reconfiguration events and `OnOutputLineReconfiguredAsync` calls `AttachCueOutputLineAsync`. Retry is still absent for topology add/remove and for a line becoming available without reconfiguration. |
| M2 | Confirmed | `MuxPacketSink.BytesWritten` reads `_fmt->pb` without synchronization while `Dispose` can free the same native state. |
| M3 | Confirmed | A partially started encode session is not reachable from `LiveStreamSession.CreateAsync`'s catch block, and the local-server configuration can validate while producing no usable sink. |
| M4 | Confirmed | Keepalive activation and submission can race, and the combined audio sink's shared scratch buffer has no serialization contract. |
| M5 | Confirmed | The history discontinuity, reused-port bind-address behavior, missing request deadline, client-cap race, HEAD/405 semantics, status accounting, long dispose under the server gate, and hostname-advertising concerns are present. |
| M6 | Confirmed | `ReconfigureLineAsync` continues after `ConfigureAwait(false)` and mutates/broadcasts view-model state without restoring UI-thread affinity. |
| M7 | Confirmed | Record-value equality can remove the wrong item when duplicate-equal definitions are present. |
| M8 | Confirmed | Readback is untimed and copied while locked, catch-up can burst, and `CurrentPreset` is assigned after the native load. |
| M9 | Confirmed | The legacy projectM owner's `Dispose` only sets a flag and does not destroy the native instance. |
| M10 | Confirmed | Unsupported audio sample-rate/codec combinations are not rejected by framework validation and fail later during codec open. |
| M11 | Confirmed | User cancellation is converted into a generic failed result rather than preserved as cancellation. |
| L1–L10 | Confirmed | Each low-severity group was spot-checked against its referenced implementation. The opacity multiplication, metrics/status inconsistencies, ignored native return, `Try*` contract violations, documentation drift, unpinned build script, renderer fallback/settings behavior, lease-map synchronization, style issues, and log-pruning behavior are all present. |

The positive observations were also spot-checked (settings mutation, ABI guards, natural-end tests,
effect ownership wrapper, and native-library probing). No contradictory evidence was found.

### Additional findings

#### A1 — MEDIUM: Dropping queued audio compresses its PTS instead of representing the lost time

`FFmpegEncodeSession.SubmitAudio` drops the oldest queued chunk on overflow and increments a metric,
but it does not carry an absolute sample position or advance the encoder timeline
(`MediaFramework/Media/S.Media.Encode.FFmpeg/FFmpegEncodeSession.cs:281-301`).
`FfmpegAudioEncoderCore` advances `_ptsSamples` only for frames it actually encodes
(`Internal/FfmpegAudioEncoderCore.cs:91-103,121-130`). After a drop, later audio is therefore
timestamped directly after the last encoded audio while video retains its media/wall-clock timeline.
Every dropped duration can become persistent A/V skew instead of an intentional timestamp gap.

**Recommendation:** queue sample positions with chunks and either advance PTS across a drop or insert
equivalent silence. Add an overload test that verifies post-drop audio/video PTS alignment.

#### A2 — MEDIUM: Encoder construction leaks native allocations when codec setup throws

The audio and video encoder constructors call `OpenCodec()` directly. That method allocates several
FFmpeg objects before all format, option, resampler, FIFO, frame, and packet operations have
succeeded (`Internal/FfmpegAudioEncoderCore.cs:25-31,186+` and
`Internal/FfmpegVideoEncoderCore.cs:33-44,175+`). A throwing constructor never exposes an object that
the caller can dispose, and there is no constructor rollback/finalizer. The session constructor also
does not dispose already-created audio encoders if a later leg fails. Unsupported settings from M10
make this reachable through normal validation failures and repeated attempts can accumulate native
resources.

**Recommendation:** make each `OpenCodec` path exception-safe with a shared idempotent cleanup
routine, and have `FFmpegEncodeSession` roll back already-created encoders/sinks if construction does
not complete. Test allocation counters across repeated deliberately invalid opens.

#### A3 — MEDIUM: Default recording filenames are not unique or atomically reserved

`FileOutputRuntime.ResolveFilePath` derives the default filename from a timestamp with one-second
resolution (`UI/HaPlay/OutputPreview/FileOutputRuntime.cs:190-213`). The file is then opened for
writing without an existence reservation. Rapid re-arm within one second—or two output lines using
the same directory/pattern—can select the same path, truncating a completed recording or allowing
two muxers to write the same file.

**Recommendation:** reserve a unique path atomically (with a numeric suffix or session ID), include
sub-second precision for readability, and expose the final selected path. Cover same-second re-arm
and simultaneous-line cases.

#### A4 — MEDIUM: Advertised LAN stream paths can differ from the actual mounted path

`HttpMediaServer.NormalizeMount` lowercases the name and removes non-alphanumeric characters
(`MediaFramework/Media/S.Media.Stream.Http/HttpMediaServer.cs:102-106`). `LiveStreamSession` retains
the original name for `MountName`, log output, and status URLs instead of using
`MountHandle.MountName` (`LiveStreamSession.cs:163,219,243-244,276,283-284`). For example, a configured
`My Stream` is served at `/mystream.ts` but reported as `/My Stream.ts`.

**Recommendation:** validate and normalize once, then retain the handle's canonical mount name for
every displayed URL and status value. Add names containing spaces, punctuation, and uppercase to
the server/session integration tests.

#### A5 — LOW: Keepalive audio drifts when sample rate is not divisible by frame rate

`StreamKeepAlive` creates exactly `audioSampleRate / rate` samples per video interval using integer
division (`StreamKeepAlive.cs:47`). At rates such as 48 kHz/59 fps, repeated truncation makes the
silence clock steadily fall behind the video/wall clock.

**Recommendation:** use a fractional sample accumulator and alternate chunk lengths so the
cumulative sample count matches elapsed time.

#### A6 — LOW: A mount's client count is server-wide, not per stream

`HttpMediaServer.MountHandle.ActiveClients` forwards the server's single global client counter
(`HttpMediaServer.cs:56,368`). If multiple mounts share a port, every `LiveStreamSession` reports the
combined clients of all streams, which makes per-output monitoring misleading.

**Recommendation:** track active requests per mount (and optionally retain a separate server-wide
total), with a two-mount concurrency test.

### Revised priority notes

The original ordering still holds after verification. Add A1 beside H3 because both affect encoded
timeline correctness; address A2 together with M10 so validation failures are resource-safe; and fix
A3/A4 before relying on file and LAN outputs unattended. M1 should be implemented as topology and
availability reconciliation plus feedback, not as an arm-event retry—the latter already exists.

---

## Implementation follow-up — 2026-07-14

The actionable findings were implemented as structural fixes rather than local suppressions. Two incident
recommendations remain deliberately open because the evidence does not justify pretending they are closed:
projectM process isolation is a separate IPC architecture project, and the H2 dump does not identify the text
that triggered font fallback (there is currently no UI binding to `CurrentPresetName`).

### Finding disposition after implementation

| ID | Status | Implemented disposition |
|---|---|---|
| H1 | **Mitigated; isolation remains** | Added an opt-in Linux software-rendered UI mode (`--safe-ui` / `HAPLAY_SAFE_UI=1`) and documented it in the README. This removes Avalonia from the GLX swap failure domain. Moving projectM/SDL GL into a helper process and compositor-aware close deferral remain open architectural work. |
| H2 | **Needs incident evidence** | Source inspection found no HaPlay UI consumer of projectM `CurrentPresetName`; `.WithInterFont()` is already configured. A blanket ASCII sanitizer or global font override would change unrelated operator text without addressing a demonstrated call site. Re-capture with heap/string evidence before changing UI text policy. |
| H3 | **Fixed** | Replaced the competing keepalive/media clocks with one session-owned monotonic cursor. Filler frames explicitly continue that cursor; a resumed/restarted source timeline re-anchors at the next tick. Fixed-rate track changes and backward seeks no longer disappear behind the old cursor. |
| H4 / M1 | **Fixed** | Added a serialized binding↔lease topology reconciler driven by output add/remove/reconfigure and binding changes. It hot-detaches/attaches live composition outputs, forces non-preserving reloads after topology changes, retries available runtimes, marks missed live mappings dirty, and surfaces unavailable/reconnected status to the operator. |
| H5 | **Fixed** | Registry-created projectM surfaces now own and dispose their source. GL resources use an explicit compositor-owner-thread `ReleaseGl` contract so native projectM instances and GL objects are destroyed before context teardown. |
| H6 | **Fixed** | `VideoEffectBusOutput` now establishes branch-local CPU backing once before any effect chain, protecting shared fan-out siblings even when an effect mutates in place. |
| M2–M4 | **Fixed** | Mux byte metrics are managed snapshots; live-session construction has transactional ownership rollback and rejects zero-sink local configurations; keepalive handoff and combined-audio splitting are serialized. |
| M5 | **Fixed** | Full TS join history is delivered without a prefix/tail discontinuity; bind identity, request timeout, atomic client admission, HEAD/405 semantics, per-mount accounting, port validation, shutdown lock duration, canonical paths, and advertised interface IPs were corrected. |
| M6–M7 | **Fixed** | Reconfigure commits/events are marshalled as one UI-thread transaction; removing an effect removes exactly one reference/equal occurrence. |
| M8–M9 | **Fixed** | Renderer publication is double-buffered, readback is timed, pacers rebase after stalls, attempted preset names publish before native load, failed offscreen startup falls back in-composition, and all legacy GL/projectM resources have owner-thread teardown. |
| M10–M11 | **Fixed** | Validation queries the selected FFmpeg encoder's sample-rate/pixel-format capabilities and rejects invalid scalar/private-option combinations; caller cancellation is rethrown from independent cue fire. |
| L1–L10 | **Fixed** | Corrected alpha blending/uniform caching, completion and comment drift, checked private options, contained `Try*` factory failures, corrected gain mute reporting/docs/style, made projectM builds architecture-aware/reproducible/non-mutating, fixed renderer fallback and settings freshness, synchronized video leases, centralized container extensions, and pinned logs beside dumps with independent run-log retention. |
| A1–A2 | **Fixed** | Audio queue entries carry absolute input positions and advance packet PTS across dropped ranges. Audio/video core constructors and the session/sink ownership seam roll back partial native construction. |
| A3–A4 | **Fixed** | Recording paths use millisecond timestamps plus atomic `CreateNew` suffix reservation. Live sessions retain the server's canonical mount name for every URL/status value. |
| A5–A6 | **Fixed** | Keepalive uses a fractional sample accumulator; client and byte counts are tracked per mount. |

### Additional issue found while implementing

HaPlay's persisted encode defaults always carry a CRF and x264-style preset, even when the selected codec is
MPEG-4/ProRes/etc. Once private-option validation became strict, that made otherwise valid UI definitions fail.
The UI→framework mapper now emits CRF/preset only for codec families that support them; direct framework users
still receive a clear validation error for unsupported combinations.

### Live SRT verification follow-up — 2026-07-14

Testing HaPlay against a real MistServer SRT ingest exposed a recovery/observability gap that the
offline URL-validation coverage could not catch. A transient `av_interleaved_write_frame` failure
permanently detached the push sink while the live runtime remained armed, and the one-second output
health poll then overwrote the useful failure state with route health. Push destinations now remain
attached through a reconnecting sink, retry with exponential backoff on a video keyframe (or the next
packet for audio-only streams), and expose connecting/reconnecting/healthy state through the normal
encode metrics and HaPlay IO health panel. Query values such as SRT `streamid` and `passphrase` are
also redacted from metric/log display names even when entered directly in the URL.

The project configuration used for the report was independently verified end-to-end at its configured
1920×1080/60 H.264/AAC shape: the provider reported a live input with both tracks. The original failure
was therefore a transient link failure made permanent by the old detach policy, not an invalid project
or unsupported SRT build. The live-stream dialog's FPS field was widened from 90 to 120 pixels so its
numeric value remains visible beside the stepper controls.

### Regression coverage added

- fixed-FPS filler→clip→restarted-track→filler continuity and audio-gap packet PTS;
- metrics polling across finish/dispose and sink rollback on invalid construction;
- in-place video-effect mutation with shared sibling backing;
- caller-cancellation propagation and throwing registry factories;
- TS history beyond the client queue, canonical mount names, HEAD/405 behavior, per-mount clients, and bind conflicts;
- concurrent atomic recording-path reservation and UI-thread reconfigure tests.

### Visualizer continuity/audio follow-up — 2026-07-14

Testing the live-output project with the visualizer already running exposed two session integration defects.
First, adding the live output left a required non-preserving document reload pending. Firing the next group
flushed that reload, rebuilt the composition, and disposed the visualizer even though the replacement
composition was still its intended target. Visualizer slots can now opt into document-reload persistence:
the session retains the projectM source, preset state, audio tap/filter, metadata subscription, placement and
ownership, then recreates only the compositor surface on the replacement composition. HaPlay cue visualizers
use this contract, so later cover-art media remains below the still-running surface layer.

Second, the session previously attached a tap directly to each clip router. ProjectM advertises a fixed 48 kHz
input, so a 44.1 kHz FLAC router rejected it while the independently-resampled 48 kHz livestream route kept
working. Fixed-rate output adaptation is now a media-registry capability supplied by the FFmpeg module. The
session caches one owned adapter per tap/router format, detaches taps immediately on unregister, and disposes
only its adapters (never the caller's tap). Coverage now includes a full composition rebuild with a persistent
visualizer and a 48 kHz tap receiving a subsequently-fired 44.1 kHz clip.

Post-fix verification: the full Debug suite passed with **1,885 passed, 9 skipped, 0 failed**; the full Release
solution build completed with **0 warnings and 0 errors**.

### Live filler/cadence follow-up — 2026-07-14

The next real SRT run exposed a second keepalive handoff defect. The run log showed the push connect and
successfully announce both streams, then fail its mux write about ten seconds later. The visualizer was not
started until after that disconnect and program audio did not begin until later still. HaPlay acquires a live
output as soon as the composition topology is attached, but the old keepalive treated that lease acquisition
as proof that samples were flowing and immediately stopped both black and silence. The result was a packetless
SRT connection despite the output being armed—the exact interval in which filler was required.

The keepalive now exposes activity-reporting sink wrappers and arbitrates each encoded track from actual
submissions, not route ownership. Acquiring an idle video/audio route continues black and silence; the first
real sample yields that track after a serialized handoff; and filler resumes automatically after a short
three-frame activity grace if an acquired route falls silent. Each audio leg has its own clock and silence
buffer, so an unfed auxiliary/language track stays valid while another track receives PCM. Explicit release
still resumes filler immediately.

Two related encoder issues were corrected in the same pass:

- `PumpOnce` previously drained an unbounded video queue before examining audio. A continuously replenished
  video queue could therefore starve AAC, eventually overflow the five-second audio backlog, and produce
  timeline gaps/discontinuities. Encoding is now weighted round-robin: one video item followed by a bounded
  catch-up batch from every audio leg.
- The encoder produced all configured frames but libx264 packets carried zero duration, which made the HLS
  muxer warn that segment duration could not be precise and left a live receiver to infer cadence from packet
  arrival. Video packets now carry the locked frame duration and output streams advertise both average and
  nominal frame rate from the configured cadence.

The project's exact 1920×1080/60, 6 Mbps H.264 + 192 kbps AAC shape was exercised for eight idle seconds:
the keepalive submitted/encoded **481 frames** (the initial frame plus 8×60), continued audio and mux bytes,
and emitted no zero-duration warnings after the timestamp fix. The provider displaying approximately
190 kbps for AAC is consistent with that configured 192 kbps target; it is not an unexpected codec setting.

Regression coverage now includes acquired-but-idle video plus two audio tracks, real-frame-to-filler resume,
audio-only silence, fixed-rate packet duration/cadence metadata, and the existing filler/media timeline
handoffs. Final verification passed with **1,888 passed, 9 skipped, 0 failed** in Debug; the full Release
solution build completed with **0 warnings and 0 errors**.

### H.264 codec-clock follow-up — 2026-07-14

Probing the running SRT ingest resolved the remaining contradictory frame-rate reports. MistServer's HLS
master advertised `FRAME-RATE=19.072`, while its own track metadata reported `fps=19.072` / `fpks=19072`
but `efps=60` / `efpks=60000`. A five-second decode read exactly 300 frames, the progressive MP4 remux
reported 60/1 nominal and average frame rate, and packet DTS advanced at 16.667 ms. The stream was therefore
actually carrying 60 fps; only its H.264-declared cadence was corrupt.

Bitstream tracing found an SPS VUI `num_units_in_tick=1` and `time_scale=180000`, which declares a derived
90,000 fps. The encoder had incorrectly used the transport/session packet clock (`1/90000`) as libx264's
codec frame clock. MistServer's TS input parses the SPS rate, multiplies it by 1000 for `fpks`, then stores
that value in a 16-bit metadata field: `90,000,000 mod 65,536 = 19,072`, exactly reproducing the displayed
number. MistServer's `efps=60` was its separately measured effective cadence.

Fixed-rate encoders now open with the inverse frame rate as their codec clock (`1/60` here). Input PTS is
rescaled from the session's fine-grained 90 kHz timeline before encoding, and encoded packets are rescaled
back to 90 kHz for fan-out/muxing. This preserves the existing transport timestamps while causing libx264
to write the correct SPS timing. A new 1080p60 local output traced as `num_units_in_tick=1`,
`time_scale=120`, decoded all 120 frames over two seconds, and reported 60/1 nominal and average rate.
Regression coverage asserts that a configured 60 fps session uses a `1/60` codec clock while retaining a
`1/90000` packet interchange clock. Final verification passed with **1,889 passed, 9 skipped, 0 failed**
in Debug; the full Release solution build completed with **0 warnings and 0 errors**.

### Live latency and rate-control follow-up — 2026-07-14

The corrected live stream was probed again to separate ingest delay from viewer delay. MistServer saw the
configured 1920×1080 H.264 video at a measured and declared 60 fps, plus 48 kHz stereo AAC-LC. The labels
`AAC`, `Aac`, and the browser codec string `mp4a.40.2` all describe that same AAC-LC track; there is no audio
codec conversion or mismatch behind the reported delay. The server did still report one B-frame and its
standard roughly 50-second live/DVR buffer. Its DASH manifest advertised a 5-second presentation delay, and
the ordinary HLS playlist exposed a multi-segment live window. A viewer landing about 20 seconds behind was
therefore consistent with segmented playback/player selection rather than a 20-second SRT ingest queue.

This distinction matters operationally. MistServer documents ordinary HLS/DASH as segmented protocols with
potentially high latency, while its WebSocket MP4 path is real-time and expected below three seconds. The
50-second Buffer input default is seekable history, not a requirement that every player stay 50 seconds
behind live. For the provider-hosted page, the server/player configuration must prefer WS/MP4, WebRTC or a
proper LL-HLS path to meet a sub-two-second target; encoder changes alone cannot turn conventional HLS into
that path. See the official [protocol comparison](https://docs.mistserver.org/protocol/),
[WS/MP4 notes](https://docs.mistserver.org/protocol/pseudostreaming/ws-mp4/), and
[Buffer input settings](https://docs.mistserver.org/mistserver/inputs/Buffer/).

The application nevertheless lacked controls for minimizing the encode and contribution portions of the
latency budget. That gap is now addressed end-to-end rather than by appending opaque encoder strings:

- `VideoEncodeOptions` has typed average/constant bitrate mode, VBV capacity, maximum B-frames, and
  zero-latency tuning. Constant mode programs bitrate, equal min/max rates, VBV size/occupancy and, for live
  transport containers, libx264 CBR HRD filler or libx265 strict-CBR/HRD. `MaxBFrames=0` removes frame
  reordering, and H.264/H.265 low-latency mode applies the encoder's `zerolatency` tune.
- HaPlay persists every setting without changing the positional project schema. The live-output dialog has
  editable bitrate mode, keyframe interval, B-frame limit, VBV duration and tune controls, plus an
  inspectable **Apply low-latency preset** action (CBR, 1-second GOP, no B-frames, 500 ms VBV,
  zero-latency tune). Existing projects retain their old average/automatic defaults until the operator opts
  in.
- Each SRT target now has a latency-in-milliseconds field (120 ms default in the editor). The runtime safely
  converts it to FFmpeg's microsecond `latency=` URL option, while preserving an explicit URL value as the
  expert override. FFmpeg defines this as the retransmission/packet-delivery window; lowering it trims only
  the network contribution and must still leave enough time for RTT and packet recovery. See the official
  [FFmpeg SRT option documentation](https://ffmpeg.org/ffmpeg-protocols.html#srt).
- Validation rejects impossible CBR/VBV combinations, unsupported codec/tune combinations, invalid B-frame
  ranges and out-of-range/non-SRT latency fields. Encoder discovery now initializes the FFmpeg binding
  itself, removing a test-order dependency exposed by isolated encoder tests.

Focused regression coverage opens the real libx264 backend with CBR HRD, asserts min/max/VBV/B-frame
programming, round-trips the new project settings, verifies the dialog preset and checks SRT URL precedence
and unit conversion. The dialog also explains that sub-two-second playback needs an appropriate output
protocol so operators do not spend time tuning SRT or x264 against latency that is actually in the HLS
player. Final verification passed with **1,891 passed, 9 skipped, 0 failed** in Release; the full Release
solution build completed with **0 warnings and 0 errors**.

### Stop-to-idle live-carrier follow-up — 2026-07-14

A subsequent SRT run exposed a timestamp-domain defect specifically at cue Stop. The runtime log showed
audio filler resume about 100 ms after Stop, but no video filler transition; roughly one minute later the
remote peer closed the half-idle SRT connection and FFmpeg reported `av_interleaved_write_frame failed
(-5)`. The reconnecting sink then correctly waited for a video keyframe, but none could arrive while the
video encoder was no longer advancing, so the I/O monitor remained red and VLC lost the source.

The route was not actually video-idle. A composition intentionally keeps pumping after its transport is
paused/stopped so an empty canvas can produce black (and persistent surfaces can still render). Those frames
all carried the stopped source's frozen media PTS. The fixed-FPS scheduler therefore dropped every frame
after the first as an already-covered target tick, while the keepalive activity wrapper saw the continuing
submissions and yielded its own black filler. The submitted-frame counter rose, but encoded video packets
and keyframes stopped; only silent AAC continued.

Live video now has an explicit carrier-clock scheduling mode. Both real composition frames and keepalive
black frames capture their monotonic intake time and map that instant to the configured output tick. Media
PTS remains the policy for file recording, and the existing explicit continuation mode remains available
for source-timeline handoffs. This is a timeline separation rather than a special-case Stop hook: paused
canvases, held images, visualizer-only frames and newly restarted source timelines all remain continuous on
the live carrier, while faster input is still dropped and slower input is still hold-duplicated by the one
fixed-rate scheduler. Consequently the stopped composition itself carries black, audio keepalive carries
silence on each unfed track, periodic video keyframes continue, and a transient SRT write failure has a
valid boundary on which to reconnect.

Regression coverage continuously submits frames with an intentionally frozen zero media timestamp while
the route remains active and asserts that encoded video advances inside the keepalive idle grace (so the
test cannot pass later via fallback black). The full encode suite (**22 tests**) and live-stream suite
(**19 tests**) pass. Final verification passed with **1,892 passed, 9 skipped, 0 failed** in Release; the
full Release solution build completed with **0 warnings and 0 errors**.

### File recording clock-policy follow-up — 2026-07-14

The live Stop fix raised the corresponding file-output policy question. Applying only wall-clock video to a
file would have been incorrect: after a cue stopped, video would continue while routed audio stopped, creating
the same duration mismatch in a different form. Both useful recording behaviours are now explicit instead:

- **Continuous program** records the complete Arm-to-Stop interval. It starts black video and every configured
  silent audio track immediately on Arm, hands each leg to routed media only while samples are arriving, then
  resumes filler. It requires a fixed video width, height and frame rate because the file must have a known
  raster before the first cue. New file outputs default to 1920×1080 at 30 fps and this policy.
- **Content only** joins source-timeline content and collapses idle gaps. A dedicated video gate drops repeated
  held-canvas presentations with an unchanged source PTS, including the source-following-FPS path that would
  otherwise monotonically clamp those repeats and extend video without audio.

The former stream-only `StreamKeepAlive` implementation was moved down into the encode layer as the reusable
`ContinuousEncodeCarrier`; live streams and continuous file outputs now use exactly the same serialized
black/silence ↔ real-media handoff and live wall-clock video scheduler. Audio handoff also retains the samples
that accrue during its short inactivity grace and emits that silence when filler resumes, so repeated stops do
not shorten audio relative to the wall-clock video program.

`FileOutputDefinition.RecordingMode` is nullable for compatibility. A missing value identifies an existing
project created before this setting and preserves its historical content-only behaviour; the add/edit dialog
always persists the operator's explicit choice. The I/O summary displays the selected policy, and both dialog
and runtime validation reject continuous video without a locked raster/cadence.

Testing this uncovered one adjacent source-following encoder defect: codec contexts used `1/90000` whenever
the FPS option was zero. Built-in MPEG-4 rejects that clock for common frame rates, and H.264 can advertise
incorrect VUI cadence. Codec timebase is now the inverse of the resolved frame rate for both fixed and
source-following modes; packets are still rescaled to the session's 90 kHz interchange clock.

Regression coverage now verifies policy persistence/defaults/legacy fallback, continuous idle black plus a
decodable silent audio track, content-only zero-output idle behaviour, frozen-PTS suppression and resumed
source time, and source-following codec-clock selection. Final verification passed with **1,896 passed,
9 skipped, 0 failed** in Release; the full Release solution build completed with **0 warnings and 0 errors**.
