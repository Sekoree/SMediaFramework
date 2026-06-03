# HaPlay MIDI / OSC Scripting Graph Checklist

Source plan: `Doc/HaPlay-MIDI-OSC-Scripting-Graph-Plan.md`

## Decisions From Initial Planning

- [x] Scripting language is Mond.
- [x] Initial MIDI controller targets:
  - Behringer X-Touch Mini in direct / MC mode.
  - Behringer BCF2000 with motor faders configured for 14-bit CC mode.
- [x] X32/M32 control should use a custom layer model.
- [x] Mixer mappings should be exposed as selectable presets so users do not
  need to look up OSC paths manually.
- [x] `Mond` and `NodifyM.Avalonia` are available as package references in the
  current project configuration.

## Phase 1: Headless Runtime Foundation

- [x] Add persisted `ControlGraphConfig` to `HaPlayProject`.
- [x] Bump project schema and update schema-version tests.
- [x] Add typed control graph nodes, connections, ports, and settings records.
- [x] Add control event model for MIDI, OSC, scalar, text, and blob events.
- [x] Add graph validation for missing nodes, missing ports, and incompatible
  port types.
- [x] Add fake-device runtime adapters for tests.
- [x] Implement `MIDI Input -> Map Range -> OSC Output` in the headless runtime.
- [x] Add unit tests for graph persistence and MIDI-to-OSC mapping.

## Phase 2: Preset Primitives

- [x] Add X32 channel fader preset helper (`/ch/{nn}/mix/fader`).
- [x] Add X32 custom layer model:
  - layer id/name,
  - fader slots,
  - target kind (`Channel`, `Bus`, `Dca`, `MainStereo`),
  - target index,
  - optional label/color.
- [x] Add BCF2000 14-bit CC mapping helper.
- [x] Add X-Touch Mini direct / MC mode mapping helper.
- [x] Add tests for preset address generation and MIDI value normalization.

## Phase 3: Real Device Sessions

- [x] Wrap `PMLib.MIDIInputDevice` and `MIDIOutputDevice` behind runtime
  interfaces.
- [x] Wrap `OSCLib.OSCServer` and `OSCClient` behind runtime interfaces.
- [x] Add endpoint/session ownership and reference counting.
  - [x] Add first-cut outgoing session ownership/caching.
  - [x] Add shared PortMidi initialization lease for MIDI input/output sessions.
- [x] Add graph runtime start/stop lifecycle.
- [ ] Add reconnect/health diagnostics.
  - [x] Add first-cut session health state.

## Phase 4: Bidirectional Feedback

- [x] Add correlation ids and origin ids to all control events.
- [x] Add no-echo-to-origin loop suppression.
- [x] Add soft takeover behavior.
- [x] Add motor-feedback-only behavior for BCF2000 faders.
- [x] Add feedback throttling/rate limiting.
- [x] Add OSC-to-MIDI feedback tests.

## Phase 5: X32/M32 Runtime Support

- [x] Add X32 endpoint preset with port `10023`.
- [x] Add `/xremote` renewal.
- [x] Add `/subscribe` renewal.
- [x] Add `/meters` renewal and blob parsing.
- [x] Add channel strip presets for fader, mute, pan, solo/status.
- [x] Add DCA, main stereo, bus, and matrix presets.
- [x] Add X32 fader normalized-value and dB conversion helpers.

## Phase 6: Mond Scripting

- [ ] Add constrained `ControlScriptHost`.
- [ ] Expose event, state, emit, math, MIDI, OSC, and X32 helper APIs.
- [ ] Add script compile/runtime diagnostics.
- [ ] Add per-node execution timeout.
- [ ] Add tests for script transforms and failure isolation.

## Phase 7: NodifyM UI

- [ ] Add `Control` workspace to the main shell.
- [ ] Add graph list and runtime start/stop controls.
- [ ] Add NodifyM graph canvas.
- [ ] Add node palette grouped by Inputs, Outputs, Transforms, Scripts, State,
  and Presets.
- [ ] Add selected-node inspector.
- [ ] Add live event monitor.
- [ ] Persist node positions, connections, and viewport.

## Phase 8: Preset UX

- [ ] Add X32 quick-start wizard:
  - mixer host,
  - MIDI controller,
  - custom layer slots,
  - fader/mute/LED feedback options.
- [ ] Add selectable presets for:
  - X32 channel faders,
  - X32 DCA faders,
  - X32 main fader,
  - BCF2000 14-bit faders,
  - X-Touch Mini knobs/buttons.
- [ ] Add import/export for user graph presets.

## Verification

- [ ] Run `bash -c 'dotnet build MFPlayer.sln'`.
- [ ] Run `bash -c 'dotnet test MFPlayer.sln --no-build'`.
- [x] Run focused control-graph tests:
  `bash -c 'dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --no-build --filter "FullyQualifiedName~ControlGraphRuntimeTests|FullyQualifiedName~RoundTrip_ControlGraphs"'`.
- [x] Run full HaPlay test project:
  `bash -c 'dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --no-build'`.
- [x] Run control graph/session focused tests with graph lifecycle:
  `bash -c 'dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --no-build --filter "FullyQualifiedName~ControlGraphRuntimeTests|FullyQualifiedName~ControlDeviceSessionTests|FullyQualifiedName~RoundTrip_ControlGraphs"'`.
- [x] Update this checklist after each implementation slice.

