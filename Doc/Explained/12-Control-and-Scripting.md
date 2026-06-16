# 12 · Control & Scripting

`S.Control` is the show-control surface: it glues MIDI controllers and OSC mixers
together with **scripts**, so you can (in the author's words) "force a tablet mixer to
behave by gluing random MIDI controllers to mixer OSC commands." The scripting runtime
is **Mond** (a small embeddable .NET language that supports NativeAOT). This module is
its own stack — it depends on the protocol libs (OSCLib, PMLib) but not on the media
engine.

The operator-facing walkthrough is `Doc/HaPlay-Control-Getting-Started.md`; the full
script API is `Doc/HaPlay-Control-Scripting-Reference.md`. This doc explains the *code*.

## The core idea: Arm → live system → Disarm

Nothing touches hardware until you **Arm**. Arming opens OSC listeners, opens MIDI
inputs, starts the periodic-send tick loop, and compiles + runs your scripts.
**Disarm** tears all of that down. The live system is one object:

* **`ControlSystemRuntimeSession`** — the armed, live control system
  (`IAsyncDisposable`). It owns the device sessions, the script runtime, the periodic
  tick loop, and the value cache. Its tick produces a `ControlSystemRuntimeTickResult`
  (what ran, what to renew). Dispose = disarm.

> **ELI5:** "Arm" is flipping the power switch on a patchbay. Now controllers are wired
> to your scripts and your scripts to the mixer. "Disarm" cuts the power so you can
> safely re-wire (edit devices/scripts), then re-arm to apply.

## Configuration model

The whole control setup is data, persisted as JSON:

* **`ControlSystemConfig`** + **`ControlConfigSlices`** — the config. A *slice* is a
  per-layer export/import ("export one layer, import a layer into another show") — it's
  itself a config carrying only specific layers + their scripts, so any config reader
  can read a slice (save/load rework, 2026-06-10).
* **`ControlSystemDocument`** + **`ControlSystemIO`** — serialization (with
  `UnsupportedControlSystemSchemaVersionException` for version guards).
* **`ControlPresets`** — built-in starting configs.

## Devices: MIDI and OSC sessions

### MIDI

* `ControlMidiDeviceManager` — opens/manages control MIDI devices.
* `ControlMidiDeviceResolver` + `ControlMidiPortCatalogProvider` — the **fallback
  device-selection** flow: when a saved device can't be confidently matched to a live
  port (USB moved between sessions), figure out which bindings need the user to pick a
  port (`ControlMidiResolutionRequest`/`Key`/`ControlMidiPortCatalog`) and write the
  picks back. Pure logic — no PortMidi, no UI.
* `ControlMidiLibraryLease` — ref-counted PortMidi lifetime so multiple sessions share
  one init.
* `ControlSystemMidiDeviceSessions` (~707 lines) — the live per-device session loops.

### OSC

* `ControlOscListenerManager` — opens/owns OSC listener sockets.
* `ControlPeriodicOscSendManager` — the tick-loop sender for periodic messages
  (the `/xremote` keep-alive an X32 needs every ~8 s, etc.).
* `ControlOscAddressPattern` — shared address matching: exact, catch-all (`*`), or a
  single `*` wildcard between a fixed prefix/suffix. Used by triggers and cache rules.

### Health

* `ControlDeviceHealthRegistry` — thread-safe latest-health per device instance,
  reported from background session loops and read by scripts/UI.
* `ControlSessionHealth` (+ `ControlSessionState`) — point-in-time health snapshots,
  surfaced to health-change triggers.

## The script runtime (Mond)

* **`ControlScriptRuntime`** (~1,053 lines) + **`ControlScriptRuntimeSession`**
  (`IControlScriptDispatcher`) — compile scripts, register their exported handlers
  against triggers, and dispatch events to them. `ControlScriptFileHost` resolves
  script files; `ControlScriptTemplates` provides starters; `ControlScriptDiagnostics`
  (+ `ControlScriptDiagnosticStage`, `ControlScriptException`) surface compile/resolve/
  invoke errors per stage.
* **`InstructionLimitDebugger`** — enforces a per-invocation Mond instruction budget by
  breaking when the limit is hit, so a runaway `while(true)` surfaces as a timeout
  instead of hanging the control runtime.

### The script API: `ControlScriptApiLibrary`

A script gets eight library handles injected (`IMondLibrary`):

| Handle | What scripts do with it |
|--------|--------------------------|
| `osc` | `osc.send(dev, addr, args)`, `osc.request(dev, addr)`, `osc.has`, `osc.cacheFloat`/`cacheSet`, `osc.float32(...)` — send OSC, query the cache, do soft-takeover. |
| `midi` | send MIDI out (LEDs, encoder rings, motor faders). |
| `x32` | address helpers + value scaling for X32/X-Air (`x32.channelFaderAddress(n)`, mute addresses, fader math). |
| `math` | `math.clamp`, etc. |
| `state` | scoped persistent state between events (project / per-script / per-device). |
| `monitor` | write to the live monitor (log rows). |
| `devices` | device health / presence. |
| `time` | timing helpers. |

A script exports handler functions (`export fun onEncoder(event, context) { … }`) and
binds them to triggers (`MidiControlChange` + a function name) in the editor. The event
carries the decoded message (`event.midi.controller`, `event.midi.value`).

> **Mond gotcha (project memory):** `UPPER.method()` fails at runtime (no `this`). Use
> **lowercase** names for method calls; reserve UPPERCASE only for `require` handles.
> Watch for this when reading or writing control scripts.

## The value cache & soft-takeover

Non-motorized controllers can't know a fader's real position, so the system caches it:

* **`ControlValueCache`** — the optimistic value store. A script asks `osc.has(dev,
  addr)`; if unknown it `osc.request`s the real value (the console replies and the cache
  fills) and skips that turn; thereafter it reads `cacheFloat`, computes the next value,
  sends it, and `cacheSet`s optimistically.
* **`ControlOscCacheCommandOverride`** — per-command cache policy (force track-on-send,
  or incoming-only) for addresses where optimistic tracking is misleading.
* OSC replies arrive on the *sending client's own connected socket* (the X32 answers the
  port we sent from) — surfaced via `IControlOscReceiver` / `ControlOscReceivedMessage`,
  no separate listener needed.

