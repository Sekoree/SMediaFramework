# 06 — Control Surface (MIDI / OSC / Mond)

Keep what works — **Mond scripting** (chosen for NativeAOT support), the OSC library, the MIDI
P/Invoke (`PMLib`), the event bus, and the automation model. Fix the one structural problem:
**device-specific protocol code is baked into the shared library** (`X32Session`,
`X32MeterCacheDecoder`, `ControlX32ProtocolMaintenanceManager`, `XTouchMiniX32FaderMapping`,
device-specific `BuiltInControlDeviceProfile*`). "Compatibility with multiple device types" can't mean
"a new file in `S.Control` per console."

## 1. Shape

```
   MIDI in/out (PMLib) ─┐                              ┌─► MIDI out (PMLib)
   OSC  in/out (OSCLib)─┼─► ControlEventQueue ─► Mond ─┼─► OSC out (OSCLib)
   device feedback ─────┘    (normalized events)  runtime└─► Session actions (cues, transport, routing)
                                   ▲                          (S.Media.Session API)
                                   │
                        Control Registry         ◄── profiles are DATA (+ optional native plugin)
```

`S.Control` keeps the **engine**: `ControlEventQueue`/`ControlEvents`, `ControlScriptRuntime`(+session)
and `ControlScriptApiLibrary` (the Mond API), the MIDI device manager/resolver/sessions, the OSC
listener/periodic-send/sender managers, `ControlValueCache`, `MidiHighResolution14BitCombiner`,
`ControlSystemConfig`/`IO`. Devices become **content**.

## 2. Devices as data (the P6 fix)

A **device profile** is a declarative description, not code:

```jsonc
{
  "id": "behringer.x32",
  "match": { "midiName": "X32*", "oscPort": 10023 },
  "controls": [
    { "id": "ch1.fader", "type": "fader", "osc": "/ch/01/mix/fader", "range": [0,1] },
    { "id": "ch1.mute",  "type": "button", "osc": "/ch/01/mix/on", "invert": true }
  ],
  "feedback": [ { "osc": "/meters/1", "decoder": "x32.meters" } ],   // names a decoder capability
  "maintenance": { "every": "5s", "send": "/xremote" }              // the "keep-alive" pattern, declared
}
```

- **Matching** (`ControlDeviceMatcher`) stays, but reads profiles from the control registry instead of
  a hardcoded factory.
- **Mappings** (XTouch→X32 fader maps, 14-bit combine) become profile entries, not C# classes.
- **Periodic/maintenance** OSC (the `/xremote` keep-alive, `ControlPeriodicOscSendManager`) is declared
  in the profile, not coded per console.
- **Protocol oddities that truly need code** — e.g. X32 meter blob decoding (`X32MeterCacheDecoder`) —
  become a named **decoder capability** resolved from the control registry. Ship the X32 decoder as a
  built-in module today; a third party can ship one as a native C-ABI plugin (`MfpControlDecoderVTable`)
  through the general `S.Abi` host described in [05](05-Plugin-Model.md). The profile just references
  `"decoder": "x32.meters"`.

Result: adding a new console = drop a profile (+ rarely a decoder plugin). `S.Control` stops growing
per device. The existing X32/XTouch support ships as the **first profiles**, proving the model.

## 3. Scripting (Mond) — keep, with a cleaner host API

- `ControlScriptRuntime` + `ControlScriptApiLibrary` stay. The Mond-exposed API gets one addition:
  it talks to the **`S.Media.Session`** action surface (fire cue, go, transport, set routing gain,
  select track) through a stable façade, rather than reaching into UI view-models. Because the session
  is now headless framework code (see [02](02-Project-Structure.md) Tier 5), Control can drive shows
  with no UI present — useful for the C-ABI host and for automation/testing.
- Keep the script file host, templates, state store, diagnostics. Keep AOT-safety (Mond's reason for
  selection): no dynamic codegen in the hot path.
- `ControlScriptMidiCommandRouter` / `ControlScriptOscCommandRouter` stay as the script→device egress.

## 4. What moves where

| Today (`S.Control`) | New home |
|---|---|
| event queue, script runtime+API, MIDI/OSC managers, value cache, 14-bit combiner, config/IO | stays in `S.Control` |
| `X32Session`, `X32MeterCacheDecoder`, `ControlX32ProtocolMaintenanceManager` | `x32` profile + `x32.meters` decoder module (built-in now; plugin-capable) |
| `XTouchMiniX32FaderMapping`, device-specific `BuiltInControlDeviceProfile*` | declarative profiles (`xtouch-mini`, `x32`) loaded by the control registry |
| MIDI/OSC P/Invoke + lib | `PMLib`, `OSCLib` (kept) |

This keeps every current capability (X32 meters, XTouch mapping, keep-alives, scripting) while making
the surface extensible by data and by the same general plugin ABI as the rest of the framework.

## 5. Where the device-code boundary actually landed (implemented 2026-06-27)

The plan above is realized. To save a future contributor the investigation: **everything device-specific is
data now, with exactly one deliberate C# exception — the binary meter-blob parse.**

- **Config, OSC address building, value/fader math, mappings (XTouch→X32), and keep-alives/subscriptions are all
  profile data.** A profile carries its OSC addresses as `commands`, an embedded Mond **`HelperScript`** for ergonomics
  (e.g. `x32.channelFaderAddress(n)` just returns `devices.command(id).address` — the address lives once, in the
  data), and **Tasks** for periodic sends (`/xremote` keep-alive via `ControlPeriodicOscSendManager`). The old C#
  (`X32Session`, `XTouchMiniX32FaderMapping`, `X32Presets`/`X32Fader`, the hardcoded `BuiltInControlDeviceProfileFactory`)
  was **deleted** — most of it was already dead once the data path existed (`X32Session`'s renewal loop, for instance,
  duplicated the profile-Task mechanism). Helper scripts are exposed as a `ScriptModule` global; note that Mond passes
  the receiver as arg 0, so a profile helper `fun(self, …)`.
- **The one thing that stays C#: decoding the X32 `/meters` *binary blob*** — `X32Meters` + `X32MeterCacheDecoder`,
  reached only through the registered `IControlMeterBlobDecoder` / `ControlMeterBlobDecoderRegistry`. The profile opts
  in by name (`Behaviors.MeterBlobDecoder = "x32"`).

**Why not generalize even that into a profile "binary-format descriptor" + a generic decoder?** It's technically
possible — the X32's two formats are regular (header bytes, then a little-endian element array, with an optional
scale), and the output address comes from the OSC argument *before* the blob in the same message (so no protocol
state is needed). We deliberately didn't, and a future contributor probably shouldn't either:

- It is a **hot path** — meters arrive at ~50 Hz × dozens of values; a generic descriptor interpreter (let alone a
  Mond byte-loop) is far slower than ~30 lines of `BinaryPrimitives`.
- The **gain is marginal** — you would trade a tiny, isolated, *pluggable* unit for a descriptor schema + interpreter
  that still would not cover a genuinely exotic device's framing.
- **The device stays data regardless**: the profile references the decoder by string, and a host — or a third-party
  C-ABI plugin (see [05](05-Plugin-Model.md)) — can register more decoders without the runtime knowing the device.

So the registered binary-decoder capability **is** the data-driven contract, not a hole in it: it is the intentional
escape hatch for the irreducible <1% (raw byte-crunching at meter rate). The bar for revisiting this: a real second
device whose meter framing the X32 decoder can't express, *and* a measured need to avoid shipping a small per-device
decoder module/plugin.
