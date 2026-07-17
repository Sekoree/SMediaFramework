# HaPlay Control — X32 + Behringer BCF2000, Five-Layer Bank Control

This guide turns a **BCF2000** (8 motor faders + 8 rotary encoders + button rows) into a
five-layer X32 control surface with full motor/LED feedback:

| Layer | Faders | Encoders (turn) | Encoder press | Button Row 1 |
| --- | --- | --- | --- | --- |
| **1** | Channel **1–8** fader | Channel 1–8 **gain** | gain → **0 dB** | Channel 1–8 **mute** |
| **2** | Channel **9–16** | Channel 9–16 gain | gain → 0 dB | Channel 9–16 mute |
| **3** | Channel **17–24** | Channel 17–24 gain | gain → 0 dB | Channel 17–24 mute |
| **4** | Channel **25–32** | Channel 25–32 gain | gain → 0 dB | Channel 25–32 mute |
| **5** | **Outputs** (Main, Aux 1/3/5/7, FX Ret 1, FX 3) | — | — | Output mute |

- The **encoders and faders are 14-bit** (high-resolution): turn = absolute value, and the
  rings/motors are driven back from the console.
- **Button Row 1** mutes (LED lit = **unmuted**, matching "NoteOn 127 when unmuted").
- **Button Group 5** switches layers: **note 51 = next, note 50 = previous**.
- **Button Group 4 button 4 (note 43) = reload** the active layer — re-requests the console
  and re-sends fader positions, so a motor fader that lagged snaps back into place.
- Every layer switch reloads that layer's faders / rings / mute LEDs.

It assumes you've done [Getting Started](HaPlay-Control-Getting-Started.md) (devices added,
`/xremote` configured). Carry-over requirements:

| Thing | Value used by the scripts |
| --- | --- |
| X32 OSC device **alias** | `x32` |
| BCF2000 MIDI device **alias** | `bcf` |
| BCF2000 **input *and* output** registered | required (output drives the motors / LEDs) |
| BCF2000 **profile** assigned | `Behringer BCF2000` (enables 14-bit CC pairing — see Step 1) |

If your aliases differ, rename the devices (Control → device row → **Edit**) or change the
`X32` / `SURFACE` constants at the top of the helper.

