# HaPlay MIDI / OSC Scripting Rewrite Plan

Created: 2026-06-04

## Goal

Replace the current graph-first MIDI/OSC scripting workflow with a clearer,
device-centric control system for HaPlay.

The rewrite should make the common live-show workflow easy:

- Connect a MIDI controller such as the Behringer X-Touch Mini.
- Connect an OSC device such as the X32 emulator at `192.168.2.76:10023`.
- See all incoming and outgoing MIDI/OSC traffic live.
- Pick known device presets instead of manually building a graph.
- Write Mond scripts against stable MIDI, OSC, device, state, and X32 libraries.
- Run scripts from events such as device enabled, device disabled, MIDI input,
  OSC input, layer changes, and periodic device tasks.
- Cache the latest received OSC values so scripts and UI can initialize feedback
  without waiting for the next broadcast.

This document supersedes the archived node-editor plan in
`Doc/Archive/HaPlay-MIDI-OSC-Scripting-Graph-Plan.md` for future work. The
existing code has useful protocol/session pieces, but the authoring model should
be rewritten.

## Current State Reviewed

- `UI/HaPlay/Models/ControlGraphConfig.cs` persists graph nodes for MIDI input,
  OSC input, mapping, OSC output, MIDI output, X32 channel faders, and Mond
  script transforms.
- `UI/HaPlay/Control/ControlGraphRuntime.cs` can route typed `ControlEvent`
  instances, map scalar ranges, send OSC/MIDI, suppress simple feedback echoes,
  rate-limit sends, and handle soft takeover.
- `UI/HaPlay/Control/ControlScriptHost.cs` embeds Mond and exposes small
  handwritten `state`, `emit`, `math`, and `x32` objects, but it compiles/runs a
  wrapped function per event and does not use Mond's library manager model.
- `UI/HaPlay/Control/ControlDeviceSessions.cs` has real OSC input/output and MIDI
  output/input session wrappers.
- `UI/HaPlay/Control/X32Session.cs` can send `/xremote`, `/subscribe`, and
  `/meters` renewals, but it is not yet integrated as a first-class device
  lifecycle in the graph session.
- `UI/HaPlay/Control/ControlPresets.cs` contains useful X32 addresses, fader
  conversion helpers, and starter BCF2000/X-Touch Mini layer builders, but these
  are hard-coded helpers rather than a browseable device/preset repository.
- `UI/HaPlay/ViewModels/ControlGraphWorkspaceViewModel.cs` and
  `UI/HaPlay/Views/ControlGraphWorkspaceView.axaml` expose a node editor and
  basic monitor.
- `Reference/XTouchMini.txt` documents the connected X-Touch Mini MIDI mapping
  in MC mode.

Important gap: endpoint IDs exist in node settings, but the current editor
snapshot drops endpoint bindings when it rebuilds MIDI/OSC node settings. Real
hardware sessions require those endpoint IDs, so device binding should be fixed
or replaced early.

## Rewrite Boundary

Keep these pieces unless a concrete problem appears:

- `PMLib` MIDI parsing and PortMidi device wrappers.
- `OSCLib` packet codec, server, client, router, typed arguments, and tests.
- X32 fader conversion helpers and address helpers after moving them into the
  profile/catalog layer.
- The `ControlEvent` idea: timestamp, origin, correlation, source, and path are
  still useful.
- Existing tests as regression coverage where they match the new architecture.

Replace or heavily revise:

- The graph canvas. Do not include a graph view in the initial rewrite.
- Hard-coded preset helper methods as the only source of device knowledge.
- The ad-hoc script host API and per-event compile/run lifecycle.
- Endpoint editing that only models one output device path and does not clearly
  separate MIDI input, MIDI output, OSC remote, and OSC local listener concerns.
- The live monitor as only a formatted `ControlEvent` list.

## Product Shape

Use a `Control` workspace, but make it device-first instead of node-first:

- `Devices`: configured MIDI and OSC devices, profile selection, current health,
  enable/disable, local listen port, and test send/receive controls.
- `Scripts`: script list, trigger list, editor, compile/runtime diagnostics, and
  sample snippets. User scripts are the primary way to connect MIDI controls to
  OSC commands.
- `Monitor`: incoming/outgoing MIDI and OSC packets with filters, raw details,
  decoded values, correlation IDs, errors, and export/replay.
- `Cache`: latest OSC values by device/address/argument, plus last MIDI control
  values by device/control.
- `Profiles`: browser for known control surfaces and OSC devices.

