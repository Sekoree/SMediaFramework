# Cue Player — UI Redesign Checklist (2026‑05‑22)

> Companion plan for the Cue Player tab redesign. Driven by operator feedback
> that the in-grid columns expose too much config, the two right-side panes
> (`VirtualOutputsTreeGrid`, `CueRoutesTreeGrid`) don't communicate when they
> apply, and per-cue Audio/Video configuration has no proper home.
>
> Phases are independently shippable — stop at any phase boundary and the
> Cue Player still loads, saves, and fires cues end-to-end.

## Legend

- 🔲 todo  · ✅ done  · ⏸ blocked  · ❌ skipped
- **Risk**: None · Low · Medium · High
- **Effort**: S (single sitting / <1 day) · M (1–2 days) · L (multi-PR / week+)
- **Breaking**: ✓ (saved `.haplaycues` files need migration) · ✗ (additive / UI-only)

## Goal

Move from a wide multi-column TreeDataGrid + two confusing side panes to:

1. A **minimal cue tree** (status indicator, number, name, duration, kind).
2. A **bottom drawer** (Expander) whose contents are scoped to the selected
   cue kind. Media cues get tabs for **General / Audio / Video / Routing /
   Notes**; Action cues get an Action tab; Comment cues get a Text tab;
   Groups get a Group tab.
3. **Per-output composition** (resolution + framerate) carried by each video
   output binding owned by the cue list (not per cue, not on the player).
4. A **LayerIndex** on display cues so the operator can stack a lower-third
   over a background without leaving the cue list.
5. The two right-side panes go away — their data lives inside the Audio /
   Routing tabs of the drawer.

## Sequencing principles

1. Phase 1 is **UI-only relocation** plus the smallest necessary model
   addition (`DurationMs`) — preserves saved cue files verbatim.
2. Phase 2 introduces the new model surface (output bindings with
   composition, layer index). Saved files from Phase 1 still load; the new
   fields default sensibly.
3. Phase 3 cleans up: removes the legacy side-pane code paths, finalizes
   migration, updates docs.

---

## Phase 1 — Slim the tree + drawer skeleton

> **Risk**: Low · **Effort**: M · **Breaking**: ✗
>
> Goal: ship the new layout with the existing data model. Operators see the
> redesigned tree and drawer immediately. No new persisted fields yet
> except `DurationMs` (which is purely additive and defaults to 0).

### 1.1 Probe + persist `DurationMs` on media cues

- ✅ Add `int DurationMs { get; init; }` to `MediaCueNode`. JSON serialization
  picks it up automatically — old files default to 0.
- ✅ Add `[ObservableProperty] private int _durationMs;` to
  `CueNodeViewModel`. Wire to/from `MediaCueNode` in `FromModel` / `ToModel`.
- ✅ Probe helper: `Playback/CueMediaProbe.cs` calls `MediaContainerDecoder.Open`
  off the UI thread, reads `Duration`. Catches exceptions, returns `null` on
  failure.
- ✅ Probe on add: `AddMediaCueAsync`, `BrowseMediaSourceAsync`,
  `AddMediaFilesFromDrop` all call `ProbeAndAssignDurationAsync`. Drop path
  fires-and-forgets the probe so the UI doesn't block.
- ✅ `CueNodeViewModel.DurationDisplay` → `mm:ss` (or `h:mm:ss` for >1h), em-dash
  when 0 or non-file source.

### 1.2 Slim the cue tree

- ✅ Reduced `CueTreeGrid` columns to: **Status indicator | Number | Name |
  Duration | Kind**.
  - Status indicator: filled circle, `OrangeRed` = current, `Goldenrod` =
    standby, hollow outlined = idle. Backed by `CueRowStatus` enum on the
    row; `CuePlayerViewModel.RefreshRowStatuses` pushes updates when
    Current/Standby change.
  - Name column kept as inline `TextBox` (rename in place).
  - Duration read-only (`DurationDisplay`).
  - Kind read-only (`KindLabel`).
