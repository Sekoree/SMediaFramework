# Media Framework Independent Review - 2026-06-02

## Scope

This review cross-checks `MediaFramework-Deep-Review-Findings.md` and
`MediaFramework-Checklist-2026-06.md` against the current implementation, then
performs a fresh pass over the framework for media-player, cue-player,
OBS-like routing/compositing, and soundboard workloads.

The conclusions below are based on code inspection, not trust in either the
old docs or the checklist. Line numbers refer to the current tree at the time
of this review.

## Executive Summary

Most of the checklist work is genuinely implemented. The demuxer now has an
FFmpeg interrupt callback and fault boundary, routers have much better
ownership and route-gated behavior, static/image/text frame lifetime is
reference-counted, SDL/video pump blocked-dispose handling was improved, and
the first-class `MediaSession`, `Soundboard`, `CueVoice`, and compositor slot
APIs exist.

However, the framework is not "all clear." The highest-risk remaining issues
are still lifecycle and concurrency bugs:

1. Shared-demux direct seek is still unsafe around concurrent video reads and
   stale pending packets.
2. `VideoPlayer` still rethrows from its decode background thread.
3. Cancelled stop/pause calls can leave an old playback thread alive while the
   object is restartable.
4. `AudioRouter.OutputPump` and NDI receiver disposal can free resources under
   still-running worker/capture threads.
5. `VideoRouter` can leak branch frames if a branch output throws during final
   delivery.
6. Declarative compositor and soundboard APIs still have mutation/disposal
   races relevant to live show-control applications.

The framework is already useful, but the remaining issues matter for host
applications that need "keep the show running" behavior: VLC-like players,
QLab-like cue stacks, OBS-like live routing, NDI ingest/egress, and hardware
soundboards.

## Prior Checklist Verification

### Verified as Implemented

- FFmpeg shared demux has `AVIOInterruptCB`, a demux stop request, a stuck
  thread guard, and a demux thread exception boundary
  (`MediaContainerSharedDemux.cs`).
- `VideoPlayer` presentation-time events now happen after a successful submit
  and event subscriber exceptions are contained.
- `VideoRouter.RemoveOutput` captures and disposes owned output registrations
  outside the router lock.
- `AudioRouter` no longer drains unrouted sources, validates invalid read
  counts, has a run-loop fault boundary, and lazy-starts output pump threads.
- `AudioRouter.RemoveOutput` disposes adaptive wrappers and the router dispose
  path disposes owned wrappers.
- Static, text, and image frame sources use reference counting so frames can
  safely outlive their sources.
- Shared-demux and standalone video seek now re-anchor fallback no-PTS counters.
- FFmpeg video encoder now allocates the encoder frame buffer once and uses
  `av_frame_make_writable`.
- FFmpeg audio encoder now uses `swr_convert` and offset-based pending sample
  buffering.
- NDI receivers now contain capture-loop exceptions and use rebased frame
  timing.
- SDL outputs and `VideoOutputPump` now avoid disposing core state when their
  worker/render thread remains blocked after the bounded join.
- `MediaSession`, builder session APIs, `Soundboard`, `CueVoice`, slot-based
  `VideoCompositorSource`, and time-driven compositor paths are present.

### Checklist Claims That Need Correction

- The P1-3 shared-demux seek locking fix is incomplete. Audio state is now
  flushed under `_audioDecodeLock`, but video codec/frame state is flushed under
  the audio lock, not `_videoDecodeLock`, and stale pending packets can be
  reintroduced after queue drain.
- The PortAudio wiring leak fix appears complete for
  `TryWirePortAudioMainForRouter`, but not for `TryCreatePortAudioMain`.
- The P2-25 `VideoFrame` geometry conclusion is too broad. Keeping the
  constructor permissive is reasonable for lifetime-only hardware frames, but
  native output/encoder handoff paths still need strict geometry validation.
- `Doc/MediaFramework-Architecture.md` describes `MediaContainerSession.Pause`
  default flush behavior incorrectly. Direct `MediaContainerSession.Pause()`
  currently passes `null`; only `MediaPlayer.Pause()` defaults to
  `FlushCodecPipelines`.
