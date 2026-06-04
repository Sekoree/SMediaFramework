# HaPlay / MediaFramework Implementation Checklist - 2026-06-03

Source review: `Doc/HaPlay-MediaFramework-Review-2026-06-03.md`

## Verification Baseline

- [x] Confirm baseline solution build passes before implementation.
  - Command: `bash -c 'dotnet build MFPlayer.sln'`
- [x] Confirm baseline test suite passes before implementation.
  - Command: `bash -c 'dotnet test MFPlayer.sln --no-build'`

## High Priority

### HaPlay Cue Dispatch UI Threading

- [x] Remove `ConfigureAwait(false)` from view-model command paths where the
  continuation mutates Avalonia-bound state.
- [x] Ensure delayed cue-plan continuations update `CurrentCueNode`,
  `SelectedCueNode`, `StandbyCueNode`, and command state on the UI thread.
- [x] Marshal `DispatchCueExecution` status updates back to the UI thread.
- [x] Marshal `DispatchCueGroupExecution` status updates back to the UI thread.
- [x] Add or update tests that exercise delayed/group dispatch without
  thread-pool property mutation.
  - Added headless Avalonia coverage that verifies dispatched cue status changes
    are raised on the UI thread.

### VideoOutputPump Lifecycle Race

- [x] Make `_disposed` a lock-protected lifecycle invariant.
- [x] Serialize first `Configure` startup with `Dispose`.
- [x] Prevent `Dispose` from tearing down `_pending`, `_cts`, or `_inner` while
  a first configure is starting the drain thread.
- [x] Keep existing bounded-join behavior for stuck drain threads.
- [x] Add a regression test for concurrent configure/dispose.

### Router and Composition Dispose Invariants

- [x] Re-check `VideoRouter` disposed state inside every graph mutation lock.
- [x] Roll back or dispose created `VideoOutputPump` instances if `VideoRouter`
  rejects registration after wrapper creation.
- [x] Re-check `AudioRouter` disposed state inside `AddSource` and `AddOutput`
  locks.
- [x] Dispose any `AudioRouter` owned source wrapper if `AddSource` loses a
  dispose race after wrapping.
- [x] Re-check `ClipCompositionRuntime` disposed state inside `AddLayer`.
- [x] Roll back mixer slots if `ClipCompositionRuntime.AddLayer` loses a
  dispose race.
- [x] Make `ClipCompositionRuntime.LayerSlot.Dispose` idempotent.
- [x] Add regression tests for add-after-dispose / dispose-race behavior.

### ClipAudioOutputRuntime Route Failure Handling

- [x] Validate or install all requested routes as an all-or-nothing operation.
- [x] Roll back registered sources when any route installation fails.
- [x] Surface route installation failure to the caller instead of only logging.
- [x] Add a regression test for invalid route rollback.

## Medium Priority

### MediaPlayerViewModel Removal Teardown

- [x] Add an async teardown path on `MediaPlayerViewModel`.
- [x] Await session close before removing the player from `MainViewModel.Players`.
- [x] Stop `_idleSlateSyncTimer` during teardown.
- [x] Stop `_holdPumpTimer` during teardown.
- [x] Cancel outstanding pre-open, waveform, and cue-envelope work during
  teardown.
- [x] Unsubscribe shared output events during teardown.
- [x] Detach selected-tab and matrix-cell subscriptions during teardown.
- [x] Make removal idempotent so repeated remove/teardown calls are harmless.

### Atomic JSON Saves

- [x] Add a shared atomic JSON save helper.
- [x] Update `ProjectIO.SaveAsync` to write temp + replace/move.
- [x] Update `PlaylistIO.SaveAsync` to write temp + replace/move.
- [x] Update `CueListIO.SaveAsync` to write temp + replace/move.
- [x] Add tests that verify existing files survive serialization/write failure
  where practical.

### CuePlaybackEngine NaturalEnd Async Contract

- [x] Replace `NaturalEnd` `EventHandler` with an awaitable callback/event, or
  add defensive invocation around every subscriber.
- [x] Keep auto-follow errors visible in `MainViewModel`.
- [x] Add a focused test or documented coverage for subscriber failure behavior.
  - Added contract coverage for the awaitable `Func<Task>` callback shape.

## Simplification Follow-Ups

- [x] Replace process-wide `OutputLineRegistry` with an injected resolver.
- [x] Consolidate lifecycle rules in framework pumps/routers/runtimes.
- [x] Share atomic JSON save semantics across future HaPlay file formats.

## Final Verification

- [x] Run `bash -c 'dotnet build MFPlayer.sln'`.
- [x] Run `bash -c 'dotnet test MFPlayer.sln --no-build'`.
- [x] Update this checklist with completed items and any deferred work.