- ✅ Dropped cell editors for Trigger, PreWait, Source/Action, Endpoint,
  Extra, Notes; their build methods + the `EndpointOption` record were
  deleted from the code-behind.
- ✅ Added column header strings `CueTreeStatusColumnHeader`,
  `CueTreeNameColumnHeader`, `CueTreeDurationColumnHeader`. Old headers
  (Trigger, Pre, Source/Action, Endpoint, Extra, Notes) remain in resx
  pending Phase 3 cleanup.

### 1.3 Drawer skeleton (bottom Expander + TabControl)

- ✅ Replaced the `<Grid ColumnDefinitions="3*,2*">` block with a nested
  `DockPanel`: `TreeDataGrid` fills, bottom `Expander` hosts the drawer.
- ✅ Drawer header binds to `CuePlayerViewModel.SelectedCueDrawerTitle` —
  `{Number} {Label} — {KindLabel}` or "Select a cue to configure it."
- ✅ `TabControl` with `IsVisible` bound to per-kind flags
  (`HasSelectedCue`, `HasSelectedMediaCue`, `HasSelectedActionCue`,
  `HasSelectedCommentCue`, `HasSelectedGroupCue`):
  - **General**: Trigger, PreWait, FadeIn/Out (media), Start/Loop/End
    (media), Notes.
  - **Audio** (media): Virtual Outputs + Route Connections grids,
    relocated unchanged.
  - **Video** (media): Phase-2 placeholder text.
  - **Action**: ActionKind, command text, "Edit Action…" button.
  - **Comment**: comment text box.
  - **Group**: fire mode picker.
- ✅ `VirtualOutputsTreeGrid` / `CueRoutesTreeGrid` removed from the
  right-side column; they now live inside the Audio tab.

### 1.4 Verification

- ✅ `dotnet build UI/HaPlay/HaPlay.csproj` clean — 0 warnings, 0 errors.
- ✅ `dotnet build UI/HaPlay.Desktop/HaPlay.Desktop.csproj` clean.
- ✅ `dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --filter
  FullyQualifiedName~CuePlayer` — 25/25 pass.
- 🔲 **Manual run (operator required)**: Claude cannot interact with the
  Avalonia GUI to confirm visual layout. The operator should run
  `dotnet run --project UI/HaPlay.Desktop` and verify:
  1. Cue tree shows the five expected columns (status / number / name /
     duration / kind).
  2. Drag-and-drop a media file → duration appears in the column.
  3. Select Standby → indicator dot turns amber on the standby row; press
     Go → indicator turns orange-red on the current row.
  4. Drawer tabs swap correctly when selecting a Media vs Action vs
     Comment vs Group cue.
  5. Loading an existing `.haplaycues` from disk does not crash; existing
     cues show duration as "—" until a re-add / source change re-probes.

**Phase 1 exit criteria**: the tree is slim, the drawer hosts every editor
that used to live in the right-side column, no saved-file format change
beyond an additive `durationMs` field.

---

## Phase 2 — Per-output composition + layer index + Audio/Video tabs

> **Risk**: Medium · **Effort**: L · **Breaking**: ✗ for old files (new
> fields default to sensible values); ✗ for the framework (consumes
> existing primitives).
>
> Goal: introduce the "output binding with composition" model the operator
> asked for, plus the LayerIndex on display cues, and split the Audio tab
> into real per-output audio configuration (device + channel routing).

### 2.1 Output binding model on `CueList`

- ✅ Added `CueOutputBinding` record (flat shape — audio + video fields in
  one record, kind-gated by the `Kind` enum). Decided against a polymorphic
  Audio/Video split because JSON polymorphism with source generators is
  fussy and the binding is small; the cost is two dead-when-not-used
  fields per record.
