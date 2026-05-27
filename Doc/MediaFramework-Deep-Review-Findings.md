# Media Framework Deep Review Findings

Date: 2026-05-27

Scope: reviewed the media framework code outside `Doc/**` and `UI/**`, including core audio/video, playback, FFmpeg decode/encode, effects/composition, SDL/OpenGL, PortAudio, NDI, MIDI and OSC support. Existing docs in `Doc/` were not used as source material except as the destination for this report.

## Executive Summary

The framework has the right major pieces for the stated goal: pull-based sources, push-based outputs, routers, hardware-aware video formats, high-level playback, and first-cut composition. The main risk is not missing features. The main risk is that ownership, lifetime, and timing contracts are inconsistent across modules. That makes simple applications easy to get running, but makes professional cue, soundboard, multi-output, and composition workflows fragile under load.

The most urgent work is:

1. Fix native lifetime and background-thread failure paths in FFmpeg demuxing and video playback.
2. Stop audio sources from being consumed merely because they were registered.
3. Redesign composition timing so compositor prebuffering does not advance layer media faster than wall-clock/playhead time.
4. Make frame ownership/refcounting explicit, especially for static/text/image sources and output handoff.
5. Replace per-output/per-test thread fan-out with a bounded scheduler model or lazy pumps.
6. Tighten `VideoFrame`/`VideoFormat` validation so bad frames fail at the boundary, not inside output/native code.

## Verification Performed

Commands run:

- `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --no-restore`
  - First sandboxed run failed because MSBuild could not create local named pipes.
  - Escalated run built and executed. Result: 392 passed, 1 failed.
  - Failure: `AudioRouterControlTests.ReconfigureSampleRateWhileRunning_WhenStopped_Throws` hit `OutOfMemoryException` at `AudioRouter.OutputPump..ctor` while starting a thread (`AudioRouter.cs:1520`). Running that single test by itself passed. This strongly suggests suite-level thread pressure from the router's per-output thread model, not a deterministic assertion failure.
- `dotnet test MediaFramework/Test/S.Media.Playback.Tests/S.Media.Playback.Tests.csproj --no-restore`
  - Passed: 25.
- `dotnet test MediaFramework/Test/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj --no-restore`
  - Passed: 159.
- `dotnet test MediaFramework/Test/S.Media.PortAudio.Tests/S.Media.PortAudio.Tests.csproj --no-restore`
  - Passed: 22.
- `dotnet test MediaFramework/Test/S.Media.NDI.Tests/S.Media.NDI.Tests.csproj --no-restore`
  - Passed: 50.
- `dotnet test MediaFramework/Test/S.Media.FFmpeg.Encode.Tests/S.Media.FFmpeg.Encode.Tests.csproj --no-restore`
  - Passed: 1. This is too little coverage for the encoder issues noted below.
- `dotnet test MediaFramework/Test/S.Media.SkiaSharp.Tests/S.Media.SkiaSharp.Tests.csproj --no-restore`
  - Passed: 8.
- `dotnet test MediaFramework/Test/S.Media.OpenGL.Tests/S.Media.OpenGL.Tests.csproj --no-restore`
  - Passed: 105.
- `dotnet test MediaFramework/Test/PMLib.Tests/PMLib.Tests.csproj --no-restore`
  - Passed: 13.
- `dotnet test MediaFramework/Test/OSCLib.Tests/OSCLib.Tests.csproj --no-restore`
  - Passed: 20.

## Critical Correctness And Stability Issues

### FFmpeg shared demuxer can race native free/seek while its demux thread is still alive

`MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs:537-553` stops the demux thread with a 4 second join, then clears `_demuxerThread` and `_demuxerStopRequest` even if `av_read_frame` is still blocked. Later seek/dispose paths can free or reuse `_fmt` and `_demuxPkt` while the old native thread may still be using them.

Impact: native use-after-free, corrupted packets, crashes during network/slow I/O, and hard-to-reproduce failures during seek or dispose.

Fix direction: install an FFmpeg interrupt callback, make stop cancellation observable by `av_read_frame`, and never clear the stop flag or free/restart demux state until the old demux thread has actually exited.

### FFmpeg demux thread exceptions can terminate the process

`MediaContainerSharedDemux.DemuxerThreadProc` has no outer exception boundary (`MediaContainerSharedDemux.cs:587-617`). `FFmpegException.ThrowIfError`, allocation failure, or unexpected native state can escape a background thread.

Impact: process-level crash instead of a source fault surfaced to the application.

