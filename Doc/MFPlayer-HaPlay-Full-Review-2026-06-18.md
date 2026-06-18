# MFPlayer / HaPlay Full Review - 2026-06-18

This review covers the current `MFPlayer.sln`, the media/control/extra libraries, HaPlay and HaPlay.Desktop, HaPlay.Tests, the smoke/probe tools, the local `Reference/` sources used to sanity-check dependency assumptions, and the active `Doc/Explained` documentation.

I focused on correctness, ownership, native-resource lifetime, A/V sync, allocation/lifecycle pressure, public API contracts, and whether the docs match the current implementation. The solution builds cleanly, and most of the framework is deliberately defensive. The findings below are the places I would fix or document before treating this as show-critical infrastructure.

## Implementation Status - 2026-06-18

Implemented in the follow-up pass:

- Remote API now binds loopback by default, requires a generated per-machine token, supports explicit LAN opt-in, removes permissive CORS, and exposes the token/bind status in the Project workspace.
- `VideoPlayer` no longer disposes/recreates the queue slot semaphore during stop; queue drains and tick-side dequeues are guarded so an in-flight tick cannot release a retired semaphore.
- `PortAudioInput` and `PortAudioOutput` serialize Start/Stop with their lifecycle gate.
- `NDISource` adapters no longer advertise fake standby formats; callers must wait for real negotiated stream formats or handle a clear exception.
- HaPlay pre-roll/pre-connect caches remove entries under their cache lock and dispose native/session resources after releasing it.
- FFmpeg demux, NDI capture lifecycle, and `VideoOutputPump` now publish stuck native-boundary teardown records through `NativeResourceHealth`; `VideoOutputPump` has regression coverage for the health signal.
- `FormatSwitchProbe` and `TransportSyncProbe` are now included in `MFPlayer.sln`.
- The affected explained docs were updated for the REST API security model, current HaPlay class sizes, and probe solution coverage.

Still intentionally left as design/refactor recommendations:

- `ClipStandbyEngine.StartGroupAsync` naming/contract clarification or a stronger framework-level synchronized group-start primitive.
- Larger HaPlay decomposition into smaller view-model/services.
- Full regeneration of `Doc/Explained/16-Type-Coverage-Appendix.md` and any chapter-wide source-count tables.
- HaPlay UI surfacing for `NativeResourceHealth` beyond the central framework registry and logs.

The findings section below preserves the original review evidence. Items listed as implemented above describe the post-fix state.

## Review Validation Performed

- `DOTNET_CLI_HOME=/tmp dotnet build MFPlayer.sln -m:1 --no-restore -v:m` - passed, 0 warnings, 0 errors.
- `DOTNET_CLI_HOME=/tmp dotnet build MediaFramework/Tools/TransportSyncProbe/TransportSyncProbe.csproj --no-restore -v:m` - passed, 0 warnings, 0 errors.
- `DOTNET_CLI_HOME=/tmp dotnet build MediaFramework/Tools/FormatSwitchProbe/FormatSwitchProbe.csproj -v:m` - restore/build passed, 0 warnings, 0 errors.
- `DOTNET_CLI_HOME=/tmp dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --no-build -v:m --filter FullyQualifiedName~VideoPlayerTests` - passed, 19 tests.
- Broad static scan for `TODO`, `NotImplementedException`, sync-over-async, empty catches, fire-and-forget tasks, thread joins, remote HTTP/auth markers, and native leak/stuck comments.
- Source/doc inventory checks with `dotnet sln list`, `find`, `rg`, `wc -l`, and line-level review of the largest playback, routing, UI, remote-control, PortAudio, FFmpeg, NDI, control, MIDI, OSC, and cache classes.

This was the baseline review validation before the implementation pass.

## Post-Fix Validation - 2026-06-18

