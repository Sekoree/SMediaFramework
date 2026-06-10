# HaPlay UI rewrite plan — 2026-06-10

Full visual/structural rewrite of the HaPlay shell and its four main surfaces. Goals, verbatim from
the brief:

- One design language across the app (today sections look unrelated).
- **Players**: one display style (drop Tabs/Stacked/Split chooser); no layout shift when
  notifications appear; touch-sized transport, advanced features may stay small; replace the
  virtual-audio-channel ("VOut N") model with a true routing **matrix with pre-defined
  layouts/gains** (Stereo default, sane behavior when a 5.1 source arrives); make **Hold image** a
  prominent transport-level toggle; remove the old force-format debug options.
- **Cue player**: keep the general layout; smaller property-tab headers; fix the stale drawer when
  switching cues; **Now Playing** gains group-level progress + optional seek (with lock); audio
  routing presets and output alias names.
- **Control**: de-clutter the single mega-page (tabs).
- **I/O**: merge Outputs + MIDI (+ OSC endpoints) into one section; add output debug stats; remove
  the video preview window option.

Framework prerequisites land first — see `MediaFramework-HotPath-API-Review-2026-06-10.md`
(R1/R2 route-add hardening, A1 attach helpers, A2 `ApplyMatrix` + downmix presets). The matrix and
Now-Playing work depend on A2 and existing pump-stats APIs only; nothing here needs new engine
features beyond that doc.

---

## 1. Design language ("Deck" system)

One token sheet in `App.axaml` (`Styles/Tokens.axaml`), consumed by every view. Kill all per-view
`<UserControl.Styles>` color/size constants (MediaPlayerView alone defines 20+ today).

**Tokens**

- Spacing scale: 4 / 8 / 12 / 16 / 24 (`Thickness` resources `SpaceXS…SpaceXL`).
- Type ramp: `TitleText` 22 SemiBold · `SectionText` 14 SemiBold · `BodyText` 13 ·
  `CaptionText` 11 @ 65 % · `MonoTime` (Consolas 13) — replaces ad-hoc `section-title`, `time`,
  `time-label` styles.
- Surfaces: `Card` (radius 6, 1 px `BorderSubtle`, `SurfaceRaised` bg, padding `SpaceM`) and
  `Strip` (flat divider bar) — the only two container styles allowed.
- Accent roles: `AccentGo` (primary action: GO, Play), `AccentWarn`, `AccentDanger`, `ChipNeutral`.
  Status pills/chips/badges all derive from these four.
- **Touch tiers** (the core of the brief):
  - `Btn.Touch` — min 56×56, 20 px glyphs: transport + GO + Hold + panic actions only.
  - `Btn.Std` — min 36 px height: normal buttons.
  - `Btn.Dense` — 28 px: advanced/inspector rows, never for transport.
  - The existing Density setting (Project page) keeps scaling `Std`/`Dense`; `Touch` never shrinks.

**Notifications without layout shift (app-wide rule)**

Today `StatusMessage`/load-error banners are `DockPanel.Dock="Top"` elements that appear/disappear
and push the transport down mid-click. Replace everywhere with:

- A **toast overlay** host in `MainView` (top-right, max 3 stacked, auto-dismiss 6 s, click to pin):
  transient errors, "output unavailable", load failures. Overlay = zero layout impact.
- A **fixed-height status line** (one `CaptionText` row, always present, empty when idle) inside
  each player deck / the cue header for persistent state ("playing what", open warnings). Reserved
  height, so content never moves.
- Rule: *no `IsVisible`-toggled element above interactive controls anywhere in the app.* Sticky
  errors get the status line + a warning chip, details on click (flyout), not an inline banner.

**Shell**

Sidebar shrinks from six entries to four workspaces + Project under a gear at the bottom:

```
▶ Play        (Ctrl+1)   players
≡ Cues        (Ctrl+2)   cue player
⌘ Control     (Ctrl+3)   MIDI/OSC control surfaces & scripting
⇄ I/O         (Ctrl+4)   outputs + MIDI devices + OSC connections + health
⚙ Project     (Ctrl+5)   project, theme/density, app settings
```

`WorkspaceItem.MidiDevices` and `Outputs` fold into I/O (§5). Keyboard map updates accordingly
(View menu stays the discoverability surface).

---

## 2. Players workspace

### 2.1 One layout, responsive