## 14-bit (high-resolution) MIDI

A high-res fader physically arrives as two CC bytes (coarse MSB + fine LSB). The
control layer recombines them so scripts see one 0–16383 value:

* **`MidiHighResolution14BitCombiner`** — caches the MSB and, on the matching LSB
  (controller +32), emits one combined `ControlChange` with `(MSB<<7)|LSB`. Profile-
  driven: a device profile marks which coarse controllers are 14-bit. (Built on PMLib's
  message layer, [02](02-Native-Bindings.md). Project memory: 14-bit CC combining.)
* **`XTouchMiniX32FaderMapping`** — the specific X-Touch Mini ↔ X32 fader mapping.

## Device profiles

Profiles describe a controller/mixer so the UI can offer command browsers and address
helpers:

* `ControlDeviceProfiles` (~454 lines) + `ControlDeviceProfileBehaviors` — the profile
  model and behaviours.
* `BuiltInControlDeviceProfileFactory` + `BuiltInProfileLoader` — generate/load the
  shipped JSON profiles (the JSON under `Profiles/` is the runtime source of truth;
  regenerate from the factory when the catalog changes).
* `ControlDeviceMatcher` — match a live port to a configured device.
* Profiles are otherwise *runtime metadata only* (project memory) — raw scripted
  OSC/MIDI works even without a matching profile.

## X32 / X-Air specifics

Behringer X32 (and the smaller X-Air/M-Air) have protocol quirks the module handles:

* **`X32Session`** — a live X32 connection: `X32Subscription` (address + frequency),
  `X32MeterSubscription`/`X32MeterBlob` (the binary meter stream), `X32Meters`.
* **`X32MeterCacheDecoder`** — decodes incoming `/meters` blob replies into stable
  numeric cache addresses (`/meters/6/0`, …).
* **`ControlX32ProtocolMaintenanceManager`** — renews `/xremote`, `/subscribe`, and
  `/meters` with protocol-aware timing (the renewal clock only advances after a *full
  successful* renew cycle, so a dropped packet doesn't desync the keep-alive).
* **Presets:** `X32Presets`, `XAirPresets` (X-Air channels share the X32 `/ch/NN/...`
  layout but buses/DCAs are single-digit and main is `/lr`), `X32EndpointPreset` /
  `X32ParameterPreset` (+ `X32ParameterValueKind`), `X32Fader` (fader value math).

## Senders & monitoring

* `IControlOscSender` / `IControlMidiSender` — the host-mediated output contracts the
  runtime and script command routers send through.
* `UdpControlOscSender` — the real OSC transport: caches one client per remote host/port,
  each listening on its own connected socket for replies; host-resolution / send
  failures degrade to a monitor failure row rather than crashing the session.
* `ControlSenders` — the sender wiring.
* `ControlScriptOscCommandRouter` / `ControlScriptMidiCommandRouter` — route script
  `osc.send`/`midi` calls to the right device session.
* `ControlMonitor` — the live monitor feed (every MIDI in, OSC out, script run, error)
  the UI filters and displays.
* `ControlEventQueue` / `ControlEvents` — the event plumbing between device loops and the
  script dispatcher.
* `ControlScriptStateStore` — backs the `state` library: project-wide / per-script /
  per-device key-value maps (values restricted to number/string/bool/null), with the
  runtime setting the current script/device scope around each invocation.

> The full operator setup (layers, LED feedback, soft-takeover) is documented in
> `Doc/HaPlay-Control-X32-XTouch-Layers.md` and `…-X32-BCF2000-Layers.md`.

Next: [13 · HaPlay UI](13-HaPlay-UI.md).