- ✅ Audio fields: `string AudioDeviceId`, `int AudioChannelCount`.
- ✅ Video fields: `string VideoOutputId`, `int VideoWidth`,
  `int VideoHeight`, `int VideoFrameRateNum`, `int VideoFrameRateDen`.
  (Skipped `PlayerOutputPreset` — operator picks W/H/fps directly; presets
  can come later as a UI affordance over the same fields.)
- ✅ `CueList.Outputs : List<CueOutputBinding>` added — old per-list
  `VirtualOutputs` kept additively; both round-trip. Phase 3 will
  migrate routes onto bindings + drop `VirtualOutputs`.
- ✅ `CueListJsonContext` registers `CueOutputBinding`.

### 2.2 LayerIndex on display cues

- ✅ `MediaCueNode.LayerIndex : int` (default 0). Higher = on top.
- ✅ `MediaCueNode.LayerPosition` — new `CueLayerPosition` enum
  (`Cover | Letterbox | Center | FillWidth | FillHeight`) — operator-friendly
  presets that map onto the framework's abstract `LayerPosition` records at
  execution time.
- ✅ `MediaCueNode.Opacity : double` clamped to [0,1] on persist.
- ✅ Mirrored on `CueNodeViewModel` as `[ObservableProperty]` fields.
- ✅ Video tab in drawer: NumericUpDown for layer index, ComboBox for
  position, Slider for opacity.

### 2.3 Real Audio tab

- ✅ Cue-level "active outputs" multi-select: `AudioOutputAssignments`
  rebuilds from the cue list's audio bindings whenever selection changes;
  each checkbox writes `MediaCueNode.AudioOutputIds`.
- ✅ `CueRouteConnectionOverride.OutputId` added — defaults to
  `Guid.Empty` for legacy compatibility. `CueRouteConnectionViewModel`
  surfaces it; the Audio tab's route grid keeps the existing channel /
  gain / mute editors. Per-route output picker UI is a Phase 3 follow-up
  once legacy `VirtualOutputs` is migrated out.
- ✅ Virtual outputs + route connections grids retained inside the Audio
  tab below the new assignment checklist.
- ⏸ Polishing the audio matrix to be fully output-aware (one route grid
  per binding, gain matrix) deferred to Phase 3 alongside the
  `VirtualOutputs` migration — would otherwise force a synchronized
  data-model change that's larger than this phase.

### 2.4 Real Video tab

- ✅ Cue-level active video outputs multi-select via
  `VideoOutputAssignments` ↔ `MediaCueNode.VideoOutputIds`.
- ✅ LayerIndex + LayerPosition + opacity controls in the Video tab.
- ✅ Each assignment checkbox shows its binding's `CompositionLabel`
  (`1920×1080 @ 60fps` etc.) so the operator sees what they're targeting.

### 2.5 Output binding management UI

- ✅ Replaced the planned modal dialog with an inline Expander above the
  cue tree titled "Output bindings". Each row is the binding (kind label,
  name TextBox, composition fields visible by kind via `IsAudio` /
  `IsVideo`). Buttons: `+ Video`, `+ Audio`, `Remove`. Removing a binding
  cascades — strips its id from every cue's `AudioOutputIds` /
  `VideoOutputIds` and clears any route `OutputId` that pointed at it.
- ⏸ Device pickers (PortAudio device dropdown, NDI/Local target dropdown)
  are still raw strings. Wiring them to the existing `OutputManagementView`
  pickers is a Phase 3 polish — for now the model holds raw ids so a Phase 2
  cue file is forward-compatible.

### 2.6 Verification

- ✅ `dotnet build UI/HaPlay/HaPlay.csproj` clean — 0 warnings, 0 errors.
- ✅ `dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj` — 81/81 pass
  (covers VirtualOutput round-trip and cue persistence; new fields default
  cleanly so existing tests are unaffected).