- The previous compositor timing concern has changed. `VideoCompositor` now has
  a clock-driven path, but its public mutable layer API is not synchronized.

## Findings

### F1 - High - Shared-Demux Direct Seek Still Races Consumer Reads

Evidence:

- `SeekPresentation` stops the demuxer and drains queues at
  `MediaContainerSharedDemux.cs:701-707`.
- It then flushes both audio and video codec contexts inside
  `lock (_audioDecodeLock)` at `MediaContainerSharedDemux.cs:736-741`.
- It also unrefs `_vFrame` and resets video state in that same audio lock at
  `MediaContainerSharedDemux.cs:751-769`.
- Only the post-seek video consume is protected by `_videoDecodeLock` at
  `MediaContainerSharedDemux.cs:775-778`.
- `VideoTrack.TryReadNextFrame` uses `_vCtx` and `_vFrame` under
  `_videoDecodeLock`, not `_audioDecodeLock`.
- `FeedAudioFromQueue` and `FeedVideoFromQueue` restore `_aPendingPacket` and
  `_vPendingPacket` outside `_queueGate` when `avcodec_send_packet` returns
  `EAGAIN` (`MediaContainerSharedDemux.cs:931-934`,
  `MediaContainerSharedDemux.cs:1128-1131`).

Impact:

Higher-level `MediaContainerSession.Seek` pauses the session first, so the
normal `MediaPlayer` file-playback path is safer. The direct
`ISeekableSource.Seek`, `AudioTrack.Seek`, and `VideoTrack.Seek` contract is
still unsafe if a source consumer is reading concurrently. A pre-seek pending
packet can also be republished after `StopDemuxerAndDrainQueues` has freed the
queues, so stale packets can feed the post-seek decoder.

Recommendation:

Use a single seek/read coordination gate or take both decode locks before
flushing either codec context. Clear and free `_aPendingPacket` and
`_vPendingPacket` under `_queueGate`, and make the EAGAIN paths republish
pending packets under that same gate with a seek-generation check.

### F2 - High - VideoPlayer Decode Thread Still Rethrows

Evidence:

- `VideoPlayer.DecodeLoop` catches general exceptions, logs them, then
  `throw;` at `VideoPlayer.cs:371-374`.

Impact:

A corrupt source frame, converter exception, source wrapper bug, or unexpected
dispose race can escape a background decode thread. This is the same failure
class that was already fixed in `AudioRouter` and NDI receivers. It is
especially problematic for cue playback and live tools, where one bad clip or
output must not terminate the host process.

Recommendation:

Add a fault state similar to `AudioRouter`. Store the exception, stop the run
loop, wake any blocked submit/tick paths, expose `Fault`/`Faulted`, and make
subsequent `Play` fail deterministically until the player is disposed or reset.

### F3 - High - Cancelled Stop/Pause Can Leave Old Threads Alive but Restartable

Evidence:

- `VideoPlayer.StopInternal` clears `_decodeThread` and `_cts` before joining
  the old thread (`VideoPlayer.cs:265-274`).
- It calls `CooperativePlaybackJoin.JoinThread(..., cancellationToken)` at
  `VideoPlayer.cs:287-296`.
- If the caller's token is cancelled, `JoinThread` throws before the code marks
  `_decodeThreadStuck` (`VideoPlayer.cs:299-305`) and before cleanup
  (`VideoPlayer.cs:308-312`).
- `AudioRouter.StopInternal` has the same shape: it clears `_thread` and `_cts`
  before a cancellable join at `AudioRouter.cs:1019-1033`.

Impact:

After a cancelled pause/stop, the caller can catch the cancellation and call
`Play`/`Resume` again while the old decode/router thread is still alive. That
can create two threads touching the same source, decoder, queue, or output
pumps. UI paths that use bounded/cancellable pause calls are exposed to this.

Recommendation:

Never leave the object restartable when the stop join is cancelled before the
old thread exits. Either make stop joins non-cancellable after the state is
made restartable, or mark the instance terminal/stuck if cancellation occurs
while the old thread is still alive. Put cleanup and yield clearing in a
`finally`.