- `DOTNET_CLI_HOME=/tmp dotnet build MFPlayer.sln -m:1 -v:m` - passed; restores the newly added probe projects.
- `DOTNET_CLI_HOME=/tmp dotnet build MFPlayer.sln -m:1 --no-restore -v:m` - passed, 0 warnings, 0 errors.
- `DOTNET_CLI_HOME=/tmp dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --no-build -v:m --filter FullyQualifiedName~VideoPlayerTests` - passed, 19 tests.
- `DOTNET_CLI_HOME=/tmp dotnet test MediaFramework/Test/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj --no-build -v:m --filter FullyQualifiedName~VideoOutputPumpTests` - passed, 6 tests.
- `DOTNET_CLI_HOME=/tmp dotnet test MediaFramework/Test/S.Media.NDI.Tests/S.Media.NDI.Tests.csproj --no-build -v:m --filter FullyQualifiedName~NDISourceApiTests` - passed, 4 tests.
- `DOTNET_CLI_HOME=/tmp dotnet test MediaFramework/Test/S.Media.PortAudio.Tests/S.Media.PortAudio.Tests.csproj --no-build -v:m` - passed, 26 tests.
- `DOTNET_CLI_HOME=/tmp dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --no-build -v:m --filter FullyQualifiedName~RemoteApiDispatcherTests` - passed, 12 tests.
- `git diff --check` - passed.

## Findings

### P1 - Remote API is an unauthenticated LAN show-control surface

HaPlay's remote API is off by default, but once enabled it is persisted and restarted on app launch (`UI/HaPlay/ViewModels/MainViewModel.cs:154`, `UI/HaPlay/ViewModels/MainViewModel.cs:156`, `UI/HaPlay/ViewModels/MainViewModel.cs:158`; `UI/HaPlay/Models/AppSettings.cs:43`). The listener first binds `http://*:{port}/` (`UI/HaPlay/Remote/RestApiServer.cs:37`) and sends permissive CORS headers (`UI/HaPlay/Remote/RestApiServer.cs:124`, `UI/HaPlay/Remote/RestApiServer.cs:125`, `UI/HaPlay/Remote/RestApiServer.cs:126`, `UI/HaPlay/Remote/RestApiServer.cs:127`). The UI copy is explicit that the API is unauthenticated (`UI/HaPlay/Resources/Strings.resx:2475`, `UI/HaPlay/Resources/Strings.resx:2476`).

The exposed commands are operationally powerful: cue GO/stop/panic (`UI/HaPlay/Remote/RemoteApiDispatcher.cs:101`, `UI/HaPlay/Remote/RemoteApiDispatcher.cs:108`, `UI/HaPlay/Remote/RemoteApiDispatcher.cs:125`, `UI/HaPlay/Remote/RemoteApiDispatcher.cs:128`), player transport and volume (`UI/HaPlay/Remote/RemoteApiDispatcher.cs:153`, `UI/HaPlay/Remote/RemoteApiDispatcher.cs:155`, `UI/HaPlay/Remote/RemoteApiDispatcher.cs:164`, `UI/HaPlay/Remote/RemoteApiDispatcher.cs:173`), soundboard playback/stop/fade (`UI/HaPlay/Remote/RemoteApiDispatcher.cs:209`, `UI/HaPlay/Remote/RemoteApiDispatcher.cs:234`, `UI/HaPlay/Remote/RemoteApiDispatcher.cs:240`, `UI/HaPlay/Remote/RemoteApiDispatcher.cs:244`), and control arm/disarm (`UI/HaPlay/Remote/RemoteApiDispatcher.cs:253`, `UI/HaPlay/Remote/RemoteApiDispatcher.cs:262`, `UI/HaPlay/Remote/RemoteApiDispatcher.cs:266`).

This is acceptable only on a fully trusted, isolated show network. On any shared Wi-Fi or venue LAN, any browser, phone, or web page able to reach the machine can trigger playback because `GET` is accepted and CORS is open.

Recommended refactor:

- Bind loopback by default; make all-interface binding an explicit advanced setting.
- Add a generated per-machine bearer token or URL secret and require it on all mutating endpoints.
- Restrict CORS to configured origins or remove browser CORS support unless explicitly enabled.
- Consider `POST`-only for mutating actions. Keep `GET` for status and maybe copyable operator shortcuts only when a token is present.
- Include the active bind mode and auth status in the Project workspace, not only in hover/copy text.

### P1 - `VideoPlayer.Stop` can race an in-flight tick and corrupt the queue semaphore

`VideoPlayer.StopInternal` sets `_isRunning = false`, unsubscribes the tick handler, cancels decode, then calls `DrainQueue()` (`MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:347`, `MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:355`, `MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:364`, `MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:366`, `MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:372`). `DrainQueue()` disposes `_slotsAvailable` and replaces it (`MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:427`, `MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:445`, `MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:446`).

