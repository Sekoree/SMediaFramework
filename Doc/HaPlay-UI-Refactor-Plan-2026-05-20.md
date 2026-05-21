# HaPlay UI Refactor — Plan (2026-05-20)

Planning document only. No code in this revision. Status legend matches
`MediaFramework-Checklist-2026-05-20.md`:

- `[ ]` not started
- `[~]` in progress
- `[x]` done
- Priority `P0`–`P3` matches the checklist conventions.

This plan covers the next round of HaPlay UI work. It does not change
framework APIs unless explicitly called out, and it does not replace the
existing `MediaFramework-Checklist-2026-05-20.md` — that doc continues to
track framework-side cleanup; this one captures product-side scope.

---

## 1. Executive Summary

HaPlay today is functional but reads as a developer testbed: outputs are
configured in dialogs and can't be edited, the media player view is a
vertical stack of small controls, and there is no way to organize media
into cue stacks. The refactor turns it into a usable show-control tool
while keeping the underlying framework unchanged where possible.

The five largest pieces:

1. **Output configuration is editable in place** — sample rate, channel
   count, fullscreen target, NDI format lock, and clone/child views all
   become first-class properties on an existing output line.
2. **MediaPlayer view is redesigned** around big transport buttons and a
   `TreeDataGrid`-first audio routing workspace: M input channels routed
   into N virtual output channels with per-connection gain/mute, plus
   master gain, hot-swap output routing, and multi-tab playlists per
   player.
3. **New Cue Player view** built on `TreeDataGrid` end-to-end: grouped
   cues, virtual-output-channel route overrides with per-connection gain,
   GO/standby/panic transport, and optional auto-follow chaining.
4. **Live inputs as media items** — NDI sources and PortAudio inputs
   become first-class items in both Media Player playlists and Cue
   Player cue stacks, with auto-discovery + manual-name flows for
   sources that aren't on the network at design time.
5. **Project save/load** — one file captures the whole session (outputs,
   players, cue stacks, layout) with versioning for future migrations.

A cross-cutting sixth track — **app-shell + dialog redesign + terminology
cleanup** (§12) — runs alongside the feature work so the views above
land in a consistent visual language, with a collapsible left sidebar
replacing the current top-tab shell, every dialog re-using the same
conventions, and implementation-leaky names (`AvaloniaOpenGl`,
`HoldFallbackVideo`, etc.) renamed to user-visible terms.

The plan also lists cross-cutting concerns (data-model versioning,
output-line uniqueness, framework gaps the UI will hit) and a suggested
phasing order so we don't try to land everything in one branch.

---

## 2. Goals & Non-Goals

### Goals

- End-user-quality polish: a non-developer should be able to configure
  outputs, build a playlist, and run a cue stack without reading source.
- No regressions in current playback scenarios (PortAudio + Avalonia +
  NDI through the MediaPlayer view).
- All persistent state survives an app restart with one project file.
- Audio routing is visible and editable (matrix view).
- Cue Player covers basic show-control workflows: GO, panic, auto-follow,
  per-cue route + gain overrides.

### Non-Goals

- No new media framework primitives unless explicitly listed in
  §9.6 (Framework Gaps). Most UI work should compose what's already
  there.
- Add-output dialogs get reviewed for consistency in §12.2 but the
  Edit form is the *same* dialog as Add — no parallel codepath.
- **OSC/MIDI direction split**: outbound triggers from action cues
  (§5.2, §5.6) *are* in scope. Inbound remote control (external
  controller fires a cue / sets a gain) is *not* — that's tracked
  as §8.4 for a later round.
- **Live source ingest is in scope** for NDI and PortAudio inputs
  (§6). Capture cards / DeckLink / video4linux are not — flagged
  in §8 as future.
- No multi-machine sync / cluster mode.

---

## 3. Output Configuration View

### 3.1 Current State

`OutputManagementView` lists outputs added via "Add PortAudio / Add
Local Video / Add NDI" dialogs. Once added, an output is immutable:
changing the sample rate or screen target means delete + re-add, which
breaks every routing that referenced it.

### 3.2 Target Design

Each line in the outputs list gets an **Edit** button that opens the
same dialog used to add it, pre-populated. On Save, the runtime
(`PortAudioOutputRuntime` / `NDIOutputPreviewRuntime` /
`AvaloniaLocalVideoPreviewRuntime`) is reconfigured *in place* — same
`OutputLineViewModel`, same identity, so existing player routings
continue to reference the line by id.

Reconfigure semantics differ per output kind:

- **PortAudio** — must Stop the persistent stream, Open at the new
  sample rate / channel count, then Start. Players currently routed to
  the line are temporarily silenced; the existing
  `ResamplingAudioSink` path absorbs the rate change without breaking
  the route.
- **Local video (Avalonia / SDL3)** — change `WindowWidth/Height`,
  `ScreenIndex`, and `SurfaceMode` while the window stays open. SDL3
  supports `SDL_SetWindowSize` / `SDL_SetWindowFullscreenMode`;
  Avalonia exposes equivalents via `Window.Width/Height/Screens`.
- **NDI** — most fields (group/source name) require sender restart, but
  sample rate and clock flags can change in-place via
  `NDIOutputPreviewRuntime` rebuild. The carrier resumes after the
  reconfigure window with the new format.

### 3.3 New Output-Level Settings

- **PortAudio**: editable sample rate, channel count, host-API/device
  reselect, suggested latency override.
- **Local video**: editable resolution (windowed only), fullscreen
  screen index, surface mode toggle. New **Clone parent** dropdown
  — when set, this output mirrors another local video output's frames
  via `VideoRouter`'s fan-out (the parent gets the route from a player;
  the clone is added as an additional output on that same route, with
  optional resize/crop).
- **NDI**: editable audio channel count (1–16), audio sample rate,
  optional **pixel-format lock** (force `Uyvy` / `Bgra32` / `Nv12` so
  the negotiated format is predictable for downstream receivers
  regardless of source), optional **resolution lock** (scale incoming
  to a fixed output size for receiver compatibility).

### 3.4 Clone / Child Output Model

The "Clone parent" feature is the only piece that needs framework
support beyond what exists. Today `VideoRouter` supports fan-out across
multiple registered outputs but each clone needs a route registered by
whoever owns the input — that's `MediaPlayer`, not the clone itself.

Two options for plumbing:

- **A. Clones are independent router outputs** — when the user adds a
  clone of output X, every player that has X selected automatically
  also routes to the clone. The clone owns its sink registration; the
  active player(s) own the route. Pros: clean separation. Cons: needs
  selection-mirroring logic in every player VM that touches output
  routing.
- **B. Clones live inside the parent runtime** — the runtime owns its
  primary sink and N clone sinks; on `AcquireForPlayback` it hands the
  caller a *composite* `IVideoSink` that fans out internally. Pros: no
  selection mirroring; clone is invisible to the player VM. Cons: bakes
  fan-out into the runtime instead of letting the existing
  `VideoRouter` do it; layered fan-out (router → runtime → SDL+Ava) is
  weirder.

Recommend **Option A** with a small `PlayerRoutingMirror` helper in the
HaPlay VM layer that owns the "if X is selected, also select clones of
X" logic.

### 3.5 Acceptance Criteria

- Edit dialog opens with current values pre-filled for every output kind.
- Save reconfigures the runtime without breaking active player routes
  (a brief silence/black-frame window is acceptable).
