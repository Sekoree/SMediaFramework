# Media Framework Deep Review Findings

Original review date: 2026-05-27
Independent verification pass: 2026-06-01

Scope: reviewed the media framework code outside `Doc/**` and `UI/**`, including core audio/video, playback, FFmpeg decode/encode, effects/composition, SDL/OpenGL, PortAudio, NDI, MIDI and OSC support.

## About this verification pass (2026-06-01)

The 2026-05-27 findings below were re-checked line-by-line against the current tree. **Each finding now carries a verdict.** The original analysis is largely accurate, but this pass found:

- **2 findings that do not hold against the current code** (NDI `Video` getter is actually thread-safe; the NDI realloc concern rests on a C `realloc` assumption that does not apply to .NET's `NativeMemory.Realloc`).
- **1 finding that has been partially addressed since the review** (the FFmpeg *video* encoder gained an `av_frame_make_writable` call — but the per-frame `av_frame_get_buffer` reallocation it was meant to fix is still there).
- **1 finding that needs an important scope correction** (composition timing): the low-level `VideoCompositorSource` is now a *push / slot* model with a latest-wins buffer and an opt-in master-aligned policy, and the production cue path uses it that way. The decode-speed problem the review describes is real **only for the declarative `VideoCompositor` convenience API**, which still pulls one frame per layer per downstream read.

Verdict legend:

- ✅ **Confirmed** — reproduced against current code; line references accurate (updated where they drifted).
- ⚠️ **Confirmed, scope/severity correction** — the defect is real but the impact, trigger, or existing mitigation differs from the original write-up.
- 🔧 **Partially addressed** — code changed since the review; the issue is reduced but not resolved.
- ❌ **Not valid in current code** — the premise does not hold (already safe, or based on an incorrect assumption).
- 🔎 **Carried over** — consistent with the code but not independently re-reproduced this pass (noted explicitly).

## Executive Summary

The framework has the right major pieces for the stated goal: pull-based sources, push-based outputs, routers, hardware-aware video formats, high-level playback, and first-cut composition. The main risk is not missing features. The main risk is that ownership, lifetime, and timing contracts are inconsistent across modules. That makes simple applications easy to get running, but makes professional cue, soundboard, multi-output, and composition workflows fragile under load.

The most urgent work is unchanged from the original review:

1. Fix native lifetime and background-thread failure paths in FFmpeg demuxing and video playback.
2. Stop audio sources from being consumed merely because they were registered.
3. Make composition frame selection time-driven (the slot engine already supports this via a master-aligned policy that is currently wired only in tests; the declarative `VideoCompositor` does not use it).
4. Make frame ownership/refcounting explicit, especially for static/text/image sources and output handoff.
5. Replace per-output/per-test thread fan-out with a bounded scheduler model or lazy pumps.
6. Tighten `VideoFrame`/`VideoFormat` validation so bad frames fail at the boundary, not inside output/native code.

## Verification Performed

Static verification (this pass): every file:line reference below was opened and checked against the current tree; verdicts and corrected line numbers reflect the code as of 2026-06-01.

Test commands (original review, plus a re-run of Core tests this pass):

- `dotnet test S.Media.Core.Tests` — original: 392 passed, 1 failed (`AudioRouterControlTests.ReconfigureSampleRateWhileRunning_WhenStopped_Throws` hit `OutOfMemoryException` in `AudioRouter.OutputPump..ctor` while starting a thread; the same single test passed in isolation). **Re-run this pass (2026-06-01): 393 passed, 0 failed — the OOM did not reproduce.** That it appears only intermittently confirms the diagnosis: suite-level thread pressure from the per-output thread model (see "Output pumps start one thread per output immediately"), not a deterministic assertion failure. The risk is real under load but load-dependent, so it won't show on a clean run.
- `dotnet test S.Media.Playback.Tests` — 25 passed.
- `dotnet test S.Media.FFmpeg.Tests` — 159 passed.
- `dotnet test S.Media.PortAudio.Tests` — 22 passed.
- `dotnet test S.Media.NDI.Tests` — 50 passed.
- `dotnet test S.Media.FFmpeg.Encode.Tests` — 1 passed. **Confirmed this pass: the project contains exactly one test method.** That is far too little coverage for the encoder issues below (audio sample-format handling, video frame reallocation), none of which a single happy-path test would catch.
- `dotnet test S.Media.SkiaSharp.Tests` — 8 passed.
- `dotnet test S.Media.OpenGL.Tests` — 105 passed.
- `dotnet test PMLib.Tests` — 13 passed.
- `dotnet test OSCLib.Tests` — 20 passed.

## Critical Correctness And Stability Issues

### FFmpeg shared demuxer can race native free/seek while its demux thread is still alive
✅ **Confirmed and fixed 2026-06-02.** The original bug was that `MediaContainerSharedDemux.StopDemuxerAndDrainQueues` set `_demuxerStopRequest = true`, joined for 4 s, then cleared `_demuxerThread`, reset `_demuxerStopRequest = false`, and freed/reused native demux state even if the demux thread was still blocked in `av_read_frame`.

Fixed via an installed FFmpeg `AVIOInterruptCB` on the shared demux `AVFormatContext` (FFmpeg.AutoGen 8 callback wrapper verified), returning abort when `_demuxerStopRequest` or `_disposed` is set. `AVERROR_EXIT` during stop is treated as clean. If a demux thread still fails to exit, seek/restart now throws and dispose deliberately does **not** free the native demux state under the running thread.

### FFmpeg demux thread exceptions can terminate the process
✅ **Confirmed.** `DemuxerThreadProc` (`MediaContainerSharedDemux.cs:587-616`) has no outer exception boundary. `FFmpegException.ThrowIfError(ret, …)` (`:607`) and `EnqueuePacketCopy`'s `OutOfMemoryException` (`:621`) escape directly off the background thread → process crash.

Fix direction: catch all exceptions, store a terminal fault state, wake consumers, stop further reads, expose an error event/state on the source/player.

### `VideoPlayer` releases a frame the output already owns after a throwing presentation event
✅ **Confirmed — three places — with a mechanism correction. FIXED 2026-06-01.** `IVideoOutput.Submit` is contractually an ownership transfer ("The output takes ownership of the frame and is responsible for calling `VideoFrame.Dispose`", `IVideoOutput.cs:42-44`). In `OnVideoTick` (`VideoPlayer.cs:429-451`), `_sink.Submit(toShow)` and `FramePresentationTimePresented?.Invoke(toShow.PresentationTime)` shared one `try`; if `Submit` succeeded (output now owns the frame) and a subscriber threw, the `catch` called `toShow.Dispose()`. The same pattern was in `PresentLatestQueuedFrame` (`:485-498`) and `TrySubmitHeldFrame` (`:545-561`).

**Mechanism correction:** `VideoFrame.Dispose()` is `Interlocked.Exchange(ref _release, null)?.Dispose()` — **idempotent** (`VideoFrame.cs:309`, documented at `:16-17`), so this is *not* a literal double-free. The real defect is a **premature release**: for an **async** output (the default `VideoOutputPump`, SDL render thread, NDI sender) that still holds the frame queued, the player's `Dispose` runs the backing `release` early and frees/returns the buffer out from under the queued frame. For a synchronous output the catch `Dispose` is a harmless no-op.

Fixed via: capture the PTS before `Submit`, gate the counters/notification on a `submitted` flag, and route `FramePresentationTimePresented` through a `RaisePresented` helper that wraps the invocation in its own try/catch (never disposes the frame, never lets a subscriber exception reach the clock-driver thread). Regression test `VideoPlayerTests.Submit_Success_With_Throwing_PresentationEvent_Does_Not_Release_Frame`.

### `VideoRouter.RemoveOutput` leaks owned outputs and pump threads
✅ **Confirmed.** `AddOutput` wraps outputs in a `VideoOutputPump` by default and records them as router-owned (`VideoRouter.cs:106-148`). `Dispose` disposes owned outputs (class remark, `:52-54`), but `RemoveOutput` (`:152-178`) only deletes the registry/route entries (`_outputs.Remove`, `_outputOwner.Remove`) — it never disposes the registration/pump/output. The asymmetry is the bug.

Impact: removed displays/NDI/file outputs keep worker threads and native resources alive; dynamic cue and multi-output apps leak over time.

Fix direction: `RemoveOutput` must honor the same ownership semantics as disposal — dispose the pump/output it owns after removing routes.

### `AudioRouter` consumes every registered source even when unrouted or muted
✅ **Confirmed.** `RunLoop` reads **every** entry in `snapshot.Sources` each chunk (`AudioRouter.cs:1153-1159`) before any route processing. There is no gate on "is this source targeted by an active, non-muted route." Registration alone (e.g. `AddOwnedSource` from `MediaPlayer`, `MediaPlayer.cs:776-782`) is enough to drain a clip.

Impact: a soundboard/cue app can lose audio before the user routes/starts it; adding an output after `Play` can find the media already consumed.

Fix direction: drive source pull from active voices/routes, not registration.

### `AudioRouter` background errors can crash the process
✅ **Confirmed.** `RunLoop` (`AudioRouter.cs:1213-1217`) catches, logs, and **rethrows** on the router thread (a background thread). Source/clock/mix errors take down the host.

Fix direction: transition the router/source/output to a faulted state, raise an event, isolate the offending node, let the host decide policy.

### `VideoPlayer.StopInternal` can leave an old decode thread alive, then allow a restart
⚠️ **Confirmed, severity correction.** `StopInternal` (`VideoPlayer.cs:248-295`) joins with a 12 s cap (`CooperativePlaybackJoin.JoinThread(toJoin, TimeSpan.FromSeconds(12), …)`) and proceeds even if the thread did not exit; a later `Play()` (`:176-208`) starts a fresh decode thread sharing the same `_source`/`_queue`. **Mitigation the review didn't credit:** for the shared-demux source the player requests `ICooperativeVideoReadInterrupt.RequestYieldBetweenReads()` (`:250-251`) so a cooperating `TryReadNextFrame` returns promptly; the unbounded-hang case requires a source that ignores the yield *and* blocks >12 s in native code. **Still valid:** nothing rejects a restart when the thread failed to stop, so concurrent decoder access remains possible in that case.

Fix direction: if the decode thread does not stop, keep the player in a blocked/stopping state and reject restart; prefer decoder interrupt support.

### FFmpeg video encoder reallocates `AVFrame` buffers every frame
🔧 **Partially addressed — still defective.** Since the review, `FfmpegVideoEncoder.Submit` gained `av_frame_make_writable(_frame)` (`FfmpegVideoEncoder.cs:90`) — **but its return value is ignored**, and `FfmpegAvFrameFill.CopyVideoFrame` still calls `av_frame_get_buffer(dst, 32)` on the reused `_frame` on *every* submit (`FfmpegAvFrameFill.cs:22`). Calling `av_frame_get_buffer` on an already-allocated frame each submit is at best redundant with `make_writable` and at worst reallocates/leaks the plane buffers per frame. The correct pattern is right next door: the **audio** encoder allocates the frame buffer once in `OpenCodecLocked` (`FfmpegAudioEncoder.cs:176`) and only calls `av_frame_make_writable` per frame (`:72`).

Fix direction: allocate the `AVFrame` buffer once for the fixed format/dimensions (as the audio encoder does), check the `av_frame_make_writable` return, and drop the per-frame `av_frame_get_buffer`.

### `AudioClipVoice` can get stuck forever when stopped at EOF
✅ **Confirmed.** `IsExhausted` is `_stopped || (cursor >= length && !_releasing && !Loop)` (`AudioClipVoice.cs:63`). If `Stop()` runs while `_cursorFrames >= SamplesPerChannel` (a non-looping voice in the window after EOF but before reaping, or a looping voice stopped exactly on the wrap boundary), `ReadInto` enters the loop, hits `if (_releasing) break;` (`:96-97`) without advancing the release ramp, and the post-loop `if (_releasing && _gain <= 0f) _stopped = true;` (`:113`) never fires because `_gain` is still positive. The voice then returns 0 samples forever while `IsExhausted` stays false — reaping never removes it.

Fix direction: when releasing with no source samples remaining, synthesize a silent release tail until the ramp completes, or mark the voice stopped immediately.

### Static/text/image video sources return frames without per-frame ownership
✅ **Confirmed, with a backing-dependent severity note.** `StaticFrameSource.TryReadNextFrame` (`StaticFrameSource.cs:84-97`), `TextLayerSource.TryReadNextFrame` (`TextLayerSource.cs:176-184`), and `ImageFileSource` emit `VideoFrame`s with `release: null` that alias a source-owned buffer. `TextLayerSource` is the worst: it re-rasterizes into the **same** `_pixelBuffer` on property change (`:186-221`) while previously emitted frames may still be queued, and `Dispose` returns that buffer to `ArrayPool<byte>.Shared` (`:229`) — a use-after-return if frames are still in flight.

Severity nuance: for `StaticFrameSource` backed by plain managed arrays with no `releaseBuffersOnDispose` hook, the GC keeps the arrays alive as long as queued frames reference them, so there is no use-after-free — the hazard is real specifically for pooled/native backings and for `TextLayerSource`'s in-place re-rasterization.

Fix direction: refcount/duplicate per emitted frame; mutating text should publish a new immutable buffer generation rather than rewriting the live buffer.

### SDL GL compositor can leak all GL resources when disposed from the wrong thread
✅ **Confirmed.** `SDL3GLVideoCompositor.Dispose` (`SDL3GLVideoCompositor.cs:165-170`) only calls `DisposeCore()` when invoked on the owner thread (or before init); from any other thread it sets `_disposeRequested` and returns, leaking the hidden SDL window/context and inner `GlVideoCompositor`. `Composite` (`:150-151`) does not observe `_disposeRequested`, so the deferred dispose only happens if the caller explicitly uses `DisposeOnOwnerThread()`.

Fix direction: own a compositor thread / enqueue disposal to the owner, or make `Dispose` reliably free resources regardless of caller thread.

## A/V Sync And Transport Problems

### Composition frame selection — scope correction
⚠️ **Confirmed for the declarative API only; the engine has since been redesigned.** Two layers exist now:

- **Low-level `VideoCompositorSource` (the engine): push / slot model.** Each `Slot` exposes an `IVideoOutput` (`SlotOutput`) that upstream players submit *into*; the slot keeps a latest-wins frame (`SubmitFromOutput`/`AcquireLatest`, `VideoCompositorSource.cs:277-328`). The downstream read just samples each slot's held frame and composites. Layer media advancement is therefore paced by **each upstream player's own clock**, not by the downstream read rate. There is also an opt-in `SlotKeepPolicy.MasterAligned` + `AcquireMasterAligned(masterPts, canvasPeriod)` (`:332-399`) that selects each slot's frame by PTS — i.e. the playhead-driven selection the original review asked for. The production cue path uses the slot model directly (`UI/HaPlay/Playback/CueCompositionRuntime.cs`).
- **High-level declarative `VideoCompositor`: still pull-on-read.** `VideoCompositor.TryReadNextFrame` (`VideoCompositor.cs:100-106`) iterates `_layers` and calls `LayerHandle.TryPullFrame` — which pulls exactly one frame from each layer's `IVideoSource` and pushes it into the slot — **on every downstream read**. A downstream `VideoPlayer` prebuffering its queue will advance every layer source at decode speed. This is the original finding, and it still holds here.

**Caveat the review missed and a gap it didn't:** the master-aligned policy is currently set/exercised **only in tests** (`VideoCompositorSourceTests.cs`); no production code sets `slot.KeepPolicy = MasterAligned` or calls the master-time overload, and the composite output PTS remains synthetic (`_nextPts += _ptsStep`, `VideoCompositorSource.cs:184-185`).

Fix direction: route the declarative `VideoCompositor` through the slot model with per-layer players (or feed slots on a timer), and wire `MasterAligned` + a real master time into the production path so multi-layer composition is frame-accurate rather than latest-wins.

### Audio/video startup order can make video late immediately
⚠️ **Confirmed, with existing mitigation.** `AvPlaybackCoordinator.Play` starts the audio router and clock before `video.Play()` (`AvPlaybackCoordinator.cs:40-53`), so the master clock can advance before the video player attaches its tick handler. **Mitigation present:** the coordinator runs `prefillBeforeHardware` and a `verifyPrebufferAfterPrefill` gate first (`:30-38`), priming the decode queue before the clock starts; the residual exposure is the few statements between `audioClock.Start()` (`:43`) and `video.Play()` (`:53`).

Fix direction: prime/start video decode and attach the tick handler inside the start barrier, before the audio/media clock starts ticking.

### Pause order lets audio continue while video shutdown waits
✅ **Confirmed.** `AvPlaybackCoordinator.Pause` (`:64-82`) calls `video.Pause(...)` in the `try` and only pauses `audioRouter`/`audioClock` in the `finally`. Because `video.Pause` can block up to 12 s joining a stuck decode thread, audio can keep playing for that whole window.

Fix direction: silence/stop audio immediately, then pause/drain video.

### Default seek path is not coordinated for shared demux
✅ **Confirmed and fixed 2026-06-02 for shared-demux playback.** `MediaContainerSession.Seek` now performs the session-layer fix rather than swapping to the old `SeekCoordinated` helper: it pauses, calls `Container.SeekPresentation(position)` once, seeks playback clock(s), and resumes only if the graph was already running. Explicit `SeekCoordinated` remains pause-and-stay-paused. `MediaPlayer.Seek` for file/container playback uses the fixed bundle session path.

### Router clocks can burst indefinitely after stalls
✅ **Confirmed.** `WallClockRouterClock.WaitForNextChunk` (`WallClockRouterClock.cs:39-44`) returns immediately whenever `_nextDeadline <= _stopwatch.Elapsed` and advances the deadline by one chunk each call; `PlaybackSlavedRouterClock.WaitForNextChunk` (`PlaybackSlavedRouterClock.cs:28-39`) does the same against `_master.ElapsedSinceStart`. After a stall both will fire back-to-back until the deadline catches up — no catch-up cap.

Fix direction: cap catch-up bursts and re-anchor deadlines after a bounded number of missed chunks.

### Fallback video PTS counters are not reset on seek
✅ **Confirmed in both decoders.** Shared demux derives no-PTS video time from `_vFramesEmitted / fps` (`MediaContainerSharedDemux.cs:1123`, incremented `:1133`) and `SeekPresentation` (`:647-718`) resets `_aEof/_aDrainedTail/_vEof/…` but **not** `_vFramesEmitted`. Standalone `VideoFileDecoder` has the same pattern: `_framesEmitted` (`VideoFileDecoder.cs:521`, used `:935`) is not reset in `Seek` (`:262-273`, which flushes codec buffers only).

Fix direction: reset the fallback emitted-frame counters on seek, or derive fallback PTS from seek target plus frames since seek.

### NDI ingest should use NDI timing, not fixed frame cadence
✅ **Confirmed and fixed 2026-06-02.** `NDIVideoReceiver` and the public combined `NDISource` now use NDI `Timecode`/`Timestamp` when available, while preserving the live `RebaseToLatest(playClock.CurrentPosition)` contract: the first post-rebase NDI time becomes the session origin and frame PTS is `rebaseBase + (frameNdiTime - origin)`. Untimed frames fall back to the existing synthetic cadence, corrected to the negotiated frame rate once format is known.

## Public API Simplification Recommendations

### Separate graph construction from transport start
✅ **Confirmed** (direct consequence of the unconditional source drain). `MediaPlayer` builds the router and `AddOwnedSource(media.Audio)` before any output is guaranteed (`MediaPlayer.cs:776-782`); once started the router drains that source regardless of routing. A cue/player API should make "registered but not started" non-consuming by construction.

### Make clips, voices, and cues first-class API concepts
✅ **Reasonable design recommendation** (unchanged). `AudioClipPlayer` is a good start; a `Soundboard`/`CueEngine` that owns clips, voices, routing, choke groups, and reaping, and whose `Fire(...)` returns a voice handle, would keep the low-level router as an implementation detail.

### Consolidate ownership rules
✅ **Confirmed structural inconsistency.** Ownership varies across builders, routers, wrappers, outputs, and companions (e.g. `MediaPlayerOpenBuilder` companion handling, `:80-88`; PortAudio defaults to caller-managed, below). A single owning `MediaSession`/`PlaybackSession` (with explicit `Borrow`/`DoNotDispose` escape hatches and `IAsyncDisposable` for native drains) would remove the ambiguity.

### Remove process-wide mutable defaults from normal user workflows
✅ **Confirmed.** `AudioRouter.DefaultAutoResample` is a mutable static (`AudioRouter.cs:75`, read at `:170`); `MediaFrameworkPlugins` exposes process-wide mutable factory slots (`MediaFrameworkPlugins.cs:18-70`); `VideoCompositor.RegisterAutoBackend` mutates a static backend list (`VideoCompositor.cs:49-55`, `116-141`). These interfere across tests/sessions.

Fix direction: keep process-wide registration as an escape hatch; add per-session `MediaFrameworkOptions`/registry.

### Tighten source/output format contracts
✅ **Confirmed for CPU frames.** `VideoFormat` is a `record struct` with no validation and no `Validate` (`VideoFormat.cs:7-11`). `VideoFrame.Validation` thoroughly validates **hardware** backings (plane-count stubs, stride mirroring, pixel format) but the CPU path (`case null`) only checks "≥1 plane" plus the trailing `planes.Length == strides.Length` and `stride > 0` — it does **not** verify plane count against the pixel format, stride ≥ row bytes, or plane length ≥ stride×height (`VideoFrame.Validation.cs`). A malformed CPU frame can travel deep into SDL/GL/FFmpeg/NDI before failing.

Fix direction: add `VideoFormat.Validate`; validate CPU plane count/stride/length at frame creation; consider exposing planes as read-only.

### Preserve seekability through wrappers
✅ **Confirmed.** `ResamplingAudioSource` implements only `IAudioSource, IDisposable` (`ResamplingAudioSource.cs:25`) — no `ISeekableSource`, `Position`, or `Duration` pass-through. `AudioRouter.AddSource(autoResample: true)` wrapping a seekable source therefore yields a non-seekable wrapper, silently dropping seek for any sample-rate-mismatched source.

Fix direction: wrappers should forward optional capabilities (seek, duration, position, clock metadata).

### Make URI/path APIs less surprising
✅ **Confirmed.** `MediaPlayerOpen.Uri(string)` builds with `UriKind.RelativeOrAbsolute` (`MediaPlayerOpenBuilder.cs:360`), but FFmpeg URI open needs an absolute URI; a relative string is accepted at the builder and fails later.

Fix direction: expose `OpenFile`, `OpenUri(Uri absoluteUri)`, `OpenStream` with early validation.

### Avoid public mutable internals
✅ **Confirmed.** `RouteGainSlot` is a public class with public mutable fields `Target`/`Current` (`RouteGainSlot.cs:7-16`), exposed through `AudioRouter.Routes`. External code can mutate gain state without locks and bypass the ramp invariants.

Fix direction: expose immutable route snapshots; keep the mutable gain slot internal.

## Allocation And Performance Findings

### Output pumps start one thread per output immediately
✅ **Confirmed** (and the likely cause of the suite-level OOM). `AudioRouter.OutputPump`'s constructor starts a dedicated `Thread` (`AudioRouter.cs:1502-1521`), and `AddOutput` creates the pump eagerly — even while the router is stopped.

Fix direction: lazy-start pumps when the router starts, share a bounded worker pool, or offer synchronous/polling outputs for test/headless cases.

### `VideoRouter` holds its global lock during conversion and output submission
⚠️ **Confirmed, with mitigation for slow inner outputs.** `VideoRouterInputOutput.Submit` (`VideoRouter.cs:746-755`) locks `_gate` and calls `SubmitLocked` (`:547-687`), which performs CPU dma-buf readback, `IVideoCpuFrameConverter.Convert`, and `VideoFrameCpuClone.DuplicateCpuBacking`, then submits to each output — all under the lock. **Mitigation the review didn't credit:** outputs are wrapped in `VideoOutputPump` by default, so the per-output `Output.Submit` under the lock is just a fast enqueue. **Still valid:** the CPU conversion/readback/clone for branch outputs runs under the lock, and a `synchronous: true` output's real `Submit` would block route changes.

Fix direction: snapshot active routes/outputs under the lock, then convert and submit outside it.

### `VideoOutputPump.Configure` can race queued old-format frames
✅ **Confirmed (now an acknowledged caveat).** `VideoOutputPump.Configure` forwards reconfiguration to the inner output without draining the queue (`VideoOutputPump.cs:105-124`); the code comment itself notes "callers doing an actual resize/pixel-format swap should pause + drain first." A live format change can therefore hand old-format frames to a newly reconfigured inner output.

Fix direction: drain/drop pending frames on reconfigure and version queues by format.

### `VideoOutputPump.Dispose` can dispose inner resources while the worker is still inside `Submit`
⚠️ **Confirmed (window reduced to 2 s).** `Dispose` cancels, joins with a **2 s** cap (reduced from the 30 s the review saw, per the code comment), then disposes the queue and the inner output when `_disposeInner` (`VideoOutputPump.cs:~252-283`). If the drainer is blocked inside the inner `Submit` longer than 2 s, the inner output is disposed underneath it.

Fix direction: don't dispose inner resources until the worker exits, or require outputs to support cancellation.

### Compositor hot path allocates every frame
✅ **Confirmed.** `VideoCompositorSource.TryReadNextFrame` allocates a `Slot[]` snapshot (`VideoCompositorSource.cs:165`) plus `List<CompositorLayer>`/`List<SlotFrameLease>` (`:167-181`) and a `SlotFrameLease` per slot per composite; the `Slots` getter also allocates (`:80`).

Fix direction: reuse arrays/pools or stable slot arrays with versioning (after the timing model is settled).

### FFmpeg planar conversion pins and allocates per frame
🔎 **Carried over** (consistent with the code; not re-reproduced line-by-line this pass). The swscale conversion paths allocate/pin per plane per frame around `VideoFileDecoder.cs:745-829` and the matching shared-demux region (`MediaContainerSharedDemux.cs:~1280-1363`).

Fix direction: `stackalloc`/`fixed` for the small fixed plane count, or persistent pooled pinned buffers.

### FFmpeg pass-through frames allocate managed memory-manager objects per plane
🔎 **Carried over.** Pass-through CPU video wraps each plane in a new `UnmanagedMemoryManager<byte>` per frame (shared demux `:~1188-1200` and `VideoFileDecoder`). Measure first; pool wrappers if it shows up.

### FFmpeg audio encoder shifts a `List<float>` for every encoded frame
✅ **Confirmed.** `FfmpegAudioEncoder.Submit` appends sample-by-sample to `_pending` then `_pending.RemoveRange(0, floatsPerFrame)` after each encoded frame (`FfmpegAudioEncoder.cs:55-66`) — O(n) shifting on long recordings/large submits.

Fix direction: ring buffer or read/write-offset pending buffer.

### CPU compositor assumes premultiplied BGRA but cannot enforce it
✅ **Confirmed and fixed 2026-06-02.** `VideoFrameMetadata` now carries `VideoAlphaMode`, exposed through `VideoFrame.AlphaMode`. `CpuVideoCompositor` keeps legacy behavior for `Unspecified` (premultiplied), normalizes explicit `Straight` alpha before `Source`/`SourceOver`, treats `Opaque` as full-alpha, and emits premultiplied output metadata. Skia sources mark premultiplied frames; FFmpeg marks alpha-bearing libav formats as straight and non-alpha formats opaque; NDI marks packed BGRA/RGBA alpha as straight.

### OpenGL compositor does not restore pack pixel-store state
✅ **Confirmed.** `GlVideoCompositor.Composite` saves/restores unpack alignment+row length (`GlVideoCompositor.cs:198-199`) but sets pack alignment/row length for `glReadPixels` (`:181-182`) without saving/restoring the prior pack state.

Fix direction: save and restore `GL_PACK_ALIGNMENT` and `GL_PACK_ROW_LENGTH`.

## FFmpeg Decode And Encode Issues

### Frame-mode audio reads can miss resampler tail or never exhaust
✅ **Confirmed and fixed 2026-06-02.** `AudioFileDecoder.TryReadNextFrame` and shared-demux `AudioTrack.TryReadNextFrame` now drain `swr` tail samples into pooled `AudioFrame`s after decoder EOF and set the drained-tail flag once empty. Frame-mode consumers no longer lose tail audio or loop forever waiting for `IsExhausted`.

### Shared-demux seek is not protected from concurrent audio reads
✅ **Confirmed (and notably asymmetric).** `SeekPresentation` flushes `_aCtx`, `swr_close`/`swr_init`, and resets `_aSamplesEmitted` (`MediaContainerSharedDemux.cs:683-701`) while holding only `_lifecycleLock` — **not** `_audioDecodeLock`, which `AudioTrack.ReadInto` does hold (`:1428`). It *does* take `_videoDecodeLock` for the video consume (`:715`), so the audio side is the unprotected one.

Fix direction: enforce coordinated seek internally (take `_audioDecodeLock` during the audio flush); don't rely on callers to stop all readers.

### FFmpeg audio encoder does not handle non-float sample formats correctly
✅ **Confirmed.** `PickSampleFormat` prefers FLTP but otherwise returns `codec->sample_fmts[0]` (`FfmpegAudioEncoder.cs:180-191`); `WriteFrameLocked` only de-interleaves for FLTP and otherwise `Buffer.MemoryCopy`s raw interleaved `float` bytes into `data[0]` (`:70-90`). A codec whose first/only format is `s16`, `s16p`, etc. gets garbage audio.

Fix direction: restrict to FLT/FLTP encoders or add swresample/sample-format conversion before encode.

## Composition And Effects Issues

### `LayerConfig.ScaleAnchor` is unused
✅ **Confirmed.** `LayerConfig.ScaleAnchor` exists and defaults to `LayerAnchor.Center` (`LayerConfig.cs`), but `LayerConfigResolver.ToTransform` never reads it; rotation is composed as `Translate ∘ Scale ∘ Rotate` with `LayerTransform2D.Rotate` rotating around the **source origin (0,0)**, not the layer center/anchor (`LayerConfigResolver.cs`, `LayerTransform2D.cs`). PiP/animated rotation will surprise users.

Fix direction: implement anchor-aware composition, or remove the property until supported.

### Layer transitions are not thread-safe
✅ **Confirmed.** `LayerHandle._config` and `_transitions` are mutated by `SetConfig`/`AddTransition`/`ClearTransitions` (`LayerHandle.cs:36-45`) with no synchronization, while `TryPullFrame` reads them from the compositor path (`:54-65`). A UI thread editing config can tear the struct read or throw "collection modified" against the iterating compositor.

Fix direction: swap immutable snapshots atomically, or lock mutation and reads.

### Layer pull leaks frames if configure throws
✅ **Confirmed.** `LayerHandle.TryPullFrame` calls `Slot.Output.Configure(frame.Format)` then `Submit(frame)` with no `try`/`catch` (`LayerHandle.cs:87-89`); if `Configure` throws (e.g. the slot rejects the pixel format), the converted/owned `frame` is never disposed.

Fix direction: wrap configure/submit and dispose the frame unless ownership has moved.

### `StaticFrameSource.FromFrame(copyBacking: false)` is a footgun
✅ **Confirmed (opt-in).** With `copyBacking: false`, `FromFrame` aliases the caller's planes and passes `release: null` (no ownership of the original) (`StaticFrameSource.cs:108-131`). If the caller disposes the original frame, the source emits invalid memory. The default is `copyBacking: true`, so this is opt-in misuse rather than a default trap.

Fix direction: remove the non-copying mode from the public API or require an explicit shared/refcounted backing.

## NDI Findings

### `NDIOutput.Video` lazy initialization is not thread-safe
❌ **Not valid in current code.** The `Video` getter delegates to `CreateVideoOutputLocked` (`NDIOutput.cs:78`), which takes `_gate` and double-checks `_videoOutput ??= new NDIVideoSender(...)` (`:163-170`). Creation is serialized and happens exactly once; the outer `??=` at the call site is at most a redundant same-reference write. No double-creation race exists. (Either this was fixed after the review or the original read predated the locked helper.)

### `NDIAudioOutput.EnsurePackedCapacity` can lose the original pointer on realloc failure
❌ **Not valid — premise is C `realloc`, not .NET.** `_packedBuffer = (byte*)NativeMemory.Realloc(_packedBuffer, …)` (`NDIAudioOutput.cs:136`). Unlike C `realloc`, **.NET's `NativeMemory.Realloc` throws `OutOfMemoryException` on failure rather than returning null**; the throw happens before the assignment, so `_packedBuffer` keeps its original, still-valid pointer and nothing is lost or leaked. The only effect on OOM is that `Submit` throws (surfaced through the pump's `RaiseOutputErrored`). No fix required for the stated concern.

