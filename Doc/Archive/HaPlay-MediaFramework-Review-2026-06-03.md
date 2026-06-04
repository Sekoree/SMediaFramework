# HaPlay / MediaFramework Review - 2026-06-03

## Scope

Fresh pass over the current `MediaFramework` reusable playback projects and the
WIP Avalonia UI app in `UI/HaPlay`. Existing archive reviews were used as
background only; the findings below were checked against the current tree.

## Verification

- `bash -c 'dotnet build MFPlayer.sln'` passed.
- `bash -c 'dotnet test MFPlayer.sln --no-build'` passed.
- Build warnings are currently limited to obsolete API usage:
  - `S.Media.Core/Clock/PlaybackTimelineClockExtensions.cs` still references
    obsolete `IPlaybackPlayhead`.
  - `S.Media.Core/Video/IVideoCpuFrameConverter.cs` still references obsolete
    `VideoCpuFrameConverterRegistry.Factory` / `CanConvertProbe`.
  - `S.Media.FFmpeg.Encode/Internal/FfmpegAudioEncoder.cs` still uses obsolete
    FFmpeg `AVCodec.sample_fmts`.

## High Priority Findings

### HaPlay cue dispatch mutates VM state off the UI thread

`UI/HaPlay/ViewModels/CuePlayerViewModel.cs`

`Go()` awaits `RunTriggerPlanAsync(...).ConfigureAwait(false)`, and
`RunTriggerPlanAsync` updates `CurrentCueNode` / `SelectedCueNode` after awaited
pre-waits. `DispatchCueExecution` and `DispatchCueGroupExecution` then run on
`Task.Run` and assign `StatusMessage` from thread-pool continuations.

Why it matters: `CuePlayerViewModel` is Avalonia-bound state. Updating
observable properties and collections off the UI thread can produce intermittent
binding errors or rare crashes, especially for delayed groups and auto-continue
plans.

Suggested fix: keep cue sequencing on `Dispatcher.UIThread`, or introduce a
small helper such as `PostUi(Action)` / `InvokeUiAsync(Func<Task>)` and route
all `StatusMessage`, `CurrentCueNode`, `SelectedCueNode`, command notification,
and collection updates through it. Avoid `ConfigureAwait(false)` in view-model
methods unless every continuation is explicitly marshalled back.

### VideoOutputPump can race Configure against Dispose

`MediaFramework/Media/S.Media.Core/Video/VideoOutputPump.cs`

`Configure` checks `_disposed`, configures `_inner`, and sets `_configured`
under `_gate`, but creates `_cts`, assigns `_thread`, and starts the drain
thread after releasing the lock. `Dispose` sets `_disposed` outside `_gate` and
can dispose `_pending` / `_cts` in that gap.

Why it matters: a host can configure and dispose from different lifecycle paths
while changing routes or tearing down output previews. The newly-started drain
thread may then touch disposed pump state.

Suggested fix: make configure/dispose one lifecycle state machine. Either start
the thread while holding the same lifecycle lock, or add an intermediate
`Starting` state that prevents `Dispose` from tearing down wait handles until
configuration completes.

### Router and composition disposed-state checks are not locked consistently

`MediaFramework/Media/S.Media.Core/Video/VideoRouter.cs`  
`MediaFramework/Media/S.Media.Core/Audio/AudioRouter.cs`  
`MediaFramework/Media/S.Media.Playback/ClipCompositionRuntime.cs`

Several public mutators check `_disposed` before taking their mutation lock, but
do not re-check once inside the lock. `VideoRouter.AddOutput`, `AddInput`,
`TryAddRoute`, `RemoveOutput`, and `RemoveInput` follow this pattern; so do
`AudioRouter.AddSource` / `AddOutput`. `ClipCompositionRuntime.AddLayer` checks
outside `_gate`, creates a mixer slot, then adds the layer under `_gate`, while
`Dispose` sets `_disposed` outside that lock. `LayerSlot.Dispose` is also not
idempotent.

Why it matters: concurrent teardown can leave late sources, outputs, pumps, or
composition slots registered after the object is considered disposed. These are
exactly the rare races that show up during live output reconfiguration and app
shutdown.

Suggested fix: treat `_disposed` as part of each class's locked invariant.
Check/set it under the same lock used for graph mutation, re-check after any
expensive object creation, and dispose/rollback any wrapper or slot created
before a locked failure path. Make `LayerSlot.Dispose` idempotent with an
`Interlocked.Exchange` or lock-protected flag.

### ClipAudioOutputRuntime swallows route installation failures

`MediaFramework/Media/S.Media.Playback/ClipAudioOutputRuntime.cs`

`AddSource` registers the source first, then catches and logs each
`_router.AddRoute` failure. The method still starts playback and returns a
source id even if all routes failed or only a subset installed.

Why it matters: a bad `AudioRouteSpec` can leave a live source running silently
or only partially routed. Callers see success and have no way to surface the
broken cue route to the operator.

Suggested fix: validate all route specs before `AddSource`, or rollback with
`RemoveSource(srcId)` and return/throw a failure if any route cannot be
installed. If partial routing is intentionally allowed, return structured
per-route diagnostics instead of only logging.