The first UI should be a list or tree grid, not a graph. A typical tree shape:

```text
Control System
  MIDI: X-Touch Mini
    Controls
    Scripts
    Layers
  OSC: X32
    Endpoint 192.168.2.76:10023
    Listener Main OSC Listener:10020
    Commands
    Cache
    Scripts
    Periodic Sends
  OSC: Additional Device
    Endpoint ...
    Listener ...
```

Use context menus on devices, endpoints, layers, and scripts to add scripts,
startup tasks, periodic OSC sends, imported helper files, and test sends.

## Device Repository

Add a versioned repository of device profiles. Load built-in profiles from the
app assembly and user profiles from a project/user directory.

Suggested model:

```csharp
public sealed record ControlDeviceProfile(
    string Id,
    string DisplayName,
    ControlDeviceProtocol Protocol,
    string Version,
    IReadOnlyList<ControlDevicePortProfile> Ports,
    IReadOnlyList<ControlControlProfile> Controls,
    IReadOnlyList<ControlCommandProfile> Commands,
    IReadOnlyList<ControlLayerProfile> Layers,
    IReadOnlyList<ControlDeviceTaskProfile> Tasks);
```

Profiles should describe capabilities, not active connections. Projects should
persist `DeviceInstance` records that point to profiles and bind them to actual
PortMidi devices, OSC host/port pairs, and local listener ports.

Profiles are suggestions and helpers, not hard compatibility gates. If a command
or control is unavailable in the selected profile, the UI should warn and let the
user choose a raw MIDI/OSC script path instead of blocking the project.

### X-Touch Mini Profile

Initial profile target:

- 8 rotary encoders with LED feedback where supported.
- Encoder push buttons.
- 16 illuminated buttons.
- 1 non-motorized fader.
- HaPlay-managed software layers. In MC mode the X-Touch Mini layer buttons are
  just normal note buttons, so HaPlay should treat them as ordinary inputs that
  can switch HaPlay layers.

MC mode mapping from `Reference/XTouchMini.txt`:

- Layer buttons: notes 84 and 85.
- Encoder strips 1..8: CC 16..23, push notes 32..39.
- Encoder turn right: values 1..10 depending on turn speed.
- Encoder turn left: values 65..72 depending on turn speed.
- Buttons 1..8: notes 89, 90, 40, 41, 42, 43, 44, 45.
- Buttons 9..16: notes 87, 88, 91, 92, 86, 93, 94, 95.
- Master fader: pitch wheel.

Still add a `Learn` mode. It is useful for validating device mode, alternate
firmware/settings, and future MIDI surfaces.

Because the X-Touch Mini fader is not motorized, remote value initialization
should use soft takeover and visible UI/cache state rather than trying to move
the physical fader. LEDs and encoder rings can still receive feedback if the
device mode supports it.

### X32 Profile

Initial profile target:

- Default endpoint preset: host `192.168.2.76`, port `10023`.
- Channel `01..32`: fader, mute/on, pan, solo status, name/color later.
- DCA `1..8`: fader and mute.
- Bus `01..16`: fader and mute.
- Matrix `01..06`: fader and mute.
- Main stereo fader and mute.
- Meter groups as named subscriptions rather than raw blob IDs.
- Configurable periodic sends. The default task should send `/xremote`, which is
  enough for most basic usage. Advanced setups can add `/subscribe`, `/meters`,
  or other OSC commands.

The profile should expose a searchable command catalog in the UI. Each command
should define address, OSC argument types, value range, normalized conversion,
read/write behavior, subscription strategy, and optional cache key.

Do not add emulator-vs-hardware compatibility switches in the first
implementation. The emulator is a test tool; user mappings should handle known
differences, such as linked channels not moving both faders when one fader moves.

## Runtime Architecture

Use device sessions as the runtime boundary:

- `ControlDeviceManager`: owns configured devices and starts/stops device
  sessions.
- `ControlDeviceMatcher`: resolves configured devices against current PortMidi
  devices by stable alias, exact name, fuzzy name, and remembered device ID. If
  no confident match exists, show a dialog so the user can select the current
  device.
- `MidiDeviceSession`: owns one PortMidi input and/or output for a MIDI device
  instance.
- `OscListenerManager`: owns one or more project-configured app-level OSC
  listeners and routes incoming packets to OSC device instances. The default
  project should have one listener on port `10020`, but projects can add more
  listeners for other OSC devices or network layouts. X32 devices should share a
  listener/socket where possible to avoid unnecessary `/xremote` consumers
  because the X32 supports only a small number of connected clients for broadcast
  update workflows.