### NDI receiver background threads need fault boundaries
✅ **Confirmed.** `NDIVideoReceiver.CaptureLoop` (`NDIVideoReceiver.cs:161-208`) wraps only the unpack/enqueue in `try`/`finally` (the `finally` just frees the frame) — the loop has no outer `catch`, and `_receiver.Capture(...)` (`:165`) is outside the `try` entirely. `NDIAudioReceiver.CaptureLoop` follows the same shape. Native/unpack errors crash the process.

Fix direction: store fault state, wake readers, expose an error event/property.

### NDI live sources expose format only after first frame
✅ **Confirmed.** `NDIVideoReceiver`/`NDIAudioReceiver` only know `Format` after `EnsureFormat` runs on the first captured frame (`NDIVideoReceiver.cs:210-222`), yet `AudioRouter.AddSource` needs a format up front.

Fix direction: provide async `Connect`/`Probe` helpers or a `TryGetFormat`/format-changed pattern.

## PortAudio Findings

### PortAudio wiring can leak outputs on partial failure
✅ **Confirmed.** `PortAudioPlaybackHost.TryWirePortAudioMainForRouter` creates a `PortAudioOutput`, then computes targets and calls `router.AddOutput`/`router.Connect`; the surrounding `catch` returns null without disposing the created output (`PortAudioPlaybackHost.cs:~120-149`). A failure between construction and successful registration leaks native PortAudio resources.

