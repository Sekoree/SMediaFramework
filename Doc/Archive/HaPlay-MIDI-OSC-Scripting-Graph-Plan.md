# HaPlay MIDI / OSC Scripting Graph Plan

Created: 2026-06-03

## Goal

Add a persistent control-workspace to HaPlay where MIDI devices, OSC devices,
scripts, and transform nodes can be connected visually. The initial workflows
should support:

- MIDI input -> OSC output, e.g. move a MIDI fader and send an X32 `/ch/01/mix/fader`.
- OSC input -> MIDI output, e.g. receive mixer state/meter updates and drive motor faders,
  LEDs, scribble-strip style feedback where the MIDI device supports it.
- OSC input -> OSC output, for remapping, filtering, mirroring, and protocol adaptation.
- MIDI input -> MIDI output, for remapping controllers, splitting layers, and building
  soft takeover / scaling behaviors.
- Scriptable transforms using the vendored Mond scripting language reference.
- Device presets, starting with Behringer/Midas X32/M32 OSC control and meter mappings.

The existing cue action system should remain as the simple one-shot path for
cue-triggered OSC/MIDI sends. This new feature is a continuously-running control
graph that can listen, maintain state, and react bidirectionally.

## References Reviewed

- `UI/HaPlay` already references `PMLib` and `OSCLib`.
- Current action cues support one-shot `OscOut` and `MidiOut` through
  `MainViewModel.ExecuteCueActionAsync`.
- `OSCLib` provides `OSCClient`, `OSCServer`, typed `OSCArgument`, bundles, and
  address routing.
- `PMLib` provides `MIDIInputDevice.MessageReceived`, `SysExReceived`, and
  `MIDIOutputDevice.Write`.
- `Reference/Mond-0.11.2` provides the scripting engine; the user called this
  "Mon", but the included source appears to be Mond.
- `Reference/NodifyM.Avalonia-12.0.0` provides MVVM-friendly node editor controls.
- `Reference/UNOFFICIAL_X32_OSC_REMOTE_PROTOCOL.pdf.txt` documents X32/M32:
  UDP port `10023`, `/xremote` deferred updates, `/subscribe`,
  `/formatsubscribe`, `/batchsubscribe`, `/renew`, `/meters`, and common address
  paths such as `/ch/01/mix/fader`, `/main/st`, `/dca`, and `/-stat`.

## Product Shape

Add a new workspace, tentatively `Control`, next to Players, Cues, Outputs, OSC
Connections, MIDI Devices, and Project. This workspace has:

- A graph editor canvas powered by `NodifyM.Avalonia`.
- A node palette grouped by Inputs, Outputs, Transforms, Scripts, State, and
  Presets.
- A property inspector for the selected node/connection.
- A live monitor panel showing recent MIDI/OSC events, transformed values, send
  errors, subscription state, and loop suppression.
- A runtime toggle: `Stopped`, `Starting`, `Running`, `Faulted`.

Keep OSC/MIDI endpoint management screens, but let graph nodes reference those
endpoints instead of re-entering host/device details per node.

## Core Runtime Architecture

### Event Bus

Create an internal typed event bus:

```csharp
public abstract record ControlEvent(DateTimeOffset Timestamp, string SourceNodeId);
public sealed record MidiControlEvent(...) : ControlEvent;
public sealed record OscControlEvent(...) : ControlEvent;
public sealed record ScalarControlEvent(...) : ControlEvent;
public sealed record TextControlEvent(...) : ControlEvent;
public sealed record BlobControlEvent(...) : ControlEvent;
```

Reasons:

- MIDI and OSC have different payload shapes, but graph nodes need a common
  envelope for timing, source tracking, diagnostics, and loop suppression.
- Scripts should work with simple value objects rather than directly owning
  device handles.
- OSC blobs and X32 meter payloads should remain representable without lossy
  conversion.

### Runtime Services

Add a `HaPlay.Control` namespace or folder with these services:

- `ControlGraphRuntime`: owns compiled nodes, connections, state, and lifecycle.
- `MidiDeviceSession`: opens one MIDI input/output device per referenced endpoint,
  subscribes to `PMLib` events, and exposes send methods.
- `OscEndpointSession`: owns `OSCClient` and optional `OSCServer` listener for
  local ports.
- `X32Session`: wraps X32-specific remote lifecycle: `/xremote` renewals,
  subscriptions, meter renewals, reconnect/backoff, and parser helpers.
- `ControlEventDispatcher`: delivers events through graph connections with
  throttling, coalescing, and loop guards.
