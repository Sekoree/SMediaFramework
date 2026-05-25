# HaPlay Media Player UX Improvements

Status: proposal  
Scope: `MediaPlayerView` / `MediaPlayerViewModel` refactoring  
Date: 2025-05-25

---

## Current State

`MediaPlayerView.axaml` (502 lines) renders **eight distinct sections** in a single
vertically-scrolling `StackPanel`. The backing `MediaPlayerViewModel.cs` is 3 324 lines
with 30 `[ObservableProperty]` fields and 14 `[RelayCommand]` methods, covering
transport, playlists, routing, audio matrix, output presets, transitions, headphones cue
bus, and idle-image configuration all in one class.

In the Stacked or Split player layouts every one of these sections is multiplied per
player, which makes the view extremely tall and hard to scan.

### What is on the main screen today

| Section | Lines | Always visible? | Frequency of use |
|---|---|---|---|
| Status banner | 53-58 | conditional | rare |
| Now-playing + transport | 63-177 | yes | constant |
| Playlist card | 180-248 | yes | frequent |
| Routing expander | 250-339 | collapsed | setup / occasional |
| Audio matrix expander | 344-434 | collapsed | setup / rare |
| Output preset + transition | 437-471 | yes (always open) | occasional |
| Idle image expander | 474-498 | collapsed | setup / rare |

The "Output preset + transition" card is always visible even though most users set it once
and forget, wasting vertical space in every layout mode.

---

## Proposal: What Stays on the Main Screen

The main screen should contain **only the controls a user touches during a live show or
rehearsal** -- media transport, volume, and playlists:

1. **Status banner** (keep, conditional)
2. **Now-playing header** (player name, source badge, waiting-for-source banner)
3. **Scrubber + time readout**
4. **Transport buttons** (prev / play-pause / next / stop)
5. **Volume** (mute toggle + master gain slider)
6. **Loop + auto-advance toggles**
7. **Playlist card** (tabs, items, add/remove/reorder)
8. **Routing quick-view** -- a read-only summary line ("2 outputs, headphones cue on")
   with a single button to open the full routing dialog.

Everything else moves to dialogs.

---

## Proposed Configuration Dialogs

### 1. Player Settings Dialog (new)

A single tabbed dialog opened via the existing gear/settings button in the player header.
Replaces the current flyout menu (save/load/unload/remove stay in the flyout; the dialog
covers everything else).

**Tab: Output & Transitions**

Currently the "Output preset + transition controls" card (lines 437-471).

| Control | Current location | Notes |
|---|---|---|
| Output preset selector | always-visible card | set-and-forget |
| Transition mode (Cut / Fade / IdleImage) | always-visible card | changed per-show at most |
| Transition duration (ms) | always-visible card | |
| Custom output width / height | conditionally visible | only when preset = Custom |
| Live UYVY passthrough toggle | always-visible card | advanced, rarely toggled |

Moving these into a dialog tab frees ~40px of vertical space per player on the main view.

**Tab: Idle / Fallback Image**

Currently the "Idle image expander" (lines 474-498).

| Control | Current location |
|---|---|
| Fallback image path + browse | expander |
| "Show when no media playing" toggle | expander |

This is pure setup config with no reason to live on the main screen.

**Tab: Headphones Cue Bus**

Currently embedded inside the routing expander (lines 298-332).

| Control | Current location |
|---|---|
| Enable toggle | routing expander |
| Target output selector | routing expander |
| Tap point (Pre-fader / Post-fader) | routing expander |
| Gain slider + dB readout | routing expander |

The cue bus is configured once per show and rarely touched again. It clutters the routing
section, which should focus on "which outputs are connected."

### 2. Audio Matrix Dialog (new)

Currently the entire audio matrix expander (lines 344-434). This section is dense, data-grid
heavy, and only useful during initial routing setup or advanced mixing scenarios.

Contents:

| Control | Notes |
|---|---|
| Per-output mix-mode preset selector | ComboBox per output |
| Per-output mute + gain slider | per output row |
| Input trims (mute + gain per input channel) | separate border, conditional |
| Per-cell gain matrix (TreeDataGrid) | dynamic columns based on source |
| Active routes summary (TreeDataGrid) | read-only diagnostic |

