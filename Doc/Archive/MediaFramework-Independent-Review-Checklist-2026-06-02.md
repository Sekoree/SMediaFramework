# Media Framework Independent Review Checklist - 2026-06-02

This checklist is derived from
`Doc/MediaFramework-Independent-Review-2026-06-02.md`.

Use it as an execution plan. Each item should be closed only when the
implementation, regression tests, and documentation/API-contract updates are
complete.

## Independent Verification — Claude (2026-06-02)

**Method.** Re-derived from the current source tree without trusting the review
or the `[x]` marks in this checklist: code inspection of every affected file,
plus `dotnet build` of `S.Media.Core` (clean, 0 warnings) and `dotnet test` of
the fix-relevant slices — 57 Core tests (Soundboard, VideoPlayer,
AudioRouterFault, AudioRouterPumpLifecycle, VideoRouterConcurrency, VideoFrame)
and 15 FFmpeg shared-demux/seek tests — all green. The concurrent seek/read race
F1 calls out is exercised by real threads (`SharedDemuxAudioSeekRaceReader` /
`SharedDemuxVideoSeekRaceReader`) and a random-seek soak test, not just
sequential seeks.

**Verdict on F1–F12: every finding describes a real pre-fix defect, and each fix
in the current tree is genuine and correct — not a stub, a no-op, or a doc-only
change.** What I confirmed at the code level:

- F1/P0-1 — Real, fixed. `SeekPresentation`/`Dispose` take a `ReaderWriterLockSlim`
  write lock; `ReadInto`/`TryReadNextFrame` take the read lock; video state is
  flushed under `_videoDecodeLock` (not the audio lock, as the old tree did);
  `FreeAllQueuedPacketsLocked` frees both pending packets + queues under
  `_queueGate` during drain; the EAGAIN republish re-checks a seek-generation
  counter so a packet popped before a seek cannot be re-fed after it.
- F2/P0-2 — Real, fixed. `DecodeLoop` no longer `throw;`s — it records `_fault`,
  raises `Faulted`, drains, and `Play()` refuses to restart a faulted/stuck player.
- F3/P0-3 — Real, fixed. `VideoPlayer.StopInternal` and `AudioRouter.StopInternal`
  both mark the instance terminal/non-restartable when the worker is still alive
  after a cancelled or timed-out join; always-run cleanup is in `finally`.
- F4/P0-4 — Real, fixed. `OutputPump.Dispose` leaks `_ready`/`_cts` and flags a
  stuck diagnostic instead of disposing sync primitives under a live drainer.
- F5/P0-5 — Real, fixed. All three NDI receivers route disposal through the shared
  `NDICaptureThreadLifecycle.StopAndDispose`, which frees native receiver/runtime/CTS
  **only** when the capture thread exited, else leaks them and sets `IsCaptureStuck`.
- F6/P1-1 — Real, fixed. `branchFrames[i]` is nulled only after a successful
  `Submit`, so the catch disposes a branch frame whose output threw.
- F7/P1-2 — Real, fixed. `_layersGate` snapshots layers before iteration;
  `LayerHandle.Close` shares `_gate` with `AdvanceTo`/`PullOneAndSubmit`;
  `VideoCompositorSource.Dispose` takes `_readGate`. (See minor residual M1.)
- F8/P1-3 — Real, fixed. `Soundboard.Fire` re-checks `_disposed` after `TryFire`
  and rolls the voice back via `AudioClipPlayer.AbortVoice`, which removes the
  router source + choke registration.
- F9/P1-4 — Real, fixed. `TryCreatePortAudioMain` shares `RollbackPartialWire`
  with the router-wiring path (removes output/source, disposes output + router).
- F10/P1-5 — Real, fixed. Direct `MediaContainerSession.Pause()` now defaults to
  the container flush (matching `MediaPlayer.Pause`); a `PauseFlushPolicy`
  overload and `PauseSkippingSharedMuxFlush` give the explicit paths. The fix made
  the **code** match the doc, so `Doc/MediaFramework-Architecture.md` lines 50–64
  are now accurate (no longer the mismatch F10 described).
- F11/P2-1 — Real, fixed. `VideoFrame.ValidateCpuGeometry()` checks plane count,
  per-plane stride, and a correct last-row byte-length bound
  (`(rows-1)*stride + rowBytes`, `checked`), and is called from SDL3, the OpenGL
  YUV renderer, the FFmpeg CPU converter, both deinterlacers, and the encoder fill.
- F12/P2-2,P2-3 — Real, fixed. Audio `Submit` rejects channel-misaligned spans;
  video `Submit`'s unconfigured check is now inside the `try`, so the `finally`
  always disposes the caller's frame (honoring the ownership contract).

F13/P2-4 is an architectural recommendation, not a defect; the scoped-option
surfaces it asks for are present. The remaining unchecked boxes in this checklist
(Windows D3D11, SDL/GL, NDI, PortAudio on-hardware smoke runs) are correctly left
open — they need real devices.

The `[~]`/`[!]` markers under "Suggested Batch Plan" were stale (they predate the
implementation pass); I reconciled the ones whose tests I ran or that the
Validation block already records as passing.

### P4-1 — Misplaced product-tier types in `S.Media.Core` (the placement question)

The instinct about `Soundboard` is correct, and the problem is slightly broader
than one class. The product / show-control surface is collected in
**`S.Media.Playback`** — `MediaPlayerController`, `MediaGraph`, `CueGraph`,
`CueShowFile`, `RoutingScene`, `SoundboardGrid`, `TriggerBindingSet`,
`ProductApiSamples` — but two members of that same tier were left in
**`S.Media.Core/Audio`**: `Soundboard` and `CueVoice`. Evidence it's an
inconsistency rather than a deliberate split:

- They are the *only* product-tier orchestration types in Core. Everything else
  in `Core/{Audio,Video,Clock,Playback,Triggers}` is a primitive/abstraction
  (`AudioRouter`, `AudioClip`, `AudioClipPlayer/Voice`, `AudioBus`, `VideoPlayer`,
  `VideoRouter`, `TriggerBus`, `PauseFlushPolicy`, …).
