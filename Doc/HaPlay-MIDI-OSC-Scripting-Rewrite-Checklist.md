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
- [x] Default the app-wide OSC listen port to `10020`, stored per project.
- [x] Use one app-wide OSC listener and share the send/receive socket when
  possible.
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
  - [x] `oscListenPort = 10020`.
  - [x] shared OSC socket mode.
  - [x] monitor max visible messages = 1000.
  - [x] monitor export format = JSON lines.
  - [x] OSC cache mode = incoming-only unless changed by the user.
  - [x] script failure threshold = 3.

## Phase 1: Device Profiles And Repository

- [ ] Add built-in profile schema.
- [ ] Add profile validation.
- [ ] Add profile loader for built-in app profiles.
- [ ] Add profile loader for user/app-level profiles.
- [ ] Add project-level profile overrides.
- [ ] Implement profile suggestion behavior:
  - [ ] Show profile warnings.
  - [ ] Allow raw MIDI/OSC scripting when a profile command/control is missing.
- [ ] Add X-Touch Mini MC-mode profile from `Reference/XTouchMini.txt`:
  - [ ] Layer A/B buttons: notes 84 and 85.
  - [ ] Encoder strips 1..8: CC 16..23.
  - [ ] Encoder push notes: 32..39.
  - [ ] Encoder increment values: 1..10.
  - [ ] Encoder decrement values: 65..72.
  - [ ] Buttons 1..8: notes 89, 90, 40, 41, 42, 43, 44, 45.
  - [ ] Buttons 9..16: notes 87, 88, 91, 92, 86, 93, 94, 95.
  - [ ] Master fader: pitch wheel.
- [ ] Add X32 profile:
  - [ ] Host preset `192.168.2.76`.
  - [ ] Port `10023`.
  - [ ] Channel fader/mute/pan/solo commands.
  - [ ] DCA fader/mute commands.
  - [ ] Bus fader/mute commands.
  - [ ] Matrix fader/mute commands.
  - [ ] Main stereo fader/mute commands.
  - [ ] X32 fader normalized/db conversions.
  - [ ] Default periodic `/xremote` task.
  - [ ] Optional `/subscribe` and `/meters` task definitions.

## Phase 2: Device Binding And Sessions

- [ ] Add `ControlDeviceMatcher`.
- [ ] Match MIDI devices by:
  - [ ] User alias.
  - [ ] Exact name.
  - [ ] Fuzzy name.
  - [ ] Remembered input device ID.
  - [ ] Remembered output device ID.
- [ ] Add fallback device selection dialog when matching is ambiguous or missing.
- [ ] Split MIDI device instances into input/output capabilities.
- [ ] Add app-wide OSC listener:
  - [ ] Project-configured local port.
  - [ ] Shared UDP socket where possible.
  - [ ] Route incoming OSC packets to device instances.
  - [ ] Preserve remote endpoint details in monitor records.
- [ ] Add X32 device behavior:
  - [ ] Start configured periodic OSC sends when enabled.
  - [ ] Stop periodic sends when disabled.
  - [ ] Send `/xremote` by default.
  - [ ] Support user-configured additional periodic OSC commands.

## Phase 3: Live Monitor

- [ ] Add monitor record model.
- [ ] Capture raw and decoded MIDI input.
- [ ] Capture MIDI output attempts and results.
- [ ] Capture raw and decoded OSC input.
- [ ] Capture OSC output attempts and results.
- [ ] Capture script emissions.
- [ ] Capture cache updates.
- [ ] Capture suppression/drop decisions.
- [ ] Capture runtime and script errors.
- [ ] Add bounded in-memory ring buffer.
- [ ] Add visible history limit setting, default `1000`.
- [ ] Add JSON lines export.
- [ ] Add JSON lines replay/import for tests.
- [ ] Add UI filters:
  - [ ] Direction.
  - [ ] Protocol.
  - [ ] Device.
  - [ ] Text search.
  - [ ] Errors only.
- [ ] Add pause/resume and clear.
- [ ] Defer high-rate X32 meter coalescing details until real hardware testing.

## Phase 4: OSC Value Cache

- [ ] Add `ControlValueCache`.
- [ ] Key cache entries by device instance ID, OSC address, and argument index or
  name.
- [ ] Store typed value, raw OSC argument, timestamp, endpoint, freshness, and
  correlation ID.