The code comment assumes that once `IsRunning` is false, no more `_slotsAvailable.Release()` calls can come from the clock (`MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:368`, `MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:369`, `MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:370`). That is not guaranteed. A `VideoTick` handler can already be executing before Stop removes the event. `OnVideoTick` checks `IsRunning` only at entry (`MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:528`, `MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:530`), then later dequeues and releases slots (`MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:570`, `MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:574`, `MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs:576`).

An in-flight tick can therefore release a disposed semaphore or release the freshly recreated full semaphore, causing `ObjectDisposedException` or `SemaphoreFullException`. `MediaClock` may swallow/log subscriber exceptions, but the underlying state contract is still wrong and can make stop/seek faults intermittent.

Recommended refactor:

- Introduce a queue/semaphore generation object guarded by a dedicated queue lock, or switch the decode queue to a bounded channel/owned queue state that can be atomically retired.
- Ensure `DrainQueue` cannot dispose/reset the slot primitive while any tick is inside the dequeue loop.
- Add a regression test with a controllable clock/output where `OnVideoTick` is held after the initial `IsRunning` check, then `Stop()` runs concurrently.

### P1 - PortAudio input/output lifecycle is not serialized

`PortAudioOutput.Start()` checks `_isRunning` and then opens a native stream and allocates `_selfHandle`, setting `_isRunning` only after `Pa_StartStream` succeeds (`MediaFramework/Media/S.Media.PortAudio/PortAudioOutput.cs:243`, `MediaFramework/Media/S.Media.PortAudio/PortAudioOutput.cs:247`, `MediaFramework/Media/S.Media.PortAudio/PortAudioOutput.cs:256`, `MediaFramework/Media/S.Media.PortAudio/PortAudioOutput.cs:269`, `MediaFramework/Media/S.Media.PortAudio/PortAudioOutput.cs:285`, `MediaFramework/Media/S.Media.PortAudio/PortAudioOutput.cs:297`). `Stop()` independently reads `_isRunning` and closes whichever `_stream` is currently stored (`MediaFramework/Media/S.Media.PortAudio/PortAudioOutput.cs:304`, `MediaFramework/Media/S.Media.PortAudio/PortAudioOutput.cs:307`, `MediaFramework/Media/S.Media.PortAudio/PortAudioOutput.cs:317`, `MediaFramework/Media/S.Media.PortAudio/PortAudioOutput.cs:320`, `MediaFramework/Media/S.Media.PortAudio/PortAudioOutput.cs:326`, `MediaFramework/Media/S.Media.PortAudio/PortAudioOutput.cs:327`).

`PortAudioInput` has the same shape (`MediaFramework/Media/S.Media.PortAudio/PortAudioInput.cs:200`, `MediaFramework/Media/S.Media.PortAudio/PortAudioInput.cs:204`, `MediaFramework/Media/S.Media.PortAudio/PortAudioInput.cs:214`, `MediaFramework/Media/S.Media.PortAudio/PortAudioInput.cs:225`, `MediaFramework/Media/S.Media.PortAudio/PortAudioInput.cs:241`, `MediaFramework/Media/S.Media.PortAudio/PortAudioInput.cs:250`; stop at `MediaFramework/Media/S.Media.PortAudio/PortAudioInput.cs:257`, `MediaFramework/Media/S.Media.PortAudio/PortAudioInput.cs:260`, `MediaFramework/Media/S.Media.PortAudio/PortAudioInput.cs:267`, `MediaFramework/Media/S.Media.PortAudio/PortAudioInput.cs:270`, `MediaFramework/Media/S.Media.PortAudio/PortAudioInput.cs:276`, `MediaFramework/Media/S.Media.PortAudio/PortAudioInput.cs:277`).

HaPlay normally calls these through single-owner runtime objects, so this is less likely in normal UI use. As public framework classes, though, two concurrent `Start()` calls can both pass the early check, allocate separate GCHandles, and overwrite `_stream` / `_selfHandle`; later `Stop()` only owns the last stored values.

Recommended refactor:

- Use a single lifecycle gate for Start/Stop/Flush/restart paths. `PortAudioOutput` already has `_streamLifecycleGate` for flush restart; include `Start()` and `Stop()` in the same state machine.
- Track `Starting`, `Running`, `Stopping`, `Stopped` states instead of one boolean.
- Store native handle/GCHandle in a local holder during open and publish it only after successful start; rollback the holder on every failure path.
- Add tests that call `Start()`/`Stop()` concurrently against a fake/native abstraction, or make the thread-safety contract explicit if the class is intentionally single-thread-affine.

### P2 - `NDISource.Audio` / `Video` expose standby formats before real stream negotiation

The NDI adapters report fake fallback formats until the first stream arrives: audio is `48000 Hz / 2 channels`, video is `16x16 BGRA32 @ 30 fps` (`MediaFramework/Media/S.Media.NDI/NDISource.cs:762`, `MediaFramework/Media/S.Media.NDI/NDISource.cs:763`, `MediaFramework/Media/S.Media.NDI/NDISource.cs:768`, `MediaFramework/Media/S.Media.NDI/NDISource.cs:769`, `MediaFramework/Media/S.Media.NDI/NDISource.cs:789`, `MediaFramework/Media/S.Media.NDI/NDISource.cs:790`). `AudioRouter.AddSource` snapshots `source.Format` immediately and allocates per-source scratch from that channel count (`MediaFramework/Media/S.Media.Core/Audio/AudioRouter.cs:210`, `MediaFramework/Media/S.Media.Core/Audio/AudioRouter.cs:211`, `MediaFramework/Media/S.Media.Core/Audio/AudioRouter.cs:213`, `MediaFramework/Media/S.Media.Core/Audio/AudioRouter.cs:256`).

If a host wires `NDISource.Audio` before waiting for a real audio format, it can build a graph around the standby format and then receive a different live format. HaPlay's NDI connector mitigates this by waiting up front (`UI/HaPlay/Playback/NdiInputConnector.cs:65`, `UI/HaPlay/Playback/NdiInputConnector.cs:67`, `UI/HaPlay/Playback/NdiInputConnector.cs:76`, `UI/HaPlay/Playback/NdiInputConnector.cs:79`), but the public framework API still encourages an unsafe path because `Audio` and `Video` are always available.

Recommended refactor:

- Make live formats explicit: expose `TryGetAudioSourceWhenReady`, `OpenAsync`, or a `FormatKnown` state instead of returning a fake `IAudioSource.Format`.
- Alternatively make the adapters throw a clear "format not negotiated yet" exception until connected, forcing callers to call `WaitForStreams` or `TryGet*Format`.
- Add a framework-level test that proves a non-48k/non-stereo NDI source cannot be added to a router using standby dimensions.

### P2 - Stuck native-thread policy is safe, but it lacks a process-level recovery model

Several native-boundary types correctly prefer leaking native state over use-after-dispose when a worker thread is still inside native code. Examples:

- FFmpeg shared demux marks the demux thread stuck after a 4 s join and reports that native demux state will not be restarted or freed (`MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs:909`, `MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs:918`, `MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs:920`, `MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs:924`, `MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs:926`). `Dispose()` then returns before freeing the native contexts (`MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs:1187`, `MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs:1201`, `MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs:1202`, `MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs:1203`).
- `VideoOutputPump.Dispose` intentionally leaks pump state if its drainer does not exit within the join cap (`MediaFramework/Media/S.Media.Core/Video/VideoOutputPump.cs:483`, `MediaFramework/Media/S.Media.Core/Video/VideoOutputPump.cs:485`, `MediaFramework/Media/S.Media.Core/Video/VideoOutputPump.cs:490`, `MediaFramework/Media/S.Media.Core/Video/VideoOutputPump.cs:492`).
- NDI capture lifecycle intentionally leaks receiver/runtime/CTS when a capture thread remains alive (`MediaFramework/Media/S.Media.NDI/NDICaptureThreadLifecycle.cs:21`, `MediaFramework/Media/S.Media.NDI/NDICaptureThreadLifecycle.cs:26`, `MediaFramework/Media/S.Media.NDI/NDICaptureThreadLifecycle.cs:34`, `MediaFramework/Media/S.Media.NDI/NDICaptureThreadLifecycle.cs:37`, `MediaFramework/Media/S.Media.NDI/NDICaptureThreadLifecycle.cs:45`, `MediaFramework/Media/S.Media.NDI/NDICaptureThreadLifecycle.cs:46`), and `NDISource` records a stuck state/fault (`MediaFramework/Media/S.Media.NDI/NDISource.cs:731`, `MediaFramework/Media/S.Media.NDI/NDISource.cs:754`, `MediaFramework/Media/S.Media.NDI/NDISource.cs:755`, `MediaFramework/Media/S.Media.NDI/NDISource.cs:756`).