- Clone output appears in the outputs list, displays its parent's
  selection state, and reflects parent route changes within one frame.
- Pixel-format lock on NDI: when set, `NDIVideoSender.Configure` is
  always called with that format; the router inserts a converter from
  the negotiated source format on the branch.

### 3.6 Open Questions (Resolved)

- Should "Edit while a player is using this output" require Stop, or
  apply hot? Recommend hot with a 1–2 frame glitch; user can stop
  manually if they want a clean transition.
  **Decided:** hot with the 1–2 frame glitch.
- Clone of a local-video output: does the user pick a window size
  independently, or does the clone match the parent? Recommend
  independent (per clone) so the same source can drive a confidence
  monitor at 480p next to a fullscreen primary.
  **Decided:** independent (per clone).

---

## 4. MediaPlayer View Redesign

### 4.1 Current State

`MediaPlayerView` is a vertical stack: file path label, transport
buttons (small), progress slider, output checkboxes, hold-image
expander, playlist list, status banner. Adding outputs requires opening
the Outputs panel. There is one playlist per player.

### 4.2 Target Layout

```
┌─────────────────────────────────────────────────────────────────┐
│  ▶  ⏸  ⏹  ⏮  ⏭   [ 00:12 / 03:45  ── seek bar ── ]   🔊 -3 dB │
├─────────────────────────────────────────────────────────────────┤
│ Tabs: [ Set A ] [ Set B ] [ Encore ] [ + ]                      │
│ ┌───────────────────────────────────────────────────────────┐   │
│ │ Playlist (current tab)                                    │   │
│ │  1. opening-bumper.mp4               00:08                │   │
│ │  2. main-show.mkv                    42:11   ▶ playing    │   │
│ │  3. closing-stinger.mp4              00:12                │   │
│ └───────────────────────────────────────────────────────────┘   │
├─────────────────────────────────────────────────────────────────┤
│ Routing                       Audio Matrix                       │
│  ☑ Main Speakers (PA)         TreeDataGrid: In1..M × VOut1..N   │
│  ☑ NDI Program                TreeDataGrid: active routes list  │
│  ☐ Confidence Monitor         (Input, VOut, Gain, Mute)         │
│  [ + Add output… ]            [ Matrix expander open/close ]    │
├─────────────────────────────────────────────────────────────────┤
│ Output preset:  ⟨ As source ⟩  ⟨ 1080p60 ⟩  ⟨ Custom… ⟩         │
│ Transition:     ⟨ Cut ⟩  ⟨ Fade 500 ms ⟩  ⟨ Hold image ⟩       │
└─────────────────────────────────────────────────────────────────┘
```

### 4.3 Detailed Pieces

#### 4.3.1 Transport bar

- Big square buttons (≈64 px), keyboard-first: Space = Play/Pause,
  `[` / `]` = prev/next playlist item, `,` / `.` = -5/+5 s seek.
- Seek bar: scrubbing emits one seek at drag-end, not per-tick
  (avoid the chained-pause-stop-seek freeze documented in
  `[[playback-teardown-timing]]`).
- Time display: elapsed / total. Click to toggle to remaining / total.
- Master volume slider on the right with mute button.

#### 4.3.2 Playlist tabs

- Tab strip above playlist. Right-click to rename; double-click to
  rename inline.
- Tab state (paths, selected index, looping, auto-advance) is
  per-tab. Switching tabs swaps the playlist; transport stays running
  on whichever tab was last triggered (no auto-stop on tab change).
- Save menu: **Save tab as…** (current playlist only) and
  **Save player as…** (all tabs + routing + per-channel gain).
- Load menu: same split. Loading a tab into an existing player adds it
  as a new tab; loading a player replaces the player.
- `+` tab opens a blank tab.
- Playlist rows can be files **or live inputs** (NDI source / PortAudio
  input). Live items hide seek / loop / auto-advance controls when
  selected — see §6 for the item kinds and add flow.
- **Playlist import** — File → Import accepts `.m3u` / `.m3u8` (parsed
  for `#EXTINF` titles and file paths only — live items have no M3U
  representation, so import is files-only). Imports append to the
  current tab. Native save/load remains the JSON `PlaylistConfig`
  (§4.5) so live items and per-item state survive a round trip.

#### 4.3.3 Routing panel

- Checkboxes for every registered output (current behavior).
- `+ Add output…` button at the bottom of the list opens the existing
  Add-Output dialog directly, so the user can wire a new PortAudio or
  NDI line without leaving the player. The new output is auto-checked.
- **Hot route changes during playback**: framework already supports
  `AudioRouter.AddSink` / `VideoRouter.AddOutput` + `TryAddRoute` while
  running; HaPlay just needs to call them. The `_logoSinks` list and
  the carrier-acquire path need to handle late-acquired NDI/local-video
  outputs (current `TryCreate` is all-or-nothing).

#### 4.3.4 Audio matrix

- A single `TreeDataGrid`-based routing workspace (no alternate matrix
  control) inside a collapsible expander.
- Decoder channels are the **input axis** (`In 1..M`).
- Selected output lines expose a deterministic **virtual output channel**
  axis (`VOut 1..N`), where each `VOut` maps to one concrete pair:
  `(OutputLineId, OutputChannelIndex)`.
- Each matrix cell is a candidate route from one input channel to one
  virtual output channel; non-zero cells are active routes.
- Under the matrix, a second `TreeDataGrid` shows one row per active
  route connection with direct edit fields:
  `Input`, `Virtual Output`, `Gain dB`, `Mute`, and computed
  `Effective Gain` (master × output trim × connection gain).
- Per-output gain stays as a convenience trim; precise balancing lives at
  route-connection rows.
- Saves into player config as:
  1. virtual output channel mapping snapshot (`VOut` -> concrete output
     channel), and
  2. per-connection rows (`input`, `vout`, `gain`, `mute`).
  Loads back via `AudioRouter.AddRoute` with one route per non-zero
  connection row.

This is the one piece that brushes against framework limits.
`AudioRouter`'s `ChannelMap` today is "each output channel picks one
input channel index or -1" with a single per-route gain. A true matrix
needs *per-cell* gain. Two ways forward:

- **Multiple routes**: register one route per (input-channel,
  output-channel) pair, each with its own `ChannelMap` and gain. The
  router already sums multiple routes targeting the same sink, so this
  works without framework changes — just more routes.
- **ChannelMap extension**: add a `WeightedChannelMap` variant that
  stores a per-cell weight matrix. Cleaner but bigger framework change.

Recommend the multi-route approach for the first cut. Revisit if route
count gets unwieldy (8 in × 8 out = 64 routes per player).

UI simplification decision: keep one reusable routing editor component
(`TreeDataGrid` matrix + `TreeDataGrid` connection list) shared by
MediaPlayer and CuePlayer instead of separate editors.

#### 4.3.5 Output preset + transition

- "Output preset" replaces the current ad-hoc hold-image flow with a
  defined composition target:
  - **As source** (current behavior — pixel format and resolution come
    from the decoder).
  - **1080p60 / 720p60 / etc.** — uses `CpuVideoCompositor` (or the
    GL compositor where available) to render decoded frames into a
    fixed target. NDI receivers see the same format regardless of
    source. Replaces the per-output pixel-format lock for the common
    case.
  - **Custom…** — arbitrary width × height × frame rate.
