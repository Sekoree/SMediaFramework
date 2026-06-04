# HaPlay MIDI / OSC Scripting Rewrite Checklist

Created: 2026-06-04

This checklist tracks the script-centric MIDI/OSC control rewrite described in
`Doc/HaPlay-MIDI-OSC-Scripting-Rewrite-Plan.md`.

## Status Legend

- `[ ]` Not started.
- `[~]` In progress.
- `[x]` Completed.
- `[!]` Blocked or needs a decision.

## Confirmed Product Decisions

- [x] Do not build an initial node graph view.
- [x] Use a list/tree-grid control workspace organized around devices,
  endpoints, scripts, layers, cache, monitor, and profile browsers.
- [x] Use user scripts as the primary way to connect MIDI controls to OSC
  commands.
- [x] Use separate project-side script files instead of storing all source inline
  in the project JSON.
- [x] Prefer exported Mond functions and importable helper scripts.
- [x] Default the project to one app-level OSC listener on `10020`.
- [x] Support multiple OSC remotes and multiple OSC listeners in one project.
- [x] Share each OSC listener send/receive socket when possible.
- [x] Default monitor visible history to 1000 messages.
- [x] Use JSON lines for monitor capture/replay export.
- [x] Make OSC cache update behavior configurable: incoming-only or optimistic
  send plus incoming.
- [x] Make X32 periodic sends configurable, with `/xremote` as the default.
- [x] Treat device profiles as suggestions, not hard compatibility gates.
- [x] Store user device profiles both per app/user profile and per project, with
  project overrides.
- [x] Keep scripts sandboxed from the filesystem; host-mediated OSC send/request
  and cache APIs are the only phase-1 network path.
- [x] Disable a script after a configurable number of consecutive failures;
  default threshold is 3.
- [x] Run scripts only while the control system is armed/enabled.
- [x] Support project, device, endpoint, and layer script scopes.
- [x] Treat cue action -> control script/state changes as a future enhancement.

## Phase 0: Project Model And Migration Shell

- [x] Add `ControlSystemConfig` to `HaPlayProject`.
- [x] Bump project schema to version 3 and keep loading schema versions 1 and 2.
- [x] Add project JSON round-trip tests for the new control system config.
- [x] Preserve legacy `ControlGraphs` during load/save.
- [x] Add a migration note for legacy graphs: convert simple graph content to
  starter script files later; do not silently discard graph data.
- [x] Add defaults:
  - [x] `isArmed = false`.
  - [x] one default OSC listener on port `10020`.
  - [x] multiple OSC listeners.
  - [x] shared OSC socket mode per listener.
  - [x] monitor max visible messages = 1000.
  - [x] monitor export format = JSON lines.
  - [x] OSC cache mode = incoming-only unless changed by the user.
  - [x] script failure threshold = 3.

## Phase 1: Device Profiles And Repository

- [x] Add built-in profile schema.
- [x] Add profile validation.
- [x] Add profile loader for built-in app profiles.
- [ ] Add profile loader for user/app-level profiles.
- [ ] Add project-level profile overrides.
- [~] Implement profile suggestion behavior:
  - [ ] Show profile warnings.
  - [x] Allow raw MIDI/OSC scripting when a profile command/control is missing.
- [x] Add X-Touch Mini MC-mode profile from `Reference/XTouchMini.txt`:
  - [x] Layer A/B buttons: notes 84 and 85.
  - [x] Encoder strips 1..8: CC 16..23.
  - [x] Encoder push notes: 32..39.
  - [x] Encoder increment values: 1..10.
  - [x] Encoder decrement values: 65..72.
  - [x] Buttons 1..8: notes 89, 90, 40, 41, 42, 43, 44, 45.
  - [x] Buttons 9..16: notes 87, 88, 91, 92, 86, 93, 94, 95.
  - [x] Master fader: pitch wheel.
- [x] Add X32 profile:
  - [x] Host preset `192.168.2.76`.
  - [x] Port `10023`.
  - [x] Channel fader/mute/pan/solo commands.
  - [x] DCA fader/mute commands.
  - [x] Bus fader/mute commands.
  - [x] Matrix fader/mute commands.
  - [x] Main stereo fader/mute commands.
  - [x] X32 fader normalized/db conversions.
  - [x] Default periodic `/xremote` task.
  - [ ] Optional `/subscribe` and `/meters` task definitions.

## Phase 2: Device Binding And Sessions

- [x] Add `ControlDeviceMatcher`.
- [x] Match MIDI devices by:
  - [x] User alias.
  - [x] Exact name.
  - [x] Fuzzy name.
  - [x] Remembered input device ID.
  - [x] Remembered output device ID.
- [ ] Add fallback device selection dialog when matching is ambiguous or missing.
- [x] Split MIDI device instances into input/output capabilities.
- [x] Add script-centric MIDI hardware sessions:
  - [x] Open enabled MIDI input bindings while the control system is armed.
  - [x] Route live CC/note input from more than one MIDI device into scripts.
  - [x] Send script MIDI output through `ControlDeviceInstanceConfig` output bindings.
  - [x] Share a physical MIDI output handle when more than one device instance uses the same output port.
  - [x] Reject ambiguous output names until the fallback selection dialog exists.