The local choices are correct. The missing piece is an app/framework-level supervisor. A show operator currently gets logs and per-object fault state, but not a single "this process has leaked native resources and should restart before the next show" signal.

Recommended refactor:

- Add a central `NativeResourceHealth` or diagnostics registry where these leak-over-UAF paths publish stuck resource records.
- Surface it in HaPlay's output/project health UI and logs.
- Include resource type, file/source/device name, thread name, join timeout, and recommended operator action.
- Consider a "quarantine" mode that refuses to reuse related devices/outputs after a stuck leak, even if the immediate object is disposed.

### P2 - Pre-roll / pre-connect caches dispose native resources under locks

`CuePreRollCache` disposes existing sessions inside `_gate` during `Store` (`UI/HaPlay/Playback/CuePreRollCache.cs:69`, `UI/HaPlay/Playback/CuePreRollCache.cs:72`, `UI/HaPlay/Playback/CuePreRollCache.cs:76`, `UI/HaPlay/Playback/CuePreRollCache.cs:81`) and invalidates by disposing every cached session while still holding the lock (`UI/HaPlay/Playback/CuePreRollCache.cs:86`, `UI/HaPlay/Playback/CuePreRollCache.cs:89`, `UI/HaPlay/Playback/CuePreRollCache.cs:92`, `UI/HaPlay/Playback/CuePreRollCache.cs:94`, `UI/HaPlay/Playback/CuePreRollCache.cs:97`). `Dispose()` calls `InvalidateAll()` while already inside `_gate` (`UI/HaPlay/Playback/CuePreRollCache.cs:131`, `UI/HaPlay/Playback/CuePreRollCache.cs:133`, `UI/HaPlay/Playback/CuePreRollCache.cs:137`, `UI/HaPlay/Playback/CuePreRollCache.cs:138`).

The NDI and PortAudio pre-connect caches use the same dispose-under-lock pattern (`UI/HaPlay/Playback/NdiInputPreConnectCache.cs:45`, `UI/HaPlay/Playback/NdiInputPreConnectCache.cs:48`, `UI/HaPlay/Playback/NdiInputPreConnectCache.cs:55`, `UI/HaPlay/Playback/NdiInputPreConnectCache.cs:63`, `UI/HaPlay/Playback/NdiInputPreConnectCache.cs:65`, `UI/HaPlay/Playback/NdiInputPreConnectCache.cs:69`, `UI/HaPlay/Playback/NdiInputPreConnectCache.cs:99`, `UI/HaPlay/Playback/NdiInputPreConnectCache.cs:106`; `UI/HaPlay/Playback/PortAudioInputPreConnectCache.cs:45`, `UI/HaPlay/Playback/PortAudioInputPreConnectCache.cs:48`, `UI/HaPlay/Playback/PortAudioInputPreConnectCache.cs:55`, `UI/HaPlay/Playback/PortAudioInputPreConnectCache.cs:63`, `UI/HaPlay/Playback/PortAudioInputPreConnectCache.cs:65`, `UI/HaPlay/Playback/PortAudioInputPreConnectCache.cs:69`, `UI/HaPlay/Playback/PortAudioInputPreConnectCache.cs:99`, `UI/HaPlay/Playback/PortAudioInputPreConnectCache.cs:106`).

This is dangerous because these disposals can call into FFmpeg, PortAudio, NDI, playback sessions, and output teardown. Those paths may block, log, or call back into UI/engine state. Holding a cache lock while doing that makes future deadlocks and UI stalls much easier to create.

Recommended refactor:

- Under the lock, remove entries and collect the disposables into a local list.
- Release the lock, then dispose the collected resources.
- Do not call public invalidation methods from `Dispose`; use a private `DrainEntriesLocked` helper that only snapshots/removes.
- Keep `EntriesChanged` invocation strictly outside all locks. `CuePreRollCache` mostly does this for normal paths, but the outer `Dispose` lock makes the current behavior less clear.

### P2 - Two documented probe tools are outside the solution restore/build graph