- `OscDeviceSession`: owns remote OSC send clients and app-listener routes for
  an OSC device instance.
- `X32DeviceBehavior`: attaches to an OSC device profile and owns `/xremote`,
  subscriptions, meter tasks, initial value requests, and X32-specific parsing.
- `ControlEventQueue`: serial worker that processes normalized events and script
  callbacks in order.
- `ControlMonitorSink`: receives every raw packet, decoded event, send attempt,
  script emission, cache update, error, and suppression decision.
- `ControlValueCache`: stores latest values with source, timestamp, freshness,
  and whether a value is confirmed from receive traffic or only optimistic from a
  send.

Device sessions should produce raw monitor records before normalization and
again after normalization. Output sends should also produce monitor records before
and after send completion.

Suggested monitor record fields:

- Timestamp.
- Direction: input, output, internal, dropped, error.
- Protocol: MIDI, OSC, script, runtime.
- Device instance ID and profile ID.
- Endpoint details: MIDI device name/ID or OSC host/port/local port.
- Decoded message: MIDI type/channel/controller/value, OSC address/arguments.
- Raw bytes when available.
- Correlation ID and origin ID.
- Source script/control logic/layer.
- Result: sent, received, cached, suppressed, failed.

Keep the monitor bounded in memory with a ring buffer, but add capture/export for
debug sessions.

## Script Runtime

Use Mond as an embedded scripting engine, but rewrite the host model around
compiled scripts and libraries.

Current host objects should become explicit libraries loaded through a controlled
library registry. Mond's `MondLibraryManager` supports adding custom
`IMondLibrary` implementations; HaPlay should use that rather than manually
injecting a new object graph for every event.

Suggested built-in libraries:

- `HaPlay.Devices`: list devices, get health, enable/disable, inspect profile
  controls/commands.
- `HaPlay.Midi`: construct CC, note on/off, program change, pitch bend, and send
  messages. Exclude SysEx in the first implementation.
- `HaPlay.Osc`: construct OSC messages with typed arguments, send messages,
  request values, and read the OSC cache. This should be the only network access
  scripts receive in phase 1.
- `HaPlay.X32`: command helpers, fader dB conversions, channel/bus/DCA/main
  lookup, subscription helpers, and `/xremote` controls.
- `HaPlay.State`: per-script, per-device, and project-scoped state.
- `HaPlay.Monitor`: log structured messages to the live monitor.
- `HaPlay.Time`: register periodic tasks through the host scheduler rather than
  spinning loops inside scripts.

Compile each script once when enabled or edited. Runtime events should call a
compiled entrypoint with a small immutable context. Keep instruction limits,
diagnostics, and fault isolation. Do not expose filesystem access in phase 1.
Document the custom functions in a dedicated user-facing scripting reference.

### Script Triggers

Support these host-managed triggers:

- Device enabled.
- Device disabled.
- Device health changed.
- MIDI message received.
- MIDI CC received.
- MIDI note received.
- OSC message received by address or address pattern.
- OSC cache value changed.
- Layer enabled.
- Layer disabled.
- Periodic timer.
- Manual/test trigger from the UI.

Scripts should run only while the control system is armed/enabled. Script
configuration should support project-level scripts plus optional per-device,
per-endpoint, and per-layer enable/disable scripts.

Use separate script files, referenced by project-relative paths, so users can
edit scripts with normal tools. Prefer exported handler/helper functions that
other scripts can import. Trigger configuration should call exported functions
instead of requiring a graph or mapping template.

Implementation note, 2026-06-04: `ControlScriptFileHost` now provides the first
rewrite runtime slice. It loads project-relative script files through a host
source provider, resolves Mond imports with `RequireLibrary`, returns exported
function names for UI/trigger binding, invokes exported functions by name, and
keeps the existing instruction-limit timeout behavior. A first host API library
also exposes the script-facing `osc`, `x32`, and `math` objects needed by the
starter X-Touch Mini to X32 fader script. These APIs currently queue OSC sends
to a host command sink and read/write `ControlValueCache`; they are not yet the
full final `HaPlay.Osc`/`HaPlay.X32` library surface. The legacy inline graph
script host remains in place until host-managed triggers and the rest of the
custom HaPlay Mond libraries are connected.