### F4 - High - Audio OutputPump Can Dispose Wait Handles Under a Live Drainer

Evidence:

- `AudioRouter.OutputPump.Dispose` completes the queue and joins for 2 seconds
  (`AudioRouter.cs:1723-1735`).
- If the thread is still alive, it cancels and joins for one more second
  (`AudioRouter.cs:1736-1748`).
- It then unconditionally disposes `_ready` and `_cts`
  (`AudioRouter.cs:1751-1752`).

Impact:

If an `IAudioOutput.Submit` blocks longer than the join caps, the drainer
thread may later touch disposed queue/CTS state. That can produce background
exceptions or use-after-dispose behavior. The video pump already uses a better
policy: leak the pump state deliberately if the worker is still alive.

Recommendation:

Apply the same "leak if still alive" policy used by `VideoOutputPump` and SDL
outputs. Also consider exposing a pump fault/blocked metric so operator HUDs can
show stuck outputs.

### F5 - High - NDI Receiver Dispose Frees Native Receiver Under Live Capture Thread

Evidence:

- `NDISource.Dispose` cancels and joins the capture thread for 2 seconds, then
  disposes `_receiver` and `_runtime` regardless of thread liveness
  (`NDISource.cs:645-664`).
- `NDIVideoReceiver.Dispose` does the same
  (`NDIVideoReceiver.cs:304-322`).
- `NDIAudioReceiver.Dispose` does the same
  (`NDIAudioReceiver.cs:443-452`).

Impact:

The new capture fault boundaries are good, but they do not protect against
native lifetime races when NDI capture is blocked in a native call past the join
timeout. Disposing the native receiver/runtime underneath that thread can crash
or corrupt later native calls.

Recommendation:

Adopt the same blocked-thread policy used for SDL and `VideoOutputPump`: if the
capture thread is still alive after the join cap, leak the receiver/runtime and
log loudly instead of disposing native state underneath it. Expose a terminal
connection state so hosts can replace the receiver instance.

### F6 - Medium - VideoRouter Leaks Branch Frames When Branch Submit Throws

Evidence:

- In `VideoRouter.InputRegistration.SubmitPhased`, branch delivery sets
  `branchFrames[i] = null` before calling `be.Output.Submit(f)`
  (`VideoRouter.cs:743-750`).
- The catch cleanup later disposes only frames still present in `branchFrames`
  (`VideoRouter.cs:755-766`).

Impact:

If a branch output throws during `Submit`, that branch frame is lost from the
cleanup list and is not disposed. With native backings, fanout views, or pooled
buffers, this can leak buffers or references. Default async pumps reduce the
chance because `Submit` is a fast enqueue, but synchronous/custom branch
outputs remain valid API usage.

Recommendation:

Keep the frame in the cleanup list until submit succeeds, or use a local
`submitted` flag and dispose on failure. Add a regression test where a branch
output throws while the primary succeeds.

### F7 - Medium - Declarative Compositor Layer Mutation Is Not Thread-Safe

Evidence:

- `VideoCompositor.AddLayer` and `RemoveLayer` mutate `_layers` without a
  shared lock (`VideoCompositor.cs:79-96`).
- `TryReadNextFrame` iterates `_layers` directly
  (`VideoCompositor.cs:101-123`).
- `LayerHandle.AdvanceTo` mutates `_lookahead`, displayed state, and converter
  state without coordination with `LayerHandle.Close`
  (`LayerHandle.cs:66-126`).
- `VideoCompositorSource.Dispose` closes slots and disposes the compositor
  without taking the `_readGate` used by `TryReadNextFrame`
  (`VideoCompositorSource.cs:180-231`).

Impact:

Declarative compositor users are likely to add/remove layers from a UI or cue
thread while a playback/read thread composites frames. That can throw
"collection modified" exceptions, close a layer while it is advancing, or
dispose the underlying compositor while a composite is in progress.

Recommendation:

Either document `VideoCompositor` as single-threaded and provide a thread-safe
runtime wrapper, or make the declarative API snapshot layers under a gate.
`LayerHandle.Close` should coordinate with `AdvanceTo`/`PullOneAndSubmit`, and
`VideoCompositorSource.Dispose` should take the read gate before disposing the
inner compositor.

### F8 - Medium - Soundboard Fire/Dispose Race Can Leak a Voice

Evidence:

- `Soundboard.Fire` locks, reads the cue entry, unlocks, then calls
  `entry.Player.TryFire` (`Soundboard.cs:89-104`).
- It creates a `CueVoice` and adds it to `_live` under a second lock without
  rechecking `_disposed` (`Soundboard.cs:106-108`).
- `Dispose` marks `_disposed`, snapshots/clears entries and live voices, then
  hard-stops the snapshot (`Soundboard.cs:167-191`).

Impact:

If `Dispose` runs between the first `Fire` lock and `TryFire`, a borrowed router
can receive a new source/route after the board has already cleared its tracking
state. That voice may not be reaped or completed. This matters for scene unload
and hardware-grid shutdown.

Recommendation:

Use a reservation under the soundboard lock, or recheck `_disposed` after
`TryFire` and roll back the newly created voice/source/route if disposal won the
race.

### F9 - Medium - PortAudio TryCreatePortAudioMain Has Partial Failure Leaks

Evidence:

- `TryCreatePortAudioMain` creates `MediaClock`, `AudioRouter`, and
  `PortAudioOutput`, then registers source/output/routes inside one `try`
  (`PortAudioPlaybackHost.cs:58-88`).
- Its catch only reports the message and returns null
  (`PortAudioPlaybackHost.cs:90-94`).
- The separate `TryWirePortAudioMainForRouter` path has rollback logic; this
  path does not.

Impact:

If output creation succeeds but source/output registration or connect fails,
the native `PortAudioOutput` and router resources are leaked. The common happy
path is fine, but this is still a host-facing helper that should be rollback
safe.

Recommendation:

Mirror the rollback pattern from `TryWirePortAudioMainForRouter`: hoist locals,
remove registered outputs/sources when possible, and dispose the output/router
on failure.

### F10 - Medium - Pause Flush Documentation and API Defaults Are Confusing

Evidence:

- `MediaContainerSession.Pause(CancellationToken, Action?)` forwards the action
  directly, and the default is `null` (`MediaContainerSession.cs:44-51`).
- `MediaPlayer.Pause` defaults to
  `PauseFlushPolicy.FlushCodecPipelines` and resolves that into
  `_bundle.Decoder.FlushCodecPipelines` (`MediaPlayer.cs:250-289`).
- `Doc/MediaFramework-Architecture.md` says omitted/null
  `flushSharedMuxAfterPause` defaults to `MediaContainerDecoder.FlushCodecPipelines`.

Impact:

Direct session users and `MediaPlayer` users get different default pause
behavior, while the architecture doc says they are the same. That can lead to
stale decoder-pipeline behavior in custom hosts, or needless flushes if users
try to work around the mismatch.

Recommendation:

Choose one contract and make it explicit. The simplest API would be a
`PauseFlushPolicy` overload everywhere and a clearly named "no flush" method
for the expert path.

### F11 - Medium - VideoFrame Geometry Validation Is Too Loose at Native Handoff

Evidence:

- `VideoFrame.Validation` checks CPU frames have at least one plane and positive
  strides, but it does not verify plane count, row size, or plane byte length
  for each pixel format (`VideoFrame.Validation.cs:11-69`).
- SDL upload paths and FFmpeg encoder fill paths index planes/strides directly.

Impact:

The permissive constructor is useful for hardware/lifetime-only frame wrappers,
but native handoff code should not trust malformed CPU frames. Bad frames can
throw late, upload stale data, copy partial rows, or fail inside native code.

Recommendation:

Keep the constructor permissive if needed, but add an explicit
`VideoFrameGeometryValidator`/`ValidateForCpuRead` helper and call it at native
output, CPU converter, and encoder boundaries. Tests should include short-plane
and wrong-plane-count frames for BGRA, NV12, I420, P010/P016, UYVY, and YUY2.