`Doc/Explained/14-Tools-and-Probes.md` presents `FormatSwitchProbe` and `TransportSyncProbe` as first-class tools (`Doc/Explained/14-Tools-and-Probes.md:20`, `Doc/Explained/14-Tools-and-Probes.md:21`, `Doc/Explained/14-Tools-and-Probes.md:101`, `Doc/Explained/14-Tools-and-Probes.md:107`). `Doc/Explained/16-Type-Coverage-Appendix.md` also catalogs them (`Doc/Explained/16-Type-Coverage-Appendix.md:37`, `Doc/Explained/16-Type-Coverage-Appendix.md:40`, `Doc/Explained/16-Type-Coverage-Appendix.md:538`, `Doc/Explained/16-Type-Coverage-Appendix.md:556`).

`MFPlayer.sln` includes `PlaybackSmoke`, `SoundboardSmoke`, `VideoPlaybackSmoke`, `NDIReceiver`, `NDIPlayer`, `CompositorSmoke`, and `EncoderSmoke` (`MFPlayer.sln:49`, `MFPlayer.sln:51`, `MFPlayer.sln:63`, `MFPlayer.sln:65`, `MFPlayer.sln:67`, `MFPlayer.sln:85`, `MFPlayer.sln:97`), but not `FormatSwitchProbe` or `TransportSyncProbe`.

Both probes compile independently in this review, but `FormatSwitchProbe` initially failed with `NETSDK1004` under `--no-restore` because it was not restored by the solution build. That means the normal "solution build is green" signal does not cover two of the tools the docs tell maintainers to use for format-change and transport-sync verification.

Recommended fix:

- Add both projects to `MFPlayer.sln`, or explicitly document that they are intentionally standalone and require separate restore/build commands.
- Prefer adding them to the solution, since they are documented as canonical verification tools.

### P3 - `ClipStandbyEngine.StartGroupAsync` exposes a group-start API that intentionally starts sequentially

The public standby engine describes an explicit arm/start barrier (`MediaFramework/Media/S.Media.Playback/ClipStandbyEngine.cs:206`, `MediaFramework/Media/S.Media.Playback/ClipStandbyEngine.cs:209`), but `StartGroupAsync` arms in parallel and then starts each clip sequentially (`MediaFramework/Media/S.Media.Playback/ClipStandbyEngine.cs:328`, `MediaFramework/Media/S.Media.Playback/ClipStandbyEngine.cs:329`, `MediaFramework/Media/S.Media.Playback/ClipStandbyEngine.cs:345`, `MediaFramework/Media/S.Media.Playback/ClipStandbyEngine.cs:346`, `MediaFramework/Media/S.Media.Playback/ClipStandbyEngine.cs:348`, `MediaFramework/Media/S.Media.Playback/ClipStandbyEngine.cs:349`). The XML comment correctly warns that starts are staggered and that hosts needing alignment should use the paused-audio collective-unpause pattern (`MediaFramework/Media/S.Media.Playback/ClipStandbyEngine.cs:329`, `MediaFramework/Media/S.Media.Playback/ClipStandbyEngine.cs:331`, `MediaFramework/Media/S.Media.Playback/ClipStandbyEngine.cs:332`, `MediaFramework/Media/S.Media.Playback/ClipStandbyEngine.cs:333`).

This is not a hidden bug, but it is an API trap. A host can reasonably interpret "StartGroup" as synchronized enough for grouped cues, while HaPlay correctly uses a stronger host-specific barrier elsewhere.

Recommended refactor:

- Rename the method to `StartSequentialGroupAsync`, or add an overload that accepts host-provided `prefillBeforeHardware`, `startHardware`, and collective release hooks.
- Put the synchronized group-start primitive in the framework if grouped cue playback is a supported product-tier scenario.

### P3 - HaPlay still has large multi-responsibility classes, even after recent shrinkage

The WIP UI has improved from the older docs, but it still has large classes that own UI state, transport, persistence, output wiring, and hardware lifecycle at once:

| File | Current lines |
|---|---:|
| `UI/HaPlay/ViewModels/MediaPlayerViewModel.cs` | 3157 |
| `UI/HaPlay/ViewModels/ControlWorkspaceViewModel.cs` | 2908 |
| `UI/HaPlay/Playback/CuePlaybackEngine.cs` | 2360 |
| `UI/HaPlay/ViewModels/CuePlayerViewModel.cs` | 2011 |
| `UI/HaPlay/Playback/HaPlayPlaybackSession.cs` | 1829 |
| `UI/HaPlay/ViewModels/MainViewModel.cs` | 1490 |

