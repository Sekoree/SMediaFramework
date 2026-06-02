# Media Framework Independent Review Checklist - 2026-06-02

This checklist is derived from
`Doc/MediaFramework-Independent-Review-2026-06-02.md`.

Use it as an execution plan. Each item should be closed only when the
implementation, regression tests, and documentation/API-contract updates are
complete.

## Status Legend

- `[ ]` Not started.
- `[~]` In progress or partially complete.
- `[x]` Complete and verified.
- `[!]` Blocked or needs a design decision.

## Definition of Done

For each fix:

- The root cause is addressed, not only the observed symptom.
- Public API behavior is documented when the fix affects ownership, threading,
  cancellation, or fault behavior.
- Regression tests cover the failing/racy path where practical.
- Long-running background threads do not rethrow process-level exceptions.
- Stop/dispose paths leave the instance in one of three clear states:
  reusable, disposed, or terminal/faulted.
- Native resources are not disposed under live threads unless the native API is
  proven safe for that pattern.
- Hot-path fixes include allocation/performance checks when they alter
  per-frame or per-buffer code.

## Implementation Pass - 2026-06-02

Status after the first implementation pass:

- [~] P0-1 Shared-demux seek coordination implemented with a read/seek gate,
      read-yield wakeups, decoder-specific flush locks, queued packet draining,
      and pending-packet EAGAIN restore under `_queueGate`.
      A separate seek generation counter was not added because the gate/yield
      scheme prevents active read paths from republishing stale pending packets
      during seek. Keep this item open if a defense-in-depth generation token is
      still desired.
- [~] P0-2 VideoPlayer decode faults now stay inside the player boundary:
      `Fault`, `Faulted`, terminal restart refusal, queue drain, held-frame
      release, tick detachment, and balanced cooperative-yield cleanup for
      already-paused stops were added.
- [~] P0-3 Stop/pause cancellation now marks live decode/router threads
      terminal and non-restartable instead of allowing duplicate workers.
- [~] P0-4 Audio output pump disposal now leaks blocked pump state instead of
      disposing synchronization primitives under a live drainer thread.
- [~] P0-5 NDI dispose paths now avoid disposing native receiver/runtime/CTS
      under a live capture thread and no longer send a stopped ingest-clock
      notification unless the thread actually exited.
- [~] P1-1 VideoRouter fanout now retains branch frames for cleanup if branch
      output submission throws.
- [~] P1-2 VideoCompositor layer mutation, layer close, slot mutation, and
      source disposal are serialized more consistently.
- [~] P1-3 Soundboard fire/dispose rollback now aborts a just-created voice
      before throwing if disposal wins the race.
- [~] P1-4 PortAudio `TryCreatePortAudioMain` now rolls back registered source,
      output, router, and output object on failure.
- [~] P1-5 Pause/seek shared-demux flush defaults now coalesce to the normal
      container flush path unless the explicit "skipping flush" APIs are used.
- [~] P2-1 CPU `VideoFrame` geometry validation was added and is called before
      SDL3 upload, OpenGL CPU upload, and FFmpeg encode frame fill.
- [~] P2-2 FFmpeg audio encoder submit now rejects packed sample buffers whose
      length is not a multiple of channel count.
- [~] P2-3 FFmpeg video encoder submit now disposes accepted frames when
      unconfigured submission throws after ownership transfer.
- [~] P2-4 Process-wide mutable defaults remain open, but the second pass
      added per-router video converter factory/probe overrides with
      process-wide `MediaFrameworkPlugins` preserved as compatibility fallback.
- [ ] P3 product-facing APIs remain open: unified graph builder, VLC-style
      facade, cue/show model, OBS-style routing/compositing, soundboard grid,
      and external trigger integrations.

Validation completed:

- [x] `git diff --check`
- [x] `dotnet build MediaFramework/Media/S.Media.Core/S.Media.Core.csproj --no-restore -v:m`
- [x] `dotnet build MediaFramework/Media/S.Media.FFmpeg/S.Media.FFmpeg.csproj --no-restore -v:m`
- [x] `dotnet build MediaFramework/Media/S.Media.OpenGL/S.Media.OpenGL.csproj --no-restore -p:BuildProjectReferences=false -v:m`
- [x] `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --no-restore -v:m`
      - 424 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj --no-restore -v:m`
      - 163 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.FFmpeg.Encode.Tests/S.Media.FFmpeg.Encode.Tests.csproj --no-restore -v:m`
      - 2 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.Playback.Tests/S.Media.Playback.Tests.csproj --no-restore -v:m`
      - 31 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.OpenGL.Tests/S.Media.OpenGL.Tests.csproj --no-restore -v:m`
      - 105 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.SkiaSharp.Tests/S.Media.SkiaSharp.Tests.csproj --no-restore -v:m`
      - 10 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.PortAudio.Tests/S.Media.PortAudio.Tests.csproj --no-restore -v:m`
      - 22 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.NDI.Tests/S.Media.NDI.Tests.csproj --no-restore -v:m`
      - 54 passed.
- [x] `dotnet test MediaFramework/Test/OSCLib.Tests/OSCLib.Tests.csproj --no-restore -v:m`
      - 20 passed.
- [x] `dotnet test MediaFramework/Test/PMLib.Tests/PMLib.Tests.csproj --no-restore -v:m`
      - 13 passed.
- [x] `dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --no-restore -v:m`
      - 116 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --no-restore --filter FullyQualifiedName~VideoRouterConcurrencyTests -v:m`
      - 4 passed.

Validation notes:

- [x] The earlier MSBuild `_GetProjectReferenceTargetFrameworkProperties`
      failure no longer reproduces after the permission/environment change.
- [ ] Core and FFmpeg.Encode still emit existing obsolete-API warnings during
      full test builds; no test failures remain.

## P0 - Must Fix Before Calling the Framework Show-Safe

### P0-1 Shared-Demux Seek Coordination

Source finding: F1.

Affected files:

- `MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs`
- Relevant tests under `MediaFramework/Test/S.Media.FFmpeg.Tests`

Tasks:

- [ ] Add a seek/read coordination strategy for shared demux direct source use.
  Choose one of:
  - [ ] A single seek/read gate that all audio/video read and seek paths obey.
  - [ ] A lock-order-safe scheme that takes both `_audioDecodeLock` and
        `_videoDecodeLock` before flushing/resetting shared decoder state.
- [ ] Move video codec/frame flush/reset out of the audio-only lock.
- [ ] Ensure `SeekPresentation` cannot flush `_vCtx` or unref `_vFrame` while
      `VideoTrack.TryReadNextFrame` is using them.
- [ ] Ensure `SeekPresentation` cannot flush `_aCtx`, `_swr`, or `_aFrame`
      while `AudioTrack.ReadInto` is using them.
- [ ] Clear `_aPendingPacket` and `_vPendingPacket` under `_queueGate`.
- [ ] Free pending packets exactly once during seek/drain.
- [ ] Change EAGAIN pending-packet restore paths to republish under
      `_queueGate`.
- [ ] Add a seek generation counter so a packet popped before seek cannot be
      restored as pending after seek.
- [ ] Re-check demux thread stop/drain interactions after generation handling.
- [ ] Confirm higher-level `MediaContainerSession.Seek` still pauses and resumes
      correctly after the internal locking changes.

Regression tests:

- [ ] Direct audio read racing `SeekPresentation` does not crash, deadlock, or
      decode from half-flushed audio state.
- [ ] Direct video read racing `SeekPresentation` does not crash, deadlock, or
      decode from half-flushed video state.
- [ ] A packet returned as EAGAIN immediately before seek is not fed after seek.
- [ ] Shared-demux `MediaPlayer.Seek` still resumes only when it was playing
      before seek.
- [ ] Non-seekable stream-backed containers still reject seek.

Acceptance criteria:

- [ ] No decoder context/frame is flushed or reset outside the lock used by its
      consumer path.
- [ ] No pre-seek queued or pending packet can feed the decoder after a
      successful seek.
- [ ] The fix does not introduce lock-order deadlocks between audio reads,
      video reads, demux stop, and seek.

### P0-2 VideoPlayer Decode Fault Boundary

Source finding: F2.

Affected files:

- `MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs`
- `MediaFramework/Test/S.Media.Core.Tests/Video/VideoPlayerTests.cs`

Tasks:

- [ ] Replace the `catch (Exception) { throw; }` path in `DecodeLoop`.
- [ ] Add a stored last-fault exception field.
- [ ] Add a public or internal `Faulted`/`LastFault` surface consistent with
      existing diagnostics style.
- [ ] Stop decode loop execution when a source/converter exception occurs.
- [ ] Wake tick/queue/slot paths so the player does not hang after fault.
- [ ] Make `Play()` fail deterministically after fault until dispose/reset.
- [ ] Decide whether `Stop()` clears faults or whether a faulted player is
      terminal. Document the decision.
- [ ] Ensure event subscriber exceptions remain contained separately from
      decode faults.
