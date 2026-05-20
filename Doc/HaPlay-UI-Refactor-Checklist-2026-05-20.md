# HaPlay UI Refactor — Checklist (2026-05-20)

Companion to [`HaPlay-UI-Refactor-Plan-2026-05-20.md`](HaPlay-UI-Refactor-Plan-2026-05-20.md).
Framework-side gaps are tracked separately in
[`HaPlay-Framework-Gaps-Checklist-2026-05-20.md`](HaPlay-Framework-Gaps-Checklist-2026-05-20.md).

Status legend:

- `[ ]` not started
- `[~]` in progress / partial
- `[x]` done

Each item links back to the plan section that motivates it.

---

## Phase A — Foundations (no user-visible change)

- [x] **Output runtime `ReconfigureAsync` support** (§3.2, §9.6)
  - [x] `PortAudioOutputRuntime.ReconfigureAsync`
  - [x] `ILocalVideoPreviewRuntime.ReconfigureAsync` (SDL3 + Avalonia variants)
  - [x] `NDIOutputPreviewRuntime.ReconfigureAsync`
  - [x] `Reconfigured` event on each runtime for Phase B orchestration
- [x] **Hot route addition API** (§4.3.3, §9.6)
  - [x] `HaPlayPlaybackSession.TryAddOutput(line, out error)`
  - [x] `HaPlayPlaybackSession.TryRemoveOutput(line, out error)`
  - [x] Per-line wiring metadata tracking + clean rollback on failure
- [x] **Project file schema + serializers + Save/Load command plumbing** (§7)
  - [x] `HaPlayProject` record + `schemaVersion`
  - [x] `JsonPolymorphic` `OutputDefinition` with discriminator
  - [x] Forward-compat fields: `LocalVideoOutputDefinition.CloneOfId`,
    `NDIOutputDefinition.PixelFormatLock` + `ResolutionLock`
  - [x] `ProjectIO.SaveAsync` / `LoadAsync` + `UnsupportedSchemaVersionException`
  - [x] `MainViewModel.BuildProjectSnapshot()` / `ApplyProjectSnapshot()`
  - [x] `HaPlay.Tests` project with round-trip + schema-version tests

## Phase B — Outputs Editable + Project Save/Load Visible

- [x] **Edit dialogs reuse Add forms** (§3.2)
  - [x] `LoadFromExisting` on each dialog VM
  - [x] Dynamic title + button label ("Add" → "Save")
  - [x] `Id` and forward-compat fields preserved across edit
  - [x] Dialogs resizable; engine combobox locked during edit
  - [x] "Edit while in use" confirm — fires only when a player is actively playing through the line
- [x] **File → Save / Open Project commands** (§7.3)
  - [x] File menu: New / Open / Save / Save As + keyboard shortcuts
  - [x] Recent projects (capped at 8) persisted to `%LocalAppData%/HaPlay/recent-projects.json`
  - [x] Missing-route detection on load surfaces a status banner
  - [x] Window title bound to `ProjectTitle`
  - [~] **Rebind missing outputs dialog** (§7.3) — currently just a banner; richer "pick a replacement device" dialog is still to-do
- [x] **App-shell sidebar** (§12.1)
  - [x] Collapsible (icon-only ~48 px vs labelled ~180 px)
  - [x] Hamburger toggle + Ctrl+B
  - [x] Ctrl+1 / 2 / 3 jumps to Players / Outputs / Project
  - [x] State persisted to `%LocalAppData%/HaPlay/app-settings.json`
- [x] **Clone-of UX + PlayerRoutingMirror** (§3.4)
  - [x] Visual nesting (indent + "↳" caption "clone of …")
  - [x] Edit Local Video dialog: "Clone of (optional)" dropdown
  - [x] Clones hidden from per-player checkbox list
  - [x] Parent-tick → wire parent + clones automatically (`SelectedOutputLines`)
  - [x] `RoutingTopologyChanged` event so Edit-time clone-of changes resync players
- [x] **Dialog conventions + terminology rename** (§12.2, §12.3)
  - [x] Title bars use action verbs ("Add X" / "Edit X")
  - [x] Primary action right-aligned, Cancel to its left
  - [x] Validation surfaces inline
  - [x] `KindLabel` shows user-visible names ("Standalone window" / "In-app preview" /
    "Local audio" / "NDI program") with technical names as tooltip
  - [x] Engine combobox carries user-visible label + subtitle
  - [x] "Hold image" → "Idle image"; toggle label → "Show when no media playing"
  - [ ] **Dialog size persistence per dialog type** (§12.2 last bullet) — deferred; nice-to-have polish