This is the most complex sub-view on the player screen. It deserves its own resizable dialog
window (like the existing `TargetConfigurationDialog` pattern) so users can expand it to
full-screen width when working with many-channel sources.

**Bonus**: as a separate window it could stay open side-by-side with the player, enabling
real-time gain tweaking while monitoring playback -- something the current collapsed
expander makes awkward.

### 3. Routing Dialog (new, or expand existing inline)

The current routing expander (lines 250-339 minus the headphones cue bus section) could
remain inline as a simplified quick-view, or move to a dialog for consistency. Two options:

**Option A -- Inline quick-view + dialog for changes**  
The main screen shows a compact summary: output count, connection status badges, and an
"Edit Routing" button that opens a dialog. Routing changes (add output, remove, reorder)
happen in the dialog; the inline view is read-only.

**Option B -- Keep the current expander but lighter**  
Remove the headphones cue bus (moved to Player Settings) and the "Add Output" dropdown
(moved to routing dialog or left inline). The expander becomes just a checkbox list.

Recommendation: **Option A** for consistency and to simplify the main view further,
especially in Split layout where horizontal space is tight.

---

## Additional UX Improvements

### 4. Playlist Toolbar Cleanup

The playlist card toolbar (line 205) has **eight buttons** in a single row:

    [Move Up] [Move Down] [Remove] [Save Tab] [Load Tab] [Add Files] [Add Live Input]

plus the tab strip with [+] and [-] buttons above it.

Recommendations:

- **Group by frequency**: "Add Files" and "Add Live Input" are the most common actions and
  should be the most prominent (leftmost or largest). Move Up/Down and Remove are
  secondary.
- **Collapse save/load into a menu**: Replace the two [Save Tab] / [Load Tab] buttons with
  a single menu dropdown, matching the pattern used in CuePlayerView (Files > Save / Load).
- **Context menu on items**: Right-click on a playlist item should offer Remove, Move
  Up/Down, and "Reveal in file manager" for file items. This lets power users skip the
  toolbar entirely.
- **Drag-and-drop reorder**: Replace Move Up/Down buttons with drag handles on playlist
  items. The buttons can remain as keyboard-accessible alternatives but should be
  de-emphasized or moved to a context menu.
- **Drop zone**: The playlist ListBox should accept file drops (drag from file manager to
  add). If not already implemented, this is a high-value UX improvement.

### 5. Player Header Enhancements

- **Playback state indicator**: Add a small colored dot or icon to the player header
  (green = playing, yellow = paused, grey = stopped). In Tabs layout this helps users see
  at a glance which player is active. In Stacked/Split layouts it helps when scanning many
  players.
- **Player rename inline**: Allow double-click on the player name in the header to rename
  it in-place (like playlist tab names already support). Currently renaming requires
  loading a saved config.
- **Transport keyboard shortcuts in header tooltip**: Show the keyboard shortcuts
  (Space, [ ], < >) in the player header tooltip to aid discoverability.

### 6. Split / Stacked Layout Density

In Split layout, each player gets `1/N` of the window width. With the current full-fat
`MediaPlayerView`, two players side-by-side overflow horizontally.

Recommendations:

- **Compact mode**: When the player width drops below a threshold (~500px), switch to a
  compact layout: single-line transport (play/pause + volume only), collapsed playlist
  (show item count, expand on click), hide all expanders.
- **Transport mini-mode**: For the Split layout, offer a "mini" transport row that merges
  play/pause, volume, and progress into a single dense bar (similar to Spotify's bottom
  bar).
- **Detach to window**: Allow any player to be "popped out" into its own window. Useful for
  multi-monitor setups (player on monitor 1, cue list on monitor 2).

### 7. Volume and Metering

- **Visual volume meter**: Add a small peak/RMS meter next to the master volume slider.
  The `SparklineControl` already exists in the codebase -- reuse it or add a simple
  bar meter. Audio-only confirmation that the player is actually outputting signal is
  critical in live scenarios.
- **Volume value click-to-edit**: Allow clicking the dB readout text to type an exact value
  (e.g., "0 dB" or "-12 dB") instead of scrubbing the slider.