Delete `PlayersLayout` (Tabs/Stacked/Split) + `TabsLayoutContent`/`StackedLayoutContent`/
`SplitLayoutContent` projections from `MainViewModel`. Replace with a single **deck grid**:

- Players render as cards in a wrap/uniform grid: 1 player = full area; 2 = side-by-side;
  3–4 = 2×2; ≥5 = scrolling grid rows (cap deck min-width ~420 px).
- A deck can be **focused** (double-tap header or expand glyph) → temporarily fills the workspace,
  Esc/collapse returns to the grid. This recovers the only real value of the old Tabs mode
  (one big player) without a mode switch.
- Per-player config (`MediaPlayerConfig`) migration: drop the persisted layout enum; ignore on load.

### 2.2 Deck anatomy (top → bottom, fixed regions)

1. **Header strip** (fixed height): state pill · editable name · source chip · current media
   (ellipsized) · overflow menu (unchanged contents minus removed items below). Status line
   (fixed-height, §1) directly under the header.
2. **Seek zone**: waveform + scrubber as today, but **28 px hit-height** (touch), time row under it
   (position / toggle middle / duration). Near-end warning recolors the middle clock (existing
   behavior, keep).
3. **Transport row — the touch tier.** Center group `⏮ ▶/⏸ ⏭ ⏹` as `Btn.Touch` (56 px, Play/Pause
   72 px wide). Right group, same height but narrower: **HOLD** toggle + **Options** flyout.
   - **HOLD** is the promoted hold-image feature: a labeled `ToggleButton` (`AccentWarn` when
     engaged) directly on the transport. Engaged = the configured idle/fallback image is pushed to
     this player's video outputs (existing `HoldFallbackVideo` plumbing); long-press/right-click →
     flyout with image path picker (moves out of Player Settings dialog tab 2, which is deleted).
   - Mute + volume slider + dB readout + level meter move to a slim row *below* the transport
     (`Btn.Std` scale) — frequently used but not panic-path.
4. **Playlist** (fills rest): tabs strip + list as today; drag reorder kept. The reorder hint text
   becomes a tooltip (dead vertical space today).
5. **Routing strip** (bottom, fixed): summary text + `Matrix…` button (§2.3) + per-output mini
   status dots (green/amber/red from pump stats — same source as I/O health, §5).

### 2.3 Audio routing: matrix with presets (replaces vOutput)

Remove the **VirtualAudioChannelAssignment / "VOut N"** model (`Models/VirtualAudioChannelAssignment.cs`,
the Virtual Audio Channels grid inside Output Management's Advanced expander, and
`AudioRouteMixMode` Stereo/Swap/MonoLeft/... enum) — superseded by one concept:

- **Routing matrix dialog** per player (evolves `AudioMatrixDialog`): rows = source channels
  (updates with loaded media: 2 for stereo, 6 for 5.1…), columns = *named output channels* (output
  alias + channel, §5 aliases). Cells are gain steppers (off/−∞ default, tap = 0 dB, fine-drag for
  trim). Backed 1:1 by framework per-cell routes via `AudioRouter.ApplyMatrix` (framework A2).
- **Layout presets** solve the "usually stereo, sometimes 5.1 shows up" case. Per player, an
  ordered list of rules: `(source channel count) → (matrix preset)`:
  - 2ch → `Stereo to Main L/R` (default)
  - 6ch → `5.1 downmix to Main` (ITU coefficients from `AudioChannelLayoutPresets`) **or**
    `5.1 passthrough to outs 1–6` — user picks/edits once, stored in the player config; when media
    with that channel count loads, the matching preset is applied automatically (current behavior
    silently identity-maps).
  - Presets are project-level objects (shared by players *and* cue routes, §3.4) with name + matrix.
- The dialog gets a live level meter per column and a `Reset to preset` button. The old
  per-route `MixModes` combobox in the player UI is removed (a preset expresses all of them).

### 2.4 Removals

- **Player Settings dialog** shrinks to one page: transitions (mode + ms) and headphones-cue bus.
  Removed: *Output preset / custom W×H force* and *Prefer live UYVY passthrough* (the old debug
  format toggles — the negotiator handles formats; passthrough preference becomes an env var for
  debugging if still wanted: `HAPLAY_DEBUG_UYVY_PASSTHROUGH`).
  Removed: Idle/Fallback tab (→ HOLD button flyout).
- Status/load-error dock banners (→ toast + status line, §1).

---

## 3. Cue player