### F12 - Low/Medium - FFmpeg Encoder Submit Contracts Are Inconsistent

Evidence:

- `IAudioOutput.Submit` requires `packedSamples.Length` to be a multiple of the
  channel count, but `FfmpegAudioEncoder.Submit` does not validate that
  (`IAudioOutput.cs`, `FfmpegAudioEncoder.cs:52-74`).
- `IVideoOutput.Submit` says the output takes ownership of the frame, but
  `FfmpegVideoEncoder.Submit` throws for unconfigured use before entering the
  `finally` that disposes the frame (`FfmpegVideoEncoder.cs:67-108`).

Impact:

These are edge cases, but they make encoder outputs less predictable than other
outputs. Misaligned audio spans can shift channel framing, and unconfigured
video submit can leak when callers rely on the documented ownership transfer.

Recommendation:

Validate audio span alignment up front. For video, either dispose on every
`Submit` call including unconfigured failure or document that ownership only
transfers after configuration/acceptance.

### F13 - Low/Medium - Process-Wide Mutable Defaults Still Hurt Embedded Hosts

Evidence:

- `AudioRouter.DefaultAutoResample`, `MediaFrameworkPlugins` factories, and
  compositor backend registration are process-wide mutable defaults.

Impact:

This is manageable in a single app, but plugin hosts, test suites, OBS-like
applications, and embedded uses may need per-session behavior. Process-wide
mutation can make unrelated sessions influence each other.

Recommendation:

Move toward per-session option objects and scoped plugin registries, while
keeping the current process-wide defaults as convenience fallbacks.

## Product and API Enhancements

### VLC-Style Media Player Workloads

Useful next layers:

- A stable `MediaPlayerController` facade with explicit states:
  `Stopped`, `Opening`, `Ready`, `Playing`, `Paused`, `Buffering`, `Faulted`,
  and `Disposed`.
- Track-selection APIs for audio/video/subtitle streams, with hot switching.
- Subtitle/caption decode and overlay support, including external subtitle
  files.
- Playback-rate control with proper audio time-stretch, not just clock speed.
- AB repeat, frame step, snapshot export, scrubbing thumbnails, and waveform
  previews.
- Gapless playlist primitives and pre-open/prebuffer of the next item.
- Device hotplug/rebind events for audio, SDL/GL displays, NDI endpoints, and
  virtual outputs.
- A public health snapshot covering decode thread state, output queue depths,
  dropped frames, audio drift, NDI overflow, and last fault.

### Cue Player / QLab / OBS-Like Workloads

Useful next layers:

- A first-class cue graph/timeline model: cue IDs, pre-wait, post-wait,
  follow-on, auto-continue, grouped cues, armed/disabled cues, and stop targets.
- Built-in pre-roll/prewarm API for video decoders, audio clip voices, NDI
  sender/receiver paths, and GL compositors.
- Transition primitives with duration/easing for opacity, position, scale,
  crop, audio gain, and output routing.
- Sync groups where multiple media sources share one master clock and report
  readiness before the cue fires.
- Output patching/matrices as first-class objects, including live re-route with
  drain/format-version semantics.
- OSC, MIDI, MTC/LTC/SMPTE, and keyboard/hardware trigger bindings at the
  framework layer, not only in app code.
- Show-file serialization for cues, outputs, routes, transitions, and device
  rebinding policy.
- Fault policy per cue: stop show, skip cue, hold last frame, fade to black,
  route to fallback, or continue audio-only.

### Soundboard / Hardware Grid Workloads

Useful next layers:

- A low-latency clip pool with decoded PCM residency, memory budgets, and
  explicit preload/unload.
- Per-pad modes beyond one-shot/latch/choke: momentary, retrigger, toggle,
  exclusive group, quantized launch, velocity-sensitive gain, and stop-on-note-off.
- Per-voice controls: seek, fade-to, pitch/speed, pan, output-route override,
  and remaining-time query.
- Automatic background reaping, not only caller-driven `Reap`.
- Hardware grid mapping: MIDI note/CC, keyboard, OSC, StreamDeck-like button
  IDs, LED feedback state, and debounce/retrigger policy.
