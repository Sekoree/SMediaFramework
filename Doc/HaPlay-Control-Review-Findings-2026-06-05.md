# HaPlay MIDI/OSC Control Review Findings

Date: 2026-06-05

## Scope

Reviewed the recent HaPlay MIDI/OSC control rewrite, the adjacent control libraries, and the local reference material:

- Rewrite docs and checklist in `Doc/HaPlay-MIDI-OSC-Scripting-Rewrite-Plan.md` and `Doc/HaPlay-MIDI-OSC-Scripting-Rewrite-Checklist.md`.
- HaPlay control runtime, models, view models, dialogs, and XAML under `UI/HaPlay`.
- PMLib and OSCLib under `MediaFramework/Extras`.
- Reference material in `Reference/Mond-0.11.2`, `Reference/XTouchMini.txt`, and `Reference/UNOFFICIAL_X32_OSC_REMOTE_PROTOCOL.pdf.txt`.

This was a static review plus focused automated validation. No physical MIDI surface, X32/M32 hardware, or emulator was exercised.

## Latest Validation Run

Updated after the `S.Control` extraction, all-MIDI trigger support, and all-MIDI
outgoing script helpers:

- `dotnet build MediaFramework/Control/S.Control/S.Control.csproj --no-restore -v:m`
  - Build succeeded with 0 warnings and 0 errors.
- `dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --no-restore -v:m`
  - Passed: 378, Failed: 0, Skipped: 0.
- `dotnet test MediaFramework/Test/OSCLib.Tests/OSCLib.Tests.csproj --no-restore -v:m`
  - Passed: 23, Failed: 0, Skipped: 0.
- `dotnet test MediaFramework/Test/PMLib.Tests/PMLib.Tests.csproj --no-restore -v:m`
  - Passed: 13, Failed: 0, Skipped: 0.
- `dotnet build MFPlayer.sln -m:1 --no-restore -v:m`
  - Build succeeded with 0 warnings and 0 errors.

## Initial Review Validation Run

- `dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --no-restore -v:m`
  - Passed: 349, Failed: 0, Skipped: 0.
- `dotnet test MediaFramework/Test/OSCLib.Tests/OSCLib.Tests.csproj --no-restore -v:m`
  - Passed: 23, Failed: 0, Skipped: 0.
- `dotnet test MediaFramework/Test/PMLib.Tests/PMLib.Tests.csproj --no-restore -v:m`
  - Passed: 13, Failed: 0, Skipped: 0.
- `dotnet build MFPlayer.sln -m:1 --no-restore -v:m`
  - Build succeeded with 0 warnings and 0 errors.

## Fix Status

Updated on 2026-06-05 after the follow-up implementation pass:

- Fixed finding 1 with a live `ControlEventQueue`, dispatcher abstraction, serialized session entry points, and queue/session concurrency regression tests. MIDI, OSC, replies, manual triggers, layer activation, and periodic script ticks now route through the queue in the armed runtime.
- Fixed finding 2 by throttling failed periodic OSC attempts by the configured interval.
- Fixed finding 3 by making OSCLib throw `OSCPacketTooLargeException` when the client rejects an oversized outgoing packet.
- Fixed finding 4 with clearer `Client source port` labeling, docs cleanup, and a profile warning when a device source port collides with an enabled app listener.
- Fixed finding 5 with clearer `Cue OSC`, `MIDI Ports`, `Use for Control`, and `Use for Control + Cues` labels.
- Fixed finding 6 by moving manual test sends into a collapsed diagnostics expander, renaming the manual trigger action, and removing hard-coded OSC test host/port defaults.
- Fixed finding 7 by removing unused Nodify package/style wiring and extracting the active control runtime/config/profile/IO surface into the framework-side `S.Control` project.
- Fixed finding 8 with a compact profile browser that shows source, imports profile JSON into project overrides, exports the selected profile, exports built-ins, and removes project override profiles.
- Fixed finding 9 with X32 command/cache column headers, filtering, grouping, selection, test-send preparation, and request actions.
- Fixed finding 10 by clarifying script failure labels and making trigger fields wrap.
- Fixed finding 11 by logging/stopping unexpected MIDI input read failures and disposing the input wake signal on `Dispose`.