Layout stays: header / cue tree center / Now Playing right / properties drawer bottom.

### 3.1 Drawer fixes

- **Compact tabs**: replace the `TabControl` headers with a segmented control style
  (`Btn.Dense`, 24 px header height, no top padding). Tab set unchanged
  (General · Preview · Audio · Video · Text · Action · Comment · Group, visibility-filtered).
- **Stale-on-switch bug**: the drawer binds `SelectedCueNode.*` directly; switching cues leaves
  stale tab content/selection. Rewrite as **per-cue-type editor VMs**: selecting a node materializes
  a fresh `CueEditorViewModel` (Media/Image/Text/Action/Comment/Group subclass) wrapping the node;
  the drawer's `ContentControl` swaps editors via DataTemplates. Tab memory: remember last tab *per
  cue type*, restore on switch; never show a tab that doesn't apply. This removes the eight
  `IsVisible="{Binding HasSelectedXCue}"` tab hacks and fixes refresh by construction.
- Drawer stays an `Expander` but with a slimmer header (cue number + name inline, `SectionText`).

### 3.2 Now Playing: group progress + seek with lock

- **Group rows**: when a fired entry is a group, render one parent row — group label, aggregate
  progress (0–100 % across the longest child timeline), chevron to expand per-cue child rows
  (today's flat list becomes the expanded state). Children keep their ✕; parent ✕ cancels the group.
- **Seekable progress**: active-cue progress bars become slim scrub sliders (drag = seek through
  the cue's transport; group parent seeks all children proportionally via their shared timeline).
  - **Lock toggle** (padlock, header row of the panel, default **locked** = bars are display-only).
    Persisted per project. Locked is default because Now Playing sits next to GO — accidental
    drags during a show must be opt-in.
- Row content otherwise unchanged (number, label, position text). Keep the panel on the existing
  splitter.

### 3.3 Header

- GO stays the accent action; promote GO + Stop-All to `Btn.Touch`.
- Apply the fixed status line + toast rules (§1) to cue warnings (`Session.OpenWarnings`, sanitized
  route drops) — no inline banners above the tree.

### 3.4 Cue audio routing presets + aliases

- Cue Audio tab: replace raw route target pickers with the same **named outputs + presets** model
  as players (§2.3): pick an output alias, pick/edit a matrix preset; "Customize…" opens the same
  matrix dialog. Presets shared project-wide.
- Output **alias names** (defined in I/O, §5) appear everywhere cue routes/outputs are listed —
  "Main PA", "Monitor", "Stream feed" instead of device strings.

---

## 4. Control workspace

One page → four tabs (`TabControl` with `SectionText` headers, content unchanged functionally,
no nested page-level ScrollViewers — each tab owns exactly one scroll region):

1. **Surfaces** — device list (control MIDI in/out pairing, connect state), profile selection,
   layer status. (Today's device + Profiles expander content.)
2. **Scripts** — script list + editor launch, per-script enable, error badges.
   (`ScriptEditorWindow` unchanged.)
3. **Targets** — action endpoints (OSC/MIDI out), health probes, rebind dialogs.
4. **Monitor** — diagnostics & manual sends + X32 command/cache browser + an incoming-event tail
   (last N control events with resolved bindings — currently scattered debug text).

Cross-tab status chips in the workspace header (devices online, scripts failing, endpoints down)
so problems are visible from any tab. The three `Expander`s die; expander-hidden features become
first-class tab content.

---

## 5. I/O workspace (new; merges Outputs + MIDI devices + OSC)

Master-detail: left = unified device/endpoint list grouped by kind; right = detail pane.

- **Groups**: Audio outputs (PortAudio) · Video outputs (Local/NDI send) · NDI sources ·
  Audio inputs · MIDI devices (from `MidiDevicesView`) · OSC connections (from `OscConnectionsView`).
  Add buttons per group (existing dialogs reused).
- **Alias names**: every output line gets a user alias (stored in project; falls back to device
  name). Aliases feed the players' matrix columns, cue route pickers, and Now Playing labels (§2.3,
  §3.4). This is the single source of naming truth.
- **Detail pane per line**: status, format, controls (start/stop, NDI record button as today,
  fullscreen/windowed for local video) **plus a live stats card**:
  - Audio: pump enqueued/processed/dropped + drop trend sparkline (`AudioRouter.GetPumpStats`),
    PortAudio played/underrun/dropped samples (`PortAudioMetricsSnapshot`), ring fill %.
  - Video: pump submitted/dropped, queue depth (`VideoOutputPumpMetrics`), negotiated format chip.
  - NDI: connection count, overflow counters (`NdiIngestMetricsSnapshot` analogues).
  - Stats poll ≤ 2 Hz, paused when the workspace is hidden (these getters allocate snapshots —
    framework review H3).
- Health roll-up chip per group in the sidebar icon (amber/red dot) replacing today's scattered
  health text; `Clear health` stays in the header.
- **Removed**: the *video preview window option* (windowed preview open/close menu and
  `ApplyHoldImageWindowSize` preview-window plumbing in `OutputManagementViewModel` /
  `LocalVideoPreviewRuntime` — keep only what real local *output* windows need, fullscreen toggle
  included). Removed: Virtual Audio Channels grid (§2.3). The shared-headphones-bus advanced
  expander moves into the Audio outputs group detail as a normal feature.

---

## 6. Phasing & verification

Order minimizes risk: tokens/shell first (pure visuals), then leaf workspaces, players last (most
behavior moved). Each phase = code + this checklist updated together + verification report
(HaPlay test suite, plus xvfb smoke for GL-affected paths per repo convention; package DLLs land in
`HaPlay.Desktop` bin for runs).

- [x] **P0 — Framework prerequisites** (other doc: R1/R2, A1, A2) merged & green — 2026-06-10.
- [x] **P1 — Token sheet + toast host + status-line control** — 2026-06-10. `Styles/Tokens.axaml`
      (Deck tokens, touch tiers, segmented tabs, Go/Warn accents) included app-wide; `ToastCenter`
      (last-wins sink) + `ToastViewModel` + overlay in MainView (max 3, 6 s auto-dismiss, click
      pins, ✕ closes); `StatusLineControl` (fixed-height) replaces the ProjectStatus banner; shell
      swapped to 5 workspaces (Players/Cues/Control/I/O/Project, Ctrl+1…5), legacy
      `outputs`/`midi` workspace ids migrate to `io` on load. Tests: `ShellAndToastTests` (9).
      *Verified: full suite green; 12 s xvfb boot smoke clean.*
- [x] **P2 — I/O workspace (first increment)** — 2026-06-10. Output **aliases**: `OutputDefinition.Alias`
      (+`EffectiveName`, JSON-compatible with old projects), inline borderless rename in the row,
      `OutputNamingChanged` re-labels player matrix rows; **VOut model removed** (assignment grid,
      `OutputManagementViewModel` map/rows/event, player `BuildVirtualOutputMap` now orders by
      alias + channel; old projects load with a one-time migration toast; `HaPlayProject` field kept
      as legacy for deserialization); matrix row labels now `Alias · channel`; per-line **stats
      summary** (cumulative frames/chunks beside the sparkline, fed by the existing 1 Hz health
      poll); **OSC connections folded into I/O** (third tab); dead StartPreview/StopPreview
      commands deleted (windowed/fullscreen real-window mode toggles kept).
      **Master-detail landed (sixth pass):** the Outputs tab is now a master-detail — slim
      selectable rows (health dot · kind · alias, REC/clone badges) on the left; the right pane
      carries the alias editor, technical identity, summary, all per-line actions, and a **stats
      card** (health detail text, delivery counters, full-width 60 s throughput sparkline).
      Selection follows adds/removes. The Outputs/MIDI/OSC tabs remain the I/O workspace's section
      structure (instead of the originally sketched single grouped list — the tabs proved the
      cleaner separation in practice). **Still staged:** pause-when-hidden stats polling, per-group
      health roll-up on the sidebar icon.
- [x] **P3 — Control tabs** — 2026-06-10. One mega-scroll page → header arm/counts card (always
      visible) + **Surfaces / Scripts / Monitor / Tools** tabs, one scroll region each; the three
      Expanders died (Profiles → Surfaces card, X32 browser + manual sends → Tools cards).
      **Deviation noted:** the planned *Targets* tab needs action endpoints to move out of
      `MainViewModel`'s Action Targets dialog — deferred to the P4/P5 pass; tab set is
      Surfaces/Scripts/Monitor/Tools until then. *Verified: suite green ×3, xvfb boot smoke clean.*
- [ ] **P4 — Cue player.** Landed: GO + Panic on the `Btn.Touch` tier (`Go` accent); drawer
      TabControl on the compact `Segmented` style; **stale-drawer fix (targeted)** — switching cue
      types snaps the drawer to the first visible tab instead of leaving a hidden tab selected
      (`EnsureVisibleDrawerTabSelected`, the actual blank/stale symptom); **Now Playing tap-to-seek
      with padlock** — progress bars seek on tap via the engine's `SeekCueAsync`, gated by a
      default-locked toggle in the panel header (locked because the panel sits next to GO).
      **Group aggregate rows landed (fourth pass):** active cues sharing a parent group node
      collapse into one `ActiveGroupViewModel` row — group label, child count, aggregate progress
      on the longest child timeline, chevron expands to the per-cue rows (collapsed by default),
      group ✕ cancels every child, and the aggregate bar is tap-to-seek (each child seeks to the
      same fraction of its own duration; same padlock gate). Top-level cues stay flat;
      `ActiveCues` remains the flat source of truth. Tests: `NowPlayingGroupRowTests` (4).
      **Drawer closed out (seventh pass):** per-cue-type **tab memory** — the drawer remembers the
      last tab used for each cue type and restores it on switch (media→group→media lands back on
      Audio); view-level regression test `CueDrawerTabMemoryTests` covers the plan's A→B→A
      scenario including the never-show-a-hidden-tab invariant. **Re-scoped:** the full
      per-cue-type editor VM refactor is downgraded to an optional internal-architecture cleanup —
      its two user-facing goals (no stale content, sensible tab on switch) are both shipped and
      regression-tested; rewiring ~500 lines of drawer bindings now carries regression risk with
      no user-visible delta.
- [x] **P5 — Players (core)** — 2026-06-10 second pass. **Deck grid + focus mode**: the
      Tabs/Stacked/Split chooser, `PlayersLayoutMode`, and the three layout hosts are gone; players
      render in one ⌈√N⌉-column uniform grid (min deck height 480, scrolls beyond), click selects,
      double-tap focuses full-workspace, Esc/double-tap returns (legacy `playersLayout` settings key
      ignored on load). **No layout shift**: both player banners (transient status + sticky load
      error) replaced by one fixed-height `StatusLineControl` in the deck header
      (`DeckStatusText`/`Severity`; errors win over status text). **Touch transport**:
      prev/play-pause/next/stop on the `Btn.Touch` tier (play carries the Go accent); **HOLD
      promoted onto the transport** (`Touch Warn` toggle bound to `HoldFallbackVideo`, image picker
      in the adjacent flyout); mute/volume/dB/meter moved to a Std-tier row below. **Settings
      slimmed to one page** (transitions + headphones cue bus); removed: output preset/custom-size
      force, the UYVY passthrough debug toggle, the idle-image tab (→ HOLD flyout).
      **P5b landed (third pass): channel-count auto-preset rules.** `ChannelPresetRule`
      (source channels → `AudioDownmixPreset`) persisted in `MediaPlayerConfig`; rules editor in the
      matrix dialog ("When source has N channels, apply …"); a rule fires when the matrix input
      count *changes* to a match (same-count reloads keep hand-tuned cells) and immediately on add
      when it matches the live source; dialog rows show output aliases. Tests:
      `ChannelPresetRuleTests` (4) — incl. ITU fold-down cell assertions and the identity default
      without a rule. **P5c landed (fifth pass).** (1) `HaPlayPlaybackSession.TrySetOutputMatrix` /
      `TrySetOutputMatrixCompoundGain` now run on framework `AudioRouter.ApplyMatrix` — one atomic
      reconcile (changed cells ramp click-free, new cells fade in, dropped cells removed) instead
      of the old remove-all-re-add hard cut; gain rides are pure `GainSlot.Target` moves through
      the same call; `CellRouteIds`/`BuildSingleCellMap` deleted. Framework prerequisite:
      `ApplyMatrix` accepts matrices *smaller* than the channel counts (hosts size from their UI
      model; uncovered channels stay unrouted; oversize still throws). (2) Framework
      **`AudioMixPreset`** file format (`S.Media.Core.Audio`, `.mfmix`, camelCase source-gen JSON,
      linear jagged gains, schema-validated load) with its first consumer: per-output
      **Save preset… / Load preset…** in the matrix dialog (`ToLinearMatrix`/`ApplyLinearMatrix`).
      (3) Alias propagation completed: cue audio-route picker, cue output setup, routing dialog,
      and headphones-cue target labels all show `EffectiveName` (persistence keys intentionally
      stay on `DisplayName`). Tests: `AudioMixPresetTests` (4), `AudioRouterMatrixTests` (8) incl.
      smaller-matrix/empty-matrix semantics.
      *Manual check pending: THE IDOLM@STER MOVIE.mkv 5.1 track with a 6ch rule (audible
      fold-down), stereo files unaffected.*
- [ ] **P6 — Sweep (mostly done).** Fourth pass: 18 dead string resources removed (zero-usage
      verified). Sixth pass: the rewrite-introduced literals localized (Control tab headers,
      Manual sends title, I/O OSC tab header) — the *pre-existing* English literals inside the
      Control view (structure-row buttons, profile builder labels, …) remain as before the
      rewrite and are out of its scope. Remaining: `VirtualAudioChannelAssignment` model deletion
      once the back-compat window closes; update `HaPlay-Control-*` docs + screenshots; manual
      touch pass (everything on the panic path operable with a thumb — user-side check).

**Verification report (seventh pass):** HaPlay **475/475** (×2; +1 drawer-memory view test,
+3 export-merge tests), full solution build 0 errors, desktop xvfb boot smoke clean. With this
pass every engineering item from the rewrite + save/load briefs is shipped or explicitly
re-scoped; what remains is operator-side (manual touch pass, doc screenshots) plus two
deliberately parked cleanups (editor-VM internal refactor; `VirtualAudioChannelAssignment`
deletion when the back-compat window closes).

**Verification report (sixth pass):** HaPlay **471/471**, desktop xvfb boot smoke clean. Landed:
I/O master-detail (above), new-literal localization, and `.mfmix` **Load preset…** in the cue
route editor (`LoadCueMixPresetAsync` — same replace-on-target-line semantics as the enum
quick-applies, non-zero cells → 1-based cue routes with dB gains), completing the preset-file
story across both players and cues. Still open overall: per-cue editor VM refactor,
composition/per-layer exports, P6 remainder (docs/screenshots, manual touch pass,
`VirtualAudioChannelAssignment` deletion later).

**Verification report (fifth pass):** HaPlay **471/471**, S.Media.Core **499** (+4 preset, +1
matrix), S.Media.Playback 82, full solution build 0 errors, desktop xvfb boot smoke clean. The
live-audio-path migration (P5c.1) is covered by the existing session/matrix suites going green
unchanged. Still staged: per-cue editor VM refactor, cue audio presets tab (the `.mfmix`
load/save in the player dialog covers players; the cue route editor still uses the enum
quick-applies), I/O master-detail shell, composition/per-layer exports, P6 remainder
(Control-literal localization, docs/screenshots, manual touch pass).

**Verification report (fourth pass):** HaPlay **471/471** (×2 runs; +4 group-row tests, dead-string
sweep applied), S.Media.Core 494 unchanged, full solution build 0 errors, desktop xvfb boot smoke
clean. Still staged after this pass: P5c (session routes onto framework `ApplyMatrix` +
`AudioMixPreset` file format — deliberately deferred: it rewires the live audio path and should be
its own reviewed change; the format ships together with its first consumer per the A3 rule),
per-cue editor VM refactor, I/O master-detail shell, composition/per-layer exports, P6 remainder.

**Verification report (third pass):** HaPlay **467/467**, stable across 5 consecutive runs after
fixing a pre-existing order-dependent flake at its root (`OpenRecentCommandTests` constructed
`MainViewModel` on the raw xunit thread; the ctor's `ApplyTheme` hits
`Application.RequestedThemeVariant`, owned by the headless UI thread once any session test ran —
now dispatched like every other VM test). Desktop xvfb boot smoke clean.

**Verification report (2026-06-10, P0–P3 + P4 partial):** HaPlay suite **459/459** green
(449 baseline + 9 shell/toast + 2 alias − 1 retired VOut test, stable across 3 consecutive runs;
one isolated flake in a timing-sensitive headless interaction test did not reproduce). Framework
suites unaffected (Core 494 / FFmpeg 180 / Playback 82). Full solution build 0 errors. Three
12-second xvfb boot smokes of the freshly built `HaPlay_Test` desktop binary: zero unhandled
exceptions / XAML load errors.

---

## 7. Sectional save/load (added 2026-06-10, second pass)

**One format instead of many.** Every scoped export is a normal `.haplayproj` file whose
`SavedSections` table (see `Models/ProjectSections.cs`) records which sections the payload carries:
`outputs.audio`, `outputs.video`, `targets.midi`, `targets.osc`, `players`, `cueLists`, `control`
(parents `outputs`/`targets` cover both children). `null` = full project — every pre-existing file
keeps working. **Opening a partial file imports only its sections** (merge: e.g. a video-only file
replaces video lines but leaves audio lines, players, cues untouched) and deliberately does *not*
become the current project path, so a later Ctrl+S can't overwrite the show with a fragment.
**Checkbox dialogs, not menu sprawl:** `File → Export Sections…` opens one checkbox list
(`ProjectExportDialog`) covering all section combinations — this is also the "choose what the
overarching project save includes" surface (export with everything checked = scoped full save).

Mapping of the requested granular formats:

| Ask | Mechanism |
|---|---|
| 1 playlist / 1 player + config | existing per-player gear menu (Save/Load Tab, Save/Load Player Config) — kept |
| multiple playlists, all players + config + playlists | `players` section export |
| channel setup configuration | rides inside player config (matrix); standalone preset files come with the P5 matrix-preset work |
| 1 cue list / all cue lists | existing Cue Files menu (Load/Save/Save As, Load-All/Save-All) — kept |
| composition config (one/all) | staged: composition sub-export when the P4 cue-editor work touches the cue model |
| entire cue player | `cueLists` section export |
| outputs: audio / video / MIDI / OSC / A+V / all | section checkboxes (`outputs.*`, `targets.*`) in the one dialog |
| control: 1 layer / all layers / + config | whole-system via `control` section + existing control config file; per-layer export staged (layer model lives in S.Control config — needs a layer-scoped slice there) |
| project save with check/uncheck | Export Sections… with everything checked; `ApplyProjectSnapshot` is fully section-aware |

Additional sections worth adding later: device profiles (control), app appearance (deliberately
per-machine, stays in app-settings), output aliases (ride with their output definitions — already
covered).

**Framework-side decision:** all payloads are HaPlay app models (playlists, cue lists, output
definitions), so the envelope/scoping layer stays app-tier; nothing framework-worthy was found
except **audio mix presets** — when P5's matrix presets land, the preset model
(name + gains + channel-count rules) belongs next to `AudioRouter.ApplyMatrix` /
`AudioChannelLayoutPresets` in the framework so smoke tools and other hosts can share preset files.
Cue-model ownership is the cue/clip RFC's call (still open).

- [x] `ProjectSections` + `HaPlayProject.SavedSections` + scoped `BuildProjectSnapshot` +
      merge-on-apply + import guard + `ProjectExportDialog` + File-menu entry — 2026-06-10
      (tests: `ProjectSectionScopeTests`, 4).
- [x] Composition sub-export — seventh pass. `CueCompositionsIO` (`.haplaycomps`, schema'd) +
      cue Files-menu **Export/Import Compositions…**; import merges by name (same name updates
      size/fps keeping the Id so placements stay bound; new names append, colliding ids
      regenerate). Test: `SectionExportMergeTests.MergeCompositions…`.
- [x] Per-layer control export — seventh pass. Framework `S.Control.ControlConfigSlices`
      (`ExtractLayers` = layer + its scripts only; `MergeLayers` = replace-by-name, colliding
      script ids regenerated and remapped); slices are plain control-config files (same format).
      UI: **Export layer…** on layer rows, **Import layer…** in the Control Files menu. Tests:
      `SectionExportMergeTests` extract/merge.
- [x] Framework `AudioMixPreset` file format (with P5 matrix presets) — 2026-06-10 fifth pass;
      `.mfmix` files saved/loaded from the matrix dialog are the "channel setup configuration"
      standalone format from the save/load wish-list.

## 8. Solution hygiene (added 2026-06-10, second pass)

- [x] `MFPlayer.sln` restructured: duplicate `Test`/`Tools`/`Media` solution folders merged into
      one of each, `Test` + `Tools` nested under `MediaFramework` to mirror the disk layout,
      `global.json` + `README.md` added to Solution Items. Project GUIDs and build configs
      untouched; full solution builds green.

Open questions parked (decide during the relevant phase): focused-deck animation or instant; whether
preset rules also auto-apply on *output* count changes (proposal: no — source-driven only); whether
Monitor tab event tail needs persistence (proposal: ring buffer, session-only).