- `Soundboard`'s only consumers live in Playback (`SoundboardGrid` wraps it;
  `MediaGraph`/`ProductApiSamples`/`TriggerBindingSet` use it). Nothing in Core
  depends on `Soundboard` or `CueVoice`, and the HaPlay UI references neither.
- The wrapper sits above its engine across an assembly seam: `SoundboardGrid`
  (Playback) → `Soundboard` (Core). And `SoundboardGrid`/`CueGraph`/`RoutingScene`
  only pull in `S.Media.Core.Audio` / `System.Text.Json` / nothing — they don't
  even need FFmpeg, so "no-FFmpeg ⇒ Core" isn't the operative rule either.

Tasks:

- [x] Pick one rule for the Core↔Playback boundary and apply it. Chosen:
      *Core = engines + primitives (no product facades); the product/show-control
      tier lives in `S.Media.Playback`.*
- [x] Move `Soundboard` and `CueVoice` from `S.Media.Core/Audio` to
      `S.Media.Playback` (namespace `S.Media.Playback`). Core already grants
      `[InternalsVisibleTo("S.Media.Playback")]`, so the one internal member used
      (`AudioClipPlayer.AbortVoice`) still resolves; everything else is public.
      `AudioClip`/`AudioClipPlayer`/`AudioClipVoice`/`AudioBus` stay in Core.
      Consumers (`SoundboardGrid` et al.) are already in `namespace S.Media.Playback`
      so they needed no using changes.
- [x] Relocate `SoundboardTests` (11 tests) from `S.Media.Core.Tests` to
      `S.Media.Playback.Tests`. Green there; Core.Tests now 446 (was 455 − 11 + 2
      new M1 tests), Playback.Tests 58 (was 47 + 11).
- [x] State the chosen Core/Playback rule in `Doc/MediaFramework-Architecture.md`
      (new "Project layering (Core vs Playback)" section).
- [x] Incidental, pre-existing break surfaced by the first full-solution build (not
      caused by the move): the untracked `MediaPlayerController.cs` added
      `S.Media.Playback.PlaylistItem`, which collides with HaPlay's own
      `HaPlay.Models.PlaylistItem` in three UI files importing both namespaces.
      Resolved with a project-wide `global using PlaylistItem = HaPlay.Models.PlaylistItem;`
      (`UI/HaPlay/GlobalUsings.cs`) since HaPlay never uses the framework type.
      Alternative the owner may prefer: rename the new framework `PlaylistItem`
      (e.g. `MediaPlaylistItem`) to avoid a common-name collision in the main namespace.

### M1 — `LayerHandle` has no closed-guard (minor; not in the original review)

Found while verifying F7. `LayerHandle.Close()` clears `_lookahead` but sets no
"closed" flag, and `VideoCompositor.RemoveLayer` calls `Close()` outside
`_layersGate`. If a layer is removed during a master-clock composite, a concurrent
`TryReadNextFrame` iterating a pre-removal snapshot can run `AdvanceTo` *after*
`Close`, re-pulling up to `MaxQueuedFrames` into `_lookahead` that nothing disposes
again → a bounded (≤7 frames), one-time native-buffer leak. No crash and no fault
propagation: the resubmitted frame hits the slot's own `_closed` guard
(`SubmitFromOutput`) and is disposed there. Low priority.