Fix direction: catch all exceptions, store a terminal fault state, wake consumers, stop further reads, and expose an error event/state on the source/player.

### `VideoPlayer` can double-dispose a frame after successful output handoff

In `VideoPlayer`, `_sink.Submit(toShow)` and `FramePresentationTimePresented?.Invoke(...)` are inside the same `try` block (`VideoPlayer.cs:429-445`, `485-496`, `545-559`). If the output accepts the frame and the event handler throws, the catch block treats the whole block as a submit failure and disposes `toShow`. At that point the output already owns the frame.

Impact: double release of pooled/native frame resources, intermittent corruption, and crashes when event subscribers fail.

Fix direction: separate output submission from event notification. After a successful `Submit`, ownership has moved and the player must not dispose the frame.

### `VideoRouter.RemoveOutput` leaks owned outputs and pump threads

`VideoRouter.AddOutput` wraps outputs in `VideoOutputPump` by default and marks them as owned (`VideoRouter.cs:106-145`). `RemoveOutput` removes routes/registrations but never disposes the registration/output (`VideoRouter.cs:152-178`).

Impact: removed displays/NDI/file outputs can keep worker threads and native resources alive. Dynamic cue and multi-output applications will leak over time.

Fix direction: `RemoveOutput` must honor the same ownership semantics as router disposal. If the router owns the output or wrapper, dispose it after removing routes.

### `AudioRouter` consumes every registered source even when unrouted or muted

The run loop reads every source each chunk (`AudioRouter.cs:1145-1157`) before route processing. Registering a source is enough to drain it, even if there are no routes or if the route is muted.

Impact: a soundboard/cue application can load or add a clip and lose audio before the user routes/starts it. Adding an audio output after `Play` may find the media already consumed.

Fix direction: source pull should be driven by active voices/routes, not registration. A source should be consumed only when at least one active route needs it, or when the API explicitly starts that source.

### `AudioRouter` background errors can crash the process

`AudioRouter.RunLoop` catches unhandled exceptions and rethrows on the router thread (`AudioRouter.cs:1213-1217`). Source read errors, clock errors, or mix errors should not terminate the host process.

Impact: one bad source/output can take down the whole app.

Fix direction: transition router/source/output to a faulted state, raise an event, stop or isolate the offending node, and let the host decide policy.

### `VideoPlayer.StopInternal` can leave an old decode thread alive, then allow a restart

`StopInternal` joins the decode thread with a 12 second cap and proceeds even if it did not exit (`VideoPlayer.cs:278-294`). A later `Play()` can create another decode thread sharing the same source, queue, and semaphore.

Impact: concurrent reads from a non-thread-safe decoder, queue corruption, and native decoder races.

Fix direction: if the decode thread does not stop, keep the player in a blocked/stopping state and reject restart. Prefer decoder interrupt support so long native reads can be cancelled.

### FFmpeg video encoder reallocates `AVFrame` buffers incorrectly

`FfmpegAvFrameFill.CopyVideoFrame` calls `av_frame_get_buffer(dst, 32)` on every submitted frame (`FfmpegAvFrameFill.cs:18-23`). The encoder reuses the same `_frame` (`FfmpegVideoEncoder.cs:88-93`) without `av_frame_unref` or one-time frame buffer allocation.

Impact: repeated native allocation, leaks, or FFmpeg errors depending on frame state.

Fix direction: allocate/configure the `AVFrame` once for fixed format/dimensions, call `av_frame_make_writable` and check its return value, or explicitly `av_frame_unref` before reallocation.

### `AudioClipVoice` can get stuck forever when stopped at EOF

`AudioClipVoice.IsExhausted` returns false while `_releasing` is true (`AudioClipVoice.cs:63`). If `Stop()` is called when `_cursorFrames >= SamplesPerChannel`, `ReadInto` breaks without advancing the release ramp (`AudioClipVoice.cs:88-99`), `_gain` never reaches zero, and `_stopped` never becomes true.

Impact: soundboard/choke-group voices can remain registered forever while returning zero samples. Reaping will not remove them.

Fix direction: if releasing and no source samples remain, either synthesize a release tail from silence until the ramp completes or immediately mark the voice stopped.

### Static/text/image video sources return frames without per-frame ownership

`StaticFrameSource`, `TextLayerSource`, and `ImageFileSource` emit `VideoFrame` instances with `release: null` that alias a buffer owned by the source (`StaticFrameSource.cs:92-95`, `TextLayerSource.cs:176-182`, `ImageFileSource.cs:125-131`). Disposal returns or invalidates the backing buffer even if frames are still queued in a `VideoPlayer`, `VideoRouter`, or output.