The findings below are the original review record. The current implementation
status is summarized in **Fix Status** above.

## Findings

### 1. High: script dispatch is not serialized across MIDI, OSC, replies, and periodic ticks

The rewrite plan explicitly called for a `ControlEventQueue` serial worker (`Doc/HaPlay-MIDI-OSC-Scripting-Rewrite-Plan.md:220`), but the current runtime calls into `ControlScriptRuntimeSession` directly from several concurrent sources.

Evidence:

- The background tick loop runs every 100 ms and calls `ScriptSession.TickPeriodicAsync` directly (`UI/HaPlay/Control/ControlSystemRuntimeSession.cs:8`, `UI/HaPlay/Control/ControlSystemRuntimeSession.cs:93-100`, `UI/HaPlay/Control/ControlSystemRuntimeSession.cs:153-180`).
- OSC listener dispatch calls `_runtimeSession.DispatchControlEventAsync` directly for each matching device (`UI/HaPlay/Control/ControlOscListenerManager.cs:81-109`).
- OSC client-socket replies are fire-and-forget routed into listener dispatch (`UI/HaPlay/Control/ControlSystemRuntimeSession.cs:103-130`).
- Live MIDI input handlers fire-and-forget async dispatch tasks from the PortMidi event thread (`UI/HaPlay/Control/ControlSystemMidiDeviceSessions.cs:341-367`, `UI/HaPlay/Control/ControlSystemMidiDeviceSessions.cs:381-391`).
- The MIDI dispatcher also calls `_runtimeSession.DispatchControlEventAsync` directly (`UI/HaPlay/Control/ControlMidiDeviceManager.cs:21-55`, `UI/HaPlay/Control/ControlMidiDeviceManager.cs:89-123`).
- `ControlScriptRuntimeSession` owns shared mutable state with no serialization: `_commandSink`, `_runtime`, `_deviceHealth`, and `_lastPeriodicDispatch` (`UI/HaPlay/Control/ControlScriptRuntimeSession.cs:7-15`). The public dispatch methods and periodic tick enter the runtime and then drain the same command sink (`UI/HaPlay/Control/ControlScriptRuntimeSession.cs:55-58`, `UI/HaPlay/Control/ControlScriptRuntimeSession.cs:139-168`, `UI/HaPlay/Control/ControlScriptRuntimeSession.cs:181-231`).
- `BufferingControlScriptCommandSink` is a plain `List<>` accumulator and drain buffer (`UI/HaPlay/Control/ControlScriptOscCommandRouter.cs:6-50`).
- `ControlScriptRuntime` mutates `_scripts`, `_diagnostics`, `_activeLayerId`, and `_faulted` during dispatch (`UI/HaPlay/Control/ControlScriptRuntime.cs:7-17`, `UI/HaPlay/Control/ControlScriptRuntime.cs:183-213`, `UI/HaPlay/Control/ControlScriptRuntime.cs:426-464`, `UI/HaPlay/Control/ControlScriptRuntime.cs:467-503`).
- `HaPlay.State` uses mutable dictionaries plus current invocation fields with no protection (`UI/HaPlay/Control/ControlScriptStateStore.cs:12-41`).
- The Mond reference source is also stateful and unsynchronized: `MondState.Call` delegates into one `Machine` (`Reference/Mond-0.11.2/Mond/MondState.cs:15-25`, `Reference/Mond-0.11.2/Mond/MondState.cs:123-125`), and the VM mutates shared call/local/eval stacks during calls (`Reference/Mond-0.11.2/Mond/VirtualMachine/Machine.cs:56-95`, `Reference/Mond-0.11.2/Mond/VirtualMachine/Machine.Stacks.cs:11-31`).

Impact:

- Two simultaneous events can interleave script execution and command flushing. Event A can drain OSC/MIDI commands queued by event B, or vice versa.
- `HaPlay.State` script/device scope can bleed between concurrent script invocations because `BeginInvocation` and `EndInvocation` are global fields.
- Periodic trigger timestamps and layer switches can race against live MIDI/OSC triggers.
- Mond module execution is being treated as reentrant/thread-safe, which the local source does not support.

Recommendation:

- Implement the planned single `ControlEventQueue` and put all script-triggering inputs through it: MIDI input, OSC listener input, OSC client replies, lifecycle events, manual/test triggers, layer activation, and periodic script ticks.
- As a short-term containment step, add one `SemaphoreSlim` gate inside `ControlScriptRuntimeSession` around every public entry point that touches `_runtime`, `_commandSink`, `_deviceHealth`, or `_lastPeriodicDispatch`. This is less clean than a queue but would stop cross-event command/state corruption.
- Add a concurrency test that fires a MIDI event, an OSC event, and a periodic tick concurrently. Each script should queue a unique OSC/MIDI command and write distinct `HaPlay.State` values; the test should prove commands and state remain attributed to the correct trigger.

### 2. Medium: failed periodic OSC sends retry at tick rate instead of configured interval

`ControlPeriodicOscSendManager` only updates `_lastSentUtc` when the send succeeds (`UI/HaPlay/Control/ControlPeriodicOscSendManager.cs:36-43`). If the host is unreachable, DNS fails, or the UDP send throws, the same periodic command remains immediately due. Because the session tick loop defaults to 100 ms (`UI/HaPlay/Control/ControlSystemRuntimeSession.cs:8`, `UI/HaPlay/Control/ControlSystemRuntimeSession.cs:178-180`), a broken X32 endpoint can produce failures at about 10 Hz even when `/xremote` is configured for 8000 ms.

Impact:

- A disconnected mixer can flood the monitor and logs.
- Failed `/xremote`, `/subscribe`, or `/meters` tasks can consume unnecessary work while armed.
- The behavior does not match the user-facing interval.

Recommendation:

- Track last attempt time separately from last successful send, or update `_lastSentUtc` before/after every attempt.
- Consider exponential backoff for repeated failures while keeping the first retry reasonably quick after a transient network issue.

### 3. Medium: OSCLib can drop an outgoing packet while HaPlay records it as sent

`OSCClient.SendAsync` logs and returns when the encoded packet exceeds `Options.MaxPacketBytes` (`MediaFramework/Extras/OSC/OSCLib/OSCClient.cs:78-90`). HaPlay's command router treats a completed send call as success and records `Sent` (`UI/HaPlay/Control/ControlScriptOscCommandRouter.cs:102-109`); periodic OSC sends do the same (`UI/HaPlay/Control/ControlPeriodicOscSendManager.cs:94-98`).

Impact:

- The monitor can show a successful send for a packet that OSCLib intentionally did not put on the wire.
- Oversized script-generated OSC messages, blob messages, or future meter-related commands would be hard to diagnose from HaPlay.

Recommendation:

- Change OSCLib to throw a dedicated exception or return a send result when it rejects an oversized packet.
- If keeping OSCLib's current policy, wrap the sender used by HaPlay so rejected sends surface as failed monitor records.

### 4. Medium: OSC listener binding, client local port, and docs are easy to confuse

There are two different local-port concepts:

- App-level OSC listeners live in `ControlSystemConfig.OscListeners`. New projects now start with no app-level listener; adding the first listener seeds local port `10020`.
- An OSC device can optionally bind its outbound client socket via `Binding.OscLocalPort` (`UI/HaPlay/Models/ControlSystemConfig.cs:113-134`). `UdpControlOscSender` uses that value to bind the sending client's socket (`UI/HaPlay/Control/UdpControlOscSender.cs:31-33`, `UI/HaPlay/Control/UdpControlOscSender.cs:62-80`).

At review time, the user-facing material was inconsistent:

- `Doc/HaPlay-Control-Setup.md` said to bind the X32 device to the main app-level listener, usually port `10020`.
- `Doc/HaPlay-Control-Getting-Started.md` says to leave the OSC device local port blank because X32 replies return on the client socket (`Doc/HaPlay-Control-Getting-Started.md:50-53`).
- The dialog implementation agrees with the client-socket model and says there is no separate listener to choose (`UI/HaPlay/ViewModels/Dialogs/OscDeviceDialogViewModel.cs:20-24`).
- The dialog label was only `Local port`, with placeholder text saying `blank = automatic; set for a fixed source/receive port` (`UI/HaPlay/Views/Dialogs/OscDeviceDialog.axaml:41-43`).
- The control structure displayed OSC listeners (`UI/HaPlay/ViewModels/ControlWorkspaceViewModel.cs:862-873`), but the visible workspace had no add/edit/remove listener controls; only endpoint scripts could be added from listener rows (`UI/HaPlay/ViewModels/ControlWorkspaceViewModel.cs:443-447`).

