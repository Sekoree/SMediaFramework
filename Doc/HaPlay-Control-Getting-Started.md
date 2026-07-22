# HaPlay Control — Getting Started

This guide walks through a complete control-system setup end to end:

1. Add a MIDI controller (input + output).
2. Add an X32 (OSC device).
3. Configure the periodic `/xremote` keep-alive.
4. Write **one** script that maps the 8 X-Touch Mini encoders to the first 8 X32
   channel faders, using a small array to decide which CC drives which channel.

Everything here is done in the app — no project-JSON editing required.

## Concepts

- The **Control** workspace (sidebar, `Ctrl+6`) owns the live control system.
- Nothing talks to hardware until you press **Arm** (top of the workspace). Arming
  opens configured OSC listener(s), opens MIDI inputs, starts the periodic-send
  tick loop, and runs your scripts. New projects do not create an app-level OSC
  listener by default; X32 replies use the OSC device client's own socket.
  **Disarm** tears all of that down.
- After changing devices/scripts/sends while armed, **re-arm** to apply the change
  (the status line reminds you).
- A device **alias** (e.g. `x32`) is the `deviceKey` your scripts use, e.g.
  `osc.send("x32", …)`. Keep aliases stable and match them in your scripts.

## Step 1 — Add your MIDI controller (input + output)

1. Open the **MIDI Ports** workspace (`Ctrl+5`).
2. Click **Refresh** to scan for connected MIDI ports.
3. Under the detected **inputs**, select your controller (e.g. *X-Touch MINI*) and
   click **Use for Control**. This registers it as a control MIDI device.
4. (Optional, for button LEDs / encoder-ring feedback) select the same device under
   detected **outputs** and click **Use for Control + Cues**.

The device now appears in the **Control** workspace structure tree under
*MIDI devices*. If you have several identical or renamed ports and the match is
ambiguous, use **Resolve MIDI…** (in the Control workspace) to pick the exact port.

> The encoder script below resolves the X32 by alias, so the MIDI device's alias
> doesn't need to match anything — but it's still good to give it a memorable one.

## Step 2 — Add the X32 (OSC device)