- [~] Add app-level OSC listeners:
  - [x] Multiple project-configured local ports.
  - [x] Shared UDP socket per listener where possible.
  - [x] Route incoming OSC packets to device instances.
  - [~] Preserve remote endpoint details for monitor records.
- [ ] Add X32 device behavior:
  - [x] Start configured periodic OSC sends when enabled.
  - [x] Stop periodic sends when disabled.
  - [x] Send configured `/xremote` tasks.
  - [x] Support user-configured additional periodic OSC commands.

## Phase 3: Live Monitor

- [x] Add monitor record model.
- [~] Capture raw and decoded MIDI input.
  - [x] Capture decoded live CC/note input.
  - [ ] Capture raw MIDI bytes from the PortMidi input path.
- [x] Capture MIDI output attempts and results.
- [~] Capture raw and decoded OSC input.
- [x] Capture OSC output attempts and results.
- [x] Capture script emissions.
- [x] Capture cache updates.
- [~] Capture suppression/drop decisions.
- [x] Capture runtime and script errors.
- [x] Add bounded in-memory ring buffer.
- [x] Add visible history limit setting, default `1000`.
- [x] Add JSON lines export.
- [x] Add JSON lines replay/import for tests.
- [x] Add UI filters:
  - [x] Direction.
  - [x] Protocol.
  - [x] Device.
  - [x] Text search.
  - [x] Errors only.
- [x] Add pause/resume and clear.
- [ ] Defer high-rate X32 meter coalescing details until real hardware testing.

## Phase 4: OSC Value Cache

- [x] Add `ControlValueCache`.
- [~] Key cache entries by device instance ID, OSC address, and argument index or
  name.
- [~] Store typed value, raw OSC argument, timestamp, endpoint, freshness, and
  correlation ID.
- [x] Implement incoming-only update mode.
- [x] Implement optimistic send plus incoming update mode.
- [x] Add per-command override support.
- [x] Mark values stale when a device is disabled.
- [x] Expose cache lookup to scripts.
- [ ] Display cache values in the X32 command browser.

## Phase 5: Script Runtime

- [x] Add first starter mapping helper:
  - [x] X-Touch Mini encoder CC16..CC23 -> X32 channel faders 1..8.
  - [x] Default uncached fader value `0.75`.
  - [x] Encoder values `1..10` increase.
  - [x] Encoder values `65..72` decrease.
  - [x] Faster encoder values apply larger deltas.
  - [x] Clamp OSC fader value to `0..1`.
- [x] Add first starter script template:
  - [x] `Scripts/xtouch-mini-x32-faders.mnd`.
  - [x] Exported `onXTouchFaderEncoder` handler.
- [~] Replace per-event ad-hoc script host with compiled script instances.
- [x] Resolve script files by project-relative paths.
- [x] Add script import support for helper files.
- [x] Detect exported handler functions.
- [x] Bind triggers to exported functions.
- [x] Keep instruction limit enforcement.
- [x] Add consecutive failure counters.
- [x] Disable failed scripts after configurable threshold.
- [x] Add compile diagnostics.
- [x] Add runtime diagnostics.
- [~] Add script scopes:
  - [x] Project.
  - [x] Device.
  - [ ] Endpoint.
  - [x] Layer.
- [x] Add host-managed triggers:
  - [x] Device enabled.
  - [x] Device disabled.
  - [x] Device health changed.
  - [x] MIDI message.
  - [x] MIDI CC.
  - [x] MIDI note.
  - [x] OSC message.
  - [x] OSC cache changed.
  - [x] Layer enabled.
  - [x] Layer disabled.
  - [x] Periodic timer.
  - [x] Manual/test trigger.
- [x] Route script OSC sends to configured OSC devices by ID, alias, name, or
  unambiguous profile ID.
- [x] Route script MIDI sends to configured MIDI devices by ID, alias, name, or
  unambiguous profile ID.
- [x] Add script runtime session bridge that dispatches triggers and flushes
  queued OSC/MIDI commands through their routers.
- [x] Add decoded MIDI input dispatcher that resolves input device IDs/names to
  enabled MIDI devices and forwards CC/note events to scripts.
- [x] Add control-system runtime session facade for script ticks, OSC listener
  management, decoded MIDI dispatch, and periodic OSC sends.
- [x] Add Mond libraries:
  - [x] `HaPlay.Devices`.
  - [x] `HaPlay.Midi`.
  - [x] `HaPlay.Osc`.
  - [x] `HaPlay.X32`.
  - [x] `HaPlay.State`.
  - [x] `HaPlay.Monitor`.
  - [x] `HaPlay.Time`.

## Phase 6: Script-Centric UI