`TextLayerSource` is worse because property changes re-rasterize into the same buffer while previously emitted frames may still be in use (`TextLayerSource.cs:167-182`, `186-220`).

Impact: use-after-return-to-pool, visual tearing, queued frames changing underneath outputs, and difficult bugs when a source is disposed before all downstream frames drain.

Fix direction: add refcounted shared backing for static frames, or duplicate/refcount on each emitted frame. Mutating text should publish a new immutable buffer generation rather than modifying the live buffer in place.

### SDL GL compositor can leak all GL resources when disposed from the wrong thread

`SDL3GLVideoCompositor.Dispose` only releases resources when called before initialization or from the owner thread (`SDL3GLVideoCompositor.cs:165-170`). From any other thread it sets `_disposeRequested` and returns, leaving the hidden SDL window/context and `GlVideoCompositor` resources alive.

Impact: normal `IDisposable` expectations are violated. A high-level `VideoCompositor` can leak GPU resources depending on which thread disposes it.

Fix direction: own a compositor thread, enqueue disposal to the owner, or make `IVideoCompositor.Dispose` reliably dispose resources regardless of caller thread.

## A/V Sync And Transport Problems

### Composition currently advances layer media at decode/prebuffer speed, not playhead speed

`VideoCompositor.TryReadNextFrame` pulls one frame from every layer source on every downstream read (`VideoCompositor.cs:100-105`). A `VideoPlayer` decode loop can prebuffer multiple compositor frames quickly, so every layer source advances immediately to fill the queue instead of advancing according to its own PTS and the master playhead. The compositor then emits synthetic `_nextPts` values (`VideoCompositorSource.cs:184-187`) unrelated to the source frame PTS.

Impact: a 24 fps clip in a 60 fps canvas can be consumed too fast, startup prebuffer can skip ahead, and layer timing becomes dependent on queue capacity rather than media time. This is a major blocker for OBS-like and QLab-like composition.

Fix direction: make composition playhead-driven. Each layer should maintain its own decoded queue keyed by PTS, and the compositor should choose frames for a requested canvas time without pulling arbitrary next frames from every source. Static/image/text layers can be time-independent.

### Audio/video startup order can make video late immediately

`AvPlaybackCoordinator.Play` starts the audio router and the clock before `video.Play()` (`AvPlaybackCoordinator.cs:40-53`). The master clock can advance before the video decode queue/tick handler is ready.

Impact: first video frames may be late and dropped at playback start.

Fix direction: prime/start video decode first, then start the hardware/audio clock and media clock in a coordinated start barrier.

### Pause order lets audio continue while video shutdown waits

`AvPlaybackCoordinator.Pause` pauses video before audio (`AvPlaybackCoordinator.cs:64-80`). If video pause waits on a blocked native decode thread, audio can continue for seconds.

Impact: user-visible pause latency and A/V divergence.

Fix direction: stop or silence audio immediately, then pause/drain video.

### Default seek path is not coordinated for shared demux

The default `Seek` path seeks audio first and video second (`AvPlaybackCoordinator.cs:92-114`). With a shared demuxer, this can seek/flush while the other track is still active. `SeekCoordinated` is safer, but not the default public path.

Impact: seek races and duplicate expensive seeks in common high-level API usage.

Fix direction: make coordinated seek the default for `MediaPlayer.Seek`. For shared demux, call `decoder.SeekPresentation(position)` once, then reset both track positions and clocks.

### Router clocks can burst indefinitely after stalls

`WallClockRouterClock.WaitForNextChunk` and `PlaybackSlavedRouterClock.WaitForNextChunk` return immediately until their internal deadlines catch up (`WallClockRouterClock.cs:37-44`, `PlaybackSlavedRouterClock.cs:27-39`). `MediaClock` has burst caps; router clocks do not.

Impact: after a stall, audio routing can run many chunks as fast as possible, increasing CPU load and latency instead of re-anchoring.

Fix direction: cap catch-up bursts and re-anchor deadlines after a bounded number of missed chunks.

### Fallback video PTS counters are not reset on seek

For no-PTS streams, shared demux computes video PTS from `_vFramesEmitted / fps` (`MediaContainerSharedDemux.cs:1129-1143`) but `SeekPresentation` does not reset `_vFramesEmitted` (`MediaContainerSharedDemux.cs:647-714`). Standalone `VideoFileDecoder` has the same pattern with `_framesEmitted` (`VideoFileDecoder.cs:927-936`, seek at `VideoFileDecoder.cs:262-279`).