> The BCF2000 control map (CCs, notes) lives in `Reference/BCF2000.txt` and is summarised in
> the [appendix](#appendix--surface-map). These numbers match the BCF preset that file
> describes — load that preset on the device.

---

## Why the profile matters (14-bit faders/encoders)

The BCF sends each 14-bit value as **two** MIDI messages: a coarse CC (the MSB) and a fine CC
(the LSB, `+32`). HaPlay only merges them into one high-resolution value (0–16383) for the
controllers a **device profile marks as 14-bit**. The built-in **Behringer BCF2000** profile
flags faders **CC0–7** and encoders **CC10–17** as 14-bit, so once it's assigned your scripts
see *one* `controlChange` per fader/encoder with `event.midi.value` in **0–16383** instead of
a `CC0`/`CC32` pair. Without the profile you'd get two half-resolution events.

> **Set the BCF ranges to full.** The profile uses the actual highest 14-bit value (`16383`).
> In the BCF editor, set the encoder and fader value ranges to their **maximum** so they emit
> the full `0–16383` span. If you keep your current limited ranges (faders `0–9999`, encoders
> `0–999`), set `FADER_IN_MAX` / `ENCODER_IN_MAX` in the helper to those numbers instead.

---

## How it fits together

```
   Button Group 5            Button Group 4 #4
   note 50 / note 51         note 43
        │ prev / next            │ reload
        ▼                        ▼
  ┌──────────────────────────────────────────────┐
  │  HaPlay layers (mutually exclusive)           │
  │   Channels 1-8 / 9-16 / 17-24 / 25-32 / Outputs│
  └──────────────────────────────────────────────┘
        │ layer-scoped scripts run only when active
        ▼
  bcf_bank_1..4.mnd          bcf_outputs.mnd
    base 0/8/16/24             output strip table
        └───────────┬───────────┘
                    ▼ shared logic, parameterised by an 8-strip array
            Scripts/bcf_surface.mnd
                    │
        ┌───────────┴───────────┐
        ▼                       ▼
   OSC out → X32        MIDI out → BCF motors / rings / LEDs
```

A **strip** is one physical column: `{ fader, mute, gain }` (OSC addresses; `gain` is `""`
when the encoder does nothing on that layer). The channel banks build their 8 strips from a
base offset; the outputs layer uses an editable table.

---

## Step 1 — Add the BCF2000 device + profile

1. In **MIDI Ports**, register the BCF2000 **input *and* output** → *Use for Control + Cues*.
   It then appears under **MIDI devices** in the Control workspace.
2. In **Control** (`Ctrl+6`), right-click the BCF2000 row → **Edit MIDI device…** and set:
   - **Alias** = `bcf`  (the short `deviceKey` scripts use, instead of the long OS port name)
   - **Profile** = **Behringer BCF2000**  ← this is what turns on 14-bit pairing
3. Confirm the X32 device alias is `x32` and `/xremote` is running (Getting Started step 3).

---

## Step 2 — Create the five layers

*Project structure* header → **Add layer…**, once per row (names must match exactly):

| Name | Priority | Active |
| --- | --- | --- |
| `Channels 1-8` | 0 | ✅ checked |
| `Channels 9-16` | 1 | unchecked |
| `Channels 17-24` | 2 | unchecked |
| `Channels 25-32` | 3 | unchecked |
| `Outputs` | 4 | unchecked |

---

## Step 3 — Add the shared helper

Add a script, **Path** `Scripts/bcf_surface.mnd`, **Project** scope, **no triggers** (it's a
library the layer scripts `require`). Paste:

```js
// Scripts/bcf_surface.mnd
// Shared BCF2000 + X32 logic. A "strip" is one of the 8 physical columns:
//   { fader: <osc addr>, mute: <osc addr>, gain: <osc addr or ""> }
// Layer scripts build an 8-element strips array and forward their events here.
// A null strip, or an "" address, means "this column does nothing on this layer".

const X32 = "x32";            // OSC device alias  (edit to match yours)
const SURFACE = "bcf";        // MIDI device alias (edit to match yours)
const MIDI_CHANNEL = 1;       // BCF2000 MIDI channel

const FADER_CC = 0;           // motor faders = 14-bit CC 0..7
const ENCODER_CC = 10;        // encoders     = 14-bit CC 10..17
const PUSH_NOTES = [0, 1, 2, 3, 4, 5, 6, 7];          // encoder press
const MUTE_NOTES = [10, 11, 12, 13, 14, 15, 16, 17];  // Button Row 1

// The BCF outputs the FULL 14-bit range once its encoder/fader ranges are set to max.
// If you leave them limited (template default: 9999 faders / 999 encoders), set to match.
const FADER_IN_MAX = 16383.0;
const ENCODER_IN_MAX = 16383.0;

const GAIN_0DB = 0.1667;      // X32 head-amp gain float for 0 dB (the -12..+60 dB law)

// ----- address helpers --------------------------------------------------------

fun pad3(n) {
    if (n < 10)  return '00{0}'.format(n);
    if (n < 100) return '0{0}'.format(n);
    return '{0}'.format(n);
}

// X32 head-amp (input) gain for a channel, assuming local input N feeds channel N
// (head-amp index = channel - 1). Adjust if your input routing differs.
export fun headampGain(channel) {
    return '/headamp/{0}/gain'.format(pad3(channel - 1));
}

// Build the 8 channel strips for a bank: base 0 -> ch 1..8, 8 -> 9..16, ...
export fun channelStrips(base) {
    var strips = [];
    for (var i = 0; i < 8; i++) {
        var ch = base + i + 1;
        strips.add({
            fader: x32.channelFaderAddress(ch),
            mute:  x32.channelMuteAddress(ch),
            gain:  headampGain(ch)
        });
    }
    return strips;
}

fun indexOf(list, value) {
    for (var i = 0; i < list.length(); i++)
        if (list[i] == value) return i;
    return -1;
}

// ----- BCF feedback: drive faders / encoder rings / button LEDs ----------------

fun paintFader(slot, value) {
    midi.sendHighResCc(SURFACE, MIDI_CHANNEL, FADER_CC + slot, math.clamp(value, 0.0, 1.0) * FADER_IN_MAX + 0.5);
}

fun paintEncoder(slot, value) {
    midi.sendHighResCc(SURFACE, MIDI_CHANNEL, ENCODER_CC + slot, math.clamp(value, 0.0, 1.0) * ENCODER_IN_MAX + 0.5);
}

fun paintMute(slot, mixOn) {
    // X32 /mix/on: 1 = unmuted, 0 = muted. LED + NoteOn velocity 127 = lit while UNMUTED.
    if (mixOn >= 0.5)
        midi.sendNoteOn(SURFACE, MIDI_CHANNEL, MUTE_NOTES[slot], 127);
    else
        midi.sendNoteOff(SURFACE, MIDI_CHANNEL, MUTE_NOTES[slot], 0);
}

// ----- incoming control handlers (called by the layer scripts) -----------------

// Motor fader moved -> set the strip's level (absolute 14-bit).
export fun handleFader(event, strips) {
    var slot = event.midi.controller - FADER_CC;
    if (slot < 0 || slot >= 8) return;          // not a fader CC
    var s = strips[slot];
    if (s == null || s.fader == "") return;
    var level = math.clamp(event.midi.value / FADER_IN_MAX, 0.0, 1.0);
    osc.send(X32, s.fader, osc.float32(level));
    osc.cacheSet(X32, s.fader, level);
}

// Encoder turned -> set the strip's gain (absolute). No-op when the strip has no gain.
export fun handleEncoder(event, strips) {
    var slot = event.midi.controller - ENCODER_CC;
    if (slot < 0 || slot >= 8) return;          // not an encoder CC
    var s = strips[slot];
    if (s == null || s.gain == "") return;
    var gain = math.clamp(event.midi.value / ENCODER_IN_MAX, 0.0, 1.0);
    osc.send(X32, s.gain, osc.float32(gain));
    osc.cacheSet(X32, s.gain, gain);
}

// Faders and encoders both arrive as MidiControlChange; route by CC range.
export fun handleCc(event, strips) {
    handleFader(event, strips);
    handleEncoder(event, strips);
}

// Notes: encoder press = gain to 0 dB; Button Row 1 = mute (latching).
export fun handleNote(event, strips) {
    var note = event.midi.note;

    var push = indexOf(PUSH_NOTES, note);
    if (push >= 0) {
        if (!event.midi.isNoteOn) return;       // momentary: act on press only
        var sp = strips[push];
        if (sp == null || sp.gain == "") return;
        osc.send(X32, sp.gain, osc.float32(GAIN_0DB));
        osc.cacheSet(X32, sp.gain, GAIN_0DB);
        paintEncoder(push, GAIN_0DB);
        return;
    }

    var mute = indexOf(MUTE_NOTES, note);
    if (mute >= 0) {
        var sm = strips[mute];
        if (sm == null || sm.mute == "") return;
        // Row 1 buttons latch: NoteOn = unmuted (mix/on = 1), NoteOff = muted (0).
        var on = event.midi.isNoteOn ? 1 : 0;
        osc.send(X32, sm.mute, osc.int32(on));
        osc.cacheSet(X32, sm.mute, on);
        paintMute(mute, on);
        return;
    }
}

// X32 reported a value (reply or /xremote push) -> repaint the matching column.
export fun handleFeedback(event, strips) {
    var address = event.osc.address;
    for (var slot = 0; slot < strips.length(); slot++) {
        var s = strips[slot];
        if (s == null) continue;
        if (s.fader != "" && address == s.fader) { paintFader(slot, event.value); return; }
        if (s.mute  != "" && address == s.mute)  { paintMute(slot, event.value);  return; }
        if (s.gain  != "" && address == s.gain)  { paintEncoder(slot, event.value); return; }
    }
}

// Paint everything we know and request what we don't. Bound to LayerEnabled (onActivate), which fires
// both on Arm and on every layer switch — so the bank loads the console's state at those moments. NOT on
// a timer, so the motors are only driven on real events (no constant re-driving / wear).
export fun refreshBank(strips) {
    for (var slot = 0; slot < strips.length(); slot++) {
        var s = strips[slot];
        if (s == null) continue;

        if (s.fader != "") {
            if (osc.has(X32, s.fader)) paintFader(slot, osc.cacheFloat(X32, s.fader, 0.0));
            else osc.request(X32, s.fader);
        }
        if (s.mute != "") {
            if (osc.has(X32, s.mute)) paintMute(slot, osc.cacheFloat(X32, s.mute, 1.0));
            else osc.request(X32, s.mute);
        }
        if (s.gain != "") {
            if (osc.has(X32, s.gain)) paintEncoder(slot, osc.cacheFloat(X32, s.gain, 0.0));
            else osc.request(X32, s.gain);
        }
    }
}

// Full reload: re-request fresh values AND re-send what we have, so a slow motor fader
// gets nudged back into place. Bound to note 43 in each layer script.
export fun forceReload(strips) {
    for (var slot = 0; slot < strips.length(); slot++) {
        var s = strips[slot];
        if (s == null) continue;
        if (s.fader != "") {
            osc.request(X32, s.fader);
            if (osc.has(X32, s.fader)) paintFader(slot, osc.cacheFloat(X32, s.fader, 0.0));
        }
        if (s.mute != "") {
            osc.request(X32, s.mute);
            if (osc.has(X32, s.mute)) paintMute(slot, osc.cacheFloat(X32, s.mute, 1.0));
        }
        if (s.gain != "") {
            osc.request(X32, s.gain);
            if (osc.has(X32, s.gain)) paintEncoder(slot, osc.cacheFloat(X32, s.gain, 0.0));
        }
    }
}
```

**Save Script.** The **Exports** line should list `headampGain, channelStrips, handleFader,
handleEncoder, handleCc, handleNote, handleFeedback, refreshBank, forceReload`.

---

## Step 4 — Add the four channel banks

Each bank is tiny — it just builds its 8 strips and forwards events. Add a script, set the
**Path**, paste, then set **Scope = `Layer`**, pick its **Layer**, and add the triggers below.

### `Scripts/bcf_bank_1.mnd` (channels 1–8)

```js
const Surface = require('Scripts/bcf_surface.mnd');  // uppercase = module namespace
const BASE = 0;                                      // base 0 -> channels 1..8

fun strips() { return Surface.channelStrips(BASE); }

export fun onCc(event, context)       { Surface.handleCc(event, strips()); }
export fun onNote(event, context)     { Surface.handleNote(event, strips()); }
export fun onFeedback(event, context) { Surface.handleFeedback(event, strips()); }
export fun onActivate(event, context) { Surface.refreshBank(strips()); }
export fun onReload(event, context)   { if (event.midi.isNoteOn) Surface.forceReload(strips()); }
```

Triggers (**Layer = `Channels 1-8`**):

| Kind | Function | Match |
| --- | --- | --- |
| `MidiControlChange` | `onCc` | — (blank) |
| `MidiNote` | `onNote` | — (blank) |
| `MidiNote` | `onReload` | note **43** |
| `OscCacheChanged` | `onFeedback` | — (blank) |
| `LayerEnabled` | `onActivate` | — |

> No periodic trigger: `LayerEnabled` (`onActivate`) fires both **on Arm** and on every layer switch, so
> the bank loads the console's state at exactly those moments and the motor faders are never re-driven on
> a timer. Live console changes still track via `OscCacheChanged`, and **note 43** force-reloads a bank if
> a motor ever lags.

### `Scripts/bcf_bank_2.mnd` … `bcf_bank_4.mnd`

Identical except the `BASE` constant and the layer. Create three more, changing **one line**
and the **Layer** picker:

| Script | `const BASE` | Scope → Layer |
| --- | --- | --- |
| `bcf_bank_2.mnd` | `8` | `Channels 9-16` |
| `bcf_bank_3.mnd` | `16` | `Channels 17-24` |
| `bcf_bank_4.mnd` | `24` | `Channels 25-32` |

Give each the **same five triggers** as `bcf_bank_1.mnd`.

---

## Step 5 — Add the outputs layer

This one uses an explicit strip table instead of a base offset (outputs aren't contiguous, and
they have no head-amp gain, so the encoders/press are inert here).

Add `Scripts/bcf_outputs.mnd`, **Scope = `Layer`**, **Layer = `Outputs`**:

```js
const Surface = require('Scripts/bcf_surface.mnd');

// One entry per physical column (slot 0..7). gain "" = encoder/press does nothing
// (busses/returns have no head-amp gain). null = unused column.
//
// VERIFY THESE FOR YOUR CONSOLE: "Aux 1/3/5/7" are taken as mix busses 1/3/5/7 (their stereo
// partners are linked on the console), and "FX Ret 1 / FX 3" as fx-return channels 01 / 03.
// Change any address to match how your X32 is laid out.
fun strips() {
    return [
        { fader: x32.mainFaderAddress(), mute: x32.mainMuteAddress(), gain: "" }, // 1  Main
        { fader: x32.busFaderAddress(1), mute: x32.busMuteAddress(1), gain: "" }, // 2  Aux 1
        { fader: x32.busFaderAddress(3), mute: x32.busMuteAddress(3), gain: "" }, // 3  Aux 3
        { fader: x32.busFaderAddress(5), mute: x32.busMuteAddress(5), gain: "" }, // 4  Aux 5
        { fader: x32.busFaderAddress(7), mute: x32.busMuteAddress(7), gain: "" }, // 5  Aux 7
        null,                                                                      // 6  (nothing)
        { fader: "/fxrtn/01/mix/fader", mute: "/fxrtn/01/mix/on", gain: "" },      // 7  FX Ret 1 (L)
        { fader: "/fxrtn/03/mix/fader", mute: "/fxrtn/03/mix/on", gain: "" }       // 8  FX 3
    ];
}

export fun onCc(event, context)       { Surface.handleCc(event, strips()); }
export fun onNote(event, context)     { Surface.handleNote(event, strips()); }
export fun onFeedback(event, context) { Surface.handleFeedback(event, strips()); }
export fun onActivate(event, context) { Surface.refreshBank(strips()); }
export fun onReload(event, context)   { if (event.midi.isNoteOn) Surface.forceReload(strips()); }
```

Add the **same five triggers** (Layer = `Outputs`).

> Because the busses are stereo-**linked on the console**, controlling Aux 1/3/5/7 (and FX
> Ret 1) moves their partner automatically — the surface only needs the odd side.

---

## Step 6 — Add the layer switch

The layer nav is **project-scoped** so it works from any layer. Add `Scripts/bcf_layers.mnd`,
keep **Scope = `Project`**:

```js
// Scripts/bcf_layers.mnd — BCF2000 Button Group 5 switches layers.
// NOTE: keep this array name LOWERCASE. Mond treats an UPPERCASE receiver as a module namespace and
// does NOT pass it as `this`, so `LAYERS.length()` would fail with "missing instance argument". A
// lowercase name (`layerNames`) makes `layerNames.length()` / indexing work normally.
const layerNames = ["Channels 1-8", "Channels 9-16", "Channels 17-24", "Channels 25-32", "Outputs"];

fun indexOf(list, value) {
    for (var i = 0; i < list.length(); i++)
        if (list[i] == value) return i;
    return -1;
}

export fun onLayerNav(event, context) {
    if (!event.midi.isNoteOn) return;            // momentary buttons: act on press
    var cur = indexOf(layerNames, layers.active());
    if (cur < 0) cur = 0;
    var step = event.midi.note == 50 ? -1 : 1;   // note 50 = previous, note 51 = next
    var next = cur + step;
    if (next < 0) next = layerNames.length() - 1;
    if (next >= layerNames.length()) next = 0;
    layers.activate(layerNames[next]);
}
```

Two triggers:

| Kind | Function | Match |
| --- | --- | --- |
| `MidiNote` | `onLayerNav` | note **50** |
| `MidiNote` | `onLayerNav` | note **51** |

`layers.activate(...)` switches after the handler returns, firing `LayerEnabled` on the new
layer → its `onActivate` → `refreshBank`, which reloads that bank's faders / rings / LEDs.

---

## Step 7 — Arm and test

Press **Arm**.

- Within a second or two the **Channels 1-8** faders motor to the console's levels, the
  encoder rings show the input gains, and the Row 1 LEDs light for unmuted channels.
- **Move a fader** → the X32 channel fader follows (full 14-bit resolution).
- **Turn an encoder** → that channel's input gain changes; **press it** → gain jumps to 0 dB.
- **Press a Row 1 button** → the channel mutes/unmutes; the LED tracks it (lit = unmuted).
- **Group 5: note 51 / note 50** → next / previous layer; the whole surface reloads.
- **Group 4 button 4 (note 43)** → reloads the current layer (re-pushes fader positions if a
  motor lagged).
- Move something **on the console** (with `/xremote` up) → the matching fader/ring/LED follows.

Watch the **live monitor**: a fader move should now show **one** `controlChange` with a value
up to `16383` (not a `CC0`+`CC32` pair) — that confirms the profile's 14-bit pairing is on.

---

## Troubleshooting

- **Faders show as two messages (`CC0` + `CC32`) / only reach half-travel.** The BCF2000
  **profile** isn't assigned, so 14-bit pairing is off. Right-click the device → **Edit MIDI
  device…** → Profile = **Behringer BCF2000**, re-arm.
- **Faders/encoders never reach the top.** Your BCF ranges are still limited. Either set the
  encoder/fader ranges to maximum in the BCF editor, or set `FADER_IN_MAX` / `ENCODER_IN_MAX`
  in the helper to your configured maxima (e.g. `9999.0` / `999.0`).
- **Motors / LEDs don't move.** The BCF needs a registered **output**. MIDI Ports → select it
  under outputs → **Use for Control + Cues**, re-arm. Check the alias is `bcf`.
- **Nothing reaches the X32.** Alias `x32`, host/port correct, `/xremote` configured. The
  first touch of an untouched control just primes the cache — try again.
- **A fader sat in the wrong spot.** Press **note 43** to reload, or switch away and back.
- **Wrong channels respond.** Each bank's **Layer** and `channelStrips(base)` must agree
  (`0/8/16/24` ↔ `1-8/9-16/17-24/25-32`).
- **Encoders do nothing on the Outputs layer.** Expected — output busses have no head-amp
  gain, so those strips use `gain: ""`.
- **Layer switch does nothing.** `bcf_layers.mnd` must be **Project** scope with two
  `MidiNote` triggers (notes 50 and 51), and the `LAYERS` names must match your layer names.
- **`require: module could not be found`.** The helper path must be exactly
  `Scripts/bcf_surface.mnd`.
- **A bank loaded with everything at minimum / all unmuted at arm.** The after-Arm load runs from the
  `LayerEnabled → onActivate` trigger (which now fires on Arm) plus `OscCacheChanged → onFeedback` painting
  the replies. Make sure the active layer has its `LayerEnabled → onActivate` trigger, the `onFeedback`
  match is **blank** (or `/ch/*`) — never `/ch/*/mix/*`, which never matches — and `/xremote` is running.
- **Nothing loads at arm (faders/LEDs stay put).** The active layer needs the `LayerEnabled → onActivate`
  trigger; without it, the bank only loads on a layer switch or a **note 43** reload.

---

## Appendix — surface map

BCF2000 (MIDI channel 1), from `Reference/BCF2000.txt`:

| Control | MIDI | Used for |
| --- | --- | --- |
| Motor faders 1–8 | 14-bit CC `0`–`7` (fine `32`–`39`) | channel / output fader |
| Encoders 1–8 | 14-bit CC `10`–`17` (fine `42`–`49`) | channel gain (layers 1–4) |
| Encoder press 1–8 | Note `0`–`7` | gain → 0 dB |
| Button Row 1 (1–8) | Note `10`–`17`, vel `127` = lit | channel / output mute (lit = unmuted) |
| Button Group 5 | Note `50` / `51` | layer **previous / next** |
| Button Group 4 #4 | Note `43` | reload current layer |

Other banks are free for your own scripts: Row 2 = notes `20`–`27`, Group 3 = `30`–`33`,
Group 4 = `40`–`43` (note `43` used here), Group 6 = `60`–`63`.

X32 OSC used:

| What | Address | Value |
| --- | --- | --- |
| Channel fader | `/ch/NN/mix/fader` | float `0.0`–`1.0` |
| Channel mute | `/ch/NN/mix/on` | int `1` = unmuted, `0` = muted |
| Channel gain (head amp) | `/headamp/NNN/gain` (`NNN` = channel − 1) | float, `0.1667` = 0 dB |
| Main fader / mute | `/main/st/mix/fader` · `/main/st/mix/on` | float · int |
| Bus fader / mute | `/bus/NN/mix/fader` · `/bus/NN/mix/on` | float · int |
| FX return fader / mute | `/fxrtn/NN/mix/fader` · `/fxrtn/NN/mix/on` | float · int |

> **Head-amp assumption:** this maps channel *N* to head-amp *N−1* (local input *N* feeds
> channel *N*, the X32 default). If your input patch differs, change `headampGain` or replace
> the `gain` address per strip.