Impact:

- A user may type `10020` into the OSC device's client source port field and collide with an explicitly enabled app listener.
- A user may search for a listener binding UI because the setup docs describe one, but the current dialog deliberately hides it.

Recommendation:

- Rename the OSC device field to `Client source port (optional)` or similar.
- Add validation/warning when an OSC device local port equals an enabled app listener local port.
- Reconcile the docs around one model: X32 replies and `/xremote` updates use the client's socket; app listeners are for separate inbound OSC control sources.
- Either add listener management UI or hide/list it as advanced read-only until listener editing is intentionally supported.

### 5. Medium UX: top-level MIDI/OSC screens overlap with Control concepts but labels do not say so

The app shell exposes separate `OSC`, `MIDI`, and `Control` workspaces (`UI/HaPlay/Models/WorkspaceItem.cs:9-15`, `UI/HaPlay/ViewModels/MainViewModel.cs:133-142`, `UI/HaPlay/Views/MainView.axaml:213-220`).

The old `OSC Connections` screen is for action-cue OSC targets, not control OSC devices (`UI/HaPlay/Views/OscConnectionsView.axaml:11-76`). Its resource hint says `Named OSC targets for action cues` (`UI/HaPlay/Resources/Strings.resx:168-178`), but the sidebar label is just `OSC`. The new Control workspace has its own OSC devices, app listeners, profiles, command browser, and test sends.

The MIDI screen also has mixed effects:

- The top buttons are `Use Input` and `Use Output` (`UI/HaPlay/Views/MidiDevicesView.axaml:38-62`).
- `Use Output` calls `AddSelectedMidiOutputToProjectCommand`, which both registers a Control MIDI output and creates/selects a cue `MidiActionEndpoint` (`UI/HaPlay/ViewModels/MainViewModel.cs:716-747`).
- The lower lists are labeled `Project MIDI inputs` and `Project MIDI outputs` (`UI/HaPlay/Views/MidiDevicesView.axaml:80-140`), but they contain shared cue/control usage.

Impact:

- Users can reasonably expect the `OSC` sidebar item to manage Control OSC devices. It does not.
- `Use Output` sounds like a local MIDI catalog selection, but it actually mutates both Control and cue action state.

Recommendation:

- Rename the top-level sidebar entries to distinguish purpose, for example `Cue OSC`, `MIDI Ports`, and `Control`.
- Rename MIDI buttons to explicit actions:
  - `Use as Control Input`
  - `Use as Control Output + Cue Endpoint`
- If both effects are intentional, show a short usage badge per project MIDI row: `Control input`, `Control output`, `Cue endpoint`, or combined.

### 6. Medium UX: the Control workspace primary screen is crowded with permanent test/debug surfaces

The Control workspace is currently a single scrollable configuration form plus a permanently visible monitor. It also includes visible instructional copy and always-visible manual test controls:

- Header explains the workspace (`UI/HaPlay/Views/ControlWorkspaceView.axaml:20-24`).
- The project structure, script list, X32 browser, test send, and monitor are all in one surface (`UI/HaPlay/Views/ControlWorkspaceView.axaml:47-200`, `UI/HaPlay/Views/ControlWorkspaceView.axaml:204-248`).
- `Test send` is always visible, with host/port/address/args plus `Send OSC` and `Run manual scripts` (`UI/HaPlay/Views/ControlWorkspaceView.axaml:151-170`).
- The test fields default to a specific X32 emulator/IP scenario (`UI/HaPlay/ViewModels/ControlWorkspaceViewModel.cs:183-193`).
- The script list includes visible instructional text instead of relying on standard affordances/tooltips (`UI/HaPlay/Views/ControlWorkspaceView.axaml:172-183`).

Impact:

- The main screen reads as a configuration/debug hybrid. That is useful during rewrite work, but it takes space from the persistent model the user actually manages: devices, scripts, layers, profile warnings, and monitor.
- `Run manual scripts` is broad and can run all manual triggers; the label does not communicate the scope.
- A hard-coded test target can make a fresh project look preconfigured for one user's lab network.

Recommendation:

- Move `Test send` and `Run manual scripts` behind a diagnostics expander, context menu, or selected-device action.
- Populate test send defaults from the selected OSC device instead of fixed constants.
- Rename `Run manual scripts` to `Run all manual triggers` or add a selected-script/selected-trigger run action.
- Replace persistent instructional text with tooltips or empty-state hints that disappear once scripts exist.

### 7. Medium simplification: the graph rewrite left legacy names and Nodify wiring in active code

At review time, the hard cut away from the graph workspace was mostly done, but several active artifacts still carried graph terminology or dependencies:

- Active control runtime classes under `UI/HaPlay/Control` used the old graph-oriented namespace at review time.
- The app still includes Nodify styles (`UI/HaPlay/App.axaml:19`) and the HaPlay project still references `NodifyM.Avalonia` (`UI/HaPlay/HaPlay.csproj:23`), but current `UI/HaPlay` usage only finds those references and the `Reference` source copy.
- Legacy `ControlGraphs` are still persisted and source-generated (`UI/HaPlay/Models/HaPlayProject.cs:37`, `UI/HaPlay/Models/HaPlayProject.cs:77-90`), with a round-trip compatibility test (`UI/HaPlay.Tests/HaPlayProjectIOTests.cs:597-723`).

Impact:

- Keeping the legacy model is appropriate for project compatibility.
- Keeping graph namespaces and Nodify resources in the active app increases cognitive load and can make future control code look tied to the removed graph editor.
- Loading unused styles/packages adds surface area without current user value.

Recommendation:

- Keep `ControlGraphConfig` as a legacy persistence model, but consider renaming it or documenting it as `LegacyControlGraphConfig`.
- Move active script-centric runtime code to a framework-side control namespace when the next breaking cleanup is acceptable.
- Remove the Nodify package/style include if no current view needs it. Keep the source copy in `Reference` if it is still useful historically.

### 8. Medium product gap: profile repository exists, but the UI does not expose a real profile browser/import/export flow

The profile backend is substantial:

- Directory loading, validation, save, and built-in export helpers exist (`UI/HaPlay/Control/ControlDeviceProfiles.cs:150-263`).
- Project/app/built-in repositories are merged (`UI/HaPlay/Control/ControlDeviceProfiles.cs:265-312`).
- X-Touch Mini and X32 built-ins are implemented (`UI/HaPlay/Control/ControlDeviceProfiles.cs:345-581`).
- The checklist marks profile import/export and profile validation as done (`Doc/HaPlay-MIDI-OSC-Scripting-Rewrite-Checklist.md:62-93`, `Doc/HaPlay-MIDI-OSC-Scripting-Rewrite-Checklist.md:269-279`).

The visible UI only uses profiles indirectly:

- OSC device dialog profile selection (`UI/HaPlay/ViewModels/Dialogs/OscDeviceDialogViewModel.cs:49-57`).
- Profile warnings and X32 command/cache rows (`UI/HaPlay/ViewModels/ControlWorkspaceViewModel.cs:759-770`, `UI/HaPlay/ViewModels/ControlWorkspaceViewModel.cs:806-837`).
- The Control view shows warnings and an X32 command/cache browser, but no profile browser/import/export controls (`UI/HaPlay/Views/ControlWorkspaceView.axaml:59-65`, `UI/HaPlay/Views/ControlWorkspaceView.axaml:130-149`).

Impact:

- The backend is reusable, but users cannot discover installed profiles, inspect profile commands/controls/tasks, or import/export custom profiles from the app.
- The checklist reads more complete than the visible product surface.

Recommendation:

- Add a compact `Profiles` tab or expander in Control with installed profile list, source, validation/load issues, import/export actions, and selected-profile details.
- If that is deferred, adjust the checklist wording to say the repository/API is complete but the profile browser UI is not.

### 9. Low UX: X32 command/cache browser is useful but hard to scan at scale