Implementation note, 2026-06-04: `ControlScriptRuntime` now binds configured
triggers to exported functions, runs only while the control system is armed,
checks enabled device/layer scopes, updates the OSC cache from incoming OSC
events before trigger invocation, records compile/runtime diagnostics, and
disables scripts after the configured consecutive failure threshold. It covers
device enabled/disabled, layer enabled/disabled, MIDI message/CC, OSC message,
manual, and explicit periodic dispatch; actual timer scheduling is still a
device/session-manager task. `ControlScriptOscCommandRouter` routes queued
script OSC sends to configured OSC devices by ID, alias, name, or unambiguous
profile ID and applies optimistic cache writes when that cache mode is enabled.
`ControlScriptRuntimeSession` now bridges these pieces for callers: it owns the
shared OSC cache, dispatches script triggers, drains queued script OSC messages,
routes them to the fake or real `IControlOscSender`, and exposes deterministic
periodic ticks for tests and a future timer loop.
`ControlOscListenerManager` now owns one OSC server per enabled project listener,
registers one catch-all handler per listener socket, resolves incoming messages
to enabled OSC device instances by listener binding and remote host/port, and
dispatches matched messages into `ControlScriptRuntimeSession`. It exposes the
remote endpoint in dispatch results so the live monitor can consume it later.

The exact Mond syntax can be refined during implementation, but the default
shape should be export/import friendly, for example:

```text
import "x32Common.mnd";

export fun onXTouchEncoder1(event, context) {
    x32.setFader("x32", 1, x32Common.encoderDeltaToFader(event));
}

export fun onX32LayerEnabled(event, context) {
    x32.requestFaders("x32", 1, 8);
}
```

Script failure policy should be configurable. The default should disable the
failed script after a small number of consecutive failed executions, initially
three.

## Message Construction

Provide structured message builders to scripts and the UI:

- MIDI CC: channel, controller, 7-bit value.
- MIDI high-resolution CC: channel, MSB controller, 14-bit value.
- MIDI note on/off: channel, note, velocity.
- MIDI program change.
- MIDI pitch bend.
- OSC message: address plus typed arguments: int32, int64, float32, double64,
  string, symbol, bool, nil, blob later if needed.

SysEx should be excluded initially, but the model should leave room for device
profiles to add safe SysEx templates later.

## OSC Value Cache

Add a cache that is updated from incoming OSC traffic and optional confirmed
query replies.

Cache key:

```text
deviceInstanceId + oscAddress + argumentIndex/name
```

Cache entry:

- Latest typed value.
- Raw OSC argument.
- Receive timestamp.
- Source endpoint.
- Freshness/TTL.
- Confirmation state: received, optimistic-send, expired.
- Correlation ID if it came from a request.

Cache update behavior should be configurable:

- Incoming-only: update cache only from received OSC messages.
- Optimistic send + incoming: update cache when HaPlay sends a value, then
  replace it when a matching receive/update arrives.
- Per-command override for commands where optimistic state is misleading.

Scripts should be able to read this cache synchronously. UI should show cache
values beside X32 command browser entries. Scripts and layer logic can use the
cache for soft takeover targets and initial feedback.

## Device Lifecycle

When a device is enabled:

1. Open transport sessions.
2. Emit a `device.enabled` event.
3. Run device profile startup tasks.
4. Run user startup scripts.
5. Send initial queries/subscriptions.
6. Populate the value cache from replies.
7. Send controller feedback for values that are known.

For X32, startup should normally send `/xremote`, start a periodic renew task,
and request or subscribe to the mapped parameters. The `/xremote` interval should
be device-profile configuration, defaulting to 8 seconds because X32 updates time
out at roughly 10 seconds.

Cue actions changing control scripts or control state are useful, but should be
treated as a future enhancement after the armed control system, device scripts,
and layer scripts are stable.

When a device is disabled:

1. Stop periodic tasks.
2. Optionally send unsubscribe/cleanup messages for OSC devices.
3. Close transports.
4. Emit `device.disabled`.
5. Mark cache values stale.

## Layers

Use explicit layers instead of burying layer behavior in scripts only.

Layer model:

- A layer has an ID, display name, active state, priority, and script bindings.
- HaPlay owns the layer model.
- Device profiles may expose physical controls that switch HaPlay layers. For
  X-Touch Mini MC mode, layer A/B are normal note events and should not be
  treated as a separate hardware layer system.
- Enabling a layer emits `layer.enabled`.
- Disabling a layer emits `layer.disabled`.
- Layer changes should refresh controller LEDs/rings from the value cache.