- "Transition" sets the behavior between playlist items / loops /
  hold-image toggles:
  - **Cut** (current default — instant).
  - **Fade N ms** — uses `FadeFromBlackVideoSource` /
    `LayerOpacityTween` for video, a router-level gain ramp for audio.
  - **Hold image** (current behavior preserved for back-compat — sets
    the per-player hold template).

This is the cleanest place to drop the current "hold image" expander.
The semantics become "what plays when no media is decoded" instead of
"override the decoded frames with a static," and transitions become
first-class.

### 4.4 Acceptance Criteria

- Transport bar visible at all sizes ≥ 800×600.
- Tab switch swaps playlist without stopping/restarting playback.
- Adding an output via the inline `+` button registers it in
  `OutputManagementViewModel` and immediately appears in every other
  player's routing list too.
- Audio matrix edits take effect within one chunk (~10 ms at default
  config) — no Stop required.
- Matrix and route-connection list stay in sync in both directions:
  matrix cell edits add/remove/update connection rows; connection-row
  edits update the matrix immediately.
- Virtual output channel numbering is deterministic for a given selected
  output set and survives save/load without route drift.
- Saving a tab produces a self-contained playlist file; loading it
  into a player with different outputs ignores the missing routes
  with a banner ("3 routes skipped — outputs not present in this
  player").

### 4.5 Open Questions (Resolved)

- Per-tab routing vs. per-player routing: should each tab remember
  its own selected outputs, or does routing live at the player level?
  Recommend per-player (simpler) with a future per-tab override.
  **Decided:** per-player. Per-tab routing isn't pursued — that level
  of differentiation is what the Cue Player (§5) is for.
- "Save just the playlist" file format: reuse the existing
  `MediaPlayerConfig` JSON with the routing fields blanked, or
  introduce a separate `PlaylistConfig`? Recommend separate type so
  the load path is unambiguous.
  **Decided:** separate `PlaylistConfig` type. Also: support loading
  third-party `.m3u` / `.m3u8` files as a one-way import (files-only —
  M3U has no live-input representation).

---

## 5. Cue Player View (NEW)

### 5.1 Motivation

Playlists are linear. Real shows are non-linear: VTs, stingers,
walk-ons, multiple looping beds. The Cue Player is a `TreeDataGrid`-based
view where each row is a cue with explicit routing/gain/fade/timing,
optionally grouped into folders that can be triggered as a unit.

Reference points: QLab (macOS), Show Cue Systems (Windows). Goal is
the same workflow, not feature-parity.

### 5.2 Data Model

```
CueList
 ├─ Group "Pre-show"
 │   ├─ Cue 1   walk-in-music.mp3   loop, fade-in 2s
 │   └─ Cue 2   announcement.wav    auto-follow Cue 1 + 60s
 ├─ Group "Show"
 │   ├─ Cue 3   opening-VT.mp4      cut, route Program+NDI
 │   ├─ Cue 4   bed-A.flac          fade-in 500ms, gain -6dB
 │   └─ Cue 5   speaker-intro.mp4   wait-for-go
 └─ Group "Encore"
     └─ Cue 6   stinger.mov         one-shot
```

Each cue is one of three kinds:

- **Media cue** — points at a file path or a live input item (§6).
  Drives a `MediaContainerSession` when fired.
- **Action cue** — fires an outbound trigger (OSC message, MIDI
  message, future: HTTP webhook / shell command) at GO. No media,
  no routing, no fades. Use for "tell the lighting desk to take
  scene 12 when this cue fires" or "send a NoteOn to the playback
  pedal's confidence light." See §5.6 for the framework
  composition.
- **Comment cue** — non-firing row; documentation in the cue list
  (stage manager notes, section breaks). Skipped by GO.

Per-cue fields shared across kinds:

- **Number / label** — auto-numbered (1, 2, 3.5, 3.6, 4) like QLab; can
  be edited; labels are free text.
- **Trigger mode** — `Manual` (wait for GO), `AutoFollow(previous +
  delay)`, `AutoContinue(start at previous + offset)`.
- **Pre-wait** — fixed delay between trigger and actual fire (so a
  manually-fired cue can still wait, e.g. for a count-in). Applies
  uniformly to media, action, and comment cues (the latter is a
  no-op).
- **Simultaneity** — implicit. Multiple cues can be active at once
  (a music bed under a video VT under an OSC trigger). The Cue
  Player owns one shared `AudioRouter` so audio sums; video routing
  flags conflicts (§5.6). To fire several cues at exactly the same
  GO, place them in a group with the group set to "fire all
  simultaneously" (§5.2 group trigger modes below).
- **Notes** — free-text per-cue for stage manager comments.

Media-cue-only fields:

- **Pre-roll** — load and warm the decoder this many seconds before
  trigger so GO is instant (open + seek to start, decode the first
  GOP). Frees the priming-style logic from being inline at GO time.
- **Start offset** — jump to this position in the file at trigger.
- **End behavior** — `Stop`, `FreezeLastFrame`, `Loop`, `FadeOutAndStop
  N ms`.
- **Per-cue route override** — subset of virtual output channels
  (`VOut` rows), with optional per-connection overrides. Default is
  inherit parent routing profile.
- **Per-cue audio gain** — overlaid on top of the inherited/overridden
  route-connection gain values.
- **Fade in / fade out** — durations applied on trigger / on end.
- **Transition** — `Cut`, `CrossfadeWithPrevious`, `FadeFromBlack`.

Action-cue-only fields:

- **Action kind** — `OscOut` (address + arg list), `MidiOut` (port +
  channel + message-type + data bytes), reserved for `HttpWebhook` /
  `ShellCommand` later.
- **OSC**: target endpoint id (one of the named endpoints registered
  in §12.6 — "Lighting Desk," "Video Switcher," etc.), address
  pattern, arg list (typed: i / f / s / T / F / blob).
- **MIDI**: target port id (one of the registered devices in §12.6),
  channel (1–16), message-type (NoteOn, NoteOff, CC, PC, SysEx),
  data bytes.
- **Repeat** — fire once / fire N times with interval (handy for
  "send the same CC every 100 ms for 1 s" patterns).

Per-group fields:

- **Trigger mode for "GO when this group is current"** — fire first
  cue only, fire all cues simultaneously (audio bed + video VT
  combined), or armed-list (GO advances within group).
- **Default route override** — inherited by every cue that doesn't
  specify its own.
- **Default fades** — same idea.

### 5.3 Transport

Cue-Player transport is separate from a normal player's because the
"current position" is a cursor in the cue list, not a media playhead:

- **GO** (big green button, keyboard `Space`) — fire the *standby* cue
  (next un-fired one in the active group), advance the standby cursor
  to the next cue.
- **Standby** indicator — visible "this fires next" highlight.
- **Pause** — pause every active cue without advancing the cursor.
- **Resume** — resume paused cues.
- **Stop active** — fade out / cut every currently playing cue per its
  end-behavior settings.
- **Panic** (red button, keyboard `Esc Esc`) — immediate hard-stop of
  every active cue, ignoring fades. Useful for fire alarms /
  evacuations.
- **Back** — rewind the standby cursor one step (does not un-fire).
- **Load cue file** / **Save cue file** — `.haplaycues.json`.

### 5.4 Visual Layout

```
┌──────────────────────────────────────────────────────────────────┐
│  ▶ GO    ⏸    ⏹ Stop    ✖ Panic    ⏮ Back                       │
│  Standby:  Cue 4  bed-A.flac                                     │
├──────────────────────────────────────────────────────────────────┤
│ # │ Label           │ File              │ Route    │ Fade │ ⏱    │
├───┼─────────────────┼───────────────────┼──────────┼──────┼──────┤
│ ▾ Pre-show                                                       │
│ 1 │ walk-in music   │ walk-in-music.mp3 │ Main+NDI │  2s  │ ∞    │
│ 2 │ announcement    │ announcement.wav  │ Main     │  -   │ 0:18 │
│ ▾ Show                                                           │
│ 3 │ opening VT      │ opening-VT.mp4    │ Pgm+NDI  │ Cut  │ 0:42 │
│ 4 │ bed A      ▶    │ bed-A.flac        │ Main     │ 500ms│ ∞    │
│ 5 │ speaker intro   │ speaker-intro.mp4 │ Pgm+NDI  │ Cut  │ 1:30 │
│ ▾ Encore                                                         │
│ 6 │ stinger         │ stinger.mov       │ Pgm+NDI  │ Cut  │ 0:08 │
└──────────────────────────────────────────────────────────────────┘
```

Right-pane (optional): selected-cue editor — file picker, route
overrides, gain/fade fields, end-behavior dropdown, notes textarea.

Below the cue list, Cue Player reuses the same routing-editor pattern as
MediaPlayer:

- `TreeDataGrid` matrix for M inputs -> N `VOut` channels.
- `TreeDataGrid` route-connection list with gain/mute per active route.
- Grouping in the connection list by `VOut` and by cue-override scope
  (inherited vs overridden).

### 5.5 Framework Composition

Each *active* cue gets its own `MediaContainerSession` (one decoder +
router branches). A `CuePlayer` controller (new HaPlay class) owns:

- The current `CueList`.
- A map `Dictionary<CueId, HaPlayPlaybackSession>` of active cues.
- Pre-roll cache: opened decoders for upcoming cues that aren't firing
  yet. Capped (e.g. last 4) so memory stays bounded.
- Scheduling for `AutoFollow` / `AutoContinue` (a `Timer` per active
  cue).

This means a single Cue Player view can have multiple cues playing
simultaneously (a video on Program + an audio bed underneath). Audio
routes from all active cues sum into the shared `AudioRouter` of the
Cue Player; video routes target the same outputs but the *last
triggered* video cue wins per output (you can't show two videos on
the same display at once — UI flags the conflict).

For compositing two video cues on the same output (PiP, lower-third
overlay), §8 lists this as a follow-up using the existing
`IVideoCompositor`.

**Action cues** don't touch the playback graph at all. They emit
outbound messages via the existing `OSCLib` (UDP sender) and `PMLib`
(MIDI port writer). HaPlay adds a thin "trigger emitter" service that
the cue controller calls on GO; the emitter draws from a project-wide
**multi-target endpoint registry** (§12.6) so a project can talk to
many OSC servers and many MIDI devices concurrently. Cues reference
endpoints by GUID (the display name is just a label), which keeps cue
files stable when endpoints are renamed or moved between IPs. The
same registry surface holds OSC/MIDI *input* bindings for the future
remote-control work in §8.4, so a future "external MIDI controller
fires cue 5 → cue 5 sends OSC to lighting" round-trip drops into the
same configuration page.

### 5.6 Acceptance Criteria

- Cue list loads from `.haplaycues.json` and shows the tree.
- GO fires the standby cue; standby advances; pre-rolled cues fire
  within ~50 ms of GO.
- Panic stops every active cue inside 200 ms regardless of fades.
- Auto-follow fires the next cue at the correct offset (verified with
  ±50 ms tolerance against a known clock).
- Per-cue route overrides honored on trigger; cue's audio appears on
  exactly the selected outputs.
- Per-cue route-connection overrides (input -> `VOut` gain/mute) are
  honored without affecting other active cues.
- Per-cue fade-in/out audible/visible at the right duration.

### 5.7 Open Questions (Resolved)

- Should cues share a single `MediaPlayer` instance (and stop the
  previous when GO fires the next), or always one player per active
  cue? Recommend one-per-active-cue to support concurrent audio +
  video; we may need to cap concurrency on resource-constrained hosts.
  **Decided:** one-per-active-cue. The Cue Player explicitly supports
  multiple cues firing at once (an audio bed under a VT under an OSC
  trigger), per-cue pre-wait delays, and future-extensible action
  cues (OSC/MIDI out — see §5.2 and §5.6). The Media Player view
  intentionally stays linear; advanced scheduling lives in the Cue
  Player.
- Where does the Cue Player view live? Same player tab strip as the
  MediaPlayer view (a tab type), or a top-level workspace alongside
  the player area? Recommend top-level workspace ("Players" /
  "Cues" / "Outputs" tabs).
  **Decided:** top-level workspace, surfaced through a **collapsible
  hamburger sidebar** on the left of the app shell. Sidebar shows
  icon-only when collapsed (so it doesn't eat horizontal space on
  laptops) and labels when expanded. Sidebar lists **Players**, **Cues**,
  **Outputs**, **OSC**, **MIDI**, **Project** (2026-05-21 — OSC connections
  and MIDI devices promoted from nested Cues/Project panels to reduce clutter).
  See §12 for the shell redesign.
- Pre-roll cost: do we pre-open every cue when the list loads, or
  only the next N? Recommend only the next N (configurable, default 4).
  **Decided:** only the next N, configurable. Same parameter
  controls the NDI-input pre-connect window from §6.11.

---

## 6. Live Inputs as Media Items

### 6.1 Motivation

Real shows mix media files with live sources: a guest microphone, a
camera feed, a remote presenter on NDI. Both the MediaPlayer view and
the Cue Player need to treat live inputs as first-class "items" —
added to a playlist or cue list, triggered on demand, routed through
the same matrix as files, level-controlled the same way.

### 6.2 Item Kinds

Two input kinds in the first cut:

- **NDI input** — identified by the source name NDI advertises
  (`MachineName (InstanceName)`). Auto-discovered or hand-typed.
- **PortAudio input** — identified by host API + device name +
  channel count + sample rate. Audio-only.

### 6.3 NDI Input UX

Add via "Add NDI input…" from the playlist context menu, cue list
add-row picker, or global Add menu. The dialog has two paths to
identify the source:

- **Live-discovered list** — `NDIFindInstance` scans the network and
  fills a refreshing dropdown of currently visible sources. Pick a
  source to populate the name field. Manual refresh button to rescan.
- **Manual name** — free text. Use when the source isn't on the
  network yet: a camera that powers up at showtime, a remote NDI
  bridge that connects on cue, a sender behind a VLAN that joins
  later. The item saves with a name that may not resolve until later,
  and the playlist/cue happily loads either way.

Optional connection hints on the same dialog:

- **Low bandwidth mode** — request the sender's preview stream
  instead of full-quality (NDI 5+).
- **Audio-only** / **Video-only** — ignore the other side even if the
  sender provides it.
- **Reconnect interval** — 2 s / 5 s / off (default 5 s).

### 6.4 PortAudio Input UX

Add via "Add PortAudio input…" from the same menus. Dialog mirrors
the existing PortAudio *output* dialog: host API → device list →
channel count (1…maxInputChannels) → sample rate → suggested latency.
The captured device descriptor is what gets serialized; on load,
devices are matched by name first with an index fallback so moving a
USB interface between ports doesn't silently break the binding.

### 6.5 Playback Semantics

- **No duration** — the transport UI swaps the seek bar for a
  connection-state indicator and an "elapsed since Play" counter.
- **Seek / loop / auto-advance hidden** — none of these apply.
- **End behavior** — Stop is the only natural end. If the underlying
  source disconnects (NDI sender goes away, USB device yanked), the
  player enters a "waiting for source" state and retries per the
  configured interval. Playlist auto-advance does NOT trigger on a
  disconnect (you don't want a flapping NDI source to skip the rest
  of the playlist).
- **Routing** — same selection model as file playback (output
  checkboxes + audio matrix). NDI input video resolution may change
  mid-stream if the sender reconfigures; the output preset (§4.3.5)
  handles size normalisation cleanly.
- **Audio matrix size** — NDI input channels can be 1 / 2 / 4 / 6 /
  8 / 16 depending on sender. The matrix auto-extends to the
  negotiated channel count once connected; saved matrix cells outside
  the negotiated count are preserved-but-inactive (so reconnecting
  the same sender at a higher channel count restores routes).
- **PortAudio input** is audio-only; never lights the video routing
  UI for that item.

### 6.6 Cue Player Semantics

Live inputs make sense as cue items:

- GO connects to the source; the cue's "duration" cell shows ∞ or
  "live."
- Stop disconnects and applies the cue's fade-out at the router-gain
  level (no source-side fade, since there's nothing to seek).
- **Per-cue fade in/out** apply as router-gain ramps.
- **Auto-follow by offset** doesn't apply to a non-finite source. UI
  replaces it with **auto-follow after N seconds** (a wall-clock
  timer from the cue's trigger).
- **Pre-roll** for an NDI input pre-connects the source so GO is the
  moment audio/video starts flowing through the matrix instead of
  the moment NDI begins its connection handshake. Drops GO →
  audible latency from ~1 s to ~50 ms for cued live sources.

### 6.7 Framework Composition

What the framework already supports:

- `PortAudioInput` exists (per `[[project-layout]]`) and wraps the
  device's read callback into an `IAudioSource`.
- `NDIAudioReceiver` exists.

What it doesn't yet:

- `NDIVideoReceiver` is listed in `[[project-layout]]` as future
  work ("**No `NDIVideoReceiver` yet** — flagged as future work").
  Until it lands, NDI input items are audio-only.
- `MediaPlayer.TryOpen` and friends construct from a file path via
  `MediaContainerDecoder`. Live inputs don't have a container —
  they're already-decoded `IAudioSource` / `IVideoSource` pairs.
  Needs a new overload, e.g. `MediaPlayer.TryOpenLive(IAudioSource?
  audio, IVideoSource? video, …)`. The framework already has the
  building blocks (`AudioPlayer.AddOwnedSource`, `VideoPlayer(source,
  sink, clock)`); MediaPlayer just doesn't expose a wiring path that
  skips the decoder.

### 6.8 Data Model

Playlist items today are `string path`. Becomes a discriminated
union:

```jsonc
PlaylistItem
 ├─ FileItem        { kind: "file", path: "..." }
 ├─ NDIInputItem    { kind: "ndi-input", sourceName: "...",
 │                    lowBandwidth: false, audioOnly: false,
 │                    videoOnly: false, retrySec: 5 }
 └─ PortAudioInputItem { kind: "pa-input",
                         device: { hostApi: "...", name: "...",
                                   indexFallback: 3 },
                         channels: 2, sampleRate: 48000 }
```

Same shape on cue list rows. JSON serializer needs polymorphism via
the `kind` discriminator — straightforward with
`JsonPolymorphic`/`JsonDerivedType` attributes on the source-generated
context.

### 6.9 "Waiting for Source" UX

When an NDI source isn't currently resolvable (offline, name typo'd,
network change mid-show):

- Video outputs show the player's hold image with a "WAITING:
  <source-name>" caption overlay (reuses the §4.3.5 hold mechanic).
- Audio outputs go silent.
- Status banner shows the reconnect countdown.
- Stop button works normally; Play retries immediately.

### 6.10 Acceptance Criteria

- "Add NDI input…" dialog lists discovered NDI sources within ~1 s of
  opening and refreshes on rescan.
- Manual-name path saves an item that loads and triggers correctly
  even though the source is currently absent (goes into waiting
  state).
- Playlist with mixed file + live items plays end-to-end; live items
  show connection state instead of duration.
- PortAudio input from one card routes through the audio matrix to
  one or more outputs at correct gain.
- Cue Player fires a live-input cue, hits Stop, fires the next file
  cue with no leaks (NDI receiver instance disposed).
- Reconnect after sender drop succeeds within the configured interval.

### 6.11 Open Questions (Resolved)

- Pre-roll for NDI inputs — pre-connect when the cue list loads, or
  only when approaching standby? Recommend on approach (when the cue
  is within N cues of standby), so opening a 200-cue show doesn't
  fire a storm of NDI connection attempts.
  **Decided:** on approach. Same N as the media-cue pre-roll window
  (§5.7).
- PortAudio device disconnect detection — PortAudio doesn't notify
  cleanly when a USB device disappears mid-stream. Recommend a
  periodic `Pa_IsStreamActive` check (every 1 s) plus the existing
  callback-fault flag pattern (already in `PortAudioOutput`; mirror
  for `PortAudioInput`).
  **Decided:** periodic active-check + callback-fault flag, mirroring
  the existing pattern.
- Should the NDI discovery dialog show sources in groups the user is
  *not* joined to? Recommend yes-with-warning ("source visible but
  not in your groups — joining required to receive"), so missing
  group membership doesn't look like the source is offline.
  **Decided:** show with warning so users understand *why* a visible
  source might still not work.

---

## 7. Project Save / Load

### 7.1 Scope

One `.haplayproj` file captures the full session:

- App version + schema version (integer; bumps on breaking field
  changes so loaders can migrate).
- All output definitions (PortAudio / Local Video / NDI) plus their
  edited settings.
- All clone-of relationships.
- All players: name, tabs, playlist paths per tab, routing selection,
  audio matrix, master gain, hold-image / preset / transition.
- All cue lists.
- Optional UI layout state (window size, split positions, last
  selected tab) — separate `_layout.json` so it doesn't pollute diffs
  when checked into a show's repo.

### 7.2 File Format

JSON with `MediaPlayerConfigJsonContext`-style source-generated
serializers. Top-level shape:

```jsonc
{
  "schemaVersion": 1,
  "haplayVersion": "...",
  "outputs": [...],
  "players": [...],
  "cueLists": [...]
}
```

### 7.3 Save / Load UX

- **File → New Project / Open Project / Save Project / Save As…**
- Default location: `~/Documents/HaPlay Projects/`.
- Recent-projects list.
- On Open: outputs registered first (so player routing IDs resolve);
  then players; then cue lists. Missing devices (e.g. PortAudio
  device index no longer valid) surface as a banner and that output
  goes into a "unavailable" state — the line stays in the list so the
  user can re-bind it.

### 7.4 Acceptance Criteria

- Save then re-Open with the app restarted produces identical UI state
  (verified by hashing the round-tripped JSON minus volatile fields).
- Loading a project authored on a different machine with different
  audio devices presents a "rebind outputs" dialog instead of silently
  losing routes.
- Schema version mismatch shows an informative error rather than
  loading corrupt state.

---

## 8. Enhancement Ideas (Other Views, Cross-Cutting)

Captured here so they don't get lost. None are blockers for the
refactor above.

### 8.1 Output Health Panel (extension of §3)

The framework already publishes `AudioRouterAggregatePumpStats`,
`VideoSinkPumpMetrics`, PortAudio underrun/dropped counters, and NDI
sender pressure events. A small panel on the Outputs view shows
per-line health LEDs (green / yellow / red based on drop rate) and a
sparkline of the last 60s. Helps the operator distinguish "the file is
bad" from "the network is bad" without attaching a debugger.

### 8.2 Per-Player Headphones Cue Bus

A separate PortAudio output line (the operator's headphones) that
receives a configurable subset of any player's audio, pre-fade or
post-fade. Standard live-sound workflow.

### 8.3 Multi-Player Workspace Layout

- Split view (two players side by side).
- Stacked (vertical list of compact players for many simultaneous
  cues).
- Operator picks per-workspace; saved with the project.

### 8.4 OSC / MIDI Remote Control

`OSCLib` and `PMLib` already exist. Add a bindings view: "this OSC
address fires this command" / "this MIDI CC sets this gain." Useful
for stream-deck-style external controllers. Mentioned as out of scope
for this refactor; tracked here for the next.

### 8.5 Drag-and-Drop File Import

Drop files onto the playlist tab to append, onto a cue list to insert
at cursor, onto an output to play immediately ("quick play" flow).

### 8.6 Theme & Density

Light / dark theme toggle. Compact / comfortable density toggle for
operators running on a small laptop screen vs. a control-room monitor.

### 8.7 Window State Persistence

Remember each preview window's last position / size / monitor.
Independent of the project file (per-machine, in app settings).

### 8.8 Recording Sink

`Doc/MediaFramework-Findings-2026-05-20.md` already lists this as a
P3 framework idea (FFmpeg-backed output sink that records what's on
program). UI side: a Record button on each NDI output line.

### 8.9 NDI Video Receiver Backfill

Live inputs as media items (§6) ship audio-only on day one because
`NDIVideoReceiver` is still listed in `[[project-layout]]` as future
work. Once it lands, the same NDI input items support video
automatically — no UI churn, the dialog gains the "video-only" and
"both" radio options that today only show for "audio-only" mode.

### 8.10 Compositor-Based Hold Image (replaces §4.3.5 hold)

When the "Output preset" is set to a fixed resolution, the hold image
becomes a layer in `CpuVideoCompositor` underneath the active video
layer. Transitions become `LayerOpacityTween` ramps. Cleaner than the
current `LogoFallbackVideoSink` template-frame approach because the
sink stays at one negotiated format always.

### 8.11 Output Activity Indicators

Tiny LED next to each output in the routing panel that flashes when
the output is actively receiving frames/samples (driven by pump
metrics). Lets the operator confirm "audio is flowing" at a glance.

---

## 9. Cross-Cutting Concerns

### 9.1 Output Line Identity

Outputs need stable IDs across project save/load so routing
references survive. Today `OutputLineViewModel.Definition.Id` is a
`Guid` — keep that as the persistent key. Reassigning the device
(e.g. new PortAudio device index) preserves the GUID; only the
underlying device binding changes.

### 9.2 Routing Mirror for Clones

Wherever a player VM iterates outputs to display checkboxes, the
clone semantic ("child output, can't be unticked independently of
parent") needs explicit UX. Cleanest: clones don't appear as
top-level checkboxes; they show as a nested indented line under the
parent that follows the parent's check state.

### 9.3 Audio Matrix Bridge to Framework

As noted in §4.3.4, the simplest implementation is "one route per
non-zero connection row (`input`, `vout`)". This may produce up to 64
routes per player at 8 in × 8 out. `AudioRouter.AddRoute` is O(n) over
the routes list internally (immutable rebuild). 64 routes is fine at
session-load time; per-frame mix cost in `RunLoop` is also linear in
route count. Worth measuring at 8×8 before assuming it scales further.
Tracked as a follow-up.

To avoid route drift and make save/load robust, route IDs should be
derived deterministically from `(playerId, inputChannel, voutIndex)` for
player routing and `(cueId, inputChannel, voutIndex)` for cue overrides.

### 9.4 Project File Migration

`schemaVersion` lets us add a `Migrate(JsonNode, int from, int to)`
pass on load. Don't over-engineer this now — bump on each breaking
change, branch in `Migrate` per version. Real-world precedent: most
small projects need 2–3 migration steps over their lifetime.

### 9.5 Threading Discipline

The MVP rule from `[[playback-teardown-timing]]` stays: never chain
Pause/Stop/Seek inline. Every transport command goes through
`WithPlaybackArcAsync` with bounded inner CTs. The redesign must not
regress this — in particular the new transport bar's seek-on-drag-end
behavior must use `RunBoundedCancelableAsync`.

### 9.6 Framework Gaps

Things the refactor needs from the framework. Scope these into the
existing `MediaFramework-Checklist-2026-05-20.md`:

- [ ] **Hot route addition to a running playback session.** Today
      `HaPlayPlaybackSession.TryCreate` builds the full output set
      up-front; there's no `AddOutputDuringPlayback(line)` method.
      Needed for §4.3.3 inline `+` button.
- [ ] **Output runtime reconfigure-in-place** for PortAudio /
      Avalonia / NDI. Today the runtime is set up once at Add and
      Disposed at Remove; needs `ReconfigureAsync(newDefinition)`.
- [ ] **NDI pixel-format / resolution lock** as an
      `NDIOutputDefinition` setting that propagates through to the
      router's branch-format pick.
- [ ] **Per-cell channel-mix matrix** (optional, §4.3.4) — only if
      the multi-route approach turns out to be a perf cliff.
- [ ] **`IVideoCompositor` as default for "output preset"** —
      threading the compositor through the existing
      `VideoRouter` fan-out path. Likely needs the player to own a
      `CompositorVideoSink` between the router input and the actual
      negotiation lead.
- [ ] **`MediaPlayer.TryOpenLive(IAudioSource?, IVideoSource?, …)`
      overload** — bypasses `MediaContainerDecoder` for live inputs
      (§6.7). Building blocks already exist; needs a new wiring path
      in `MediaPlayer.TryOpenCore`.
- [ ] **`NDIVideoReceiver`** — unblocks full NDI input (§6, §8.9).
      Currently `NDIAudioReceiver` exists; video side is the gap.
- [ ] **`PortAudioInput` disconnect detection** — periodic
      `Pa_IsStreamActive` + callback-fault flag, mirroring the
      `PortAudioOutput` pattern. Needed for the "waiting for source"
      state on live PortAudio cues (§6.5, §6.11).

### 9.7 Testing Strategy

- New view-model tests for: tab add/rename/save/load, matrix
  add/remove route, cue-list GO/standby/panic, project save/load
  round-trip.
- Smoke test for "edit output while a player is using it" — verify no
  exceptions, brief silence acceptable.
- Cue Player has the most interesting timing logic — needs
  injectable time provider for auto-follow assertions (same pattern
  flagged by the existing `[[diagnostics-logging]]` infrastructure
  for clocks).

---

## 10. Suggested Phasing

Not promises — sequencing notes so we don't try to land everything in
one branch.

### Phase A — Foundations (no user-visible change)

1. Output runtime `ReconfigureAsync` support (§9.6).
2. Hot route addition API (§9.6).
3. Project file schema + serializers + Save/Load command plumbing
   (no UI yet — verifiable via tests).

### Phase B — Outputs Editable + Project Save/Load Visible

1. Edit dialogs reuse Add forms (§3.2).
2. Clone-of relationships (§3.4) with the simplest UX (nested rows).
3. File → Save / Open Project commands (§7.3).
4. **App-shell rework (§12.1)** lands here too — once Project / Outputs
   live in their own sidebar entries it's easier to add the Cues entry
   later. Sidebar plus the dialog convention pass (§12.2, §12.3) is the
   single visual baseline everything after this builds on.

### Phase C — MediaPlayer Redesign

1. New transport bar + master volume.
2. Playlist tab strip + per-tab save/load.
3. Inline `+ Add output…`.
4. TreeDataGrid-only routing editor (matrix + per-connection list).
5. Output preset + transition picker (replaces hold-image expander).

### Phase C.5 — Live Inputs (shared by Players and Cue Player)

1. `MediaPlayer.TryOpenLive` overload (framework gap §9.6).
2. `PortAudioInput` disconnect detection (framework gap §9.6).
3. PortAudio-input item: dialog, data model, playback.
4. NDI-input item (audio-only first, gated on `NDIAudioReceiver`):
   discovery dialog, manual-name path, data model, "waiting for
   source" UI.
5. NDI-input video support — backfill once `NDIVideoReceiver` lands
   (§8.9).

### Phase D — Cue Player

1. Cue list data model (media / action / comment kinds) + serialization.
2. TreeDataGrid cue list with grouping + row editing. *(Landed 2026-05-21)*
3. GO / Standby / Pause / Stop / Panic transport. *(Landed 2026-05-21 as
   cue-stack transport state; media/action firing still pending in step 8.)*
4. Per-cue virtual-output route overrides + per-connection gain/mute +
   fade + pre-wait. *(Partially landed 2026-05-21: `VOut` registry +
   route-connection row editing + pre-wait delay scheduling in cue transport +
   media-cue route overrides pushed into MediaPlayer matrix on cue fire.
   Fade path still open.)*
5. Pre-roll cache.
6. Auto-follow / auto-continue scheduling (offset for media, timer for
   live/action).
7. Group-level overrides + defaults; "fire all simultaneously" group
   mode.
8. Action-cue emitters: OSC out via `OSCLib`, MIDI out via `PMLib`,
   multi-target endpoint registry (§12.6) and the Target
   Configuration dialog (§12.2). Endpoints referenced by GUID from
   cues; rebind-missing-endpoints flow parallels rebind-missing-
   outputs. *(Partially landed 2026-05-21: OSC + MIDI first-send paths
   wired via `OSCLib` / `PMLib`; project-level endpoint registry
   persisted and referenced by cue `EndpointId`; OSC/MIDI sidebar workspaces
   host endpoint management + MIDI catalog refresh; the Cue workspace uses
   `ActionCueBuilderDialog` (**Edit action…**) instead of an inline builder
   panel. Target-config/rebind UX depth and endpoint-health surface remain
   open.)*

### Phase E — Polish & Follow-ups (§8)

Pick from §8 based on user feedback after D is in real use.

---

## 11. Decisions Needed Before Implementation Starts

Listed here so the planning phase resolves them rather than the
implementation phase rediscovering them:

1. **Clone-of plumbing**: §3.4 Option A vs. B. Recommendation: A.
2. **Audio matrix backing**: multi-route vs. weighted ChannelMap.
   Recommendation: multi-route first, weighted matrix only if perf
   demands.
3. **Cue Player view location**: top-level workspace tab vs. nested
   inside a player. Recommendation: top-level.
4. **Per-tab routing**: per-player vs. per-tab. Recommendation:
   per-player with future per-tab override.
5. **Pre-roll cap**: how many cues to pre-open. Recommendation: 4,
   configurable.
6. **Hot reconfigure semantics**: silent-glitch hot vs. require-Stop.
   Recommendation: silent-glitch hot, with a confirm prompt when an
   active session is using the line.
7. **Live-input rollout**: ship NDI-input audio-only when `NDIVideoReceiver`
   isn't ready, then add video later — or wait for full feature parity?
   Recommendation: ship incremental. Audio-only NDI input is useful on
   its own (remote mic, IFB return) and an upgrade-in-place that adds
   video later is invisible to existing playlists.
8. **Routing UI surface**: maintain two editors (matrix vs list) or one
   unified TreeDataGrid-first editor shared by MediaPlayer and Cue
   Player? Recommendation: one shared editor (matrix + connection list)
   with virtual output channel numbering.

---

## 12. App Shell, Dialogs & Terminology Cleanup

Pulled into its own top-level section because the dialogs and the
shell touch every view above; doing them piecemeal per-view leads to
inconsistent UX.

### 12.1 App Shell — Collapsible Hamburger Sidebar

A persistent left sidebar replaces the current tab-based main shell:

- Collapsed: icon-only column ~48 px wide. Icons for **Players**,
  **Cues**, **Outputs**, **OSC**, **MIDI**, **Project**. Tooltip on hover
  gives the label.
- Expanded: full label column ~180 px wide. Toggled by a hamburger
  button at the top of the sidebar (or by keyboard `Ctrl+B`).
- Sidebar state (collapsed / expanded) is per-machine (in app
  settings), not part of the project file.
- The current "Outputs / Players / etc. tabs along the top" goes
  away. The main content area is whichever sidebar item is selected.

### 12.2 Dialog Redesign Pass

Every existing dialog gets reviewed in the same pass so they share
visual language and field ordering. Inventory of dialogs to touch:

- **Add PortAudio Output** — already exists; reuses the Edit form
  per §3.2.
- **Add Local Video Output** — same: now also Edit, plus the
  clone-of dropdown per §3.4.
- **Add NDI Output** — same: Edit, plus pixel-format / resolution
  lock per §3.3.
- **Add NDI Input** (new, §6.3) — live-discovery list + manual-name
  field + connection hints.
- **Add PortAudio Input** (new, §6.4) — host API / device /
  channels / rate.
- **Add Cue → File / NDI input / PortAudio input / Action (OSC /
  MIDI)** (new, §5.2) — picker that branches into the right
  per-kind sub-form.
- **Edit Audio Matrix** (new, §4.3.4) — full N×M grid view.
- **Project → New / Open / Save As** (new, §7) — standard file
  dialogs but with recent-projects list and "rebind missing
  outputs" sub-dialog.
- **Save Player / Save Tab / Save Cue List** (new, §4.3.2, §5.3) —
  small confirm dialogs with destination picker.
- **OSC/MIDI Target Configuration** (new, §5.6) — project-wide
  endpoint registry (which OSC IP:port, which MIDI output port) so
  action cues can reference them by name.

Shared visual guidelines:

- Title bar describes the action (`Add NDI Input` not `NDI Input
  Dialog`).
- Primary action button is right-aligned, bottom of the dialog
  (`Add` / `Save` / `Open`), with `Cancel` to its left.
- All dialogs are resizable and remember their last size per dialog
  type (separate from the project file).
- Validation errors surface inline (red text under the field), not
  modal alerts.

### 12.3 Terminology Cleanup

A few names today describe the implementation, not the user-visible
behavior. Renaming candidates the planning round should pick from
before the implementation phase starts:

- **`VideoOutputEngine.AvaloniaOpenGl` vs. `.SdlOpenGl`** — both are
  GL surfaces, but Avalonia paints on the UI thread (so a heavy
  paint stalls the app) and SDL3 runs on its own thread. The
  user-visible distinction is what should drive the label, not the
  toolkit name. Candidate names:
  - `In-App Preview` (Avalonia, in the app shell) vs. `Standalone
    Window` (SDL3, dedicated thread, can fullscreen on any monitor
    without UI overhead).
  - `Avalonia (UI thread)` vs. `SDL3 (display thread)` — keeps the
    backend in the label for power users, surfaces the threading
    impact.
  - Recommend the first pair; show the technical name as a tooltip
    or smaller subtitle.
- **`StreamMode = VideoAndAudio / VideoOnly / AudioOnly`** (NDI) —
  fine, but the dialog should explain the practical impact ("video
  side will not appear / audio side will not be heard") not just
  show the enum value.
- **`HoldFallbackVideo`** — current name reads like a property
  toggle. After §4.3.5 / §8.10 this becomes the "Idle image" with
  a "Show when no media playing" checkbox; rename the model field
  to match.
- **`OutputLineViewModel.KindLabel`** — internal label that leaks
  into status messages ("Auto-routed to NDI — HaPlay Output."). New
  labels: `Local audio (PortAudio)`, `Preview window`, `Standalone
  window`, `NDI program`. Driven by the rename pass above.
- **`MediaPlayerConfig`** — the saved-state type. After §4.5 splits
  this into per-tab `PlaylistConfig` and per-player
  `PlayerConfig`, the model names should match the UI verbs (`Save
  Tab` writes a `PlaylistConfig`, `Save Player` writes a
  `PlayerConfig`).
- **`OutputManagementViewModel`** — keep internally but the user-
  facing label is just "Outputs" (sidebar entry name).
- **`HaPlayPlaybackSession`** — internal class name fine; if it
  surfaces in error messages, swap to "Playback session."

### 12.4 Acceptance Criteria

- Sidebar collapse/expand persists across app restarts (per-machine
  setting).
- All dialogs share the title/button/validation conventions in §12.2.
- No user-facing string references "Avalonia" or "SDL3" unless the
  user opts into a "show technical names" preference.
- Editing an output uses the *same* dialog as adding it (single
  source of truth for field validation).

### 12.5 Open Questions (Resolved)

- Should the sidebar support keyboard shortcuts to jump to each
  view (`Ctrl+1` Players, `Ctrl+2` Cues, etc.)? Recommend yes.
  **Decided:** yes — `Ctrl+1` … `Ctrl+N` cycle through sidebar
  entries in display order. Shortcuts work whether the sidebar is
  collapsed or expanded.
- Localization scope — English-only for now, but make all visible
  strings go through a single `Strings.resx`-style file so future
  translation isn't a code change in 30 places.
  **Decided:** English-only ship target, but every user-visible
  string lives in a single resx-style resource file from day one so
  future translation is a content change, not a code-grep across
  30 view-models.
- Should the OSC/MIDI Target Configuration dialog live under
  Project, or as its own sidebar entry? Recommend nested under
  Project for now; promote if it grows.
  **Decided (revised 2026-05-21):** promote to **two sidebar workspaces** —
  **OSC** for the UDP endpoint registry (`OscConnectionsView`) and **MIDI** for
  output endpoints plus the PortMidi device catalog (`MidiDevicesView`). The
  Cue workspace stays focused on the cue tree, VOut registry, and per-cue route
  overrides; **Project** is file metadata + recent projects only. Keyboard
  shortcuts: `Ctrl+1` Players, `Ctrl+2` Cues, `Ctrl+3` Outputs, `Ctrl+4` OSC,
  `Ctrl+5` MIDI, `Ctrl+6` Project. A unified "Target Configuration" dialog (§12.2)
  remains optional; per-kind editing happens in the sidebar workspaces today.

### 12.6 Multi-Target OSC / MIDI Endpoint Registry

The Target Configuration dialog manages a *list* of endpoints, not a
single one:

- **OSC endpoints** — display name, host, port, transport (UDP /
  TCP if/when supported). A project can hold many ("Lighting Desk,"
  "Video Switcher," "Backup Lighting Console"). Add / Remove / Edit
  in the dialog; each entry has a stable GUID.
- **MIDI devices** — display name, port (output port enumerated via
  `PMLib`). Many concurrent output ports; the same registry also
  holds *input* ports for the future remote-control work in §8.4 so
  OSC/MIDI in and out share one configuration surface.
- **Action cue target reference** — the **Edit Action Cue** dialog
  (`ActionCueBuilderDialog`, §5.2) shows a dropdown of endpoint *names* from
  the registry and composes OSC/MIDI command text into the selected cue. The cue
  stores the endpoint's GUID, not the inline IP/port. Renaming an endpoint
  propagates to every cue automatically; moving an endpoint to a new IP doesn't
  require touching cues at all.
- **Broken-reference state** — deleting an endpoint marks every
  dependent action cue as "broken" with a banner offering to
  re-bind. Loading a project on a machine where a MIDI port name
  differs surfaces the same dialog, parallel to the "rebind missing
  outputs" flow (§7.3).
- **Live status indicator** — each endpoint shows open / closed /
  error so the operator can confirm the lighting desk is reachable
  before show GO.

Project-file impact: the endpoint registry is part of `.haplayproj`
(§7), keyed by GUID. The registry is small (typically <10 entries
even for big shows) so it lives inline in the same JSON, no
separate file.

### 12.7 String Resource Plumbing

To keep the "future-translatable" promise from §12.5 honest without
introducing localization machinery now:

- One `Strings.resx` (and one `.Designer.cs` accessor) per project
  (`UI/HaPlay/Resources/Strings.resx`).
- All XAML uses `{x:Static r:Strings.SomeKey}` instead of inline
  text.
- All ViewModel-emitted strings (status banners, error text) use
  `Strings.SomeKey`.
- Adding a new string is "add to resx + use it"; no per-view
  duplication.
- No `.fr.resx` / `.de.resx` siblings yet — when a translator
  arrives, they drop a sibling file and the existing satellite-
  assembly machinery in .NET picks it up with zero code change.

---

## Appendix — Existing Memories Referenced

- `[[project-layout]]` — 25-project layout still current after
  Phase 6.
- `[[playback-teardown-timing]]` — bounded-CT pattern for Pause/Stop/
  Seek; redesign must preserve.
- `[[playback-master-clock-wiring]]` — PortAudio wrapping concern
  surfaces in the audio matrix (multiple sinks, only one paces the
  router).
- `[[video-sink-pump-reconfigure]]` — idempotent `Configure` fix
  unblocks multi-output topologies the redesign relies on.
- `[[video-priming-routes-via-router]]` — the priming-via-router
  pattern lets us replace hold-image with the compositor without
  losing NDI warmup.
- `[[diagnostics-logging]]` — every new VM gets a static
  `MediaDiagnostics.CreateLogger(...)` so traces stay coherent.