- [ ] Implement incoming-only update mode.
- [ ] Implement optimistic send plus incoming update mode.
- [ ] Add per-command override support.
- [ ] Mark values stale when a device is disabled.
- [ ] Expose cache lookup to scripts.
- [ ] Display cache values in the X32 command browser.

## Phase 5: Script Runtime

- [ ] Replace per-event ad-hoc script host with compiled script instances.
- [ ] Resolve script files by project-relative paths.
- [ ] Add script import support for helper files.
- [ ] Detect exported handler functions.
- [ ] Bind triggers to exported functions.
- [ ] Keep instruction limit enforcement.
- [ ] Add consecutive failure counters.
- [ ] Disable failed scripts after configurable threshold.
- [ ] Add compile diagnostics.
- [ ] Add runtime diagnostics.
- [ ] Add script scopes:
  - [ ] Project.
  - [ ] Device.
  - [ ] Endpoint.
  - [ ] Layer.
- [ ] Add host-managed triggers:
  - [ ] Device enabled.
  - [ ] Device disabled.
  - [ ] Device health changed.
  - [ ] MIDI message.
  - [ ] MIDI CC.
  - [ ] MIDI note.
  - [ ] OSC message.
  - [ ] OSC cache changed.
  - [ ] Layer enabled.
  - [ ] Layer disabled.
  - [ ] Periodic timer.
  - [ ] Manual/test trigger.
- [ ] Add Mond libraries:
  - [ ] `HaPlay.Devices`.
  - [ ] `HaPlay.Midi`.
  - [ ] `HaPlay.Osc`.
  - [ ] `HaPlay.X32`.
  - [ ] `HaPlay.State`.
  - [ ] `HaPlay.Monitor`.
  - [ ] `HaPlay.Time`.

## Phase 6: Script-Centric UI

- [ ] Replace graph-first control workspace with tree/list layout.
- [ ] Device tree:
  - [ ] MIDI devices.
  - [ ] OSC devices.
  - [ ] X32 endpoint and command browser.
  - [ ] Cache view.
  - [ ] Layers.
  - [ ] Scripts.
  - [ ] Periodic sends.
- [ ] Context menus:
  - [ ] Add device script.
  - [ ] Add endpoint script.
  - [ ] Add layer script.
  - [ ] Add project script.
  - [ ] Add imported helper file.
  - [ ] Add periodic OSC send.
  - [ ] Test MIDI send.
  - [ ] Test OSC send.
- [ ] Script editor:
  - [ ] Script path selector.
  - [ ] Exported function list.
  - [ ] Trigger editor.
  - [ ] Scope selector.
  - [ ] Enable/disable.
  - [ ] Failure policy editor.
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
- [ ] Document app-wide OSC listener and shared socket behavior.
- [ ] Document OSC cache modes.
- [ ] Document JSON lines monitor capture/replay.
- [ ] Add starter scripts:
  - [ ] X-Touch Mini encoder -> X32 fader.
  - [ ] X-Touch Mini button -> X32 mute.
  - [ ] Layer enabled -> X32 initial value requests.
  - [ ] X32 cache update -> MIDI LED/ring feedback.

## Phase 8: Validation

- [ ] Unit tests:
  - [ ] Project JSON round trips.
  - [ ] Profile loading and validation.
  - [ ] Device matching.
  - [ ] OSC app listener routing.
  - [ ] Monitor JSON lines export/replay.
  - [ ] Cache update modes.
  - [ ] Script compile and import behavior.
  - [ ] Script failure threshold.
  - [ ] Trigger dispatch.
- [ ] Integration tests:
  - [ ] OSC loopback monitor capture.
  - [ ] Fake MIDI input to script trigger.
  - [ ] Script output to fake OSC sender.
  - [ ] Script output to fake MIDI sender.
  - [ ] X32 `/xremote` periodic sends.
- [ ] Manual tests:
  - [ ] X-Touch Mini MIDI input catalog detection.
  - [ ] X-Touch Mini control monitor decode.
  - [ ] X-Touch Mini LED/ring feedback where supported.
  - [ ] X32 emulator at `192.168.2.76:10023`.
  - [ ] App-wide OSC listener on `10020`.
  - [ ] Shared socket behavior.
  - [ ] `/xremote` periodic send for more than 60 seconds.
  - [ ] Cache update from incoming X32 values.