This lets a script send one-time X32 value requests when a layer becomes active
and then update X-Touch Mini feedback before the user touches anything.

## UI Rewrite

### Live Monitor

Build this first because it is needed to verify hardware and profiles.

Minimum features:

- Direction filter: input, output, internal, errors.
- Protocol filter: MIDI, OSC, script/runtime.
- Device filter.
- Text search by OSC address, MIDI controller, script name, endpoint.
- Decoded packet summary plus expandable raw details.
- Pause/resume capture.
- Clear.
- Export current capture.
- Send/test panel for MIDI CC and OSC messages.

The visible monitor history should default to 1000 messages and be configurable.
High-rate streams such as X32 meters should support coalescing so the monitor
does not make the UI unusable.

### Device Profile Browser

Show device profiles as selectable templates:

- X-Touch Mini controls with learnable bindings.
- X32 OSC command tree with search.
- Current connected MIDI input/output candidates.
- Current OSC endpoint and local listener status.

### Script Editor

Show:

- Script enable toggle.
- Project-relative script file path.
- Exported function/handler list.
- Trigger registrations detected from the script.
- Compile diagnostics.
- Runtime diagnostics.
- Last run time and error count.
- Sample scripts for X-Touch Mini and X32.

### First Starter Script

The first built-in starter script should map the X-Touch Mini MC-mode encoder
strip to the first 8 X32 channel faders:

- Encoder CC16 -> `/ch/01/mix/fader`.
- Encoder CC17 -> `/ch/02/mix/fader`.
- Continue through CC23 -> `/ch/08/mix/fader`.
- Encoder values `1..10` increase the normalized OSC fader value.
- Encoder values `65..72` decrease the normalized OSC fader value.
- Larger relative values move the fader faster.
- Unknown/currently uncached fader values start at `0.75`.
- Values are clamped to the X32 normalized fader range `0..1`.

The initial implementation uses one X32 fader quantum per relative encoder step:
`next = clamp(current + deltaSteps * (1 / 1023), 0, 1)`.

## Persistence

Introduce a new project schema for the rewritten control system, likely
`SchemaVersion = 3`.

Suggested top-level model:

```csharp
public sealed record ControlSystemConfig
{
    public List<ControlOscListenerConfig> OscListeners { get; init; } = new();
    public List<DeviceInstanceConfig> Devices { get; init; } = new();
    public List<ControlLayerConfig> Layers { get; init; } = new();
    public List<ControlScriptConfig> Scripts { get; init; } = new();
    public ControlMonitorOptions Monitor { get; init; } = new();
}
```

Keep existing `ControlGraphs` readable during migration. Convert obvious script
nodes and endpoint references where possible, or mark old graphs as legacy data.
Do not silently discard current project data.

## Implementation Phases

### Phase 0: Stabilize The Existing WIP

- Avoid relying on the current graph editor for real hardware tests. If it stays
  temporarily usable, fix endpoint binding loss first.
- Add an X32 emulator endpoint preset for `192.168.2.76:10023`.
- Integrate `X32Session` with runtime start/stop enough to send `/xremote`.
- Verify current OSC send/receive against the emulator.
- Verify current MIDI input from the connected X-Touch Mini.

### Phase 1: Live Monitor And Transport Taps

- Add monitor records at MIDI input, MIDI output, OSC input, OSC output, script
  emit, cache update, suppression, and error points.
- Build the monitor UI with filtering and a test send panel.
- Add capture export/replay format for regression tests.
- Use the monitor to record actual X-Touch Mini control numbers and X32 emulator
  responses.

### Phase 2: Device Repository

- Add profile schema and loader.
- Add built-in X-Touch Mini profile from `Reference/XTouchMini.txt` with
  learnable control bindings.
- Add built-in X32 profile with command catalog, fader conversion, startup tasks,
  configurable periodic sends, and default `/xremote`.
- Add project `DeviceInstanceConfig` records that bind profiles to real MIDI/OSC
  endpoints.

### Phase 3: Value Cache And Device Lifecycle

- Add `ControlValueCache`.
- Update cache from incoming OSC messages and selected MIDI controls.
- Add device enabled/disabled lifecycle hooks.
- Add layer enabled/disabled hooks.
- Add profile startup tasks and periodic tasks.
- Add X32 initial request/subscription flow for mapped parameters.

### Phase 4: Script Runtime Rewrite