1. Open the **Control** workspace (`Ctrl+6`).
2. In the **Project structure** header click **Add OSC device…**.
3. In the dialog:
   - **Name**: `X32`
   - **Profile**: `Behringer X32 (behringer.x32.osc)` — gives you the command/cache
     browser and address helpers.
   - **Host / Port**: `192.168.2.76` / `10023` (your console's IP and OSC port).
   - **Alias**: `x32` — this is what scripts pass as the `deviceKey`.
   - **Client source port**: leave **blank** for an automatic (ephemeral) source port. The
     X32 replies to whatever port we send from, and HaPlay receives those replies on
     that same client socket — so you don't need to run or pick a listener. Set a
     fixed value only if your network setup needs a known, deterministic source port.
   - **Enabled**: checked.
4. **Save**. The X32 shows up under *OSC devices*.

To change the host/port later, right-click the X32 row → **Edit OSC device…**.

## Step 3 — Periodic `/xremote`

The X32 only keeps broadcasting parameter changes to clients that periodically send
`/xremote` (it times out at ~10 s).

1. In the structure tree, right-click the **X32** row → **Add periodic OSC send…**.
2. In the dialog, keep **Name**/**Address** = `/xremote` and set **Interval (ms)**
   (8000 is the recommended cadence; anything < ~10000 works). Leave **Enabled**
   checked, then **Save**.
3. It appears under *Periodic sends*. To change the interval later, right-click that
   row → **Edit periodic send…** (or **Remove periodic send**).

`/xremote` runs automatically on the background tick loop whenever the system is
**armed** and the device is enabled, and stops on disarm.

## Step 4 — One script for all 8 encoders → faders

You do **not** need a script per encoder. A single handler reads the incoming CC
number, looks up the target channel in an array, and moves that fader.

### 4a — Create the script file

1. In the **Control** workspace, **Scripts** section, click **`+`** to add a script.
2. Set its **Path** (in the editor window, or it defaults under `Scripts/`), e.g.
   `Scripts/xtouch-faders.mnd`.
3. **Double-click** the script in the list (or click **Edit…**) to open the
   pop-out **Script Editor** window, and paste:

```js
// Map the 8 X-Touch Mini MC-mode encoders (CC16..CC23) to X32 channel faders 1..8.
// Edit this array to remap encoders -> channels. Index 0 is CC16, index 7 is CC23.
const channels = [1, 2, 3, 4, 5, 6, 7, 8];

const faderStep = 1.0 / 1023.0;  // one X32 fader quantum per encoder tick

// MC-mode relative encoder value: 1..10 = clockwise, 65..72 = counter-clockwise
// (a faster turn sends a larger magnitude).
fun encoderDelta(value) {
    if (value >= 1 && value <= 10)
        return value;
    if (value >= 65 && value <= 72)
        return -(value - 64);
    return 0;
}

export fun onEncoder(event, context) {
    var index = event.midi.controller - 16;
    if (index < 0 || index >= channels.length())
        return;                          // not one of our 8 encoders

    var delta = encoderDelta(event.midi.value);
    if (delta == 0)
        return;

    var channel = channels[index];
    var address = x32.channelFaderAddress(channel);   // -> /ch/0X/mix/fader

    // Don't guess a starting value. If we haven't seen this fader yet, ask the X32
    // for it (osc.request sends an address-only message; the console replies with the
    // current value, which lands in the cache). Skip this turn — the next turn moves it.
    if (!osc.has("x32", address)) {
        osc.request("x32", address);
        return;
    }

    var current = osc.cacheFloat("x32", address, 0.0);
    var next = math.clamp(current + delta * faderStep, 0.0, 1.0);

    osc.send("x32", address, osc.float32(next));
    osc.cacheSet("x32", address, next);   // optimistic: next turn continues from here
}
```

4. Click **Save Script**. The editor scans the file; the **Exports** line should
   show `onEncoder` and the diagnostics panel should be empty.

> The first time you nudge an untouched fader, the script *requests* its current
> value from the X32 and waits — so that first nudge primes the cache and the second
> one starts moving. With `/xremote` running, the cache then stays in sync as values
> change on the console. (If you'd rather pre-load all 8 up front, request them once
> from a `Periodic` trigger or on layer-enable — see `x32-layer-initial-requests.mnd`.)

To remap, e.g., put the encoders on channels 9–16, just change the array to
`[9, 10, 11, 12, 13, 14, 15, 16]`. To use a different controller, change the
`- 16` offset (and `encoderDelta`) to match your encoders' CC numbers and values.

### 4b — Bind the trigger

Still in the Script Editor window, in the **Triggers** section:

1. Click **Add trigger**.
2. **Kind** = `MidiControlChange`.
3. **Function** = `onEncoder`.
4. Leave **chan** and **cc** blank (match any CC) — the script itself decides which
   CCs it handles via the `channels` array bounds.

> Tip: instead of typing, use **Learn MIDI** — Arm the system, click *Learn MIDI*,
> then twist an encoder; it captures the control and pre-fills a trigger. Just set
> the function name to `onEncoder` and clear the specific CC so all 8 match.

### 4c — Run it

1. Press **Arm** at the top of the Control workspace.
2. Twist encoders 1–8 on the controller. You should see OSC `/ch/0X/mix/fader`
   messages in the **live monitor** (bottom of the workspace), and the X32 faders
   move.

## Notes & troubleshooting

- **Live monitor**: the bottom panel shows every MIDI in, OSC out, script run, and
  error. Use the direction/protocol/device filters and **Errors only** to narrow it.
- **Soft takeover**: the X-Touch Mini fader/encoders aren't motorized. The script
  requests the real fader value from the X32 on first use rather than guessing, then
  tracks optimistically via `osc.cacheSet`. With `/xremote` running, incoming X32
  values refresh the cache so the script follows the console's real state.
- **Mute toggle**: the same pattern with `x32.channelMuteAddress(channel)` and a
  `MidiNote` trigger toggles channel mutes (X32 `/mix/on`: 1 = unmuted, 0 = muted).
  See the built-in `xtouch-mini-x32-mutes.mnd` starter for an 8-button version.
- **Profile warnings** under the structure tree are advisory — raw scripted OSC/MIDI
  still works even if a profile is missing.
- **Profiles** and **X32 command/cache browser** are collapsed by default. Use
  them to import/export profile JSON, filter X32 commands, prepare a test send,
  or request the selected X32 address while armed.
- **Full API**: see `Doc/HaPlay-Control-Scripting-Reference.md` for every `osc`,
  `x32`, `midi`, `state`, `devices`, `monitor`, and `time` function.
- **Next step — layers + LED feedback**: `Doc/HaPlay-Control-X32-XTouch-Layers.md`
  builds a complete two-layer setup (encoders→faders 1–8 / 9–16, fan-display LED rings,
  encoder-press reset, mute buttons with LED feedback, Layer A to switch banks).