- [ ] Ensure `ObjectDisposedException` from cooperative shutdown is still
      treated as normal shutdown, not as a fault.

Regression tests:

- [ ] `TryReadNextFrame` throwing from a fake source faults the player without
      background rethrow.
- [ ] `Play()` after decode fault throws a deterministic exception or remains
      stopped according to the chosen contract.
- [ ] `Dispose()` after decode fault is idempotent.
- [ ] Frame presentation event subscriber exceptions still do not fault the
      player.

Acceptance criteria:

- [ ] A bad source frame cannot terminate the host process through the decode
      thread.
- [ ] Hosts can discover the fault and replace the player.

### P0-3 Stop/Pause Cancellation Must Not Leave Restartable Live Threads

Source finding: F3.

Affected files:

- `MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs`
- `MediaFramework/Media/S.Media.Core/Audio/AudioRouter.cs`
- `MediaFramework/Media/S.Media.Core/Threading/CooperativePlaybackJoin.cs`

Tasks:

- [ ] Audit every call to `CooperativePlaybackJoin.JoinThread` that passes a
      caller cancellation token.
- [ ] For `VideoPlayer.StopInternal`, ensure cancellation during join cannot
      leave `_decodeThreadStuck == false` while the old thread is alive.
- [ ] For `AudioRouter.StopInternal`, ensure cancellation during join cannot
      leave `_thread == null` and `_isRunning == false` while the old run loop
      remains alive and restartable.
- [ ] Move cleanup that must always happen into `finally` blocks.
- [ ] Ensure cooperative yield requests are cleared only when safe.
- [ ] Decide on a shared stop result contract:
      - [ ] Stop completed and reusable.
      - [ ] Stop requested but join cancelled; instance is terminal.
      - [ ] Stop timed out; instance is terminal/stuck.
- [ ] Document the cancellation contract for `Pause`, `Stop`, and `Dispose`.
- [ ] Update UI/app call sites if they assume a cancelled pause leaves the
      player/router reusable.

Regression tests:

- [ ] Fake video source blocks in `TryReadNextFrame`; cancel `Pause`/`Stop`;
      assert a subsequent `Play` cannot start a second decode thread.
- [ ] Fake audio source/output blocks the router run loop; cancel `Stop`;
      assert a subsequent `Start` cannot start a second router thread.
- [ ] Non-cancelled full-timeout stop still marks the instance terminal.
- [ ] Normal stop/pause remains reusable.

Acceptance criteria:

- [ ] There is no code path where cancellation leaves an old thread alive and a
      new thread can be started on the same instance.

### P0-4 Blocked-Thread Dispose Policy for Audio OutputPump

Source finding: F4.

Affected files:

- `MediaFramework/Media/S.Media.Core/Audio/AudioRouter.cs`
- `MediaFramework/Test/S.Media.Core.Tests/Audio/AudioRouterPumpLifecycleTests.cs`

Tasks:

- [ ] Match `VideoOutputPump` blocked-dispose policy for audio output pumps.
- [ ] After join attempts, check `_thread.IsAlive`.
- [ ] If still alive, do not dispose `_ready` or `_cts`.
- [ ] Log a clear error/warning that pump state is intentionally leaked to
      avoid use-after-dispose.
- [ ] Decide whether the inner `IAudioOutput` should also be leaked when the
      drainer is blocked inside `Submit`.
- [ ] Expose pump stuck/blocked state through `OutputPumpStats` or a new
      diagnostic surface.
- [ ] Ensure unstarted lazy pumps can still be disposed cheaply.

Regression tests:

- [ ] Inner `IAudioOutput.Submit` blocks longer than the join cap; dispose does
      not dispose wait handles under the live drainer.
- [ ] Unstarted pump dispose still succeeds.
- [ ] Normal pump dispose still disposes queue/CTS.
- [ ] Router dispose with one stuck pump does not block forever.

Acceptance criteria:

- [ ] Audio pump disposal cannot cause the drainer to touch disposed wait
      handles or cancellation sources.

### P0-5 Blocked-Thread Dispose Policy for NDI Receivers

Source finding: F5.

Affected files:

- `MediaFramework/Media/S.Media.NDI/NDISource.cs`
- `MediaFramework/Media/S.Media.NDI/Video/NDIVideoReceiver.cs`
- `MediaFramework/Media/S.Media.NDI/Audio/NDIAudioReceiver.cs`
- NDI tests under `MediaFramework/Test/S.Media.NDI.Tests`

Tasks:

- [ ] After capture-thread join, check whether the capture thread is still
      alive.