- [~] Replace graph-first control workspace with tree/list layout.
  - [x] Remove the graph workspace (hard cut) and host a new `ControlWorkspaceView`.
  - [x] App-shell wiring: live `ControlSystemRuntimeSession` + `ControlMonitorBuffer`,
    arm/disarm lifecycle, project `ControlSystem` load/save, project-relative script root.
  - [x] Live MIDI input/output sessions scoped to the armed control runtime.
  - [x] MIDI manager screen simplified to detected input/output lists with add actions.
  - [x] MIDI manager shows project-selected input/output lists shared by Cue outputs and Control devices.
  - [x] Live monitor view (filter/errors-only/pause/clear) + OSC test-send and manual-run.
  - [x] Device/script tree layout.
- [~] Device tree:
  - [x] MIDI devices.
  - [x] OSC devices.
  - [ ] X32 endpoint and command browser.
  - [ ] Cache view.
  - [x] Layers.
  - [x] Scripts.
  - [x] Periodic sends.
- [ ] Context menus:
  - [ ] Add device script.
  - [ ] Add endpoint script.
  - [ ] Add layer script.
  - [ ] Add project script.
  - [ ] Add imported helper file.
  - [ ] Add periodic OSC send.
  - [ ] Test MIDI send.
  - [ ] Test OSC send.
- [~] Script editor:
  - [x] Script file list.
  - [x] AvaloniaEdit text editor.
  - [x] Save project-relative script files.
  - [x] Script path selector/project-relative path field.
  - [ ] Exported function list.
  - [x] Trigger summary display.
  - [ ] Trigger editor.
  - [x] Scope selector.
  - [x] Enable/disable.
  - [x] Failure policy editor.
  - [ ] Diagnostics panel.
- [ ] Learn mode:
  - [ ] Capture selected MIDI control from monitor traffic.
  - [ ] Generate trigger declaration or script snippet.
  - [ ] Confirm generated trigger before saving.

## Phase 7: Documentation

- [ ] Add user scripting reference.
- [ ] Document custom Mond libraries and functions.
- [ ] Document X-Touch Mini MC-mode setup.
- [ ] Document X32 connection setup.
- [ ] Document app-level OSC listeners and shared socket behavior.
- [ ] Document OSC cache modes.
- [ ] Document JSON lines monitor capture/replay.
- [ ] Add starter scripts:
  - [x] X-Touch Mini encoder -> X32 fader.
  - [ ] X-Touch Mini button -> X32 mute.
  - [ ] Layer enabled -> X32 initial value requests.
  - [ ] X32 cache update -> MIDI LED/ring feedback.

## Phase 8: Validation

- [ ] Unit tests:
  - [x] Project JSON round trips.
  - [x] Profile loading and validation.
  - [x] Device matching.
  - [x] Script-centric MIDI live input/output session routing.
  - [x] OSC app listener routing.
  - [x] Monitor JSON lines export/replay.
  - [x] Cache update modes.
  - [x] Cache change detection and `OscCacheChanged` trigger dispatch.
  - [x] Per-command OSC cache override resolution.
  - [x] Device health-change trigger dispatch and transition dedup.
  - [x] `HaPlay.State` project/script/device scope behavior.
  - [x] `HaPlay.Monitor` script logging with script attribution.
  - [x] `HaPlay.Devices` list/get/isEnabled/health inspection.
  - [x] `HaPlay.Time` host clock (`now`/`nowIso`).
  - [x] `HaPlay.Osc` extended typed args (int64/symbol/nil), request, cache string read.
  - [x] `HaPlay.X32` address builders and fader conversions.
  - [x] `HaPlay.Midi` high-resolution CC send.
  - [x] Script compile and import behavior.
  - [x] Starter X-Touch Mini encoder -> X32 fader script execution.
  - [x] Script failure threshold.
  - [x] Trigger dispatch.
  - [x] Script runtime session bridge.
  - [x] X-Touch Mini relative encoder -> X32 fader helper.
  - [x] MIDI command router.
  - [x] MIDI note trigger dispatch.
  - [x] MIDI catalog input/output registration into control devices.
  - [x] Shared project MIDI input/output list projection.
  - [x] Control workspace structure list projection.
  - [x] Control workspace script editor file load/save.
  - [x] Control workspace script metadata editor.
  - [x] Live monitor direction/protocol/device filters.
  - [x] Periodic OSC send manager.
  - [x] Control-system runtime session tick orchestration.
- [ ] Integration tests:
  - [ ] OSC loopback monitor capture.
  - [x] Fake MIDI input to script trigger.
  - [x] Script output to fake OSC sender.
  - [x] Script output to fake MIDI sender.
  - [x] X32 `/xremote` periodic sends.
  - [x] Background tick loop drives periodic sends and stops cleanly.
- [ ] Manual tests:
  - [ ] X-Touch Mini MIDI input catalog detection.
  - [ ] X-Touch Mini control monitor decode.
  - [ ] X-Touch Mini LED/ring feedback where supported.
  - [ ] X32 emulator at `192.168.2.76:10023`.
  - [ ] Default OSC listener on `10020`.
  - [ ] Additional OSC listener on another project-configured port.
  - [ ] Shared socket behavior.
  - [ ] `/xremote` periodic send for more than 60 seconds.
  - [ ] Cache update from incoming X32 values.