The X32 browser is collapsed by default and renders a fixed-width row layout without headers, filtering, grouping, or direct actions (`UI/HaPlay/Views/ControlWorkspaceView.axaml:130-149`). The built-in X32 profile generates channel, DCA, bus, matrix, and main commands (`UI/HaPlay/Control/ControlDeviceProfiles.cs:456-550`).

The local X32 reference confirms `/xremote` updates need renewal before 10 seconds (`Reference/UNOFFICIAL_X32_OSC_REMOTE_PROTOCOL.pdf.txt:325-337`) and that `/meters` can return updates around every 50 ms as OSC blobs (`Reference/UNOFFICIAL_X32_OSC_REMOTE_PROTOCOL.pdf.txt:498-519`). The checklist also defers high-rate meter coalescing (`Doc/HaPlay-MIDI-OSC-Scripting-Rewrite-Checklist.md:148`).

Impact:

- With a real X32/M32 profile, a flat fixed-width list will quickly become difficult to scan.
- If meter/cache support expands, users will need filtering and coalescing controls rather than a raw list.

Recommendation:

- Add column headers and a filter field for device, address, command name, and changed cache values.
- Group commands by channel/bus/DCA/matrix/main.
- Add row actions such as copy address, send/request value, and create script trigger/send.
- Keep meter traffic hidden/coalesced by default once meter subscriptions are implemented.

### 10. Low UX: script editor controls can overflow and some labels are ambiguous

The script editor uses a minimum width of 640 (`UI/HaPlay/Views/ScriptEditorWindow.axaml:7-10`), but each trigger row uses a fixed 160 px kind column plus a horizontal stack containing fixed-width function/address/channel/controller/note/interval fields and a remove button (`UI/HaPlay/Views/ScriptEditorWindow.axaml:66-103`). At narrower widths, this is likely to clip or force awkward layout.

The failure policy area labels the threshold as `Failures` and places the failure-mode combo without a label (`UI/HaPlay/Views/ScriptEditorWindow.axaml:31-53`).

Impact:

- Users with smaller windows or long function names may lose access to trigger fields.
- `Failures` does not clearly mean `disable/fault after N consecutive failures`.

Recommendation:

- Wrap trigger fields into multiple rows or use a grid whose optional match fields move below the trigger kind/function fields.
- Rename `Failures` to `Disable after failures` or `Failure threshold`.
- Add an explicit label for the failure action/mode combo.

### 11. Low library robustness: PMLib input polling ignores non-overflow read errors and leaks the wake signal object

`MIDIInputDevice` creates a `ManualResetEventSlim` (`MediaFramework/Extras/MIDI/PMLib/Devices/MIDIInputDevice.cs:14-17`) and sets it during close (`MediaFramework/Extras/MIDI/PMLib/Devices/MIDIInputDevice.cs:91-117`), but the base `Dispose` path only calls `Close` and does not dispose the wake signal (`MediaFramework/Extras/MIDI/PMLib/Devices/MIDIDevice.cs:77-85`).

The polling loop handles positive reads and `BufferOverflow`, but any other negative `Pm_Read` error is ignored and the loop continues silently (`MediaFramework/Extras/MIDI/PMLib/Devices/MIDIInputDevice.cs:147-165`).

Impact:

- Unexpected PortMidi read failures can leave a control input apparently armed but not functioning.
- The undisposed wait handle is small, but it is still avoidable lifecycle debt in a library type.

Recommendation:

- Log non-overflow negative read results, and consider faulting/stopping the input after a repeat threshold.
- Dispose `_pollWakeSignal` in the derived class dispose/close path.

## Positive Checks

- The X-Touch Mini MC-mode profile matches the local `Reference/XTouchMini.txt` note: layer buttons, encoder CCs, push notes, increment/decrement values, button notes, and master fader are represented (`Reference/XTouchMini.txt:1-10`, `UI/HaPlay/Control/ControlDeviceProfiles.cs:361-453`).
- The default X32 `/xremote` interval of 8000 ms stays within the reference requirement to renew before the approximate 10 second timeout (`UI/HaPlay/Control/ControlDeviceProfiles.cs:513-521`, `Reference/UNOFFICIAL_X32_OSC_REMOTE_PROTOCOL.pdf.txt:325-337`).
- The focused HaPlay, OSCLib, and PMLib test suites pass after the review.