Impact: after seek, no-PTS streams can resume with stale future timestamps, causing bad A/V sync and frame dropping.

Fix direction: reset fallback emitted-frame counters on seek, or derive fallback PTS from seek target plus frames since seek.

### NDI ingest should use NDI timing, not fixed frame cadence

`NDIVideoReceiver` synthesizes PTS from a fixed frame-rate step (`NDIVideoReceiver.cs:176-180`, `216-220`) rather than using NDI timestamps/timecode or an ingest clock.

Impact: jitter, VFR, and dropped NDI frames can drift relative to audio and other sources.

Fix direction: preserve NDI-provided timing when available. For untimed streams, use a monotonic ingest clock with jitter smoothing.

## Public API Simplification Recommendations

### Separate graph construction from transport start

Today `MediaPlayer` can start an audio router that consumes sources even with no output attached (`MediaPlayer.cs:776-782`, plus the router behavior above). A professional cue/player API should make this impossible by construction.

Recommended shape:

- Build a graph/cue with sources, routes, outputs, composition, and clocks.
- Validate/prepare/prime it.
- Start it.
- Let outputs be added dynamically only with explicit catch-up or sync policy.

### Make clips, voices, and cues first-class API concepts

`AudioClipPlayer` is a good start for soundboards, but the general framework still exposes low-level router/source details for common tasks. End users should not need to reason about `AddSource`, `Route`, choke groups, reaping, resampling wrappers, and source disposal to fire a pad.

Recommended shape:

- `Soundboard` or `CueEngine` owns clips, voices, output routing, choke groups, and reaping.
- `Fire(...)` returns a voice handle with `Stop`, `FadeTo`, `Seek`, `SetGain`, and completion/fault events.
- The router remains the engine implementation, not the default public workflow.

### Consolidate ownership rules

Ownership varies between builders, routers, wrappers, outputs, frames, and companion objects. For example, the PortAudio builder creates a host but `MediaPlayer.Dispose` does not clearly own it; `TryBuildWithCompanions` disposes the player on later failure but not necessarily already-created companions (`MediaPlayerOpenBuilder.cs:80-88`). `WithPortAudio` defaults to caller-managed ownership (`MediaPlayerOpenBuilderPortAudioExtensions.cs:12-18`, `47-82`).

Recommended shape:

- Use one `MediaSession`/`PlaybackSession` object that owns the player, outputs, companion hosts, and plugin resources by default.
- Offer explicit `Borrow(...)`/`DoNotDispose(...)` only for advanced integrations.
- Use `IAsyncDisposable` where native drains or async teardown may block.

### Remove process-wide mutable defaults from normal user workflows

Several behaviors are controlled by process-wide state:

- `AudioRouter.DefaultAutoResample` (`AudioRouter.cs:75`).
- `MediaFrameworkPlugins` global factory slots (`MediaFrameworkPlugins.cs:18-74`).
- `VideoCompositor.RegisterAutoBackend` global backend list (`VideoCompositor.cs:49-55`, `116-140`).

Impact: test interference, hard-to-reason plugin ordering, and behavior changing globally for all sessions.

Recommended shape: keep process-wide registration as an escape hatch, but introduce per-session `MediaFrameworkOptions` or a service-provider-like backend registry.

### Tighten source/output format contracts

`VideoFormat` is a public record struct with no validation (`VideoFormat.cs:7-11`). `VideoFrame` stores caller-provided `Planes` and `Strides` arrays directly and exposes them as mutable arrays (`VideoFrame.cs:68-76`, `84-103`). `VideoFrame.Validation` does not verify pixel-format plane count, row byte size, or plane length (`VideoFrame.Validation.cs:13-68`).

Impact: a bad frame can travel deep into SDL, OpenGL, FFmpeg, NDI, or compositing before failing.

Recommended shape:

- Add `VideoFormat.Validate`.
- Make `VideoFrame` arrays immutable from the public API, or expose `ReadOnlyMemory<ReadOnlyMemory<byte>>`/spans backed by private arrays.
- Validate plane count, stride, and buffer length at frame creation.
- Keep a trusted internal fast path only if profiling proves the validation cost matters.

### Preserve seekability through wrappers

`ResamplingAudioSource` does not implement `ISeekableSource` (`ResamplingAudioSource.cs:25-115`). When `AudioRouter.AddSource(autoResample: true)` wraps a seekable source, `AudioRouter.SeekSource` later sees a non-seekable wrapper.