- 🔲 **Manual run (operator required)**: verify on the Avalonia GUI:
  1. New "Output bindings" expander above the cue tree — `+ Video` /
     `+ Audio` add rows; W/H/fps editable for video, channel count for
     audio.
  2. Video tab on a media cue: layer index spinner, position picker,
     opacity slider, and the list of video bindings with checkboxes.
  3. Audio tab on a media cue: list of audio bindings with checkboxes
     above the existing virtual-outputs + routes grids.
  4. Saving + reloading a cue list preserves bindings, layer index,
     opacity, and the audio/video output id selections.
- ⏸ "Old file → defaults to 1080p60 + stereo with route migration" — this
  is migration logic; deferred to Phase 3.1 as scoped in the doc.

**Phase 2 exit criteria**: cue lists own their output bindings (audio +
video) with composition, display cues carry a layer index, the drawer
tabs are scope-correct, no more references to "virtual outputs" in the
operator-facing surface.

---

## Phase 3 — Migration, cleanup, docs

> **Risk**: Low · **Effort**: S · **Breaking**: ✗ (read-side migration
> only, write-side already on new schema after Phase 2).

### 3.1 Migration

- ✅ `CueListIO.LoadAsync` calls `MigrateIfNeeded`: when `Outputs` is empty
  and the file is `HaPlayCueList/v1` (or unversioned), synthesise a
  default 1080p60 "Program" video binding and a stereo "Main" audio
  binding. Per-cue id lists stay empty so the operator opts in.
- ✅ `CueList.Schema` default bumped to `HaPlayCueList/v2`; the migration
  rewrites the schema string on load.
- ⏸ Auto-attaching legacy `routeConnections` to the new audio binding
  (`OutputId = main audio binding id`) deferred — the existing route
  data still works in the legacy flat-channel mode, and migrating routes
  forces a design decision about how virtual outputs map to bindings
  that the operator may want to make manually. Routes default
  `OutputId = Guid.Empty` and the audio matrix UX keeps working.

### 3.2 Code cleanup

- ⏸ `CueVirtualOutputChannel` and `CueVirtualOutputChannelViewModel`
  retained — `CuePlayerViewModelTests` still exercises the
  add/remove/duplicate-resolve behaviour, and deleting the type forces
  a route-migration design that's bigger than this checklist. Tracked
  for a future iteration when the audio matrix UX is rewritten
  output-aware.
- ✅ Removed dead string resources from `Strings.resx` +
  `Strings.cs`: `CueTreeTriggerColumnHeader`, `CueTreePreMsColumnHeader`,
  `CueTreeSourceActionColumnHeader`, `CueTreeEndpointIdColumnHeader`,
  `CueTreeExtraColumnHeader`, `CueTreeNotesColumnHeader`,
  `VideoTabPhase2Placeholder`, `SelectMediaCueHint`,
  `CueConfigDrawerHeader`.
- ✅ Right-side TreeDataGrid plumbing already removed in Phase 1.3 —
  `CuePlayerView.axaml.cs` only holds the cue tree + the (Audio-tab)
  virtual-outputs + routes wiring.

### 3.3 Docs

- ⏸ `Doc/MediaFramework-Architecture.md` Cue Player section refresh —
  deferred; current architecture doc still describes the per-list
  virtual-output model. Will update alongside the eventual output-aware
  audio matrix UX.
- ✅ No framework-side promotion was needed — the new model lives
  entirely in `UI/HaPlay/`; the framework checklist is unaffected.
- ✅ This document tracks Phase 3 as **partially shipped**: migration +
  resource cleanup landed; legacy-type deletion + architecture-doc
  refresh deferred.

### 3.4 Verification

- ✅ `dotnet build` clean.
- ✅ `dotnet test UI/HaPlay.Tests` — 81/81 pass.
- 🔲 **Manual run (operator required)**: load a pre-Phase-2 saved
  `.haplaycues` (or a freshly-written file from before this work) →
  verify the new "Output bindings" expander shows the auto-created
  Program video + Main audio bindings without operator intervention.