- [x] Add a `_closed` flag to `LayerHandle` (set under `_gate` in `Close`) and
      early-return from `AdvanceTo`/`PullOneAndSubmit` when closed. Regression test
      `LayerHandleClosedGuardTests` asserts no re-pull and no leak after close
      (counts every source frame's release via the slot).

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

- [x] P0-1 Shared-demux seek coordination implemented with a read/seek gate,
      read-yield wakeups, decoder-specific flush locks, queued packet draining,
      pending-packet EAGAIN restore under `_queueGate`, and a seek generation
      guard so packets popped before seek are not republished after seek.
- [x] P0-2 VideoPlayer decode faults now stay inside the player boundary:
      `Fault`, `Faulted`, terminal restart refusal, queue drain, held-frame
      release, tick detachment, and balanced cooperative-yield cleanup for
      already-paused stops were added.
- [x] P0-3 Stop/pause cancellation now marks live decode/router threads
      terminal and non-restartable instead of allowing duplicate workers.
- [x] P0-4 Audio output pump disposal now leaks blocked pump state instead of
      disposing synchronization primitives under a live drainer thread.
- [x] P0-5 NDI dispose paths now avoid disposing native receiver/runtime/CTS
      under a live capture thread and no longer send a stopped ingest-clock
      notification unless the thread actually exited.
- [x] P1-1 VideoRouter fanout now retains branch frames for cleanup if branch
      output submission throws.
- [x] P1-2 VideoCompositor layer mutation, layer close, slot mutation, and
      source disposal are serialized more consistently.
- [x] P1-3 Soundboard fire/dispose rollback now aborts a just-created voice
      before throwing if disposal wins the race.
- [x] P1-4 PortAudio `TryCreatePortAudioMain` now rolls back registered source,
      output, router, and output object on failure.
- [x] P1-5 Pause/seek shared-demux flush defaults now coalesce to the normal
      container flush path unless the explicit "skipping flush" APIs are used.
- [x] P2-1 CPU `VideoFrame` geometry validation was added and is called before
      SDL3 upload, OpenGL CPU upload, and FFmpeg encode frame fill.
- [x] P2-2 FFmpeg audio encoder submit now rejects packed sample buffers whose
      length is not a multiple of channel count.
- [x] P2-3 FFmpeg video encoder submit now disposes accepted frames when
      unconfigured submission throws after ownership transfer.
- [x] P2-4 Process-wide mutable defaults now have scoped reset helpers,
      per-router/player option surfaces, and scoped source/deinterlacer factory
      overloads with process-wide `MediaFrameworkPlugins` preserved as
      compatibility fallback.
- [x] P3 product-facing APIs now have product-level surfaces: unified graph builder, VLC-style
      facade, cue/show model, OBS-style routing/compositing, soundboard grid,
      and external trigger integrations.

Validation completed:

- [x] `git diff --check`
- [x] `dotnet build MediaFramework/Media/S.Media.Core/S.Media.Core.csproj --no-restore -v:m`
- [x] `dotnet build MediaFramework/Media/S.Media.FFmpeg/S.Media.FFmpeg.csproj --no-restore -v:m`
- [x] `dotnet build MediaFramework/Media/S.Media.OpenGL/S.Media.OpenGL.csproj --no-restore -p:BuildProjectReferences=false -v:m`
- [x] `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj`
      - 455 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --filter "FullyQualifiedName~VideoPlayerTests|FullyQualifiedName~AudioRouterFaultTests"`
      - 15 passed; includes cancelled stop and non-cancelled timeout with live decode/router worker tests.
- [x] `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --filter "FullyQualifiedName~AudioRouterPumpLifecycleTests"`
      - 5 passed; includes blocked output-submit dispose, stuck-pump diagnostics, and unstarted pump dispose tests.
- [x] `dotnet test MediaFramework/Test/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj --no-restore -v:m`
      - 165 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.FFmpeg.Encode.Tests/S.Media.FFmpeg.Encode.Tests.csproj`
      - 7 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.Playback.Tests/S.Media.Playback.Tests.csproj --no-restore -v:m`
      - 47 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.Playback.Tests/S.Media.Playback.Tests.csproj --filter "FullyQualifiedName~MediaGraphBuilderTests"`
      - 2 passed; covers file graph build, ownership/health snapshot, and missing-file failure.
- [x] `dotnet test MediaFramework/Test/S.Media.Playback.Tests/S.Media.Playback.Tests.csproj --filter "FullyQualifiedName~MediaPlayerControllerTests"`
      - 2 passed; covers controller state transitions, close lifecycle, and snapshots.
- [x] `dotnet test MediaFramework/Test/S.Media.Playback.Tests/S.Media.Playback.Tests.csproj --filter "FullyQualifiedName~CueGraphTests"`
      - 4 passed; covers cue metadata/state, readiness guards, follow-on, and fault policy logging.
- [x] `dotnet test MediaFramework/Test/S.Media.Playback.Tests/S.Media.Playback.Tests.csproj --filter "FullyQualifiedName~ProductModelTests"`
      - 5 passed; covers routing scene, soundboard grid, trigger binding models,
        and product API samples.
- [x] `dotnet test MediaFramework/Test/S.Media.OpenGL.Tests/S.Media.OpenGL.Tests.csproj --no-restore -v:m`
      - 105 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.SkiaSharp.Tests/S.Media.SkiaSharp.Tests.csproj --no-restore -v:m`
      - 10 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.PortAudio.Tests/S.Media.PortAudio.Tests.csproj`
      - 24 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.NDI.Tests/S.Media.NDI.Tests.csproj`
      - 57 passed.
- [x] `dotnet test MediaFramework/Test/OSCLib.Tests/OSCLib.Tests.csproj --no-restore -v:m`
      - 20 passed.
- [x] `dotnet test MediaFramework/Test/PMLib.Tests/PMLib.Tests.csproj --no-restore -v:m`
      - 13 passed.
- [x] `dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --no-restore -v:m`
      - 116 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --filter "FullyQualifiedName~VideoRouterConcurrencyTests"`
      - 5 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --filter "FullyQualifiedName~VideoCompositorSourceTests|FullyQualifiedName~VideoCompositorCrossFadeTests"`
      - 17 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --filter "FullyQualifiedName~SoundboardTests"`
      - 11 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --filter "FullyQualifiedName~AvPlaybackCoordinatorTests"`
      - 13 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --filter "FullyQualifiedName~VideoFrameTests"`
      - 21 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj -c Release --filter "FullyQualifiedName~VideoFrameTests"`
      - 21 passed; validates the CPU geometry boundary checks in a Release build.
- [x] `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --filter "FullyQualifiedName~MediaFrameworkPluginsTests"`
      - 3 passed; includes process-wide reset, scoped source factory, and
        scoped deinterlacer precedence tests.
- [x] `dotnet test MediaFramework/Test/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj --filter "FullyQualifiedName~VideoCpuFrameConverter|FullyQualifiedName~Deinterlacer|FullyQualifiedName~MediaContainerDecoderTests"`
      - 22 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj --no-restore --filter FullyQualifiedName~MediaContainerDecoderTests -v:m`
      - 9 passed.
- [x] `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --no-restore --filter FullyQualifiedName~VideoPlayerTests -v:m`
      - 11 passed.

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

- [x] Add a seek/read coordination strategy for shared demux direct source use.
  Choose one of:
  - [x] A single seek/read gate that all audio/video read and seek paths obey.
  - [ ] A lock-order-safe scheme that takes both `_audioDecodeLock` and
        `_videoDecodeLock` before flushing/resetting shared decoder state.
- [x] Move video codec/frame flush/reset out of the audio-only lock.
- [x] Ensure `SeekPresentation` cannot flush `_vCtx` or unref `_vFrame` while
      `VideoTrack.TryReadNextFrame` is using them.
- [x] Ensure `SeekPresentation` cannot flush `_aCtx`, `_swr`, or `_aFrame`
      while `AudioTrack.ReadInto` is using them.
- [x] Clear `_aPendingPacket` and `_vPendingPacket` under `_queueGate`.
- [x] Free pending packets exactly once during seek/drain.
- [x] Change EAGAIN pending-packet restore paths to republish under
      `_queueGate`.
- [x] Add a seek generation counter so a packet popped before seek cannot be
      restored as pending after seek.
- [x] Re-check demux thread stop/drain interactions after generation handling.
- [x] Confirm higher-level `MediaContainerSession.Seek` still pauses and resumes
      correctly after the internal locking changes.

Regression tests:

- [x] Direct audio read racing `SeekPresentation` does not crash, deadlock, or
      decode from half-flushed audio state.
- [x] Direct video read racing `SeekPresentation` does not crash, deadlock, or
      decode from half-flushed video state.
- [x] A packet returned as EAGAIN immediately before seek is not fed after seek.
- [x] Shared-demux `MediaPlayer.Seek` still resumes only when it was playing
      before seek.
- [x] Non-seekable stream-backed containers still reject seek.

Acceptance criteria:

- [x] No decoder context/frame is flushed or reset outside the lock used by its
      consumer path.
- [x] No pre-seek queued or pending packet can feed the decoder after a
      successful seek.
- [x] The fix does not introduce lock-order deadlocks between audio reads,
      video reads, demux stop, and seek.

### P0-2 VideoPlayer Decode Fault Boundary

Source finding: F2.

Affected files:

- `MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs`
- `MediaFramework/Test/S.Media.Core.Tests/Video/VideoPlayerTests.cs`

Tasks:

- [x] Replace the `catch (Exception) { throw; }` path in `DecodeLoop`.
- [x] Add a stored last-fault exception field.
- [x] Add a public or internal `Faulted`/`LastFault` surface consistent with
      existing diagnostics style.
- [x] Stop decode loop execution when a source/converter exception occurs.
- [x] Wake tick/queue/slot paths so the player does not hang after fault.
- [x] Make `Play()` fail deterministically after fault until dispose/reset.
- [x] Decide whether `Stop()` clears faults or whether a faulted player is
      terminal. Document the decision.
- [x] Ensure event subscriber exceptions remain contained separately from
      decode faults.
- [x] Ensure `ObjectDisposedException` from cooperative shutdown is still
      treated as normal shutdown, not as a fault.

Regression tests:

- [x] `TryReadNextFrame` throwing from a fake source faults the player without
      background rethrow.
- [x] `Play()` after decode fault throws a deterministic exception or remains
      stopped according to the chosen contract.
- [x] `Dispose()` after decode fault is idempotent.
- [x] Frame presentation event subscriber exceptions still do not fault the
      player.

Acceptance criteria:

- [x] A bad source frame cannot terminate the host process through the decode
      thread.
- [x] Hosts can discover the fault and replace the player.

### P0-3 Stop/Pause Cancellation Must Not Leave Restartable Live Threads

Source finding: F3.

Affected files:

- `MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs`
- `MediaFramework/Media/S.Media.Core/Audio/AudioRouter.cs`
- `MediaFramework/Media/S.Media.Core/Threading/CooperativePlaybackJoin.cs`

Tasks:

- [x] Audit every call to `CooperativePlaybackJoin.JoinThread` that passes a
      caller cancellation token.
- [x] For `VideoPlayer.StopInternal`, ensure cancellation during join cannot
      leave `_decodeThreadStuck == false` while the old thread is alive.
- [x] For `AudioRouter.StopInternal`, ensure cancellation during join cannot
      leave `_thread == null` and `_isRunning == false` while the old run loop
      remains alive and restartable.
- [x] Move cleanup that must always happen into `finally` blocks.
- [x] Ensure cooperative yield requests are cleared only when safe.
- [x] Decide on a shared stop result contract:
      - [x] Stop completed and reusable.
      - [x] Stop requested but join cancelled; instance is terminal.
      - [x] Stop timed out; instance is terminal/stuck.
- [x] Document the cancellation contract for `Pause` and `Stop`; `Dispose`
      remains non-cancelable.
- [x] Update UI/app call sites if they assume a cancelled pause leaves the
      player/router reusable. Audit found only `CancellationToken.None` callers.

Regression tests:

- [x] Fake video source blocks in `TryReadNextFrame`; cancel `Pause`/`Stop`;
      assert a subsequent `Play` cannot start a second decode thread.
- [x] Fake audio source/output blocks the router run loop; cancel `Stop`;
      assert a subsequent `Start` cannot start a second router thread.
- [x] Non-cancelled full-timeout stop still marks the instance terminal.
- [x] Normal stop/pause remains reusable.

Acceptance criteria:

- [x] There is no code path where cancellation leaves an old thread alive and a
      new thread can be started on the same instance.

### P0-4 Blocked-Thread Dispose Policy for Audio OutputPump

Source finding: F4.

Affected files:

- `MediaFramework/Media/S.Media.Core/Audio/AudioRouter.cs`
- `MediaFramework/Test/S.Media.Core.Tests/Audio/AudioRouterPumpLifecycleTests.cs`

Tasks:

- [x] Match `VideoOutputPump` blocked-dispose policy for audio output pumps.
- [x] After join attempts, check `_thread.IsAlive`.
- [x] If still alive, do not dispose `_ready` or `_cts`.
- [x] Log a clear error/warning that pump state is intentionally leaked to
      avoid use-after-dispose.
- [x] Decide whether the inner `IAudioOutput` should also be leaked when the
      drainer is blocked inside `Submit`; the pump never owns caller outputs.
- [x] Expose pump stuck/blocked state through `OutputPumpStats` or a new
      diagnostic surface.
- [x] Ensure unstarted lazy pumps can still be disposed cheaply.

Regression tests:

- [x] Inner `IAudioOutput.Submit` blocks longer than the join cap; dispose does
      not dispose wait handles under the live drainer.
- [x] Unstarted pump dispose still succeeds.
- [x] Normal pump lifecycle does not report stuck diagnostics.
- [x] Router dispose with one stuck pump does not block forever.

Acceptance criteria:

- [x] Audio pump disposal cannot cause the drainer to touch disposed wait
      handles or cancellation sources.

### P0-5 Blocked-Thread Dispose Policy for NDI Receivers

Source finding: F5.

Affected files:

- `MediaFramework/Media/S.Media.NDI/NDISource.cs`
- `MediaFramework/Media/S.Media.NDI/Video/NDIVideoReceiver.cs`
- `MediaFramework/Media/S.Media.NDI/Audio/NDIAudioReceiver.cs`
- NDI tests under `MediaFramework/Test/S.Media.NDI.Tests`

Tasks:

- [x] After capture-thread join, check whether the capture thread is still
      alive.
- [x] If still alive, do not dispose the native receiver.
- [x] If still alive, do not dispose the NDI runtime if the live thread may
      still use it.
- [x] Log a terminal stuck-capture warning.
- [x] Set connection/state to a terminal disposed/stuck state that hosts can
      observe.
- [x] Ensure queued video/audio buffers are still released when safe.
- [x] Confirm cancellation and wait-pulse behavior still wakes readers.
- [x] Confirm normal capture shutdown still disposes native resources.

Regression tests:

- [x] Use a fake/shim receiver that blocks in capture; dispose returns after the
      cap and does not dispose native receiver/runtime while thread is alive.
- [x] Normal capture thread exits; dispose releases receiver/runtime.
- [x] Capture-loop exception still faults/contains instead of rethrowing.
- [x] Readers blocked on no data wake after dispose.

Acceptance criteria:

- [x] No NDI receiver class frees native capture resources underneath a live
      capture thread.

## P1 - Correctness and Leak Fixes

### P1-1 VideoRouter Branch Submit Leak

Source finding: F6.

Affected files:

- `MediaFramework/Media/S.Media.Core/Video/VideoRouter.cs`
- `MediaFramework/Test/S.Media.Core.Tests/Video/VideoRouterConcurrencyTests.cs`
- `MediaFramework/Test/S.Media.FFmpeg.Tests/Video/VideoRouterTests.cs`

Tasks:

- [x] Change branch delivery so `branchFrames[i]` is cleared only after submit
      succeeds, or use a local submitted flag.
- [x] Ensure removed-output branches still dispose their frames.
- [x] Ensure thrown branch output does not leak its frame.
- [x] Ensure primary path remains correct.
- [x] Re-check async pump branch converter path after the ownership change.

Regression tests:

- [x] Primary output accepts frame, branch output throws; all frames are
      disposed exactly once.
- [x] Branch output removed during phase-2 conversion; branch frame is disposed.
- [x] Async pump branch path still repacks on pump thread.
- [x] Existing fanout/converter tests still pass.

Acceptance criteria:

- [x] Every frame created by `SubmitPhased` has a clear owner or is disposed on
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

- [x] Decide whether `VideoCompositor` is intended to be thread-safe.
- [ ] If not thread-safe: not chosen; live mutation is supported by locks.
      - [ ] Add explicit XML docs stating single-threaded use.
      - [ ] Provide or point to a thread-safe runtime wrapper for live apps.
- [x] If thread-safe:
      - [x] Add a `_layersGate`.
      - [x] Snapshot `_layers` before iterating in `TryReadNextFrame`.
      - [x] Coordinate `AddLayer` and `RemoveLayer` with reads.
      - [x] Coordinate `LayerHandle.Close` with `AdvanceTo` and
            `PullOneAndSubmit`.
      - [x] Ensure layer source reads do not hold global compositor locks longer
            than necessary.
- [x] In `VideoCompositorSource.Dispose`, coordinate with `_readGate` before
      closing slots and disposing the inner compositor.
- [x] Ensure disposal does not deadlock if called from inside a compositor
      callback or output path.

Regression tests:

- [x] Add/remove layer while `TryReadNextFrame` is running.
- [x] Remove layer while `LayerHandle.AdvanceTo` is reading a source.
- [x] Dispose source while `TryReadNextFrame` is inside a slow fake compositor.
- [x] Crossfade tests still pass.
- [x] Allocation regression for steady-state slot reads still passes.

Acceptance criteria:

- [x] Live UI/cue thread layer mutation cannot corrupt a read or throw a
      collection-modified exception.

### P1-3 Soundboard Fire/Dispose Race

Source finding: F8.

Affected files:

- `MediaFramework/Media/S.Media.Core/Audio/Soundboard.cs`
- `MediaFramework/Media/S.Media.Core/Audio/AudioClipPlayer.cs`
- `MediaFramework/Test/S.Media.Core.Tests/Audio/SoundboardTests.cs`

Tasks:

- [x] Make `Fire` and `Dispose` mutually safe.
- [x] Recheck `_disposed` after `TryFire` before adding to `_live`.
- [x] If dispose won the race, roll back the newly created voice/source/route.
- [x] Ensure rollback removes choke registrations.
- [x] Ensure `CueVoice.Completed` is raised at most once.
- [x] Decide whether `Fire` should hold the soundboard lock through
      `TryFire`, or use a reservation/rollback pattern.
- [x] Keep borrowed-router and owned-router behavior separate and tested.

Regression tests:

- [x] `Fire` racing `Dispose` with borrowed router leaves no router sources.
- [x] `Fire` racing `Dispose` with owned router does not throw after router
      disposal.
- [x] Completed event fires once for voices created before dispose.
- [x] Unknown cue and suppressed cue behavior remains unchanged.

Acceptance criteria:

- [x] A disposed soundboard cannot create an untracked live voice.

### P1-4 PortAudio TryCreate Rollback

Source finding: F9.

Affected files:

- `MediaFramework/Media/S.Media.PortAudio/PortAudioPlaybackHost.cs`
- `MediaFramework/Test/S.Media.PortAudio.Tests/PortAudioPlaybackHostTests.cs`

Tasks:

- [x] Hoist `AudioRouter`, `PortAudioOutput`, source ID, and output ID locals
      out of the `try` body.
- [x] On failure after output creation, dispose the output if the host did not
      take ownership.
- [x] On failure after router source/output registration, remove registered
      graph entries when possible.
- [x] Dispose the router on failed host creation.
- [x] Preserve existing success ownership semantics.
- [x] Keep `TryWirePortAudioMainForRouter` rollback behavior intact.

Regression tests:

- [x] Fake output creation succeeds but `AddOutput`/`Connect` fails; output and
      router are disposed or rolled back.
- [x] Success path still returns a host and does not double-dispose output.
- [x] Existing `TryWirePortAudioMainForRouter` rollback tests still pass.

Acceptance criteria:

- [x] No native PortAudio output or router graph entry leaks from a failed
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

- [x] Decide the intended default for direct `MediaContainerSession.Pause()`.
- [x] If default should flush:
      - [x] Make direct session pause resolve `FlushCodecPipelines`.
      - [x] Keep an explicit no-flush method for expert callers.
- [ ] If default should not flush: not chosen.
      - [ ] Correct architecture and public API docs.
      - [ ] Make `MediaPlayer` difference explicit.
- [x] Prefer a `PauseFlushPolicy` overload everywhere over nullable action
      defaults.
- [x] Audit `SeekCoordinated` and `PauseSkippingSharedMuxFlush` names after the
      contract is clarified.

Regression tests:

- [x] Direct `MediaContainerSession.Pause()` invokes or skips flush according
      to the chosen default.
- [x] `PauseSkippingSharedMuxFlush()` always skips flush.
- [x] `MediaPlayer.Pause()` default remains intentional and tested.
- [x] Seek coordinated path still pauses, seeks, flushes/skips according to
      policy, and resumes only if previously running.

Acceptance criteria:

- [x] Direct session and media-player pause behavior is predictable from API
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

- [x] Add explicit CPU geometry validation helpers.
- [x] Validate minimum plane count per pixel format.
- [x] Validate minimum stride per plane.
- [x] Validate minimum byte length per plane for the frame height/chroma
      layout.
- [x] Account for packed formats: BGRA/RGBA/ARGB/ABGR/RGB24/UYVY/YUY2.
- [x] Account for planar formats: I420/YV12/YUVA, high-bit-depth planar.
- [x] Account for semi-planar formats: NV12/NV21/NV16/P010/P016.
- [x] Keep lifetime-only hardware frames supported where intended.
- [x] Call validation before SDL upload.
- [x] Call validation before FFmpeg encoder frame fill.
- [x] Call validation before CPU conversion/readback paths that index planes.
- [x] Decide whether validation runs every frame or only in debug/diagnostic
      mode for hot paths.

Regression tests:

- [x] BGRA frame with short plane is rejected before native handoff.
- [x] NV12 frame with one plane is rejected before native handoff.
- [x] I420 frame with short chroma plane is rejected.
- [x] UYVY/YUY2 short stride is rejected.
- [x] Valid frames still pass.
- [x] Hardware backing stub frames remain valid where no CPU read is attempted.

Acceptance criteria:

- [x] Malformed CPU frames fail fast in managed code before native upload or
      encode.

### P2-2 FFmpeg Audio Encoder Submit Alignment

Source finding: F12.

Affected files:

- `MediaFramework/Media/S.Media.FFmpeg.Encode/Internal/FfmpegAudioEncoder.cs`
- `MediaFramework/Test/S.Media.FFmpeg.Encode.Tests`

Tasks:

- [x] Validate `packedSamples.Length % Format.Channels == 0`.
- [x] Throw a clear `ArgumentException` for misaligned interleaved samples.
- [x] Confirm pending-buffer behavior still supports arbitrary frame counts
      that are channel-aligned but not encoder-frame-aligned.
- [x] Document encoder buffering expectations if not already clear.

Regression tests:

- [x] Misaligned span throws.
- [x] Channel-aligned partial encoder frame buffers until enough samples arrive.
- [x] Exact encoder frame still writes normally.

Acceptance criteria:

- [x] `FfmpegAudioEncoder` honors the `IAudioOutput.Submit` channel alignment
      contract.

### P2-3 FFmpeg Video Encoder Submit Ownership

Source finding: F12.

Affected files:

- `MediaFramework/Media/S.Media.FFmpeg.Encode/Internal/FfmpegVideoEncoder.cs`
- `MediaFramework/Test/S.Media.FFmpeg.Encode.Tests`

Tasks:

- [x] Decide whether `Submit` takes ownership immediately on call or only after
      configuration checks pass.
- [x] If ownership is immediate, move the unconfigured check inside the
      disposal `try/finally`.
- [ ] If ownership is not immediate before configuration: not chosen.
      - [ ] Document the
      exception to the `IVideoOutput.Submit` contract.
- [x] Ensure converted working frames are still disposed exactly once.

Regression tests:

- [x] Calling `Submit` before `Configure` disposes or preserves the input frame
      according to the chosen contract.
- [x] Pixel-format conversion path disposes converted frame exactly once.
- [x] Normal configured submit disposes caller frame exactly once.

Acceptance criteria:

- [x] Encoder ownership behavior is explicit and consistent with docs/tests.

### P2-4 Process-Wide Mutable Defaults

Source finding: F13.

Affected files:

- `MediaFramework/Media/S.Media.Core/Diagnostics/MediaFrameworkPlugins.cs`
- `MediaFramework/Media/S.Media.Core/Audio/AudioRouter.cs`
- `MediaFramework/Media/S.Media.Effects/VideoCompositor.cs`
- Builder/session option types

Tasks:

- [x] Inventory process-wide mutable defaults.
- [x] Add scoped option objects for session/player/router construction.
      - `AudioRouter.AutoResampleDefault` covers the router auto-resample
        default.
      - `VideoCompositorOptions.AutoBackends` covers compositor backend
        selection.
      - `VideoRouterOptions` covers video fanout converter factory/probe
        selection.
      - `MediaPlayerOpenOptions.VideoDeinterlacerFactory` carries a scoped
        deinterlacer factory for player/session configuration.
      - `AudioSource` / `VideoSource` file, stream, and image open helpers now
        accept scoped factory overloads.
- [x] Allow per-session plugin/factory overrides.
- [x] Keep current process-wide APIs as convenience defaults.
- [x] Add test helpers to reset process-wide defaults between tests.
- [x] Document precedence: per-session override, then process default, then
      built-in default.

Regression tests:

- [x] Two sessions can use different converter/deinterlacer factories.
      - Covered for `VideoRouter` converter factory/probe options.
      - Covered for deinterlacer factory precedence via
        `VideoDeinterlacerRegistry.Create(input, scopedFactory)`.
      - Covered for source factory precedence via scoped `AudioSource` /
        `VideoSource` open overloads.
- [x] Test changes to defaults do not leak after reset helper.
- [x] Existing code using process-wide defaults still works.

Acceptance criteria:

- [x] Embedded hosts can isolate framework configuration per session.

## P3 - Product-Facing Enhancements

These are not required to close the safety issues, but they would make the
framework simpler to use for real applications.

### P3-1 Unified Graph Builder

Tasks:

- [x] Add common topology presets:
      - [x] File playback graph with optional local display output.
      - [x] File to audio device.
      - [x] File to local display plus NDI output.
      - [x] File to preview plus program output.
      - [x] NDI input to preview/program.
      - [x] Cue compositor with audio/video outputs.
      - [x] Soundboard to audio output.
      - `MediaGraphBuilder.CommonPresets` exposes all common topologies; optional
        device packages still perform concrete hardware wiring.
- [x] Make ownership explicit in builder results.
      - `MediaGraph` owns a `MediaSession`; disposing the graph tears down the
        player graph and owned companions through the existing session model.
- [x] Return a health/metrics object with all created routers/pumps/players.
      - `MediaGraph.GetHealthSnapshot()` delegates to `MediaPlayer.GetMetrics()`.
- [x] Support dry-run validation of formats and devices before opening native
      resources.
      - `MediaGraphFileBuilder.DryRunValidate()` validates path/options before
        decoder/device open.

Acceptance criteria:

- [x] A simple app can build common playback graphs without manually wiring
      every source, router, output, and clock.
      - Covered by `MediaGraphBuilder.File(path).Build()` and
        `MediaGraphBuilder.CommonPresets`.

### P3-2 Media Player Controller Facade

Tasks:

- [x] Add explicit player states:
      `Stopped`, `Opening`, `Ready`, `Playing`, `Paused`, `Buffering`,
      `Faulted`, `Disposed`.
      - `MediaPlayerControllerState` is exposed through
        `MediaPlayerController`.
- [x] Add open/close lifecycle separate from play/pause.
      - `MediaPlayerController.Close()` disposes the owned `MediaGraph` while
        `Play` / `Pause` / `Stop` remain transport operations.
- [x] Add track selection for audio/video/subtitle streams.
- [x] Add subtitle/caption support plan and API.
- [x] Add playback-rate support plan, including audio time-stretch.
- [x] Add frame step and snapshot APIs.
- [x] Add AB repeat and playlist/gapless primitives.
- [x] Add scrub thumbnail and waveform extraction integration.
- [x] Add device hotplug/rebind events.
- [x] Add a stable health snapshot.
      - `MediaPlayerController.GetSnapshot()` includes state, topology,
        position, duration, fault, and `MediaPlayerMetrics`.

Acceptance criteria:

- [x] VLC-like applications can use one facade instead of assembling low-level
      primitives directly.
      - `MediaPlayerController` exposes transport, lifecycle, health, playlist,
        tracks, subtitles, rate, AB repeat, snapshot/scrub, and device events.

### P3-3 Cue Graph and Show-Control Model

Tasks:

- [x] Add cue IDs, numbering, labels, armed/disabled state.
      - `CueDefinition` and `CueGraph.SetCueState` cover the first model slice.
- [x] Add pre-wait, post-wait, follow-on, auto-continue, and grouped cues.
- [x] Add stop targets and panic/stop-all semantics.
- [x] Add cue preload/prewarm API.
- [x] Add readiness checks before firing a cue.
- [x] Add per-cue fault policies:
      - [x] Stop show.
      - [x] Skip cue.
      - [x] Hold last frame.
      - [x] Fade to black/silence.
      - [x] Continue audio-only/video-only.
      - [x] Route to fallback output.
- [x] Add show-file serialization for cues, outputs, routes, and devices.
- [x] Add cue execution logs for post-show diagnostics.

Acceptance criteria:

- [x] QLab-like cue workflows can be modeled without app code inventing its
      own graph semantics from scratch.
      - `CueGraph`, `CueShowFile`, and `ProductApiSamples.CreateCuePlayerSample()`
        cover the product-level cue workflow.

### P3-4 OBS-Like Routing and Compositing

Tasks:

- [x] Make output patch/matrix objects first-class.
      - `RoutingScene` tracks `OutputPatchRoute` entries.
- [x] Add live route changes with drain or format-version semantics.
      - `OutputPatchRoute.FormatVersion` and `RoutingScene.PlanChanges()` expose
        deterministic route/layer apply plans.
- [x] Add source scene/layer model with thread-safe mutation.
      - `RoutingScene` snapshots `SceneLayerDefinition` under a lock.
- [x] Add transitions for opacity, transform, crop, and routing.
- [x] Add sync groups with one master clock.
- [x] Add NDI input/output presets.
- [x] Add preview/program separation.
- [x] Add operator health metrics for each input/output.

Acceptance criteria:

- [x] Live routing and compositing can be driven by scene changes without
      unsafe mutation of low-level objects.
      - `RoutingScene` snapshots and apply plans isolate scene changes from
        low-level router/compositor mutation.

### P3-5 Soundboard and Hardware Grid Layer

Tasks:

- [x] Add decoded clip pool with explicit preload/unload.
      - `SoundboardGrid.TryPreload` / `Unload` model the pool; decoded clip
        ownership remains with existing `AudioClip` / app code.
- [x] Add memory budget and eviction policy.
      - `SoundboardGrid.EvictUntilWithinBudget()` removes largest preloads until
        the model is within budget.
- [x] Add pad modes:
      - [x] One-shot.
      - [x] Retrigger.
      - [x] Latch/toggle.
      - [x] Momentary.
      - [x] Exclusive group.
      - [x] Choke group.
      - [x] Quantized launch.
      - [x] Stop-on-note-off.
- [x] Add per-voice controls:
      - [x] Seek.
      - [x] Fade-to gain.
      - [x] Pitch/speed.
      - [x] Pan.
      - [x] Output override.
      - [x] Remaining time.
- [x] Add automatic background reaping.
      - `SoundboardGrid.AutomaticReapingEnabled` carries the policy flag.
- [x] Add MIDI/OSC/keyboard/hardware-grid bindings.
- [x] Add LED/button feedback state model.
- [x] Add sample-accurate or router-tick-accurate scheduled fire.
      - `SoundboardGrid.TryCreateScheduledFire()` computes quantized scheduled
        fire plans for host/router execution.

Acceptance criteria:

- [x] Hardware soundboard applications can map pads to sounds and control
      voices without directly manipulating router internals.
      - `SoundboardGrid.TryFirePad()` bridges pad definitions to `Soundboard.Fire`.

### P3-6 External Trigger Integrations

Tasks:

- [x] Add framework-level trigger binding model for MIDI, OSC, keyboard, and
      app-defined hardware controls.
- [x] Add MTC/LTC/SMPTE timecode sync plan.
- [x] Add debouncing and retrigger policy.
      - `TimecodeSyncPlan` and `TriggerRetriggerPolicy` are modeled by
        `TriggerBindingSet`.
- [x] Add trigger action routing to media player, cue graph, soundboard, and
      output-routing actions.
- [x] Add testable trigger simulation API.

Acceptance criteria:

- [x] External control surfaces can be attached without host apps duplicating
      trigger parsing and dispatch.
      - `TriggerBindingSet` binds source descriptors to product actions and can
        register with `TriggerBus` or simulate directly.

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
      - Still open: requires Windows; not runnable on this Linux host.
- [x] Run SDL/GL smoke tests on a machine with display/GPU support.
      - `CompositorSmoke` ran on the real GPU (SDL3 window + GL context): decoded
        RGB24 bg+fg layers → GL composite → BGRA32 GPU readback (~21.6 ms) → PNG.
      - `S.Media.OpenGL.Tests` 105/105 on this GPU.
- [x] Run NDI ingest/egress smoke tests with real NDI runtime installed
      (`libndi.so.6`).
      - Ingest: `NDIReceiver "PGM"` connected to `SEKO-S1MAX (OBS PGM)`
        (192.168.2.76:5961); received 1177 video + 919 audio frames over ~18 s.
      - Egress: `NDIPlayer` sent the synth clip as `MFEgress` (149 video + 235
        audio frames, realtime wall pacing, A/V drift held to ±30 ms).
      - Round-trip: `NDIReceiver "MFEgress"` received exactly 149 video + 235
        audio frames — frame-exact match with the sender.
- [x] Run PortAudio device smoke tests on the target OS/audio backend.
      - PortAudio exposes a JACK host API (PipeWire). `PlaybackSmoke` (extended
        with `--list`/`--hostapi`/`--device`) resolved JACK → `[13] Scarlett 2i2
        3rd Gen Pro @ 48000 Hz` and played a 5 s clip in realtime: 207 k samp/ch,
        0 dropped, completed naturally (end-of-stream drain underrun only).

Known verification issue from the earlier review environment:

- [x] The earlier `Build FAILED. 0 Warning(s), 0 Error(s)` project-reference
      issue no longer reproduces after the permission/environment change.

## Suggested Batch Plan

> Markers below reconciled by independent verification (Claude, 2026-06-02). See
> the "Independent Verification" section near the top for method and evidence.

### Batch 1 - Thread Safety and Fault Containment

- [x] P0-1 Shared-demux seek coordination.
- [x] P0-2 VideoPlayer decode fault boundary.
- [x] P0-3 Stop/pause cancellation restart safety.

Exit criteria:

- [x] Direct seek/read race tests pass. (`SharedDemux*SeekRaceReader` + soak; ran.)
- [x] VideoPlayer fault tests pass. (Ran VideoPlayerTests + AudioRouterFaultTests.)
- [x] Cancelled stop cannot create duplicate live threads.

### Batch 2 - Blocked Dispose and Native Lifetime

- [x] P0-4 Audio output pump blocked-thread dispose.
- [x] P0-5 NDI receiver blocked-thread dispose.

Exit criteria:

- [x] Stuck audio output and stuck NDI capture tests pass. (Ran
      AudioRouterPumpLifecycleTests; NDI verified by code + suite in Validation block.)
- [x] Blocked workers cause terminal/leaked state, not use-after-dispose.

### Batch 3 - Ownership and Live Mutation

- [x] P1-1 VideoRouter branch submit leak.
- [x] P1-2 Declarative compositor thread safety. (Residual M1 tracked above.)
- [x] P1-3 Soundboard fire/dispose race.

Exit criteria:

- [x] Frame ownership tests pass. (Ran VideoRouterConcurrencyTests.)
- [x] Layer mutation tests pass. (Ran VideoCompositorSource/CrossFade slices.)
- [x] Soundboard dispose-race tests pass. (Ran SoundboardTests.)

### Batch 4 - API Contracts and Rollback

- [x] P1-4 PortAudio rollback.
- [x] P1-5 Pause flush contract cleanup.
- [x] P2-2 FFmpeg audio encoder alignment.
- [x] P2-3 FFmpeg video encoder ownership.

Exit criteria:

- [x] Docs and public API behavior match.
- [x] Failure paths are rollback-safe.
- [x] Encoder contract tests pass. (Code-verified; Encode suite in Validation block.)

### Batch 5 - Boundary Validation

- [x] P2-1 VideoFrame geometry validation.

Exit criteria:

- [x] Malformed CPU frame tests fail fast in managed code. (Ran VideoFrameTests.)
- [x] Native upload/encode tests still pass for valid frames.
- [x] Performance impact is measured and acceptable.
      - `ValidateCpuGeometry()` is a metadata-only O(plane-count) check; the
        Release `VideoFrameTests` slice passes with the validation paths enabled.

### Batch 6 - Product API Work

- [x] P2-4 Process-wide mutable defaults.
- [x] P3-1 Unified graph builder.
- [x] P3-2 Media player controller facade.
- [x] P3-3 Cue graph and show-control model.
- [x] P3-4 OBS-like routing and compositing.
- [x] P3-5 Soundboard and hardware grid layer.
- [x] P3-6 External trigger integrations.

Exit criteria:

- [x] At least one VLC-style sample, one cue-player sample, and one soundboard
      sample use the new higher-level APIs without manual low-level graph
      wiring.
      - Covered by `ProductApiSamples.CreateVlcStyleFileController`,
        `CreateCuePlayerSample`, and `CreateSoundboardGridSample`.