Impact: a sample-rate mismatch silently removes seek support.

Recommended shape: wrappers should pass through optional capabilities such as seek, duration, position, and clock metadata.

### Make URI/path APIs less surprising

`MediaPlayerOpen.Uri(string)` accepts `UriKind.RelativeOrAbsolute` (`MediaPlayerOpenBuilder.cs:353-361`), but FFmpeg URI open requires absolute URIs. Paths should be paths; URIs should be absolute URIs.

Recommended shape: expose `OpenFile`, `OpenUri(Uri absoluteUri)`, and `OpenStream` with early validation.

### Avoid public mutable internals

`RouteGainSlot` is a public type with mutable fields (`RouteGainSlot.cs:7-16`). `AudioRouter.Routes` exposes route records that include the mutable slot (`AudioRouter.cs:604-619`, `1413-1419`).

Impact: external code can mutate router internals without locks and bypass gain-ramp invariants.

Recommended shape: expose immutable route snapshots and keep mutable gain slots internal.

## Allocation And Performance Findings

### Output pumps start one thread per output immediately

`AudioRouter.AddOutput` creates an `OutputPump`, whose constructor starts a dedicated thread (`AudioRouter.cs:1502-1521`). This happens even while the router is stopped. The full `S.Media.Core.Tests` run failed once with `OutOfMemoryException` from `Thread.StartCore` at this exact path when many tests ran together.

Impact: thread pressure for many dynamic outputs, tests, cue systems, and temporary routes.

Fix direction: lazy-start pumps when the router starts, share a bounded worker pool, or provide synchronous/polling outputs for test/headless cases.

### `VideoRouter` holds its global lock during conversion and output submission

`VideoRouterInputOutput.Submit` locks the router and calls `SubmitLocked` (`VideoRouter.cs:746-754`). `SubmitLocked` can perform CPU conversion/readback and submit to branch outputs while still under that lock (`VideoRouter.cs:547-687`).

Impact: slow outputs block route changes and other submissions. Output callbacks that touch the router risk deadlock.

Fix direction: snapshot active routes/outputs under lock, then perform conversion and submit outside the router lock.

### `VideoOutputPump.Configure` can race queued old-format frames

`VideoOutputPump.Configure` forwards reconfiguration without draining queued frames (`VideoOutputPump.cs:105-124`). If a format changes while old frames are queued, the inner output may receive old-format frames after new `Configure`.

Impact: dynamic output changes can produce format mismatch exceptions or corrupt display/encoder state.

Fix direction: drain/drop pending frames on reconfigure and version queues by format.

### `VideoOutputPump.Dispose` can dispose inner resources while the worker is still inside `Submit`

`VideoOutputPump.Dispose` cancels and joins for 2 seconds, then disposes pending queues and possibly the inner output (`VideoOutputPump.cs:252-280`). If the worker is blocked inside an inner `Submit`, the inner output can be disposed underneath it.

Impact: use-after-dispose in slow/blocking outputs.

Fix direction: track blocked pump state, do not dispose inner resources until the worker exits, or require outputs to support cancellation.

### Compositor hot path allocates every frame

`VideoCompositorSource.TryReadNextFrame` allocates a slot snapshot array and `List<CompositorLayer>`/`List<SlotFrameLease>` on every composite (`VideoCompositorSource.cs:162-187`). `Slots` also allocates on every access (`VideoCompositorSource.cs:74-81`).

Impact: avoidable GC pressure in exactly the path that should be stable for live composition.

Fix direction: use reusable arrays/pools or stable slot arrays with versioning. This matters after fixing the larger timing model.

### FFmpeg planar conversion pins and allocates per frame

The FFmpeg video conversion paths allocate `List<GCHandle>` and pin per plane per frame (`VideoFileDecoder.cs:745-829`; shared demux has the same pattern around `MediaContainerSharedDemux.cs:1280-1363`).

Impact: GC handle churn and potential fragmentation under high frame rates.

Fix direction: use stackalloc for the small fixed plane count, fixed blocks where possible, or persistent pooled pinned buffers.

### FFmpeg pass-through frames allocate managed memory-manager objects per plane

Pass-through CPU video wraps each plane in a new `UnmanagedMemoryManager<byte>` per frame (`MediaContainerSharedDemux.cs:1188-1200`, with the same pattern in `VideoFileDecoder`).