**Phase 3 status**: migration + resource cleanup shipped. Legacy
virtual-output deletion + architecture-doc refresh deferred to a future
iteration that rewrites the audio matrix output-aware. NumericUpDown
gained a global `MinWidth=120` style in `App.axaml` (operator feedback —
the up/down buttons left too little room for the value).

---

## Phase 4 — Cue Player as an independent playback surface

> **Risk**: High · **Effort**: L · **Breaking**: ✓ (saved `.haplaycues`
> files load with empty new fields; operator re-creates outputs /
> compositions / routes — see 4.0)
>
> Goal: stop borrowing the selected MediaPlayer tab's outputs. The Cue
> Player becomes a first-class playback surface with its own engine, its
> own output registry, and its own compositions. The "virtual output
> channels" concept is removed entirely — audio routes pick a device
> output channel directly.

### Architecture summary

- **CueComposition** — virtual canvas (W × H + framerate). Not bound to
  an output.
- **CueVideoOutput** — id + name + target (Local window / NDI) +
  composition id. Many outputs can reference the same composition (the
  composition is rendered once and fanned out).
- **CueAudioOutput** — id + name + device (host API + device name) +
  channel count.
- **MediaCue → Audio tab** — list of `CueAudioRoute` rows: source channel
  → (audio output, device channel #, gain, mute). No virtual outputs.
- **MediaCue → Video tab** — list of `CueVideoPlacement` rows: per
  composition, "appears in" + layer index + position + opacity.
- **CuePlaybackEngine** — owned by `CuePlayerViewModel`. Opens decoders,
  drives an `AudioRouter` + a `VideoCompositor` per composition, fans
  composed frames to the cue list's video outputs and channel-mapped
  audio to its audio outputs. Completely independent of every
  MediaPlayer tab.

### 4.0 Operator-decided posture (this iteration)

- Output registry is **wholly separate** from `OutputManagementView`.
  The Cue Player owns its own list of audio + video outputs. Device
  pickers are duplicated in the cue-player config UI (acceptable cost).
- Compositions + outputs are configured **inline in the Cue Player
  tab** via collapsible expanders at the top.
- **No migration** — pre-Phase-4 `.haplaycues` files load with empty
  output / composition lists; the operator sets up routing fresh. Old
  schema fields (`virtualOutputs`, `routeConnections`, layer index on
  the cue, `outputs` from Phase 2) still deserialize so loading doesn't
  crash; they're ignored.

### 4.1 Model rewrite — ✅

- ✅ Added records: `CueComposition`, `CueAudioOutput`, `CueVideoOutput`,
  `CueAudioRoute`, `CueVideoPlacement`. Added enum `CueVideoOutputKind`
  (`LocalWindow | Ndi`).
- ✅ `CueList.Compositions`, `CueList.AudioOutputs`,
  `CueList.VideoOutputs` replace Phase 2's flat `Outputs` list and Phase 1's
  `VirtualOutputs`.
- ✅ `MediaCueNode.AudioRoutes` + `MediaCueNode.VideoPlacements` replace
  Phase 2's `AudioOutputIds` / `VideoOutputIds` / on-node `LayerIndex` /
  `Position` / `Opacity` and Phase 1's `VirtualOutputChannels` /
  `RouteConnections`.
- ✅ `CueList.Schema` bumped to `HaPlayCueList/v3`.
- ✅ All new types registered in `CueListJsonContext` and
  `HaPlayProjectJsonContext`.
- ✅ Deleted old types: `CueVirtualOutputChannel`,
  `CueRouteConnectionOverride`, `CueOutputBinding`, `CueOutputKind`.

### 4.2 ViewModel rewrite — ✅

- ✅ New VMs added: `CueCompositionViewModel`, `CueAudioOutputViewModel`,
  `CueVideoOutputViewModel`, `CueAudioRouteViewModel`,
  `CueVideoPlacementViewModel`.
- ✅ Deleted: `CueOutputBindingViewModel`,
  `CueOutputAssignmentViewModel`, `CueVirtualOutputChannelViewModel`,
  `CueRouteConnectionViewModel`. Deleted the assignment-checklist
  refresh machinery + virtual-output collision normalisation entirely.
- ✅ `Selected*` properties for the new types added; commands renamed
  (`AddComposition` / `AddAudioOutput` / `AddVideoOutput` /
  `AddAudioRoute` / `AddVideoPlacement` + `Remove*`).

### 4.3 Cue Player tab — inline config expanders — ✅

- ✅ Phase 2's "Output bindings" expander removed.
- ✅ Three inline expanders above the cue tree:
  - **Compositions**: rows of (Name, W, H, fps) — editable inline; +/− buttons.
  - **Audio outputs**: rows of (Name, Host API, Device, Channels) — text
    fields with watermarks; +/− buttons. Device pickers stay as plain
    `TextBox`es for now; wiring them to the PortAudio enumeration helper
    is a follow-up polish, not blocking.
  - **Video outputs**: rows of (Name, Kind dropdown, Target name,
    Composition dropdown) — kind is `LocalWindow | Ndi`; composition
    references one of the cue list's compositions.

### 4.4 Drawer Audio tab — direct channel routing — ✅

- ✅ Assignment checklist + virtual-outputs grid + routes grid all
  removed. Single Routes ListBox: per row,
  **Src ch | Output dropdown | Out ch | Gain dB | Mute**.
- ✅ Add/remove buttons wired (`AddAudioRouteCommand`,
  `RemoveAudioRouteCommand`).
- ⏸ "Auto-fill from source" — deferred. Operator adds routes manually for
  now; auto-detect of channel count via `MediaContainerDecoder` lands
  alongside the Phase 4.6 playback engine.

### 4.5 Drawer Video tab — per-composition placements — ✅

- ✅ ListBox of placements: **Composition dropdown | Layer | Position |
  Opacity**. Add/remove buttons (`AddVideoPlacementCommand`,
  `RemoveVideoPlacementCommand`).
- ✅ Composition dropdown's display uses `DisplayName` (name + summary
  like `Program (1920×1080 @ 60fps)`), so the operator sees the spec
  inline.