- `ControlScriptHost`: embeds Mond and exposes a constrained API.

### Threading Model

- Device callbacks arrive on background threads.
- The graph runtime processes events on a single serial worker queue per graph.
- UI state updates are posted to `Dispatcher.UIThread`.
- Device sends run async and should not block graph evaluation.
- High-rate streams, especially X32 meters, must support coalescing and frame-rate
  limited UI updates.

### Loop Suppression

Bidirectional mappings can easily echo forever:

1. MIDI fader sends OSC fader.
2. Mixer echoes OSC fader update.
3. OSC update drives MIDI motor fader.
4. MIDI device reports motor movement.

Each event should carry:

- `OriginId`: stable id for the physical device/source that created it.
- `CorrelationId`: generated for a user action or inbound packet.
- `Path`: node ids visited.
- `SuppressUntil`: optional deadline for feedback mute.

Mapping nodes should expose a `Feedback mode`:

- `Send feedback`: normal bidirectional behavior.
- `Do not echo to origin`: default for mirrored controls.
- `Soft takeover`: ignore device input until it crosses the current remote value.
- `Motor feedback only`: send MIDI feedback but do not re-trigger OSC.

## Persisted Model

Extend `HaPlayProject` with a new top-level collection:

```csharp
public List<ControlGraphConfig> ControlGraphs { get; init; } = new();
```

Use a schema-version bump because this is persisted project state.

Suggested model:

- `ControlGraphConfig`
  - `Id`, `Name`, `IsEnabled`, `Nodes`, `Connections`, `Viewport`, `PresetRefs`
- `ControlNodeConfig`
  - `Id`, `Kind`, `DisplayName`, `Position`, `Settings`, `Ports`
- `ControlConnectionConfig`
  - `Id`, `FromNodeId`, `FromPortId`, `ToNodeId`, `ToPortId`
- `ControlPresetConfig`
  - `PresetId`, `DeviceKind`, `Version`, `Parameters`

Store node settings as typed records rather than loose JSON when possible. If
plugin/preset nodes need flexible data, use a small `JsonElement Settings` island
with version fields and explicit migration.

## Node Types

### Inputs

- `MIDI Input`
  - Select endpoint/device, channel filter, message type filter, controller/note filter.
  - Emits `MidiControlEvent`.
- `OSC Input`
  - Select local listen port or endpoint session, address pattern, argument filter.
  - Emits `OscControlEvent`.
- `X32 Subscription`
  - Select X32 endpoint, address or preset field, renewal interval/frequency.
  - Emits normalized `OscControlEvent` or scalar values.
- `X32 Meter`
  - Select meter bank and channel/range, parse blob into channel levels.
  - Emits scalar arrays or per-channel meter events.
- `Timer / Tick`
  - Useful for polling, rate limiting, and scripted periodic sends.

### Outputs

- `MIDI Output`
  - Send note, CC, program change, pitch bend, SysEx, or raw event.
  - Supports feedback throttling for motor faders/LEDs.
- `OSC Output`
  - Send arbitrary OSC address + typed arguments to endpoint.
- `X32 Parameter Set`
  - Device-aware OSC output with unit conversion and address templates.
- `Log / Monitor`
  - Development aid; displays payloads in the live monitor.

### Transforms

- `Map Range`
  - Map MIDI 0..127 to OSC float 0..1, dB curves, pan ranges, etc.
- `Curve`
  - Linear, log, exponential, X32 fader law, custom points.
- `Filter`
  - Drop events by channel, address, value threshold, duplicate value, deadband.
- `Latch / Toggle`
  - Convert button press to toggled state.
- `Gate`
  - Enable/disable flow based on another value.
- `Merge / Split`
  - Combine variables into OSC argument lists or split incoming OSC args.
- `Rate Limit / Debounce`
  - Essential for faders, meters, and noisy controls.
- `State Variable`
  - Store last value, expose it to scripts and other nodes.

### Script Nodes

- `Script Transform`
  - Input event(s) -> output event(s).
- `Script Condition`
  - Return bool to pass/drop.
- `Script Action`
  - Can emit MIDI/OSC events via host API.

Mond host API should be small and deterministic:

```text
event.type
event.midi.channel
event.midi.controller
event.midi.value
event.osc.address
event.osc.args
state.get(name)
state.set(name, value)
emit.scalar(name, value)
emit.osc(address, args)
emit.midi.cc(channel, controller, value)
math.map(value, inMin, inMax, outMin, outMax)
x32.faderToDb(value)
x32.dbToFader(db)
```