Impact: less severe than pixel-copy costs, but still hot-path allocation.

Fix direction: measure first. If meaningful, pool wrappers or introduce a frame/backing type that can represent native AVFrame plane lifetimes without per-plane manager allocation.

### FFmpeg audio encoder shifts a `List<float>` for every encoded frame

`FfmpegAudioEncoder.Submit` appends all samples to `_pending`, encodes one frame, then calls `_pending.RemoveRange(0, floatsPerFrame)` (`FfmpegAudioEncoder.cs:55-66`).

Impact: O(n) shifting on long recordings or large submit chunks.

Fix direction: use a ring buffer or a compact pending buffer with read/write offsets.

### CPU compositor assumes premultiplied BGRA but cannot enforce it

`CpuVideoCompositor` treats BGRA32 frames as premultiplied (`CpuVideoCompositor.cs:16-20`, blend code at `176-204`). FFmpeg BGRA frames are usually alpha 255, but external BGRA overlays may be straight-alpha.

Impact: incorrect blending for straight-alpha user frames.

Fix direction: encode alpha mode in `VideoFrameMetadata` or pixel format, or normalize at source boundaries.

### OpenGL compositor does not restore pack pixel-store state

`GlVideoCompositor.Composite` saves/restores unpack alignment/row length (`GlVideoCompositor.cs:149-150`, `197-199`) but sets pack alignment/row length for `glReadPixels` (`GlVideoCompositor.cs:180-184`) without restoring previous pack state.

Impact: embedding the compositor in another GL renderer can corrupt later readbacks/downloads.

Fix direction: save and restore `GL_PACK_ALIGNMENT` and `GL_PACK_ROW_LENGTH`.

## FFmpeg Decode And Encode Issues

### Frame-mode audio reads can miss resampler tail or never exhaust

`AudioTrack.TryReadNextFrame` and `AudioFileDecoder.TryReadNextFrame` return false at packet EOF without draining swresample tail, and `IsExhausted` can remain false because the tail-drained flag is only set in `ReadInto` (`MediaContainerSharedDemux.cs:1475-1494`, `AudioFileDecoder.cs:65-70`, `156-167`).

Impact: frame-mode consumers can miss tail audio or loop waiting for exhaustion.

Fix direction: share the same tail-drain path between `ReadInto` and `TryReadNextFrame`.

### Shared-demux seek is not protected from concurrent audio reads

`SeekPresentation` flushes `_aCtx`, `_swr`, and audio counters without holding `_audioDecodeLock` (`MediaContainerSharedDemux.cs:647-714`). The public contract may say no concurrent reads, but the framework itself exposes paths where video/audio and seek can overlap.

Impact: audio decode state can be reset while another thread is reading.

Fix direction: enforce coordinated seek internally. Public players should not rely on callers to stop all readers correctly.

### FFmpeg audio encoder does not handle non-float sample formats correctly

`PickSampleFormat` prefers FLTP but otherwise returns the first codec sample format (`FfmpegAudioEncoder.cs:180-190`). `WriteFrameLocked` only handles FLTP specially; all other formats receive raw `float` bytes copied into the frame (`FfmpegAudioEncoder.cs:70-90`).

Impact: codecs whose first supported sample format is `s16`, `s16p`, or another non-float format get invalid audio.

Fix direction: either restrict to FLT/FLTP encoders or add swresample/sample-format conversion before encode.

## Composition And Effects Issues

### `LayerConfig.ScaleAnchor` is unused

`LayerConfig` includes `ScaleAnchor` (`LayerConfig.cs:4-10`), but `LayerConfigResolver.ToTransform` never uses it (`LayerConfigResolver.cs:7-40`). Rotation is composed around the source origin, not the layer center or anchor (`LayerTransform2D.cs:30-39`).

Impact: API promises a control that does nothing. Rotation/scale behavior will surprise users building PiP and animated layouts.

Fix direction: either implement anchor-aware transform composition or remove the property until supported.

### Layer transitions are not thread-safe

`LayerHandle` stores `_config` and `_transitions` without synchronization (`LayerHandle.cs:10-15`, `34-45`) while `TryPullFrame` reads them from the compositor path (`LayerHandle.cs:47-65`).

Impact: UI/control thread changes can race with composition.

Fix direction: make layer state immutable snapshots swapped atomically, or protect mutation and reads with a lock.

### Layer pull leaks frames if configure throws

`LayerHandle.TryPullFrame` converts or accepts a frame, then calls `Slot.Output.Configure(frame.Format)` and `Slot.Output.Submit(frame)` (`LayerHandle.cs:67-89`). If `Configure` throws, the frame is not disposed.

