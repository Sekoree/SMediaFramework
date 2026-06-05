# HaPlay Control — X32 + X-Touch Mini, Two-Layer Bank Control

This guide builds a **two-layer** control surface: the 8 X-Touch Mini encoders/buttons
drive X32 channels **1–8** on one layer and **9–16** on the other, with full LED
feedback, and the **Layer A** button flips between them.

Per layer (active bank):

- **Encoders 1–8 → channel faders.** Relative turns nudge the X32 fader.
- **LED rings show the live fader value** using the MC “fan” display.
- **Press an encoder → reset that fader to `0.75`.**
- **Buttons 1–8 → channel mute toggle.** The button LED is **lit when the channel is
  unmuted**, dark when muted.
- **On every layer switch the rings + mute LEDs reload** to the new bank’s values.

It assumes you have already done the [Getting Started](HaPlay-Control-Getting-Started.md)
guide (devices added, `/xremote` configured). The only hard requirements carried over:

| Thing | Value used by the scripts |
| --- | --- |
| X32 OSC device **alias** | `x32` |
| X-Touch Mini MIDI device **alias** | `xtouch` |
| X-Touch Mini **input *and* output** registered | required (output drives the LEDs) |
| X-Touch Mini set to **MC mode** | required |

If your aliases differ, either rename the devices (Control → device row → **Edit**) or
change the `X32` / `SURFACE` constants at the top of the helper script.