### 4.6 Playback engine — ✅ (v1)

- ✅ `Playback/CuePlaybackEngine.cs` — owns one active
  `HaPlayPlaybackSession` at a time. Drives playback against the output
  lines the cue itself references (resolved from its `AudioRoutes` and,
  via the cue list's `VideoOutputs`, its `VideoPlacements`).
- ✅ `CuePlayerViewModel.MediaCueExecutor` rewired to
  `_cuePlaybackEngine.ExecuteAsync` in `MainViewModel`'s constructor.
- ✅ `CuePlayerViewModel.StopPlaybackCallback` added; `Stop` and `Panic`
  forward to `_cuePlaybackEngine.StopAsync`, tearing down the session.
- ✅ `MainViewModel.ExecuteCueMediaAsync` deleted — no more
  `SelectedPlayer`-based wiring. The Cue Player and MediaPlayer tabs are
  fully independent at the execution layer.
- ✅ Natural-end watcher: a background loop polls `PlayClock.CurrentPosition`
  against `Duration`; raises `NaturalEnd` on the UI thread when the file
  finishes. `MainViewModel` forwards that to
  `CuePlayer.OnMediaCueNaturallyEndedAsync` so AutoFollow keeps working.

**Scope notes (v1 — operator-visible)**

- File sources (anything via `MediaContainerDecoder`) play end-to-end.
  Audio + video both route to whichever output lines the cue's routes /
  placements reference.
- NDI input and PortAudio input cues use the same code path (the
  underlying `HaPlayPlaybackSession.TryCreate` supports them) but
  haven't been smoke-tested through the cue pipeline yet.
- Per-source-channel routing in `CueAudioRoute` is **not yet honored**
  by the audio router — `HaPlayPlaybackSession` currently sends source
  audio to the output line as a whole. Output-channel + gain selection
  per route is a v2 follow-up that will hand a real `ChannelMap` to
  `AudioRouter.AddRoute`.
- Compositor layering with multiple cues sharing a composition isn't
  wired — each cue's video goes to the output lines the placements
  reference, but they don't yet stack via `VideoCompositor`. Single-cue
  video works.
- `MediaPlayerViewModel.ApplyCueRouteOverrides` stays as a no-op stub
  (kept the signature; the cue-side path no longer calls it).

### 4.7 Tests — ✅

- ✅ Deleted the three VirtualOutput tests in `CuePlayerViewModelTests`.
- ✅ Added: `RemovingAudioOutput_PrunesRoutesThatTargetedIt`,
  `RemovingComposition_PrunesPlacementsAndClearsVideoOutputRefs`,
  `V3RoundTrip_PreservesCompositionsOutputsRoutesAndPlacements`.
- ✅ `AddRouteButton_Click_AddsRouteToSelectedMediaCue` rewritten to
  `AddAudioRouteButton_Click_AddsRouteToSelectedMediaCue` (uses the new
  audio output + audio route path).
- ✅ `RoundTrip_ProjectCueList_PreservesCueNodeTypes` migrated to
  compositions + audio outputs + audio routes + video placements.

### 4.8 Verification — ✅ (model + UI)

- ✅ `dotnet build UI/HaPlay/HaPlay.csproj` — 0 warnings, 0 errors.
- ✅ `dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj` — 81/81 pass.
- 🔲 **Manual run (operator required)**: build & run the desktop app;
  configure compositions + outputs inline, add a cue, set routes +
  placements, save + reload. Cue Player is now visually independent
  of MediaPlayer tabs.
- ⏸ End-to-end playback validation gates on Phase 4.6
  (`CuePlaybackEngine`) — the executor still calls into the selected
  MediaPlayer tab but with a stubbed `ApplyCueRouteOverrides` so no
  cross-talk happens. Real audio + video routing through the cue
  player's own engine is the next session's work.

### 4.9 Reuse output registry + layout polish — ✅

> Operator feedback after 4.1–4.5 landed: separate device config in the
> cue tab was error-prone; expander stacks fought with the cue tree for
> space.

- ✅ Output registry is now **shared with `OutputManagementView`** —
  walked back the "wholly separate" call from §4.0. `CuePlayerViewModel`
  exposes `AvailableAudioOutputs` + `AvailableVideoOutputs` populated
  by `MainViewModel` from `OutputManagement.Outputs`. Live updates —
  adding/removing an output line flows straight through.
- ✅ **Dropped `CueAudioOutput` entirely.** Audio routes reference
  output lines directly via `CueAudioRoute.OutputLineId` (matches
  `OutputDefinition.Id`).
- ✅ **`CueVideoOutput` → `CueVideoOutputBinding`** — trimmed to
  `{ Id, OutputLineId, CompositionId }`. Name / Kind / TargetName come
  from the referenced output definition; no duplicate config.
- ✅ Audio outputs expander removed from the cue tab. The route dropdown
  picks any PortAudio output line directly.
- ✅ Video outputs expander now picks `(Output line, Composition)` pairs.
- ✅ Expanders gained a `dense` class (App.axaml style) — 24px header
  height, 6×2 padding.
- ✅ Cue tree `MinHeight` removed — tree now yields space cleanly when
  expanders open.
- ✅ Tests updated: dropped the audio-output cascade test (concept gone);
  added `RemovingComposition_PrunesPlacementsAndVideoOutputBindings`;
  renamed round-trip test to `V3RoundTrip_PreservesCompositionsVideoBindingsRoutesAndPlacements`.
- ✅ Build clean. `dotnet test UI/HaPlay.Tests` — 80/80 pass.
