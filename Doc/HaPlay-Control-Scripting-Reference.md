# HaPlay Control Scripting Reference

HaPlay control scripts are Mond files referenced by project-relative paths.
Scripts should export functions and bind those functions to host-managed
triggers. Scripts run only while the control system is armed.

```text
export fun onXTouchFaderEncoder(event, context) {
    osc.send("x32", x32.channelFaderAddress(1), osc.float32(0.5));
}
```

## Script Shape

- Use `export fun name(event, context)` for trigger handlers.
- Use `require("Scripts/helper.mnd")` to import helper files.
- Keep long-running behavior declarative: use Periodic triggers instead of loops.
- Scripts do not have filesystem or direct network access.

## Event And Context

Common event fields:

- `event.timestamp`: ISO timestamp.
- `event.sourceNodeId`: source endpoint/listener/device id.
- `event.originId`: device id that produced the event.
- `event.correlationId`: correlation id for monitor/cache tracing.
- `event.value`: scalar value when available.

MIDI events expose:

- `event.midi.message`: `controlChange`, `noteOn`, or `noteOff`.
- `event.midi.channel`
- `event.midi.controller`
- `event.midi.note`
- `event.midi.value`
- `event.midi.velocity`
- `event.midi.isNoteOn`

OSC events expose:

- `event.osc.address`
- `event.osc.args`

OSC cache change events expose:

- `event.type == "oscCacheChanged"`
- `event.deviceKey`
- `event.osc.address`
- `event.osc.argumentIndex`
- `event.value`
- `event.source`

Context fields:

- `context.scriptId`
- `context.scriptName`
- `context.triggerId`
- `context.triggerKind`
- `context.scope`
- `context.deviceInstanceId` when device-scoped or device-originated.
- `context.endpointInstanceId` when endpoint-scoped or endpoint-originated.
- `context.layerId` when layer-scoped or layer-originated.

## OSC API

Message arguments:

- `osc.float32(value)`
- `osc.double64(value)`
- `osc.int32(value)`
- `osc.int64(value)`
- `osc.string(value)`
- `osc.symbol(value)`
- `osc.boolean(value)`
- `osc.nil()`

Send/request/cache:

- `osc.send(deviceKey, address, args...)`
- `osc.request(deviceKey, address)`
- `osc.cacheFloat(deviceKey, address, defaultValue)`
- `osc.cacheString(deviceKey, address, defaultValue)`
- `osc.cacheSet(deviceKey, address, value)`

`deviceKey` resolves by configured device id, alias, name, or unambiguous profile
id.

## MIDI API

- `midi.sendCc(deviceKey, channel, controller, value)`
- `midi.sendHighResCc(deviceKey, channel, controller, value14Bit)`
- `midi.sendNoteOn(deviceKey, channel, note, velocity)`
- `midi.sendNoteOff(deviceKey, channel, note, velocity)`
- `midi.sendProgramChange(deviceKey, channel, program)`
- `midi.sendPitchBend(deviceKey, channel, value)`

SysEx is intentionally excluded from the first scripting surface.

## X32 API

Address helpers:

- `x32.channelFaderAddress(channel)`
- `x32.channelMuteAddress(channel)`
- `x32.channelPanAddress(channel)`
- `x32.channelSoloAddress(channel)`
- `x32.dcaFaderAddress(dca)`
- `x32.dcaMuteAddress(dca)`
- `x32.busFaderAddress(bus)`
- `x32.busMuteAddress(bus)`
- `x32.matrixFaderAddress(matrix)`
- `x32.matrixMuteAddress(matrix)`
- `x32.mainFaderAddress()`
- `x32.mainMuteAddress()`

Fader helpers:

- `x32.faderToDb(normalized)`
- `x32.dbToFader(db)`
- `x32.quantizeFader(normalized)`

## State API

Script-private state:

- `state.get(key, defaultValue)`
- `state.set(key, value)`
- `state.has(key)`
- `state.remove(key)`
- `state.keys()`

Shared scopes:

- `state.script` is the current script scope.
- `state.project` is shared by all scripts in the project.
- `state.device` is scoped to the current device context.

State values can be numbers, strings, booleans, or `null`.

## Devices, Monitor, And Time

Devices:

- `devices.list()`
- `devices.get(key)`
- `devices.isEnabled(key)`
- `devices.health(key)`

Monitor:

- `monitor.log(message)`
- `monitor.error(message)`

Time:

- `time.now()` returns Unix epoch milliseconds.
- `time.nowIso()` returns an ISO UTC timestamp.

## Built-In Starter Scripts

- `Scripts/xtouch-mini-x32-faders.mnd`: X-Touch Mini encoders CC16..CC23 to X32
  channel faders 1..8.
- `Scripts/xtouch-mini-x32-mutes.mnd`: X-Touch Mini buttons 1..8 to X32 channel
  mute toggles.
- `Scripts/x32-layer-initial-requests.mnd`: request X32 fader/mute values for
  channels 1..8 when a layer is enabled.
- `Scripts/xtouch-mini-x32-mute-feedback.mnd`: update X-Touch Mini button LEDs
  from X32 mute cache updates.