- **Double-click to reset**: Double-click the volume slider to reset to 0 dB (unity gain).

### 8. Scrubber Improvements

- **Waveform overlay**: For file-based media, render a waveform on the scrubber slider
  track. This is standard in DAWs and helps users identify silent sections and song
  structure. Even a low-resolution overview waveform is valuable.
- **Hover preview**: Show a time tooltip when hovering over the scrubber before clicking.
- **Remaining time toggle**: Click the remaining-time display to toggle between remaining
  and elapsed. Some users prefer one over the other.

### 9. Unified Settings Access

The current UI has configuration scattered across:
- Player header flyout menu (save/load/unload/remove)
- Routing expander (outputs + headphones cue)
- Audio matrix expander (mix modes + gain)
- Output preset card (format + transitions)
- Idle image expander (fallback image)
- Main menu > View > Target Configuration (OSC + MIDI)
- Project workspace (theme + density)

After the proposed refactoring, the hierarchy simplifies to:

```
Main screen
  +-- Transport + Volume + Playlist     (always visible)
  +-- Routing quick-view (read-only)    (always visible, compact)
  +-- [Player Settings] button          --> Player Settings Dialog
  |     Tab: Output & Transitions
  |     Tab: Idle / Fallback Image
  |     Tab: Headphones Cue Bus
  +-- [Audio Matrix] button             --> Audio Matrix Dialog (resizable window)
  +-- [Edit Routing] button             --> Routing Dialog (add/remove/reorder outputs)

Menu bar
  +-- View > Target Configuration       --> OSC + MIDI dialog (unchanged)

Sidebar > Project
  +-- Theme + Density                   (unchanged)
```

### 10. Keyboard Shortcut Discoverability

The code-behind mentions keyboard transport (`Space`, `[`, `]`, `,`, `.`) but these are
not surfaced in the UI. Add a small "Keyboard shortcuts" link or `?` button that shows
a tooltip or flyout with the available shortcuts. Alternatively, add InputGesture hints
to the transport button tooltips (like the menu items already do for Ctrl+S etc.).

---

## Summary: Effort vs. Impact Matrix

| Change | Effort | UX Impact | Priority |
|---|---|---|---|
| Player Settings Dialog (output/transition + idle + cue bus) | Medium | High | P1 |
| Audio Matrix Dialog (standalone window) | Medium | High | P1 |
| Routing quick-view + dialog | Medium | Medium | P2 |
| Playlist toolbar cleanup (menu, context menu) | Low | Medium | P2 |
| Playback state indicator in header | Low | Medium | P2 |
| Player rename inline | Low | Low | P3 |
| Split layout compact mode | High | High | P2 |
| Visual volume meter | Medium | High | P2 |
| Scrubber waveform overlay | High | Medium | P3 |
| Keyboard shortcut discoverability | Low | Low | P3 |
| Drag-and-drop playlist reorder | Medium | Medium | P3 |
| Player detach to window | High | Medium | P3 |

---

## ViewModel Impact

The `MediaPlayerViewModel` (3 324 lines, 30 properties, 14 commands) would benefit from
extracting sub-ViewModels for each dialog:

- `PlayerOutputSettingsViewModel` -- output preset, transition mode/duration, custom
  dimensions, UYVY passthrough
- `PlayerIdleImageViewModel` -- fallback image path, hold toggle
- `PlayerHeadphonesCueViewModel` -- enable, target, tap point, gain
- `PlayerAudioMatrixViewModel` -- already partially exists as `AudioMatrixViewModel`;
  promote to own dialog VM

This follows the same pattern used in the Cue player refactoring where
`CueListSettingsDialogViewModel` was extracted from `CuePlayerViewModel`.

Estimated property migration:
- ~8 properties move to `PlayerOutputSettingsViewModel`
- ~2 properties move to `PlayerIdleImageViewModel`
- ~4 properties move to `PlayerHeadphonesCueViewModel`
- ~6 properties stay matrix-related (already partially separated)

The main `MediaPlayerViewModel` would drop to ~16 core properties (transport, volume,
playlist, routing summary), making it much more focused.