- [x] **Hot-routing follow-up** (§4.3.3) — landed early
  - [x] `OutputLineRemoving` event so sessions unwire BEFORE the runtime is disposed
    (fixes the spammed `ObjectDisposedException` on remove-during-play)
  - [x] Checkbox toggle during playback hot-wires via `TryAddOutput` / `TryRemoveOutput`

## Phase C — MediaPlayer Redesign

- [x] **Transport bar + master volume** (§4.3.1)
  - [x] Big buttons (Play/Pause, Prev/Next, Stop)
  - [x] Master gain slider + Mute toggle
  - [x] Three-time display (position / remaining / duration)
  - [x] **Seek-on-drag-end with bounded CT** (§4.3.1, §9.5) — verified 2026-05-20:
    `MediaPlayerView.axaml.cs` only fires `SeekToSliderCommand` from
    `OnSeekSliderPointerReleased` and `OnSeekSliderKeyUp` (arrow / Home / End / PgUp / PgDn).
    The slider's `Mode=TwoWay` keeps the visual in sync per-tick without decoding.
    `SeekToSliderAsync` runs through `WithPlaybackArcAsync` with the
    `[[playback-teardown-timing]]` two-tier wall (inner 3 s, outer 5 s).
  - [x] **Keyboard-first transport shortcuts** (§4.3.1) — Space play/pause,
        `[` / `]` prev/next, `,` / `.` jog ±5 s. Tunnel-routed `KeyDown` handler on
        `MediaPlayerView` bails on `TextBox`, `NumericUpDown`, `Slider` sources and
        any modifier-chord (so playlist-tab rename, transition-ms NumericUpDown,
        slider scrubbing, and `Ctrl+S` on `MainView` keep working). New
        `JogBackCommand` / `JogForwardCommand` reuse `SeekToSliderAsync` so the
        bounded-CT teardown timing matches a normal drag-end commit.
- [x] **Playlist tabs** (§4.3.2)
  - [x] `PlaylistTabViewModel` + `PlaylistTabs` collection on the player
  - [x] Tab strip with inline rename TextBox
  - [x] Add / Remove tab commands
  - [x] Save tab / Load tab commands
  - [x] **Save Player as…** (§4.3.2) — verified 2026-05-20: `BuildPlayerConfig`
    bundles every `PlaylistTabs` entry (incl. per-tab Looping / AutoAdvance / SelectedPath),
    `SelectedPlaylistTabIndex`, master `MasterVolumeDb` / `MasterMuted`,
    `OutputPreset` + `TransitionMode` + `TransitionDurationMs`, the ticked
    `SelectedOutputDisplayNames`, and `OutputGains` per route. `MediaPlayerConfig`
    schema is `HaPlayPlayerConfig/v1` with the legacy flat `PlaylistPaths` retained
    so older player/project files still load via the `ApplyPlayerConfig` fallback.
  - [x] **Playlist import (`.m3u` / `.m3u8`)** (§4.3.2 last bullet) — verified
    2026-05-20: `PlaylistIO.LoadAsync` branches on extension and uses
    `ParseM3uAsync` to honor `#EXTM3U`-style comments and resolve relative paths
    against the playlist's directory. The Load-tab picker already lists
    `*.m3u;*.m3u8` as a filter; an imported m3u creates a new tab whose name
    defaults to the filename.
  - [ ] **Live items in playlists** (§6 / Phase C.5)
- [x] **Inline `+ Add output…`** (§4.3.3)
  - [x] Flyout in the Routing expander with PortAudio / Local Video / NDI entries
  - [x] New output auto-wires on tick (via the hot-route follow-up above)