- [ ] If still alive, do not dispose the native receiver.
- [ ] If still alive, do not dispose the NDI runtime if the live thread may
      still use it.
- [ ] Log a terminal stuck-capture warning.
- [ ] Set connection/state to a terminal disposed/stuck state that hosts can
      observe.
- [ ] Ensure queued video/audio buffers are still released when safe.
- [ ] Confirm cancellation and wait-pulse behavior still wakes readers.
- [ ] Confirm normal capture shutdown still disposes native resources.

Regression tests:

- [ ] Use a fake/shim receiver that blocks in capture; dispose returns after the
      cap and does not dispose native receiver/runtime while thread is alive.
- [ ] Normal capture thread exits; dispose releases receiver/runtime.
- [ ] Capture-loop exception still faults/contains instead of rethrowing.
- [ ] Readers blocked on no data wake after dispose.

Acceptance criteria:

- [ ] No NDI receiver class frees native capture resources underneath a live
      capture thread.

## P1 - Correctness and Leak Fixes

### P1-1 VideoRouter Branch Submit Leak

Source finding: F6.

Affected files:

- `MediaFramework/Media/S.Media.Core/Video/VideoRouter.cs`
- `MediaFramework/Test/S.Media.Core.Tests/Video/VideoRouterConcurrencyTests.cs`
- `MediaFramework/Test/S.Media.FFmpeg.Tests/Video/VideoRouterTests.cs`

Tasks:

- [ ] Change branch delivery so `branchFrames[i]` is cleared only after submit
      succeeds, or use a local submitted flag.
- [ ] Ensure removed-output branches still dispose their frames.
- [ ] Ensure thrown branch output does not leak its frame.
- [ ] Ensure primary path remains correct.
- [ ] Re-check async pump branch converter path after the ownership change.

Regression tests:

- [ ] Primary output accepts frame, branch output throws; all frames are
      disposed exactly once.
- [ ] Branch output removed during phase-2 conversion; branch frame is disposed.
- [ ] Async pump branch path still repacks on pump thread.
- [ ] Existing fanout/converter tests still pass.

Acceptance criteria:

- [ ] Every frame created by `SubmitPhased` has a clear owner or is disposed on
      every exception path.

### P1-2 Declarative VideoCompositor Thread Safety

Source finding: F7.

Affected files:

- `MediaFramework/Media/S.Media.Effects/VideoCompositor.cs`
- `MediaFramework/Media/S.Media.Effects/LayerHandle.cs`
- `MediaFramework/Media/S.Media.Effects/VideoCompositorSource.cs`
- `MediaFramework/Test/S.Media.Core.Tests/Video/VideoCompositorSourceTests.cs`
- `MediaFramework/Test/S.Media.Core.Tests/Video/VideoCompositorCrossFadeTests.cs`

Tasks:

- [ ] Decide whether `VideoCompositor` is intended to be thread-safe.
- [ ] If not thread-safe:
      - [ ] Add explicit XML docs stating single-threaded use.
      - [ ] Provide or point to a thread-safe runtime wrapper for live apps.
- [ ] If thread-safe:
      - [ ] Add a `_layersGate`.
      - [ ] Snapshot `_layers` before iterating in `TryReadNextFrame`.
      - [ ] Coordinate `AddLayer` and `RemoveLayer` with reads.
      - [ ] Coordinate `LayerHandle.Close` with `AdvanceTo` and
            `PullOneAndSubmit`.
      - [ ] Ensure layer source reads do not hold global compositor locks longer
            than necessary.
- [ ] In `VideoCompositorSource.Dispose`, coordinate with `_readGate` before
      closing slots and disposing the inner compositor.
- [ ] Ensure disposal does not deadlock if called from inside a compositor
      callback or output path.

Regression tests:

- [ ] Add/remove layer while `TryReadNextFrame` is running.
- [ ] Remove layer while `LayerHandle.AdvanceTo` is reading a source.
- [ ] Dispose source while `TryReadNextFrame` is inside a slow fake compositor.
- [ ] Crossfade tests still pass.
- [ ] Allocation regression for steady-state slot reads still passes.

Acceptance criteria:

- [ ] Live UI/cue thread layer mutation cannot corrupt a read or throw a
      collection-modified exception.

### P1-3 Soundboard Fire/Dispose Race

Source finding: F8.

Affected files:

- `MediaFramework/Media/S.Media.Core/Audio/Soundboard.cs`
- `MediaFramework/Media/S.Media.Core/Audio/AudioClipPlayer.cs`
- `MediaFramework/Test/S.Media.Core.Tests/Audio/SoundboardTests.cs`

Tasks:

- [ ] Make `Fire` and `Dispose` mutually safe.
- [ ] Recheck `_disposed` after `TryFire` before adding to `_live`.
- [ ] If dispose won the race, roll back the newly created voice/source/route.
- [ ] Ensure rollback removes choke registrations.
- [ ] Ensure `CueVoice.Completed` is raised at most once.
- [ ] Decide whether `Fire` should hold the soundboard lock through
      `TryFire`, or use a reservation/rollback pattern.
- [ ] Keep borrowed-router and owned-router behavior separate and tested.

Regression tests:

- [ ] `Fire` racing `Dispose` with borrowed router leaves no router sources.
- [ ] `Fire` racing `Dispose` with owned router does not throw after router
      disposal.
- [ ] Completed event fires once for voices created before dispose.
- [ ] Unknown cue and suppressed cue behavior remains unchanged.

Acceptance criteria:

- [ ] A disposed soundboard cannot create an untracked live voice.

### P1-4 PortAudio TryCreate Rollback

Source finding: F9.

Affected files:

- `MediaFramework/Media/S.Media.PortAudio/PortAudioPlaybackHost.cs`
- `MediaFramework/Test/S.Media.PortAudio.Tests/PortAudioPlaybackHostTests.cs`

Tasks:

- [ ] Hoist `AudioRouter`, `PortAudioOutput`, source ID, and output ID locals
      out of the `try` body.
- [ ] On failure after output creation, dispose the output if the host did not
      take ownership.
- [ ] On failure after router source/output registration, remove registered
      graph entries when possible.
- [ ] Dispose the router on failed host creation.
- [ ] Preserve existing success ownership semantics.
- [ ] Keep `TryWirePortAudioMainForRouter` rollback behavior intact.

Regression tests:

- [ ] Fake output creation succeeds but `AddOutput`/`Connect` fails; output and
      router are disposed or rolled back.
- [ ] Success path still returns a host and does not double-dispose output.
- [ ] Existing `TryWirePortAudioMainForRouter` rollback tests still pass.

Acceptance criteria:

- [ ] No native PortAudio output or router graph entry leaks from a failed
      `TryCreatePortAudioMain`.

### P1-5 Pause Flush Contract Cleanup

Source finding: F10.

Affected files:

- `MediaFramework/Media/S.Media.FFmpeg/MediaContainerSession.cs`
- `MediaFramework/Media/S.Media.Playback/MediaPlayer.cs`
- `Doc/MediaFramework-Architecture.md`
- `Doc/MediaFramework-PublicAPI.md`
- Relevant playback tests

Tasks:

- [ ] Decide the intended default for direct `MediaContainerSession.Pause()`.
- [ ] If default should flush:
      - [ ] Make direct session pause resolve `FlushCodecPipelines`.
      - [ ] Keep an explicit no-flush method for expert callers.
- [ ] If default should not flush:
      - [ ] Correct architecture and public API docs.
      - [ ] Make `MediaPlayer` difference explicit.
- [ ] Prefer a `PauseFlushPolicy` overload everywhere over nullable action
      defaults.
- [ ] Audit `SeekCoordinated` and `PauseSkippingSharedMuxFlush` names after the
      contract is clarified.

Regression tests:

- [ ] Direct `MediaContainerSession.Pause()` invokes or skips flush according
      to the chosen default.
- [ ] `PauseSkippingSharedMuxFlush()` always skips flush.
- [ ] `MediaPlayer.Pause()` default remains intentional and tested.
- [ ] Seek coordinated path still pauses, seeks, flushes/skips according to
      policy, and resumes only if previously running.

Acceptance criteria:

- [ ] Direct session and media-player pause behavior is predictable from API
      names and docs.

## P2 - Boundary Validation and API Consistency

### P2-1 VideoFrame Geometry Validation at Native Handoff

Source finding: F11.

Affected files:

- `MediaFramework/Media/S.Media.Core/Video/VideoFrame.Validation.cs`
- `MediaFramework/Media/S.Media.SDL3/SDL3VideoOutput.cs`
- `MediaFramework/Media/S.Media.SDL3/SDL3GLVideoOutput.cs`
- `MediaFramework/Media/S.Media.FFmpeg.Encode/Internal/FfmpegAvFrameFill.cs`
- `MediaFramework/Media/S.Media.FFmpeg/Video/VideoCpuFrameConverter.cs`
- Relevant tests in Core, FFmpeg, SDL/OpenGL where available

Tasks:

- [ ] Add explicit CPU geometry validation helpers.
- [ ] Validate minimum plane count per pixel format.
- [ ] Validate minimum stride per plane.
- [ ] Validate minimum byte length per plane for the frame height/chroma
      layout.
- [ ] Account for packed formats: BGRA/RGBA/ARGB/ABGR/RGB24/UYVY/YUY2.
- [ ] Account for planar formats: I420/YV12/YUVA, high-bit-depth planar.
- [ ] Account for semi-planar formats: NV12/NV21/NV16/P010/P016.
- [ ] Keep lifetime-only hardware frames supported where intended.
- [ ] Call validation before SDL upload.
- [ ] Call validation before FFmpeg encoder frame fill.
- [ ] Call validation before CPU conversion/readback paths that index planes.
- [ ] Decide whether validation runs every frame or only in debug/diagnostic
      mode for hot paths.

Regression tests:

- [ ] BGRA frame with short plane is rejected before native handoff.
- [ ] NV12 frame with one plane is rejected before native handoff.
- [ ] I420 frame with short chroma plane is rejected.
- [ ] UYVY/YUY2 short stride is rejected.
- [ ] Valid frames still pass.
- [ ] Hardware backing stub frames remain valid where no CPU read is attempted.

Acceptance criteria:

- [ ] Malformed CPU frames fail fast in managed code before native upload or
      encode.

### P2-2 FFmpeg Audio Encoder Submit Alignment

Source finding: F12.

Affected files:

- `MediaFramework/Media/S.Media.FFmpeg.Encode/Internal/FfmpegAudioEncoder.cs`
- `MediaFramework/Test/S.Media.FFmpeg.Encode.Tests`

Tasks:

- [ ] Validate `packedSamples.Length % Format.Channels == 0`.
- [ ] Throw a clear `ArgumentException` for misaligned interleaved samples.
- [ ] Confirm pending-buffer behavior still supports arbitrary frame counts
      that are channel-aligned but not encoder-frame-aligned.
- [ ] Document encoder buffering expectations if not already clear.

Regression tests:

- [ ] Misaligned span throws.
- [ ] Channel-aligned partial encoder frame buffers until enough samples arrive.
- [ ] Exact encoder frame still writes normally.

Acceptance criteria:

- [ ] `FfmpegAudioEncoder` honors the `IAudioOutput.Submit` channel alignment
      contract.

### P2-3 FFmpeg Video Encoder Submit Ownership

Source finding: F12.

Affected files:

- `MediaFramework/Media/S.Media.FFmpeg.Encode/Internal/FfmpegVideoEncoder.cs`
- `MediaFramework/Test/S.Media.FFmpeg.Encode.Tests`

Tasks:

- [ ] Decide whether `Submit` takes ownership immediately on call or only after
      configuration checks pass.
- [ ] If ownership is immediate, move the unconfigured check inside the
      disposal `try/finally`.
- [ ] If ownership is not immediate before configuration, document the
      exception to the `IVideoOutput.Submit` contract.
- [ ] Ensure converted working frames are still disposed exactly once.

Regression tests:

- [ ] Calling `Submit` before `Configure` disposes or preserves the input frame
      according to the chosen contract.
- [ ] Pixel-format conversion path disposes converted frame exactly once.
- [ ] Normal configured submit disposes caller frame exactly once.

Acceptance criteria:

- [ ] Encoder ownership behavior is explicit and consistent with docs/tests.

### P2-4 Process-Wide Mutable Defaults

Source finding: F13.

Affected files:

- `MediaFramework/Media/S.Media.Core/Diagnostics/MediaFrameworkPlugins.cs`
- `MediaFramework/Media/S.Media.Core/Audio/AudioRouter.cs`
- `MediaFramework/Media/S.Media.Effects/VideoCompositor.cs`
- Builder/session option types

Tasks:

- [~] Inventory process-wide mutable defaults.
- [~] Add scoped option objects for session/player/router construction.
      - `AudioRouter.AutoResampleDefault` covers the router auto-resample
        default.
      - `VideoCompositorOptions.AutoBackends` covers compositor backend
        selection.
      - `VideoRouterOptions` covers video fanout converter factory/probe
        selection.
- [~] Allow per-session plugin/factory overrides.
- [x] Keep current process-wide APIs as convenience defaults.
- [ ] Add test helpers to reset process-wide defaults between tests.
- [~] Document precedence: per-session override, then process default, then
      built-in default.

Regression tests:

- [~] Two sessions can use different converter/deinterlacer factories.
      - Covered for `VideoRouter` converter factory/probe options.
      - Deinterlacer/session-wide source factories remain open.