Impact: pooled/native frame leak on dynamic format mismatch.

Fix direction: use a `try`/`catch` around configure/submit that disposes the frame unless ownership has definitely moved.

### `StaticFrameSource.FromFrame(copyBacking: false)` is a footgun

With `copyBacking: false`, the source aliases the caller's frame planes and does not own/dispose the original (`StaticFrameSource.cs:108-130`). If the caller disposes the original frame, the source emits invalid memory.

Impact: easy misuse for a convenience API.

Fix direction: remove the non-copying mode from public API or require an explicit shared/refcounted frame backing.

## NDI Findings

### `NDIOutput.Video` lazy initialization is not thread-safe

The `Video` getter checks `_disposed` and initializes `_videoOutput ??=` without holding the lock (`NDIOutput.cs:73-79`), even though a locked `CreateVideoOutputLocked` helper exists.

Impact: two threads can create two senders, leaking one or racing NDI sender state.

Fix direction: make the getter call the locked helper.

### `NDIAudioOutput.EnsurePackedCapacity` can lose the original pointer on realloc failure

`NativeMemory.Realloc` is assigned directly to `_packedBuffer` (`NDIAudioOutput.cs:126-138`). If realloc returns null/fails, the original allocation can be lost from managed state.

Impact: native memory leak and possible null pointer use.

Fix direction: assign to a temporary pointer, check it, then update the field.

### NDI receiver background threads need fault boundaries

`NDIVideoReceiver.CaptureLoop` and `NDIAudioReceiver.CaptureLoop` have no outer catch (`NDIVideoReceiver.cs:161-208`, `NDIAudioReceiver.cs:229-307`).

Impact: receiver/native/unpack errors can crash the process.

Fix direction: store fault state, wake readers, and expose an error event/property.

### NDI live sources expose format only after first frame

`NDIAudioReceiver.Format` and `NDIVideoReceiver.Format` are only known after capture receives a frame (`NDIAudioReceiver.cs:59-67`, `NDIVideoReceiver.cs:43-48`). `AudioRouter.AddSource` requires a format up front.

Impact: users must manually wait/probe before wiring a live source.

Fix direction: provide async `Connect/Probe` helpers or a `TryGetFormat`/format-changed event pattern.

## PortAudio Findings

### PortAudio wiring can leak outputs on partial failure

`PortAudioPlaybackHost.TryWirePortAudioMainForRouter` creates a `PortAudioOutput`, then calls router wiring. If a later step fails, the catch path does not dispose the created output (`PortAudioPlaybackHost.cs:120-149`).

Impact: native PortAudio resources can leak during setup failure.

Fix direction: use ownership transfer only after successful router registration; dispose on failure.

### PortAudio companion ownership is awkward for the easy path

The builder extensions create a PortAudio host as a companion, but default ownership requires the caller to dispose it separately (`MediaPlayerOpenBuilderPortAudioExtensions.cs:12-18`, `47-82`).

Impact: the simple "open with audio" path is easy to leak.

Fix direction: the high-level session/player should own the companion host by default.

### PortAudio prefill allocates every call

`PortAudioOutput.PrefillFrom` allocates a new float buffer (`PortAudioOutput.cs:351-382`).

Impact: not a hot path, but unnecessary allocation.

Fix direction: use `ArrayPool<float>` or reuse a scratch buffer.

## SDL/OpenGL Output Findings

### SDL output disposal can dispose synchronization handles while the render thread is still alive

`SDL3VideoOutput.Dispose` joins the render thread for 2 seconds, then disposes `_wakeup` and `_ready` (`SDL3VideoOutput.cs:223-250`). If the thread did not exit, it may still wait on those handles (`SDL3VideoOutput.cs:258-300`). `SDL3GLVideoOutput` has the same pattern with a 45 second join (`SDL3GLVideoOutput.cs:421-447`, `473-520`).

Impact: rare shutdown crashes or ObjectDisposedException on render threads when an output blocks in native present.

Fix direction: do not dispose wait handles or inner resources until the render thread has exited, or mark the output as failed-to-stop and require external teardown.

### `SDL3VideoOutput` cannot reconfigure, while GL output can

`SDL3VideoOutput.Configure` throws if already configured (`SDL3VideoOutput.cs:122-128`), while `SDL3GLVideoOutput.Configure` supports same-format idempotence and reconfiguration (`SDL3GLVideoOutput.cs:259-317`).