- [x] **Audio matrix** (§4.3.4)
  - [x] Per-output gain + mute (the under-matrix convenience sliders)
  - [x] Edits apply live via per-route `SetRouteGain`
  - [x] **Per-output channel-mix mode** (Stereo / Swap / Mono-L / Mono-R / Silence) —
    landed 2026-05-20. `AudioRouteMixMode` enum on `OutputGainConfig` round-trips;
    `HaPlayPlaybackSession.TrySetOutputChannelMap` rebuilds the route's `ChannelMap`
    in-place. Mix mode now doubles as a preset that rebuilds the matrix below.
  - [x] **Full N×M cell grid with per-(input-channel, output-channel) gain** —
    landed 2026-05-20. `AudioMatrixCellConfig` persisted per output gain;
    `AudioMatrixViewModel.Resize` auto-sizes to source channel count on session
    open (sink is currently 2 — see WireAudio downmix). Per-cell `GainDb` slider
    + Mute toggle in the TreeDataGrid; cell edits push down via
    `HaPlayPlaybackSession.TrySetOutputMatrix`.
  - [x] **Multi-route backing** (one `AudioRouter` route per non-zero cell) —
    framework `AudioRouter.AddRoute(source, sink, routeId, map, gain)` plus
    `RemoveRouteById` / `SetRouteGainById` shipped 2026-05-20. Master/per-output
    gain rides ride every cell route click-free via
    `TrySetOutputMatrixCompoundGain` → `SetRouteGainById`.
  - [x] **Channel labels** along TreeDataGrid columns ("In L / In R" or "In 1..N")
    and rows ("Device · Out L / Out R" or "Device · Out 1..N").
  - [x] **TreeDataGrid grid view** — `MatrixTreeGrid` in `MediaPlayerView`
    (`FlatTreeDataGridSource<AudioMatrixRow>`); columns built dynamically in
    code-behind from `AudioMatrixInputChannelCount` so the layout follows the
    loaded source's channel count.
  - [ ] **Per-channel input column attenuation** (separate per-input trim row) —
    deferred polish; per-cell gain already covers most use cases.
  - [ ] **"Expand → full matrix" secondary dialog** (§4.3.4 last paragraph) —
    deferred polish; the inline TreeDataGrid is full-featured already.
- [~] **Output preset + transition** (§4.3.5)
  - [x] UI: preset combobox (`AsSource` / `1080p60` / `720p60` / `Custom`)
  - [x] UI: transition combobox + duration NumericUpDown
  - [ ] **Preset wired through `IVideoCompositor` / `CpuVideoCompositor`**
        — gated on framework gap "Output preset compositor path" in
        `HaPlay-Framework-Gaps-Checklist-2026-05-20.md`. The selected
        `OutputPreset` value is already persisted via `MediaPlayerConfig`
        and survives save/load; it just doesn't change what plays today.
  - [ ] **Fade transition** via `FadeFromBlackVideoSource` /
        `LayerOpacityTween` / router-level audio gain ramp — same framework
        prerequisite (a compositor sits between the router and the sinks
        before per-layer opacity tweens become reachable).
  - [ ] **"Idle image" as a compositor layer instead of `LogoFallbackVideoSink`
        template frame** (§8.10) — gated on the same compositor path. Today's
        `LogoFallbackVideoSink` mechanism still works as the fallback.

## Phase C.5 — Live Inputs (shared by Players and Cue Player)

- [x] **`MediaPlayer.TryOpenLive(IAudioSource?, IVideoSource?, …)`** (§6.7, §9.6) — landed in framework
- [x] **`PortAudioInput` disconnect detection** (§6.11, §9.6) — landed in framework
- [ ] **PortAudio-input item: dialog, data model, playback** (§6.4)
- [ ] **NDI-input item — discovery dialog** (§6.3)
- [ ] **NDI-input item — manual-name path** (§6.3)
- [ ] **NDI-input item — "waiting for source" UI** (§6.9)
- [ ] **Playlist items as `FileItem | NDIInputItem | PortAudioInputItem` discriminated union** (§6.8)
- [ ] **Live audio matrix auto-extension on NDI source negotiate** (§6.5)
- [ ] **`NDIVideoReceiver` backfill** — gated by framework gap of same name (§8.9)

## Phase D — Cue Player

- [ ] **Cue list data model** (§5.2) — `CueList`, `Group`, `MediaCue` / `ActionCue` / `CommentCue`
- [ ] **`.haplaycues.json` serialization**
- [ ] **TreeDataGrid view with row editing** (§5.4)
- [ ] **Transport: GO / Standby / Pause / Stop / Panic / Back** (§5.3)
- [ ] **Per-cue route + gain + fade + pre-wait** (§5.2)
- [ ] **Pre-roll cache (next-N cues)** (§5.7)
- [ ] **Auto-follow / auto-continue scheduling** (§5.2, §5.6)
- [ ] **Group-level overrides + "fire all simultaneously" mode** (§5.2)
- [ ] **Action-cue emitters** (§5.2, §5.6)
  - [ ] OSC out via `OSCLib`
  - [ ] MIDI out via `PMLib`
  - [ ] Multi-target endpoint registry (§12.6) + rebind-missing-endpoints dialog
  - [ ] Target Configuration dialog (§12.2, §12.6)

## Phase E — Polish & Follow-ups (§8)