The code is not "wrong" because it is large; many pieces have careful cancellation and teardown. The risk is change coupling. The same classes own user commands, native resource orchestration, status strings, caching, command enablement, and cross-workspace coordination.

Recommended decomposition:

- Move remote API state/control out of `MainViewModel` into a small service plus settings adapter.
- Split `MediaPlayerViewModel` by playlist management, transport/session lifecycle, output routing, pre-open/waveform work, and UI status.
- Split `ControlWorkspaceViewModel` by device/profile management, script editing, runtime arming, live monitor, and learn-MIDI workflows.
- Split `CuePlaybackEngine` around audio output pools, composition pools, per-cue session state, preview state, and genlock coordination.
- Prefer small state-machine types for playback/session states instead of spreading guard booleans through VMs.

## Documentation Alignment

The active explained docs are broadly useful, but they are not fully in line with the current source.

### Stale inventory and line counts

`Doc/Explained/README.md` says the set was refreshed on 2026-06-14 (`Doc/Explained/README.md:13`). The type appendix says 579 production files, 108,545 production lines, and 1,214 type definitions (`Doc/Explained/16-Type-Coverage-Appendix.md:7`). Current source counts have drifted. Examples:

- `Doc/Explained/13-HaPlay-UI.md` says `MainViewModel` is about 2,091 lines (`Doc/Explained/13-HaPlay-UI.md:26`), current file is 1,490 lines.
- The same doc says `HaPlayPlaybackSession` is about 2,196 lines (`Doc/Explained/13-HaPlay-UI.md:50`), current file is 1,829 lines.
- It says `CuePlaybackEngine` is about 2,395 lines (`Doc/Explained/13-HaPlay-UI.md:55`), current file is 2,360 lines.
- `Doc/Explained/15-Issues-and-Improvements.md` lists older large-class counts for `CuePlayerViewModel`, `MediaPlayerViewModel`, and `ControlWorkspaceViewModel` (`Doc/Explained/15-Issues-and-Improvements.md:26`, `Doc/Explained/15-Issues-and-Improvements.md:28`, `Doc/Explained/15-Issues-and-Improvements.md:29`, `Doc/Explained/15-Issues-and-Improvements.md:30`); the current counts are materially lower.

Recommendation: regenerate `Doc/Explained/16-Type-Coverage-Appendix.md` and refresh all approximate line counts in chapters 09, 10, 11, 12, 13, and 15.

### `13-HaPlay-UI.md` contains stale/editorial language

The HaPlay UI doc includes an author-disclaimer quote about the UI/UX (`Doc/Explained/13-HaPlay-UI.md:9`, `Doc/Explained/13-HaPlay-UI.md:10`, `Doc/Explained/13-HaPlay-UI.md:11`, `Doc/Explained/13-HaPlay-UI.md:12`). That may have been useful during early WIP notes, but it is not appropriate for the main explained documentation now that HaPlay is a real app surface.

It also describes the REST API as "the latest commit" (`Doc/Explained/13-HaPlay-UI.md:160`, `Doc/Explained/13-HaPlay-UI.md:161`), which is inherently stale wording.

Recommendation: replace the disclaimer with a neutral "HaPlay is the integration app and has more UI/workflow churn than the framework" note, and describe the REST API as a normal feature with its unauthenticated/security constraints.

### Remote API docs do not emphasize the security boundary enough

The UI string and settings comment say the API is unauthenticated and all-interface (`UI/HaPlay/Resources/Strings.resx:2475`, `UI/HaPlay/Resources/Strings.resx:2476`; `UI/HaPlay/Models/AppSettings.cs:43`, `UI/HaPlay/Models/AppSettings.cs:44`). `Doc/Explained/13-HaPlay-UI.md` says it is for LAN controllers and lists endpoints (`Doc/Explained/13-HaPlay-UI.md:158`, `Doc/Explained/13-HaPlay-UI.md:160`, `Doc/Explained/13-HaPlay-UI.md:167`, `Doc/Explained/13-HaPlay-UI.md:168`, `Doc/Explained/13-HaPlay-UI.md:171`, `Doc/Explained/13-HaPlay-UI.md:172`), but it does not spell out the operational risk or recommended network isolation.