- [ ] Test changes to defaults do not leak after reset helper.
- [ ] Existing code using process-wide defaults still works.

Acceptance criteria:

- [ ] Embedded hosts can isolate framework configuration per session.

## P3 - Product-Facing Enhancements

These are not required to close the safety issues, but they would make the
framework simpler to use for real applications.

### P3-1 Unified Graph Builder

Tasks:

- [ ] Add common topology presets:
      - [ ] File to local display.
      - [ ] File to audio device.
      - [ ] File to local display plus NDI output.
      - [ ] File to preview plus program output.
      - [ ] NDI input to preview/program.
      - [ ] Cue compositor with audio/video outputs.
      - [ ] Soundboard to audio output.
- [ ] Make ownership explicit in builder results.
- [ ] Return a health/metrics object with all created routers/pumps/players.
- [ ] Support dry-run validation of formats and devices before opening native
      resources.

Acceptance criteria:

- [ ] A simple app can build common playback graphs without manually wiring
      every source, router, output, and clock.

### P3-2 Media Player Controller Facade

Tasks:

- [ ] Add explicit player states:
      `Stopped`, `Opening`, `Ready`, `Playing`, `Paused`, `Buffering`,
      `Faulted`, `Disposed`.
- [ ] Add open/close lifecycle separate from play/pause.
- [ ] Add track selection for audio/video/subtitle streams.
- [ ] Add subtitle/caption support plan and API.
- [ ] Add playback-rate support plan, including audio time-stretch.
- [ ] Add frame step and snapshot APIs.
- [ ] Add AB repeat and playlist/gapless primitives.
- [ ] Add scrub thumbnail and waveform extraction integration.
- [ ] Add device hotplug/rebind events.
- [ ] Add a stable health snapshot.

Acceptance criteria:

- [ ] VLC-like applications can use one facade instead of assembling low-level
      primitives directly.

### P3-3 Cue Graph and Show-Control Model

Tasks:

- [ ] Add cue IDs, numbering, labels, armed/disabled state.
- [ ] Add pre-wait, post-wait, follow-on, auto-continue, and grouped cues.
- [ ] Add stop targets and panic/stop-all semantics.
- [ ] Add cue preload/prewarm API.
- [ ] Add readiness checks before firing a cue.
- [ ] Add per-cue fault policies:
      - [ ] Stop show.
      - [ ] Skip cue.
      - [ ] Hold last frame.
      - [ ] Fade to black/silence.
      - [ ] Continue audio-only/video-only.
      - [ ] Route to fallback output.
- [ ] Add show-file serialization for cues, outputs, routes, and devices.
- [ ] Add cue execution logs for post-show diagnostics.

Acceptance criteria:

- [ ] QLab-like cue workflows can be modeled without app code inventing its
      own graph semantics from scratch.

### P3-4 OBS-Like Routing and Compositing

Tasks:

- [ ] Make output patch/matrix objects first-class.
- [ ] Add live route changes with drain or format-version semantics.
- [ ] Add source scene/layer model with thread-safe mutation.
- [ ] Add transitions for opacity, transform, crop, and routing.
- [ ] Add sync groups with one master clock.
- [ ] Add NDI input/output presets.
- [ ] Add preview/program separation.
- [ ] Add operator health metrics for each input/output.

Acceptance criteria:

- [ ] Live routing and compositing can be driven by scene changes without
      unsafe mutation of low-level objects.

### P3-5 Soundboard and Hardware Grid Layer

Tasks:

- [ ] Add decoded clip pool with explicit preload/unload.
- [ ] Add memory budget and eviction policy.
- [ ] Add pad modes:
      - [ ] One-shot.
      - [ ] Retrigger.
      - [ ] Latch/toggle.
      - [ ] Momentary.
      - [ ] Exclusive group.
      - [ ] Choke group.
      - [ ] Quantized launch.
      - [ ] Stop-on-note-off.
- [ ] Add per-voice controls:
      - [ ] Seek.
      - [ ] Fade-to gain.
      - [ ] Pitch/speed.
      - [ ] Pan.
      - [ ] Output override.
      - [ ] Remaining time.
- [ ] Add automatic background reaping.
- [ ] Add MIDI/OSC/keyboard/hardware-grid bindings.
- [ ] Add LED/button feedback state model.
- [ ] Add sample-accurate or router-tick-accurate scheduled fire.

Acceptance criteria:

- [ ] Hardware soundboard applications can map pads to sounds and control
      voices without directly manipulating router internals.

### P3-6 External Trigger Integrations

Tasks:

- [ ] Add framework-level trigger binding model for MIDI, OSC, keyboard, and
      app-defined hardware controls.
- [ ] Add MTC/LTC/SMPTE timecode sync plan.
- [ ] Add debouncing and retrigger policy.
- [ ] Add trigger action routing to media player, cue graph, soundboard, and
      output-routing actions.
- [ ] Add testable trigger simulation API.

Acceptance criteria:

- [ ] External control surfaces can be attached without host apps duplicating
      trigger parsing and dispatch.

## Verification and CI Checklist

Before closing a batch:

- [x] `dotnet build MediaFramework/Media/S.Media.Core/S.Media.Core.csproj --no-restore -v:m`
- [x] `dotnet build MediaFramework/Media/S.Media.FFmpeg/S.Media.FFmpeg.csproj --no-restore -v:m`
- [x] `dotnet build MediaFramework/Media/S.Media.Playback/S.Media.Playback.csproj --no-restore -v:m`
      - Covered by `S.Media.Playback.Tests` build.
- [x] `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --no-restore -v:m`
- [x] `dotnet test MediaFramework/Test/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj --no-restore -v:m`
- [x] `dotnet test MediaFramework/Test/S.Media.FFmpeg.Encode.Tests/S.Media.FFmpeg.Encode.Tests.csproj --no-restore -v:m`
- [x] `dotnet test MediaFramework/Test/S.Media.NDI.Tests/S.Media.NDI.Tests.csproj --no-restore -v:m`
- [x] `dotnet test MediaFramework/Test/S.Media.PortAudio.Tests/S.Media.PortAudio.Tests.csproj --no-restore -v:m`
- [ ] Run Windows-specific D3D11/Win32 shared-handle tests on Windows.
- [ ] Run SDL/GL smoke tests on a machine with display/GPU support.
- [ ] Run NDI ingest/egress smoke tests with real NDI runtime installed.
- [ ] Run PortAudio device smoke tests on the target OS/audio backend.

Known verification issue from the earlier review environment:

- [x] The earlier `Build FAILED. 0 Warning(s), 0 Error(s)` project-reference
      issue no longer reproduces after the permission/environment change.

## Suggested Batch Plan

### Batch 1 - Thread Safety and Fault Containment

- [~] P0-1 Shared-demux seek coordination.
- [~] P0-2 VideoPlayer decode fault boundary.
- [~] P0-3 Stop/pause cancellation restart safety.

Exit criteria:

- [!] Direct seek/read race tests pass.
- [!] VideoPlayer fault tests pass.
- [~] Cancelled stop cannot create duplicate live threads.

### Batch 2 - Blocked Dispose and Native Lifetime

- [~] P0-4 Audio output pump blocked-thread dispose.
- [~] P0-5 NDI receiver blocked-thread dispose.

Exit criteria:

- [!] Stuck audio output and stuck NDI capture tests pass.
- [~] Blocked workers cause terminal/leaked state, not use-after-dispose.

### Batch 3 - Ownership and Live Mutation

- [~] P1-1 VideoRouter branch submit leak.
- [~] P1-2 Declarative compositor thread safety.
- [~] P1-3 Soundboard fire/dispose race.

Exit criteria:

- [!] Frame ownership tests pass.
- [!] Layer mutation tests pass.
- [!] Soundboard dispose-race tests pass.

### Batch 4 - API Contracts and Rollback

- [~] P1-4 PortAudio rollback.
- [~] P1-5 Pause flush contract cleanup.
- [~] P2-2 FFmpeg audio encoder alignment.
- [~] P2-3 FFmpeg video encoder ownership.

Exit criteria:

- [~] Docs and public API behavior match.
- [~] Failure paths are rollback-safe.
- [!] Encoder contract tests pass.

### Batch 5 - Boundary Validation

- [~] P2-1 VideoFrame geometry validation.

Exit criteria:

- [!] Malformed CPU frame tests fail fast in managed code.
- [!] Native upload/encode tests still pass for valid frames.
- [ ] Performance impact is measured and acceptable.

### Batch 6 - Product API Work

- [ ] P2-4 Process-wide mutable defaults.
- [ ] P3-1 Unified graph builder.
- [ ] P3-2 Media player controller facade.
- [ ] P3-3 Cue graph and show-control model.
- [ ] P3-4 OBS-like routing and compositing.
- [ ] P3-5 Soundboard and hardware grid layer.
- [ ] P3-6 External trigger integrations.

Exit criteria:

- [ ] At least one VLC-style sample, one cue-player sample, and one soundboard
      sample use the new higher-level APIs without manual low-level graph
      wiring.
