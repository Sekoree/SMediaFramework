# HaPlay Control Setup

This guide covers the first script-centric MIDI/OSC control workflow:

- X-Touch Mini in MC mode.
- X32/M32 OSC endpoint.
- HaPlay app-level OSC listeners.
- OSC value cache modes.
- Live monitor capture/replay.

## X-Touch Mini MC Mode

The built-in X-Touch Mini profile expects MC mode. In this mode HaPlay treats the
hardware layer buttons as normal MIDI note inputs; HaPlay layers are managed in
the project.

Reference mapping:

- Layer A/B buttons: notes `84` and `85`.
- Encoders 1..8: CC `16`..`23`.
- Encoder push buttons: notes `32`..`39`.
- Encoder turn right: values `1`..`10`.
- Encoder turn left: values `65`..`72`.
- Buttons 1..8: notes `89`, `90`, `40`, `41`, `42`, `43`, `44`, `45`.
- Buttons 9..16: notes `87`, `88`, `91`, `92`, `86`, `93`, `94`, `95`.
- Master fader: pitch wheel.

Recommended project setup:

1. Add the X-Touch Mini as a control MIDI input device.
2. Add the same device as a control MIDI output device if LED/ring feedback is
   needed.
3. Select the built-in `behringer.xtouch-mini.mc` profile, or use a learned
   external JSON profile if the device has a custom layout.
4. Bind scripts to MIDI CC or MIDI note triggers. The starter scripts use:
   - `onXTouchFaderEncoder` for encoder CC `16`..`23`.
   - `onXTouchMuteButton` for buttons 1..8.

Use the live monitor to confirm actual incoming CC/note values before relying on
a profile. Profiles are suggestions; raw MIDI triggers remain available when a
control is not in the selected profile.

## X32 / M32 OSC Setup

The built-in X32 profile targets:

- Default host preset: `192.168.2.76`.
- Default OSC port: `10023`.
- Default periodic task: `/xremote` every `8000 ms`.

Recommended project setup:

1. Add an OSC control device for the X32.
2. Set host to the mixer/emulator IP and port to `10023`.
3. Bind the device to the main app-level OSC listener, usually local port
   `10020`.
4. Keep the default `/xremote` periodic send enabled so the X32 continues to
   broadcast changes.
5. Add optional `/subscribe` or `/meters` periodic sends only when a mapping
   needs them. Meter streams can be high-rate, so monitor coalescing should be
   tuned during real hardware tests.

Starter scripts cover common workflows:

- Encoder changes send X32 fader values.
- Button presses toggle X32 channel mute/on values.
- Layer enable requests current fader/mute values for channels 1..8.
- X32 mute cache changes update X-Touch Mini button LEDs.

## App-Level OSC Listeners

HaPlay stores OSC listeners in the project, separate from OSC devices. The
default project has one listener:

- Name: `Main OSC Listener`
- Local port: `10020`
- Socket mode: `SharedAppListener`

Multiple OSC devices may share one listener. Incoming OSC packets are routed to
enabled OSC devices by listener binding plus remote host/port matching. If a
device has no explicit listener binding, the first enabled listener is used as
the default.

Projects can add more listeners for isolated network layouts, but each enabled
listener must use a unique local port.

## OSC Cache Modes

HaPlay keeps an OSC value cache keyed by device, OSC address, and argument index.
Scripts can read it with `osc.cacheFloat(...)`, `osc.cacheString(...)`, and write
script-side updates with `osc.cacheSet(...)`.

Project cache modes:

- `IncomingOnly`: only received OSC messages update the cache.
- `OptimisticSendAndIncoming`: script/periodic sends update the cache
  immediately, then incoming OSC can confirm or replace the value.

Per-command overrides can force a different mode for matching OSC addresses.
Use overrides for commands where optimistic state would be misleading, or for
commands where immediate UI/controller feedback is useful under an incoming-only
default.

When a device is disabled, cached values for that device are marked stale.

## Live Monitor Capture

The live monitor records:

- MIDI input/output attempts and results.
- OSC input/output attempts and results.
- Script invocations and script logs.
- Cache updates.
- Runtime/script errors.
- Dropped/suppressed messages where the runtime can identify them.

The visible history defaults to `1000` records. Capture export uses JSON lines,
one monitor record per line. This makes hardware sessions easy to diff, replay in
tests, or attach to bug reports.

Recommended debugging flow:

1. Arm the control system.
2. Move one physical control or send one OSC message.
3. Confirm the monitor row has the expected direction, protocol, device,
   endpoint, decoded value, and errors.
4. Export JSON lines if the behavior needs regression coverage.
5. Replay/import the JSON lines in tests to validate parser and filtering
   behavior.

High-rate streams such as X32 meters should be captured carefully until
coalescing behavior is tuned with real hardware.