Fix direction: transfer ownership only after successful registration; dispose on failure.

### PortAudio companion ownership is awkward for the easy path
✅ **Confirmed structural.** The builder extensions create a PortAudio host as a companion but default to caller-managed disposal (`MediaPlayerOpenBuilderPortAudioExtensions.cs`), so the simple "open with audio" path is easy to leak.

Fix direction: the high-level session/player should own the companion host by default.

### PortAudio prefill allocates every call
✅ **Confirmed (minor).** `PortAudioOutput.PrefillFrom` allocates `new float[bufFloats]` per call (`PortAudioOutput.cs:~372`). Not a hot path (startup only), but avoidable.

Fix direction: `ArrayPool<float>` or a reused scratch buffer.

## SDL/OpenGL Output Findings

### SDL output disposal can dispose synchronization handles while the render thread is still alive
🔎 **Carried over** (pattern consistent; not re-reproduced this pass). `SDL3VideoOutput.Dispose` joins ~2 s then disposes `_wakeup`/`_ready`; `SDL3GLVideoOutput` uses the same shape with a longer join. If the render thread is still blocked in native present, it can touch disposed handles.

Fix direction: don't dispose wait handles/inner resources until the render thread exits, or mark the output failed-to-stop.

### `SDL3VideoOutput` cannot reconfigure, while GL output can
✅ **Confirmed.** `SDL3VideoOutput.Configure` throws if already configured ("create a new output to switch format", `SDL3VideoOutput.cs:122-128`), whereas `SDL3GLVideoOutput.Configure` supports same-format idempotence and a full render-thread rebuild on reconfigure (`SDL3GLVideoOutput.cs:259-320`). Two SDL backends, two contracts.