Avoid giving scripts direct filesystem/network access in phase 1. Add execution
timeouts and error counters so a bad script cannot stall the graph.

## X32 / M32 Presets

Start with a built-in preset library, versioned and testable:

### Endpoint Preset

- Host/IP, port `10023`.
- Optional local listen port.
- `/xremote` renewal every 8 seconds while running.
- Subscription renewal every 8 seconds for `/subscribe` and `/meters` streams
  that time out around 10 seconds.

### Channel Strip Presets

Per channel `01..32`:

- Fader: `/ch/{nn}/mix/fader`, float `0..1`.
- Mute/on: `/ch/{nn}/mix/on`, int/bool depending on OSC response.
- Pan: `/ch/{nn}/mix/pan`, float.
- Solo status where applicable through `/-stat/solosw/{nn}`.
- Name/color/icon if useful for label feedback.

### Bus / DCA / Main Presets

- DCA fader and mute for `/dca/{n}` paths.
- Main stereo fader/mute under `/main/st`.
- Bus sends and matrices as second wave.

### Meter Presets

Expose X32 meter nodes as named options rather than raw ids:

- Input channel levels.
- Gate/dynamics reduction.
- Bus/main meters.
- Selected-channel detailed meters.

Implementation should parse meter blobs into stable channel labels and normalized
values. UI meters and MIDI LED feedback should consume normalized scalar events,
not raw blobs.

### Conversion Helpers

The reference includes an appendix for converting X32 fader data to dB and back.
Implement this as a tested helper:

- `X32Fader.FromNormalized(float value) -> dB`
- `X32Fader.ToNormalized(float db) -> float`
- Support `-inf`, practical floor, unity, and +10 dB range.

## UI Plan

### Workspace Layout

- Left: node palette and preset browser.
- Center: Nodify graph canvas.
- Right: selected node inspector.
- Bottom: live event monitor and diagnostics.

### Node Design

View-models should implement NodifyM's `INodePosition` rather than depending on
sample base classes. Use local VM types:

- `ControlGraphViewModel`
- `ControlNodeViewModel`
- `ControlPortViewModel`
- `ControlConnectionViewModel`
- `ControlPresetViewModel`

Use compiled bindings and explicit data templates in `ControlGraphView.axaml`.

### Authoring Flow

Examples:

1. Drag `MIDI Input` node.
2. Pick controller device, channel 1, CC 0.
3. Drag `Map Range`.
4. Drag `X32 Channel Fader` preset node for channel 1.
5. Connect MIDI value -> map -> X32 fader.
6. Enable graph and move fader.
7. Add reverse connection X32 fader -> MIDI motor fader feedback with loop
   suppression.

## Runtime Details

### MIDI Handling

Normalize incoming messages:

- Note on/off.
- Control change, including optional 14-bit high-resolution CC.
- Program change.
- Pitch bend.
- SysEx as blob.

Outputs should support:

- 7-bit CC.
- 14-bit CC.
- Note LED modes: momentary, toggle, velocity brightness.
- Pitch bend.
- SysEx templates for devices that require feedback messages.

### OSC Handling

Normalize incoming messages:

- Address.
- Typed argument list.
- Remote endpoint.
- Receive timestamp.

Outputs should support:

- Typed argument construction.
- Argument templates using input variables.
- Bundles later, but not required in phase 1.

### Device Session Ownership

The runtime should open each physical MIDI or OSC session once per graph runtime,
even if several nodes reference it. Reference count graph nodes internally and
restart only affected sessions on node edits.

### Hot Editing

Phase 1 can require stopping/restarting the graph after structural edits. Later:

- Adding/removing transform nodes can be hot-swapped.
- Device endpoint changes restart only the affected session.
- Script edits recompile only that node.

## Implementation Phases

### Phase 0: Project and Package Decisions

- Decide whether to reference `NodifyM.Avalonia` and `Mond` as NuGet packages or
  local project references from `Reference`.
- Recommendation: use NuGet package references for app dependencies, keep
  `Reference` as source/reference only unless local patches are required.
- Add package references centrally in `Directory.Packages.props` if NuGet is used.

### Phase 1: Headless Runtime Core

- Add persisted `ControlGraphConfig` model and project JSON coverage.
- Implement typed `ControlEvent`.
- Implement graph compiler/validator:
  - port type compatibility,
  - missing endpoint detection,
  - cycle policy,
  - script compile errors.
- Implement basic runtime with fake device adapters for tests.
- Add MIDI input -> OSC output and OSC input -> MIDI output unit tests without UI.