Recommendation: add a short "Security model" subsection to the REST API docs and to any operator-facing quickstart: off by default, unauthenticated, all-interface when enabled, use only on an isolated trusted network until token/bind controls exist.

### Tools docs do not match solution coverage

As noted in P2 above, the docs list `FormatSwitchProbe` and `TransportSyncProbe`, but `MFPlayer.sln` does not include them. Either the solution should include them or the docs should say they are standalone probes with separate restore/build commands.

### Source comment mismatch in pre-roll support

`PlaylistItemPreRollExtensions.SupportsPreRoll` says "File media suitable for pre-roll (live inputs are excluded)" (`UI/HaPlay/Playback/CuePreRollCache.cs:171`), but the method returns true for NDI inputs with audio enabled and PortAudio inputs (`UI/HaPlay/Playback/CuePreRollCache.cs:172`, `UI/HaPlay/Playback/CuePreRollCache.cs:173`, `UI/HaPlay/Playback/CuePreRollCache.cs:174`, `UI/HaPlay/Playback/CuePreRollCache.cs:175`). The implementation appears intentional because separate pre-connect caches exist; the XML comment is stale.

Recommendation: rename the comment to "Items that can be warmed before GO" and explain that file media uses decoder pre-roll while live inputs use pre-connect caches.

## What Looks Correct

- The build graph for the main solution is clean. There were no `NotImplementedException` hits in shipping code, and the `NotSupportedException` hits I checked are unsupported-format/API guardrails rather than unfinished paths.
- The FFmpeg shared demux, video router, audio router, and output pump code are much more defensive than typical media-stack code. The ownership comments are specific and usually backed by tests.
- HaPlay's NDI input path correctly waits for stream formats before wiring routers, which mitigates the standby-format footgun in the app path.
- The control/MIDI layer has a shared PortMidi library lease (`MediaFramework/Control/S.Control/ControlMidiLibraryLease.cs:20`, `MediaFramework/Control/S.Control/ControlMidiLibraryLease.cs:31`, `MediaFramework/Control/S.Control/ControlMidiLibraryLease.cs:47`, `MediaFramework/Control/S.Control/ControlMidiLibraryLease.cs:49`) and explicit thread-safety notes in the PortMidi device wrapper (`MediaFramework/Extras/MIDI/PMLib/Devices/MIDIDevice.cs:21`, `MediaFramework/Extras/MIDI/PMLib/Devices/MIDIDevice.cs:22`, `MediaFramework/Extras/MIDI/PMLib/Devices/MIDIDevice.cs:23`, `MediaFramework/Extras/MIDI/PMLib/Devices/MIDIDevice.cs:24`, `MediaFramework/Extras/MIDI/PMLib/Devices/MIDIDevice.cs:25`, `MediaFramework/Extras/MIDI/PMLib/Devices/MIDIDevice.cs:26`).
- The script file path resolver keeps project-relative script paths under the project root (`UI/HaPlay/ViewModels/ControlWorkspaceViewModel.cs:2134`, `UI/HaPlay/ViewModels/ControlWorkspaceViewModel.cs:2139`, `UI/HaPlay/ViewModels/ControlWorkspaceViewModel.cs:2140`, `UI/HaPlay/ViewModels/ControlWorkspaceViewModel.cs:2141`, `UI/HaPlay/ViewModels/ControlWorkspaceViewModel.cs:2142`).
- UI `async void` occurrences found in the broad scan are mostly event handlers/code-behind, not arbitrary fire-and-forget library code.

## Suggested Fix Order

1. Add auth/bind controls to the Remote API and update the docs at the same time.
2. Fix the `VideoPlayer` queue/semaphore generation race and add a concurrency regression test.
3. Serialize `PortAudioInput` / `PortAudioOutput` lifecycle.
4. Remove standby-format ambiguity from `NDISource` public adapters.
5. Refactor HaPlay pre-roll/pre-connect caches to dispose outside locks.
6. Add the omitted probe projects to `MFPlayer.sln`.
7. Regenerate `Doc/Explained/16-Type-Coverage-Appendix.md` and refresh stale line counts/phrasing.
8. Plan the larger HaPlay decomposition after the lifecycle/security fixes are done.