- [ ] **§8.1 Output Health Panel** — per-line LEDs + sparklines from existing pump stats
- [ ] **§8.2 Per-Player Headphones Cue Bus**
- [ ] **§8.3 Multi-Player Workspace Layout** (split / stacked, persisted)
- [ ] **§8.4 OSC / MIDI Remote Control** (inbound bindings — out for this round)
- [ ] **§8.5 Drag-and-Drop File Import** (playlist / cue list / quick-play)
- [ ] **§8.6 Theme & Density** (light/dark, compact/comfortable)
- [ ] **§8.7 Window State Persistence** (per-machine app settings)
- [ ] **§8.8 Recording Sink** (UI side — Record button on NDI outputs)
- [ ] **§8.9 NDI Video Receiver Backfill** — gated by framework gap
- [ ] **§8.10 Compositor-Based Hold Image** — folds into Output preset compositor path above
- [ ] **§8.11 Output Activity Indicators** — per-output LED driven by pump metrics
- [ ] **§12.7 String resource plumbing** — `Strings.resx` for all XAML + ViewModel strings (deferred from Phase B)

## Cross-Cutting (§9)

- [x] **§9.1 Output line stable Guid identity** — Phase A persisted IDs
- [x] **§9.2 Routing mirror for clones** — Phase B.4 PlayerRoutingMirror
- [x] **§9.3 Audio matrix bridge** — framework multi-route per `(source, sink)` pair
  shipped 2026-05-20 (`AudioRouter.AddRoute(..., routeId, ...)`, `RemoveRouteById`,
  `SetRouteGainById`). HaPlay installs one router route per non-zero matrix cell
  via `HaPlayPlaybackSession.TrySetOutputMatrix`. Click-free gain rides through
  `SetRouteGainById` per cell. 402 framework tests pass; 27 HaPlay tests pass.
- [x] **§9.4 Project file migration** — `schemaVersion=1` in place; `Migrate(JsonNode, from, to)` will be needed at the next bump
- [x] **§9.5 Threading discipline** — `WithPlaybackArcAsync` + bounded inner CTs preserved
  in Phase A/B/C. Seek-on-drag-end commit point verified 2026-05-20 (see Phase C transport
  bar item). New `JogBack` / `JogForward` jog commands route through the same arc.
- [x] **§9.6 Framework gaps** — tracked in
  [`HaPlay-Framework-Gaps-Checklist-2026-05-20.md`](HaPlay-Framework-Gaps-Checklist-2026-05-20.md)
- [~] **§9.7 Testing strategy** — `HaPlay.Tests` has IO + dialog VM coverage;
  no Avalonia-headless ViewModel tests yet; no Cue Player timing tests yet (Phase D)

## Cross-Cutting (§12 App Shell + Dialogs + Terminology)

- [x] §12.1 Sidebar — done in B.3
- [x] §12.2 Dialog convention pass — done in B.5
- [x] §12.3 Terminology cleanup — done in B.5
- [x] §12.4 Acceptance — sidebar collapse persists; no Avalonia/SDL strings leak by default
- [x] §12.5 Open Questions (Resolved) — Ctrl+1..N implemented; English-only ship
- [ ] §12.6 **Multi-target OSC/MIDI endpoint registry** — Phase D scope
- [ ] §12.7 **String resource plumbing** — Phase E polish (`Strings.resx`)

---

## Currently uncovered / "will land later" — quick index

| Topic | Plan ref | Status | Notes |
|---|---|---|---|
| Idle image via compositor (image-layer) | §8.10 / §4.3.5 | `[ ]` | Becomes natural once the compositor path lands for the Output preset. Until then, `LogoFallbackVideoSink` template frames are the mechanism. |
| Output preset → CompositorVideoSink | §4.3.5, framework gap | `[ ]` | Same framework prerequisite as Idle image via compositor. |
| Live inputs (NDI / PortAudio) as media items | §6, Phase C.5 | `[ ]` | Framework prerequisites already done; UI side is the next chunk. |
| Cue Player view | §5, Phase D | `[ ]` | Whole new workspace. |
| Recording sink, drag-and-drop, theme, etc. | §8 | `[ ]` | Phase E polish. |
| Per-dialog size persistence | §12.2 | `[ ]` | Small polish item; not load-bearing. |
| `Strings.resx` plumbing | §12.7 | `[ ]` | Deferred to Phase E. |
| Rebind missing devices dialog | §7.3 | `[~]` | Banner only today; richer dialog when load-side errors get reviewed. |

---

## Maintenance

Update this file alongside any change that lands or moves a checklist item. Keep
section ordering aligned with the plan so a reviewer can read plan → checklist
in lockstep. Framework-side work (anything that needs an `S.Media.*` change)
lives in the framework gaps checklist, *not* here.