### Phase 2: Real MIDI/OSC Sessions

- Wrap `PMLib.MIDIInputDevice` and `MIDIOutputDevice`.
- Wrap `OSCLib.OSCServer` and `OSCClient`.
- Add runtime diagnostics and health states.
- Add manual test nodes to send/receive events.
- Add loop suppression and rate limiting.

### Phase 3: UI Graph Editor

- Add `Control` workspace.
- Integrate NodifyM canvas with node palette, inspector, connections, and monitor.
- Add create/delete/connect/select/move persistence.
- Add validation badges on nodes and connections.

### Phase 4: Mond Script Nodes

- Add `ControlScriptHost`.
- Expose constrained host API.
- Add compile/runtime errors in UI.
- Add unit tests for transformations and timeouts.
- Add built-in examples:
  - MIDI CC -> X32 fader,
  - OSC fader -> MIDI motor feedback,
  - mute toggle with LED feedback.

### Phase 5: X32 Preset Library

- Add `X32DevicePreset` definitions.
- Add channel strip, DCA, main stereo, and meter nodes.
- Implement `/xremote` renewal.
- Implement `/subscribe` and `/meters` renewal.
- Implement fader dB conversion helper with tests.
- Add X32 quick-start wizard:
  - mixer host,
  - choose MIDI controller,
  - map 8/16 faders to channels,
  - enable bidirectional feedback.

### Phase 6: Polish and Safety

- Add import/export for graph presets.
- Add duplicate graph and template library.
- Add event recorder/replay for debugging mappings without hardware.
- Add rate warnings for high-frequency OSC/MIDI loops.
- Add per-node enable/disable and bypass.
- Add project migration notes.

## Testing Strategy

### Unit Tests

- Graph validation and compilation.
- Port type compatibility.
- MIDI message normalization.
- OSC argument normalization.
- Range maps and X32 fader conversion.
- Loop suppression and correlation behavior.
- Script compile/runtime failure handling.
- Project JSON round trips.

### Integration Tests

- OSC UDP loopback: server receives client sends.
- MIDI adapter tests with fake `IMidiInputSession` / `IMidiOutputSession`.
- Graph runtime tests for:
  - MIDI -> OSC,
  - OSC -> MIDI,
  - OSC -> OSC,
  - MIDI -> MIDI,
  - bidirectional feedback with echo suppression.

### UI Tests

- Add graph node, connect ports, persist positions.
- Node inspector edits update config.
- Validation errors display.
- Event monitor receives synthetic runtime events.

### Manual Hardware Tests

- X32/M32 over Ethernet:
  - connect,
  - send fader,
  - receive `/xremote` update,
  - subscribe to fader and meter,
  - renew subscriptions for >60 seconds.
- MIDI controller:
  - CC input,
  - motor fader feedback if available,
  - LED feedback,
  - reconnect after unplug/replug.

## Risks and Mitigations

- **Feedback loops**: default to no-echo-to-origin, correlation ids, rate limits.
- **High-rate meters**: coalesce meter updates and cap UI refresh rate.
- **UDP loss**: X32 subscriptions need renewal and state reconciliation.
- **Script stalls**: per-node timeout, cancellation, and runtime fault isolation.
- **Hardware hotplug**: sessions should expose degraded health and retry without
  crashing the graph.
- **Project complexity**: keep simple cue action commands separate from graph
  runtime; do not force users into node editing for basic cue sends.

## Open Questions / Reference Needed

- Which MIDI controllers are the initial targets? Motor fader and LED feedback
  vary widely and often require device-specific SysEx.
- Should graphs run globally whenever the project is open, or only when a show
  is armed/running?
- Should cue actions be able to trigger graph inputs or set graph variables?
- Do you want local OSC listen ports per graph, per endpoint, or one app-wide
  OSC server with routing?
- For X32, should the first preset focus on 8-fader bank control, 16/32 channel
  direct mapping, or a custom layer model?

## Suggested First Implementation Slice

Build the smallest useful vertical slice:

1. Persist one `ControlGraphConfig` in the project.
2. Add a non-visual runtime with fake device tests.
3. Implement nodes:
   - MIDI Input,
   - Map Range,
   - OSC Output.
4. Add one X32 preset: channel fader `/ch/{nn}/mix/fader`.
5. Add a minimal Control workspace with node list + property editor; use Nodify
   canvas after the runtime contracts are stable.

This delivers MIDI fader -> X32 fader quickly while keeping the architecture
ready for bidirectional feedback, meters, and scripting.