Impact: inconsistent output API across two SDL backends.

Fix direction: align the contracts. Either all outputs are single-format lifetime objects, or reconfigure support is explicit and consistent.

## Smaller But Important Contract Issues

### `AudioRouter.RunLoop` does not validate source read counts

The run loop trusts `ReadInto` return values (`AudioRouter.cs:1155-1157`). A buggy source returning negative or too many samples can break routing.

Fix direction: validate `0 <= read <= scratch.Length` and fault the source if violated.

### `AudioRouter.WaitForIdle` can wait even after queues were abandoned

`WaitForIdle` checks `processed < enqueued` (`AudioRouter.cs:1590-1598`). `AbandonQueue` records drops but does not increment processed, so idle waits can time out despite no queued work.

Fix direction: model dropped/abandoned chunks as completed for idle-drain purposes.

### Adaptive-rate output wrapper can become a fake primary clock

`AdaptiveRateAudioOutput` implements `IClockedOutput` unconditionally (`AdaptiveRateAudioOutput.cs:38`, `138-144`). `AudioRouter.AddOutput` wraps before auto-wiring primary output (`AudioRouter.Playback.cs:100-124`). If adaptive mode is enabled and the first output is not actually clocked, the wrapper can satisfy the `IClockedOutput` check and become the primary clock.

Impact: the router can slave to a fake clock that just reports capacity.

Fix direction: only wrap non-primary outputs after a real primary exists, or do not expose `IClockedOutput` unless the inner output is actually clocked.

### Adaptive-rate wrappers are not disposed by the router

The router creates adaptive wrappers (`AudioRouter.Playback.cs:115-124`), but output removal/disposal does not clearly own and dispose the wrapper. `AdaptiveRateAudioOutput.Dispose` unsubscribes monitor state (`AdaptiveRateAudioOutput.cs:146-157`).

Impact: monitor subscriptions can leak.

Fix direction: store wrapper ownership in `OutputEntry` and dispose it on remove/router dispose.

## Suggested Refactoring Roadmap

1. Frame contracts first.
   - Add validation for `VideoFormat` and `VideoFrame`.
   - Make frame backing immutable/refcounted.
   - Fix static/text/image source frame lifetimes.

2. Safe transport and faults.
   - Add fault events/states for audio router, video player, FFmpeg sources, and NDI receivers.
   - Replace background-thread rethrows with controlled shutdown.
   - Fix FFmpeg demux stop/interrupt.

3. Source activation model.
   - Sources should not be pulled unless an active route/voice needs them.
   - Make voices/cues explicit high-level objects.
   - Preserve seek and duration through wrappers.

4. A/V sync and seek.
   - Make coordinated seek the default.
   - Use a start barrier for audio/video/clock.
   - Cap router clock catch-up.
   - Reset fallback PTS counters on seek.

5. Composition timing redesign.
   - Make the compositor request frames by playhead/canvas time.
   - Decode layers into bounded PTS queues.
   - Treat static/text/image layers as immutable timed assets.
   - Implement or remove unused transform API like `ScaleAnchor`.

6. Output lifecycle and threading.
   - Dispose removed outputs.
   - Avoid one dedicated thread per output where practical.
   - Do conversion/submission outside router locks.
   - Make output reconfiguration/draining explicit.

7. High-level API cleanup.
   - Introduce an owned `MediaSession`/`PlaybackSession`.
   - Move process-wide plugin defaults into per-session options.
   - Make file/URI/stream opening strict and predictable.
   - Make PortAudio/NDI/Skia companion ownership automatic in simple workflows.

## Priority Backlog

P0:

- Fix FFmpeg demux stop race.
- Fix `VideoPlayer` post-submit double-dispose.
- Dispose `VideoRouter` outputs on remove.
- Stop `AudioRouter` from draining unrouted sources.
- Add fault handling instead of background-thread rethrows.
- Fix static/text/image frame backing lifetime.

P1:

- Make coordinated seek the default.
- Fix composition pull/prebuffer timing.
- Reset no-PTS fallback counters on seek.
- Fix `AudioClipVoice` EOF release hang.
- Fix FFmpeg video encoder frame allocation.
- Fix NDI getter/realloc/background fault issues.
- Preserve seekability through resampling.

P2:

- Reduce compositor/router/FFmpeg hot-path allocations.
- Replace or lazy-start per-output threads.
- Normalize output reconfiguration semantics.
- Improve high-level builder/session ownership.
- Add validation to video contracts.