Fix direction: align the contracts (either single-format lifetime everywhere, or consistent explicit reconfigure).

## Smaller But Important Contract Issues

### `AudioRouter.RunLoop` does not validate source read counts
✅ **Confirmed.** The loop trusts `ReadInto` (`AudioRouter.cs:1155-1157`): a negative return makes `src.Scratch.AsSpan(read)` throw (→ the background rethrow → crash), and an over-count silently misrepresents how much was produced.

Fix direction: validate `0 <= read <= scratch.Length` and fault the source on violation.

### `AudioRouter.WaitForIdle` can wait even after queues were abandoned
✅ **Confirmed.** `WaitForIdle` loops while `_processed < _enqueued` (`AudioRouter.cs:1593`), but `AbandonQueue` (`:1577-1584`) only increments `_dropped` via `RecordDrop` — never `_processed`. Abandoned-but-enqueued chunks keep `_enqueued > _processed`, so idle waits run the full timeout despite an empty queue.

Fix direction: count dropped/abandoned chunks as completed for idle-drain purposes.

### Adaptive-rate output wrapper can become a fake primary clock
✅ **Confirmed.** `AdaptiveRateAudioOutput` implements `IClockedOutput` unconditionally (`AdaptiveRateAudioOutput.cs:38`); `WaitForCapacity` returns `!token.IsCancellationRequested` when the inner output is **not** clocked (`:141-144`) — i.e. always "capacity available." If the router selects this wrapper as primary clock while wrapping a non-clocked output, it slaves to a clock that never blocks, defeating pacing.