- Sample-accurate or router-tick-accurate scheduled fire for tight musical
  triggering.

### Framework-Wide Simplifications

- A unified "graph builder" that can assemble common topologies:
  file-to-window, file-to-NDI, file-to-audio-device, file-to-preview-plus-NDI,
  cue-compositor, and soundboard.
- Consistent lifetime ownership rules:
  "owns on success", "owns on call", or "caller retains on throw" should be
  documented per interface and followed by all implementations.
- Consistent stop semantics:
  cancellable request phase, bounded join phase, terminal stuck state, and
  restart policy should be common across video player, audio router, NDI
  receivers, and output pumps.
- Fault propagation should be uniform: every long-running background component
  should expose `Faulted`, last exception, and an event/callback that is safe
  for UI/logging.
- Validation should happen at boundaries: source adapters can be permissive,
  but native outputs, encoders, and GPU uploaders should reject malformed frames
  before native calls.
- Hot-path allocations should remain visible through tests. The compositor slot
  path improved, but router fanout, metadata cloning, audio route snapshots,
  and per-frame diagnostics should continue to have allocation regression tests.

## Recommended Priority Order

1. Fix shared-demux seek coordination and stale pending packets.
2. Add `VideoPlayer` fault containment and make cancelled stop non-restartable
   when the old thread remains alive.
3. Apply the blocked-thread leak policy to `AudioRouter.OutputPump` and NDI
   receivers.
4. Fix the `VideoRouter` branch submit leak.
5. Add locks/snapshots around declarative compositor mutation and soundboard
   fire/dispose.
6. Correct pause-flush docs/API defaults and PortAudio rollback.
7. Add strict geometry validation at native/encoder boundaries.
8. Expand product APIs for cue graphs, preload/prewarm, device rebinding,
   health snapshots, and soundboard hardware mappings.

## Test Gaps to Add

- Direct shared-demux audio/video reads racing a direct `SeekPresentation`.
- EAGAIN pending-packet republish across seek generation.
- `VideoPlayer` source throws from `TryReadNextFrame`; assert no process-level
  background rethrow and a visible fault state.
- `VideoPlayer.Stop`/`Pause` cancellation while the decode thread is blocked;
  assert the instance cannot restart unsafely.
- `AudioRouter.OutputPump` inner output blocks forever; assert pump state is not
  disposed under the live drainer.
- NDI receiver fake/native shim blocks in capture; assert receiver/runtime are
  not disposed while the capture thread remains alive.
- `VideoRouter` branch output throws during phase-3 submit; assert all frames
  are disposed exactly once.
- `VideoCompositor` add/remove layer while a read is in progress.
- `VideoCompositorSource.Dispose` while `TryReadNextFrame` is inside the inner
  compositor.
- `Soundboard.Fire` racing `Dispose` with a borrowed router.
- Encoder submit contract tests for misaligned audio spans and unconfigured
  video submit ownership.
- CPU frame geometry tests for every native upload/encode pixel format.

## Verification Performed

Commands run from `/home/seko/RiderProjects/MFPlayer`:

- `dotnet build MediaFramework/Media/S.Media.Core/S.Media.Core.csproj --no-restore -v:m`
  - Succeeded.
  - Reported 5 obsolete API warnings.
- `dotnet build MediaFramework/Media/S.Media.FFmpeg/S.Media.FFmpeg.csproj --no-restore -v:m`
  - Succeeded.
  - Reported the same 5 obsolete API warnings from `S.Media.Core`.
- `dotnet build MediaFramework/Media/S.Media.Playback/S.Media.Playback.csproj --no-restore -v:m`
  - Failed before compiler diagnostics with `Build FAILED. 0 Warning(s), 0 Error(s)`.
- `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --no-restore -v:m`
  - Did not reach a meaningful test run in this environment.
  - A diagnostic build attempt showed failure in
    `_GetProjectReferenceTargetFrameworkProperties` while MSBuild still
    reported `0 Warning(s), 0 Error(s)`.

Because the test project did not run, the findings above should be treated as
code-review findings supported by line inspection, not as reproduced failing
tests.