## Medium Priority Findings

### Removing a media player is fire-and-forget teardown

`UI/HaPlay/ViewModels/MediaPlayerViewModel.cs`  
`UI/HaPlay/ViewModels/MainViewModel.cs`

`MediaPlayerViewModel.RemovePlayer` starts `CloseSessionAsync()` without
awaiting it, then asks `MainViewModel` to remove the VM from `Players`.
The VM subscribes to shared output events and starts `_idleSlateSyncTimer` in
the constructor, but there is no `Dispose` / `IAsyncDisposable` path that
unsubscribes those events, stops timers, cancels pre-open/waveform work, and
waits for session teardown.

Why it matters: removed players can keep reacting to output changes and timer
ticks after they are no longer visible. Session close may also race with later
output reconfiguration.

Suggested fix: make removal async. Add an owned teardown method on
`MediaPlayerViewModel` that awaits `CloseSessionAsync`, stops `_idleSlateSyncTimer`
and `_holdPumpTimer`, cancels outstanding CTS fields, unsubscribes `_outputs`
events, and detaches selected-tab / matrix-cell handlers.

### Project, playlist, and cue-list saves can truncate existing files

`UI/HaPlay/Models/ProjectIO.cs`

`ProjectIO.SaveAsync`, `PlaylistIO.SaveAsync`, and `CueListIO.SaveAsync` write
directly with `File.Create(path)`.

Why it matters: cancellation, serialization exceptions, process exit, or disk
errors after `File.Create` can leave the operator's existing project/list as an
empty or partial JSON file.

Suggested fix: write to a temp file in the same directory, flush it, then use
`File.Move(temp, path, overwrite: true)` or `File.Replace` where available.
Keep cleanup best-effort for the temp file.

### CuePlaybackEngine NaturalEnd uses synchronous EventHandler for async work

`UI/HaPlay/Playback/CuePlaybackEngine.cs`  
`UI/HaPlay/ViewModels/MainViewModel.cs`

`CuePlaybackEngine.WatchNaturalEndAsync` invokes `NaturalEnd` through
`Dispatcher.UIThread.InvokeAsync(() => NaturalEnd?.Invoke(...))`. The current
subscriber in `MainViewModel` is `async void` and catches its own exception, but
the event contract makes future async subscribers easy to get wrong.

Why it matters: natural-end auto-follow is transport logic, not just a UI
notification. The event shape cannot await subscribers or aggregate failures.

Suggested fix: replace this with a `Func<Task>` callback or a dedicated
`AsyncEvent` helper, or keep `EventHandler` but wrap each invocation defensively
inside the engine and document that subscribers must not throw.

## Simplification / Refactor Opportunities

### Replace process-wide OutputLineRegistry with an injected resolver

`UI/HaPlay/ViewModels/CuePlayerViewModel.cs`

`OutputLineRegistry` is a static mutable dictionary used by route/binding VMs to
resolve output lines. It is pragmatic for the current single-window app, but it
hides dependencies, complicates tests, and makes multi-window/multi-project
support fragile.

Suggested direction: make line resolution an instance service owned by
`CuePlayerViewModel` or `OutputManagementViewModel`, and pass it to route and
placement VMs as they are created/loaded. This is a breaking UI refactor, but it
removes global state from the cue model layer.

### Consolidate lifecycle patterns for pumps, routers, and runtimes

The framework has several hand-rolled lifecycle styles:

- pump start/stop with background threads,
- router graph mutation with immutable snapshots,
- composition runtime slots and output leases,
- HaPlay player session teardown with UI timers and preview caches.

Suggested direction: standardize on a small set of lifecycle rules:

- `_disposed` is always checked and set under the same lock that protects graph
  mutation.
- If a method creates a wrapper/pump/slot before acquiring the mutation lock, it
  must have a rollback path.
- Disposable handles returned to callers should be idempotent.
- Background-thread teardown should have explicit states such as `NotStarted`,
  `Starting`, `Running`, `Stopping`, and `Disposed`.

This would make future review and testing easier than fixing each race one at a
time.

### Move save durability into one shared helper

The three JSON save methods have the same durability issue and likely want the
same formatting, cancellation, and temp-file cleanup semantics. A small internal
`AtomicJsonFile.SaveAsync<T>` helper would simplify the IO layer and give tests
one place to exercise failure behavior.

## Suggested Regression Tests

- `VideoOutputPump` configure/dispose race using a controllable test output and
  concurrent tasks.
- `VideoRouter` / `AudioRouter` add-after-dispose races that assert late
  registrations fail and created wrappers are disposed.
- `ClipCompositionRuntime.AddLayer` racing `Dispose`, including double-dispose
  of a returned `LayerSlot`.
- `ClipAudioOutputRuntime.AddSource` with an invalid route spec, asserting
  rollback or structured failure.
- HaPlay cue dispatch thread affinity: delayed/group cue paths should update VM
  properties only on the Avalonia UI thread.
- Atomic save failure behavior: simulate a serializer/stream failure and verify
  the previous project file survives.