> The X-Touch Mini reference (CCs, notes, LED-ring values) lives in
> `Reference/XTouchMini.txt`. The mapping this guide uses is summarised in the
> [appendix](#appendix--surface-map).

---

## How it fits together

```
                       Layer A button (note 84)
                                │  toggles
                                ▼
        ┌─────────────────────────────────────────────┐
        │  HaPlay layers (mutually exclusive)          │
        │   • "Bank 1-8"   active ⟶ base 0             │
        │   • "Bank 9-16"  active ⟶ base 8             │
        └─────────────────────────────────────────────┘
            │ layer-scoped scripts run only when active
            ▼
   bank_1.mnd (Bank 1-8)        bank_2.mnd (Bank 9-16)
        │  BASE = 0                   │  BASE = 8
        └───────────┬─────────────────┘
                    ▼  call shared logic with their base
            Scripts/x32_surface.mnd   ← all the real work
                    │
        ┌───────────┴───────────┐
        ▼                       ▼
  OSC out → X32          MIDI out → X-Touch LEDs
```

Three pieces:

1. **Two layers** — `Bank 1-8` (active by default) and `Bank 9-16`.
2. **One shared helper** (`Scripts/x32_surface.mnd`) — all the encoder/mute/LED logic,
   parameterised by a `base` channel offset (`0` or `8`). Not triggered directly.
3. **Two thin layer-scoped scripts** (`bank_1.mnd`, `bank_2.mnd`) that bind the
   X-Touch events **for their layer** and forward to the helper with their `base`.
4. **One project script** (`layer_button.mnd`) — the Layer A toggle, always active.

Why a shared helper? The two banks are identical except for the channel offset, so the
logic lives once. The bank scripts reference it through an **uppercase** variable
(`const Surface = require(...)`); in Mond an uppercase receiver is treated as a module
namespace, so `Surface.handleEncoder(event, BASE)` passes your arguments straight
through (a lowercase `osc.send(...)` instead injects the object as `this`, which is why
the built-in libraries are lowercase).

---

## Step 1 — Create the two layers

In the **Control** workspace (`Ctrl+6`), *Project structure* header → **Add layer…**

1. **Add layer** → Name `Bank 1-8`, Priority `0`, **Active = checked** → **Save**.
2. **Add layer** → Name `Bank 9-16`, Priority `1`, **Active = unchecked** → **Save**.

You should now see both under the **Layers** group, with `Bank 1-8` marked *active*.
(The *Layers: 2* counter at the top updates too.)

> The layer **names must match** the `BANK_1` / `BANK_2` constants in `layer_button.mnd`
> below. If you name them differently, update those constants.

---

## Step 2 — Add the shared helper script

In the **Scripts** section click **`+`**, open it (double-click / **Edit…**), set its
**Path** to `Scripts/x32_surface.mnd`, paste the code below, and **Save Script**.

Leave it **Project** scope and **give it no triggers** — it’s a library the bank scripts
`require`, not a script that runs on its own.

```js
// Scripts/x32_surface.mnd
// Shared X32 + X-Touch Mini logic for one bank of 8 channels.
// Bank scripts call these with a `base` offset: 0 -> channels 1..8, 8 -> channels 9..16.
// `slot` is always the physical control index 0..7 (encoder/ring/button), so the same
// 8 encoders and buttons are reused by both banks.

const X32 = "x32";          // OSC device alias  (edit to match your device)
const SURFACE = "xtouch";   // MIDI device alias (edit to match your device)

const MIDI_CHANNEL = 1;     // X-Touch Mini in MC mode talks on MIDI channel 1
const ENCODER_CC = 16;      // encoders send CC16..CC23
const RING_CC = 48;         // LED rings are CC48..CC55
const PUSH_NOTES = [32, 33, 34, 35, 36, 37, 38, 39];   // encoder press
const BUTTON_NOTES = [89, 90, 40, 41, 42, 43, 44, 45]; // buttons 1..8

const FADER_STEP = 1.0 / 1023.0;  // one X32 fader quantum per encoder tick
const RESET_FADER = 0.75;         // encoder press resets the fader to this

// ----- small helpers -----------------------------------------------------------

fun indexOf(list, value) {
    for (var i = 0; i < list.length(); i++) {
        if (list[i] == value)
            return i;
    }
    return -1;
}

// MC relative encoder: 1..10 = clockwise, 65..72 = counter-clockwise (bigger = faster).
fun encoderDelta(value) {
    if (value >= 1 && value <= 10)
        return value;
    if (value >= 65 && value <= 72)
        return -(value - 64);
    return 0;
}

// ----- X-Touch LED feedback ----------------------------------------------------

fun paintRing(slot, fader) {
    var v = math.clamp(fader, 0.0, 1.0);
    // MC "fan" ring values run 33 (empty) .. 43 (full). +0.5 rounds to the nearest step
    // (the MIDI sender truncates to an int).
    midi.sendCc(SURFACE, MIDI_CHANNEL, RING_CC + slot, 33 + v * 10 + 0.5);
}

fun paintButton(slot, mixOn) {
    // X32 /mix/on: 1 = unmuted, 0 = muted. Button LED is lit while UNMUTED.
    var velocity = mixOn >= 0.5 ? 127 : 0;
    midi.sendNoteOn(SURFACE, MIDI_CHANNEL, BUTTON_NOTES[slot], velocity);
}

// ----- exported entry points (called by the bank scripts) ----------------------

// Encoder turned: move the mapped fader.
export fun handleEncoder(event, base) {
    var slot = event.midi.controller - ENCODER_CC;
    if (slot < 0 || slot >= 8)
        return;                              // not one of our 8 encoders

    var delta = encoderDelta(event.midi.value);
    if (delta == 0)
        return;

    var address = x32.channelFaderAddress(base + slot + 1);

    // Don't guess: if we've never seen this fader, ask the X32 for it. The reply lands
    // in the cache (and repaints the ring via handleFeedback); the next turn moves it.
    if (!osc.has(X32, address)) {
        osc.request(X32, address);
        return;
    }

    var next = math.clamp(osc.cacheFloat(X32, address, 0.0) + delta * FADER_STEP, 0.0, 1.0);
    osc.send(X32, address, osc.float32(next));
    osc.cacheSet(X32, address, next);        // optimistic: next turn continues from here
    paintRing(slot, next);
}

// A note arrived: encoder press = reset fader, button = toggle mute. Other notes ignored.
export fun handleNote(event, base) {
    if (!event.midi.isNoteOn)                // press only, ignore the release
        return;

    var note = event.midi.note;

    var push = indexOf(PUSH_NOTES, note);
    if (push >= 0) {
        var faderAddress = x32.channelFaderAddress(base + push + 1);
        osc.send(X32, faderAddress, osc.float32(RESET_FADER));
        osc.cacheSet(X32, faderAddress, RESET_FADER);
        paintRing(push, RESET_FADER);
        return;
    }

    var button = indexOf(BUTTON_NOTES, note);
    if (button >= 0) {
        var muteAddress = x32.channelMuteAddress(base + button + 1);
        if (!osc.has(X32, muteAddress)) {    // prime the mute state on first press
            osc.request(X32, muteAddress);
            return;
        }
        var next = osc.cacheFloat(X32, muteAddress, 1.0) >= 0.5 ? 0 : 1;  // flip
        osc.send(X32, muteAddress, osc.int32(next));
        osc.cacheSet(X32, muteAddress, next);
        paintButton(button, next);
        return;
    }
}

// The X32 reported a new value (a reply to our request, or an /xremote push). If it
// belongs to this bank, repaint the matching ring or mute LED.
export fun handleFeedback(event, base) {
    var address = event.osc.address;
    for (var slot = 0; slot < 8; slot++) {
        var ch = base + slot + 1;
        if (address == x32.channelFaderAddress(ch)) {
            paintRing(slot, event.value);
            return;
        }
        if (address == x32.channelMuteAddress(ch)) {
            paintButton(slot, event.value);
            return;
        }
    }
}

// Sync this bank's rings + mute LEDs to the console.
//
// IMPORTANT: OSC replies are asynchronous. If you request a value and read the cache on
// the next line it is NOT there yet, so you'd paint the fallback default (0.0 -> lowest
// ring, 1.0 -> "unmuted") — that's why everything looked wrong at arm. Instead: only paint
// values we actually HAVE, and request the ones we don't. Replies arrive a moment later
// and are painted by handleFeedback (instant) and/or the next onSync tick. Because onSync
// calls this repeatedly, a lost/slow reply is simply re-requested until the bank is
// populated, then it just keeps the LEDs in sync.
export fun refreshBank(base) {
    for (var slot = 0; slot < 8; slot++) {
        var ch = base + slot + 1;
        var faderAddress = x32.channelFaderAddress(ch);
        var muteAddress = x32.channelMuteAddress(ch);

        if (osc.has(X32, faderAddress))
            paintRing(slot, osc.cacheFloat(X32, faderAddress, 0.0));
        else
            osc.request(X32, faderAddress);

        if (osc.has(X32, muteAddress))
            paintButton(slot, osc.cacheFloat(X32, muteAddress, 1.0));
        else
            osc.request(X32, muteAddress);
    }
}
```

After **Save Script**, the **Exports** line should list
`handleEncoder, handleNote, handleFeedback, refreshBank` and the diagnostics panel
should be empty.

---

## Step 3 — Add the two bank scripts

These are tiny: they bind the X-Touch events **for their layer** and forward to the
helper with their channel offset.

### 3a — `bank_1.mnd` (channels 1–8)

Add a script, set **Path** `Scripts/bank_1.mnd`, paste:

```js
// Scripts/bank_1.mnd — X-Touch surface for X32 channels 1..8.
const Surface = require('Scripts/x32_surface.mnd');  // uppercase = module namespace
const BASE = 0;                                       // channels 1..8

export fun onEncoder(event, context)  { Surface.handleEncoder(event, BASE); }
export fun onNote(event, context)     { Surface.handleNote(event, BASE); }
export fun onFeedback(event, context) { Surface.handleFeedback(event, BASE); }

// Repaint when this bank becomes active (every switch into it).
export fun onActivate(event, context) { Surface.refreshBank(BASE); }

// Periodic sync: paints cached values and re-requests any still missing, so the bank
// converges to the console's real state after Arm (LayerEnabled doesn't fire on arm) and
// self-heals any lost OSC reply. Safe to run every tick — it never paints a guess.
export fun onSync(event, context) { Surface.refreshBank(BASE); }
```

Then set its **Scope** and **Layer**, and add its triggers:

1. In the editor header set **Scope = `Layer`**. The **Layer** picker appears — choose
   **`Bank 1-8`**.
2. In **Triggers**, add these five (leave match fields blank unless noted):

   | Kind | Function | Match |
   | --- | --- | --- |
   | `MidiControlChange` | `onEncoder` | — (any CC) |
   | `MidiNote` | `onNote` | — (any note) |
   | `OscCacheChanged` | `onFeedback` | — (blank) or `/ch/*` |
   | `LayerEnabled` | `onActivate` | — |
   | `Periodic` | `onSync` | interval **1000** ms |

   > **Address match gotcha:** the matcher only honours one `*` (prefix + suffix around
   > the first star). Use **blank** or **`/ch/*`** here — **not** `/ch/*/mix/*`, which
   > would require addresses to literally end in `/mix/*` and so never match.

3. **Save Script.**

### 3b — `bank_2.mnd` (channels 9–16)

Add a second script, **Path** `Scripts/bank_2.mnd`. It is **identical** to `bank_1.mnd`
except the one constant:

```js
// Scripts/bank_2.mnd — X-Touch surface for X32 channels 9..16.
const Surface = require('Scripts/x32_surface.mnd');
const BASE = 8;                                       // channels 9..16

export fun onEncoder(event, context)  { Surface.handleEncoder(event, BASE); }
export fun onNote(event, context)     { Surface.handleNote(event, BASE); }
export fun onFeedback(event, context) { Surface.handleFeedback(event, BASE); }
export fun onActivate(event, context) { Surface.refreshBank(BASE); }
export fun onSync(event, context)     { Surface.refreshBank(BASE); }
```

Set **Scope = `Layer`**, **Layer = `Bank 9-16`**, and add the **same five triggers** as
`bank_1.mnd`. **Save Script.**

> Because these are *layer-scoped*, `bank_1`’s handlers only run while `Bank 1-8` is
> active and `bank_2`’s only while `Bank 9-16` is active — so the encoders/buttons always
> drive the right 8 channels, and only the active bank’s `Periodic`/feedback runs.

---

## Step 4 — Add the Layer A toggle

The Layer A button (note **84**) flips banks. This script is **project-scoped** so it’s
always live regardless of which layer is active.

Add a script, **Path** `Scripts/layer_button.mnd`, paste:

```js
// Scripts/layer_button.mnd — X-Touch "Layer A" toggles the two HaPlay banks.
const BANK_1 = "Bank 1-8";    // must match your layer names exactly
const BANK_2 = "Bank 9-16";

export fun onLayerButton(event, context) {
    if (!event.midi.isNoteOn)                 // act on press only
        return;
    if (layers.active() == BANK_1)
        layers.activate(BANK_2);
    else
        layers.activate(BANK_1);
}
```

Keep **Scope = `Project`** and add one trigger:

| Kind | Function | Match |
| --- | --- | --- |
| `MidiNote` | `onLayerButton` | **note `84`** |

**Save Script.** `layers.activate(...)` queues the switch; HaPlay applies it after this
handler returns, firing `LayerDisabled` on the old bank and `LayerEnabled` on the new one
— which runs the new bank’s `onActivate` → `refreshBank`, reloading its rings + mute LEDs.

---

## Step 5 — Arm and test

Press **Arm** at the top of the Control workspace.

- Over the first second or two the rings + mute LEDs for **Bank 1-8** converge to the
  console’s current state: `onSync` requests any value it doesn’t have yet, and each value
  is painted as its reply lands (it deliberately never paints a guessed default, so you
  won’t see a wrong value flash first).
- **Turn an encoder** → its X32 fader moves and the ring tracks it.
- **Push an encoder** → that fader jumps to `0.75` (which is **0 dB** on the X32 fader
  law) and the ring snaps to match.
- **Press a button** → the channel mutes/unmutes and the button LED follows
  (lit = unmuted).
- **Press Layer A** → you’re now on **Bank 9-16**; the rings + mute LEDs reload to
  channels 9–16. Press again to go back.
- Move a fader/mute **on the console** (with `/xremote` running) → the matching ring or
  LED updates live via `OscCacheChanged`.

Watch the **live monitor** (bottom) for `MidiControlChange`/`MidiNote` in, `/ch/..` OSC
out, and script runs. Use **Errors only** if something misbehaves.

---

## Troubleshooting

- **LEDs never light.** The X-Touch needs a registered **output**. In **MIDI Ports**,
  select it under outputs → **Use for Control + Cues**, then re-arm. Confirm its alias is
  `xtouch` (Control → device row → **Edit**), or change `SURFACE` in the helper.
- **Nothing moves the X32.** Check the X32 alias is `x32` (or edit `X32`), the host/port
  are right, and `/xremote` is configured (Getting Started step 3). The first encoder turn
  on an untouched fader just *primes* the cache (requests the value) — turn again.
- **Wrong channels respond.** Make sure each bank script’s **Layer** is set correctly
  (`bank_1` → `Bank 1-8`, `bank_2` → `Bank 9-16`) and `BASE` matches (`0` / `8`).
- **Layer A does nothing.** The toggle script must be **Project** scope with a `MidiNote`
  trigger on **note 84**, and the layer names must match `BANK_1`/`BANK_2`.
- **`require: module could not be found`.** The helper path must be exactly
  `Scripts/x32_surface.mnd` (same casing) and saved to disk.
- **Rings jump in coarse steps.** That’s expected: the MC “fan” display only has 11 steps
  (values 33–43). Encoder motion is still full-resolution on the X32 fader itself.
- **Switch doesn’t reload LEDs.** Ensure each bank script has the `LayerEnabled →
  onActivate` trigger. The `Periodic → onSync` trigger covers the initial paint after Arm
  and keeps things in sync.
- **At arm all rings sit at minimum and all mute LEDs are on, ignoring real state.** This
  is the classic “painted a default before the reply arrived” bug. Make sure `refreshBank`
  uses the `osc.has(...)` guard (paint only what’s cached, request the rest) and that
  `onSync` calls it **every** tick (no one-shot `painted` flag). Also check the
  `OscCacheChanged → onFeedback` trigger uses a match of **blank** or **`/ch/*`** — a
  pattern like `/ch/*/mix/*` never matches, so live corrections never fire.

---

## Appendix — surface map

X-Touch Mini (MC mode, MIDI channel 1), from `Reference/XTouchMini.txt`:

| Control | MIDI | Used for |
| --- | --- | --- |
| Encoders 1–8 | CC `16`–`23` (relative) | move channel fader |
| Encoder press 1–8 | Note `32`–`39` | reset fader to `0.75` |
| LED rings 1–8 | CC `48`–`55` | fan display of fader (values `33`–`43`) |
| Buttons 1–8 | Note `89, 90, 40, 41, 42, 43, 44, 45` | toggle channel mute |
| Button LEDs 1–8 | NoteOn velocity `127`/`0` | lit = unmuted |
| Layer A | Note `84` | toggle bank |

LED-ring value ranges (the script uses **fan**): `0` = off, `1`–`11` single,
`17`–`27` trim, `33`–`43` fan, `49`–`54` spread.

X32 OSC (via the `x32.*` address helpers):

| Helper | Address | Value |
| --- | --- | --- |
| `x32.channelFaderAddress(ch)` | `/ch/NN/mix/fader` | float `0.0`–`1.0` |
| `x32.channelMuteAddress(ch)` | `/ch/NN/mix/on` | int `1` = unmuted, `0` = muted |

`base + slot + 1` maps physical control `slot` (0–7) to X32 channel: `base 0` → 1–8,
`base 8` → 9–16.
