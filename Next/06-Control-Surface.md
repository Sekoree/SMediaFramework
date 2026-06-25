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