Fix direction: only expose `IClockedOutput` when the inner output is actually clocked, or only wrap non-primary outputs after a real primary exists.

### Adaptive-rate wrappers are not disposed by the router
🔎 **Carried over** (structural; not re-reproduced this pass). The router creates adaptive wrappers (`AudioRouter.Playback.cs`) but output removal/disposal doesn't clearly own/dispose the wrapper, so its monitor subscriptions (`AdaptiveRateAudioOutput.Dispose`) can leak.

Fix direction: store wrapper ownership in the output entry and dispose on remove/router-dispose.

## Corrections And Net-New Observations From This Pass

- **NDI `Video` getter** and **`NDIAudioOutput` realloc** findings are withdrawn (see above): the first is already correctly double-checked under a lock; the second assumed C `realloc` null-return semantics that don't apply to .NET's throwing `NativeMemory.Realloc`.
- **Video encoder finding is now a partial-fix, not a missing-fix:** `av_frame_make_writable` was added but its result is unchecked and the per-frame `av_frame_get_buffer` remains. The **audio** encoder is the reference for the correct allocate-once pattern.
- **The `VideoPlayer` post-submit double-dispose appears in three methods**, not one (`OnVideoTick`, `PresentLatestQueuedFrame`, `TrySubmitHeldFrame`) — fix all three.
- **Composition is two-tier now.** The slot engine already supports the playhead-driven design the review recommended (`SlotKeepPolicy.MasterAligned`), but it is wired only in tests; the declarative `VideoCompositor` convenience API still pulls one frame per layer per read. Prioritize wiring master-aligned selection into production over re-architecting from scratch.
- **`SeekPresentation` is asymmetric about locking** — it protects the video decode with `_videoDecodeLock` but flushes audio (`_aCtx`/`_swr`/counters) with no `_audioDecodeLock`. That asymmetry is a concrete, fixable instance of the "coordinate seek internally" recommendation.
- **Late verification correction (2026-06-02): combined `NDISource` is a third NDI receive path.** The original NDI fault finding named the standalone `NDIVideoReceiver`/`NDIAudioReceiver`; HaPlay actually opens the public combined `NDISource`, whose capture loop had the same missing outer fault boundary. That path has now been patched to expose terminal `Fault`/`Faulted`, wake blocked video reads, and report audio/video adapters exhausted on fault.
- **Late verification correction (2026-06-02): first-class cue firing needed rollback.** The new `Soundboard`/`AudioClipPlayer.TryFire` path added after the original review could add an `AudioRouter` source before failing route creation for a stale/removed output. That is now guarded by output validation and source/choke rollback.