- Replace the current ad-hoc script host with compiled script instances.
- Add Mond libraries for devices, MIDI, OSC, X32, state, monitor, and time.
- Add host-managed trigger registration.
- Add script diagnostics and timeout tests.
- Add script examples for:
  - X-Touch Mini fader/encoder to X32 fader.
  - X32 fader cache update to controller feedback.
  - X-Touch Mini button to X32 mute toggle.
  - X32 `/xremote` periodic task.

### Phase 5: Script-Centric UI

- Build script-centric tree/list UI for device scripts, endpoint scripts, layer
  scripts, periodic tasks, and imported helper files.
- Add learn mode from monitor traffic that can generate trigger declarations or
  pre-filled script snippets.
- Add command browser for X32.
- Add script snippets for common feedback behavior:
  cache-to-LED/ring, do-not-echo-to-origin, and soft takeover.

### Phase 6: Migration And Cleanup

- Add schema migration tests.
- Convert simple existing graphs into starter script files where possible.
- Remove the graph canvas from the initial UI surface. Keep legacy graph data
  readable long enough to migrate or archive it.
- Archive obsolete graph-specific docs after migration.

## Testing Strategy

Unit tests:

- Profile loading, version checks, and validation.
- X-Touch Mini profile control lookup.
- X32 command catalog lookup and fader conversion.
- MIDI message builders and value clamping.
- OSC message builders and typed arguments.
- Value cache updates, expiry, and optimistic-send behavior.
- Device lifecycle event ordering.
- Periodic task scheduling and cancellation.
- Script library bindings and trigger dispatch.
- Migration from `ControlGraphs` to `ControlSystemConfig`.

Integration tests:

- OSC loopback monitor captures input and output.
- OSC cache updates from received messages.
- X32 session sends `/xremote` and subscription renewals.
- Fake MIDI input drives scripts and monitor records.
- Fake MIDI output receives feedback from OSC cache changes.
- Script runtime emits MIDI and OSC messages through fake transports.

Manual hardware tests:

- X-Touch Mini appears in MIDI input/output catalog.
- Moving each encoder/button/fader creates a decoded monitor row.
- Learn mode assigns each physical control correctly.
- Button/LED or encoder-ring feedback sends expected MIDI output where supported.
- X32 emulator at `192.168.2.76:10023` receives `/xremote`.
- X32 emulator receives fader/mute sends from scripts.
- Incoming X32 values update the cache and UI.
- Enabling an active layer requests current X32 values and refreshes controller
  feedback before user input.
- The `/xremote` periodic task stays active for more than 60 seconds.

## Resolved Decisions

- Do not build a graph view for the initial rewrite.
- Use the new `Reference/XTouchMini.txt` mapping as the starting X-Touch Mini MC
  mode profile.
- HaPlay owns the layer system. X-Touch Mini layer A/B buttons are regular input
  events that can switch HaPlay layers.
- Match MIDI devices by a combination of alias, name, and ID. If no good match is
  found, ask the user to select the current device.
- Default to one app-level OSC listener while allowing additional listeners.
- Make OSC cache update behavior configurable between incoming-only and
  optimistic send + incoming.
- Make X32 periodic sends configurable. Default to `/xremote`.
- Do not add emulator-vs-real-X32 compatibility switches in the first pass.
- Use separate project-side script files.
- Prefer export/import-oriented Mond scripts and document custom functions
  separately.
- Disable scripts after a configurable number of consecutive failures. Default
  threshold: 3.
- Store OSC listeners per project. Default: one app-level listener on `10020`.
- Allow multiple OSC remotes and multiple OSC listeners in one project.
- Share each OSC listener socket for send/receive when possible.
- Use JSON lines for monitor capture/replay export.
- Treat high-rate stream coalescing as a hardware-calibration task during real
  X32 testing.
- Store user device profiles both per app/user profile and per project, with
  project overrides.
- Treat device profiles as suggestions. Missing profile commands should warn but
  should not block raw scripted MIDI/OSC.
- Run scripts only while the control system is armed/enabled.
- Support per-device, per-endpoint, and per-layer script enable/disable hooks.
- Treat cue actions changing control scripts/state as a future enhancement.
- Default the live monitor display to 1000 messages and make it configurable.
- Do not give scripts filesystem access. Network access should be host-mediated
  OSC send/request/cache functions only.

## Remaining Open Questions

No product-level questions are currently blocking the first implementation
slice. High-rate X32 meter coalescing should be tuned during real hardware
testing rather than designed in the abstract.