## Suggested Refactoring Roadmap

1. **Frame contracts first.** Add `VideoFormat`/CPU-`VideoFrame` validation; make frame backing immutable/refcounted; fix static/text/image source frame lifetimes.
2. **Safe transport and faults.** Done for the reviewed paths: AudioRouter/demux/NDI fault boundaries, VideoPlayer restart guard, and FFmpeg demux interrupt/stop handling are implemented.
3. **Source activation model.** Done for the router/cue scope: unrouted sources are no longer drained, seekability is preserved through resampling wrappers, and `Soundboard`/`CueVoice` provide first-class cue handles.
4. **A/V sync and seek.** Mostly done: shared-demux default seek is coordinated, seek flushes hold `_audioDecodeLock`, fallback PTS counters reset, NDI video timing uses NDI time when available, and router clock catch-up is capped. Residual: tighter start-barrier work could still reduce the tiny gap between clock start and video tick attachment.
5. **Composition timing.** Done for the declarative compositor: clock-driven layer selection is wired. Remaining work is mainly product-level policy around which production graphs opt into master-aligned slot behavior.
6. **Output lifecycle and threading.** Mostly done: removed outputs are disposed, pumps lazy-start, and output reconfigure/drain/dispose races were hardened. Remaining open item: conversion/readback/clone still happens under `VideoRouter._gate`.
7. **High-level API cleanup.** Mostly done: `MediaSession`, per-session options, stricter URI handling, and companion ownership are implemented. Remaining cleanup is incremental API polish, not a known correctness blocker.

## Priority Backlog

P0:

- No P0 findings remain open after the 2026-06-02 implementation follow-up.

P1:

- No P1 findings remain open after the 2026-06-02 implementation follow-up.

P2:

- Move `VideoRouter` conversion/readback/clone and synchronous output submission outside the global router lock. Correct implementation needs converter lifetime leases or deferred converter disposal so route reconfiguration cannot dispose a converter while an in-flight submit snapshot still uses it.
- Profile planar swscale conversion before pooling the escaping per-frame descriptor arrays. The remaining allocations are mostly frame-owned arrays that live until `VideoFrame.Dispose`; pooling them is feasible but should be driven by measured hot-path pressure.
