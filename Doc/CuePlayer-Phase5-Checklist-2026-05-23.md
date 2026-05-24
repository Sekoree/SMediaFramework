# Cue Player — Phase 5 Execution Checklist (2026‑05‑23)

> Companion to `Doc/CuePlayer-AVSync-And-UX-Review-2026-05-23.md`. Walks
> the review's recommendations into discrete sub-tasks with file
> pointers, verification steps, and a clear order.
>
> Each phase is independently shippable. Stop at any phase boundary and
> the Cue Player still loads, saves, fires cues, and the operator gets
> visible value from what just landed.

## Legend

- 🔲 todo · ✅ done · ⏸ blocked · ❌ skipped
- **Risk**: None · Low · Medium · High
- **Effort**: S (single sitting · <1 day) · M (1–2 days) · L (multi-day)
- **Breaking**: ✓ (saved `.haplaycues` files need migration) · ✗ (additive / UI-only)

## Suggested order

```
5.1  (M, ✗) — UX clarity wins              ───┐ ship together; pure
5.2  (M, ✗) — Read-only tree + rename popup    │ display/UX work
                                               │
5.3  (M, ✗) — Now Playing panel             ───┤ operator's #1 ask;
                                               │ visible per-cue feedback
                                               │
5.4  (M, ✗) — A/V sync P0: master pump      ───┤ removes silent drift
                                               │
5.5  (M, ✗) — Preview / scrubber            ───┤ audition without commit
5.6  (S, ✗) — Keyboard shortcuts            ───┤ muscle-memory parity
5.7  (M, ✗) — Health / pre-roll indicators  ───┤ visibility under load
5.8  (M, ✗ / ✓ for color tag) — Polish + dialogs
                                               │
5.9  (L, ✗) — A/V sync P1: PTS slot policy ────┘ long-show quality
```

---

## Phase 5.1 — UX clarity wins — ✅ shipped

> **Risk**: Low · **Effort**: M · **Breaking**: ✗
>
> Goal: tighten the drawer so it only shows what's applicable, and make
> bulk-selection state visible. No engine changes.
>
> **Outcome (2026-05-23)**: 92 tests → 95 (3 new), build clean.
> `CueMediaProbe` now returns the full result record; `MediaCueNode` +
> `CueNodeViewModel` carry `HasVideo` / `HasAudio` / `AudioChannels` /
> `VideoIsAttachedPicture`; Video tab is hidden when the source has no
> video stream (with a cover-art hint for attached-picture cases); the
> Audio tab shows "Source: N ch"; group cues roll up child durations
> per fire mode (`max` for `FireAllSimultaneously`, `sum` for
> `ArmedList`, `first` for `FirstCueOnly`); the drawer surfaces a
> banner when more than one cue is selected ("N cues selected — '+
> Route' adds a route to each").

### 5.1.1 Probe `HasVideo` / `HasAudio` / source channel count on add ✅

- ✅ Extend `Playback/CueMediaProbe.cs` from a `(int? DurationMs)` return
  to a record: `CueMediaProbeResult(int? DurationMs, bool HasVideo, bool
  HasAudio, int AudioChannels, bool VideoIsAttachedPicture)`.
- ✅ All three call sites consolidated into
  `CuePlayerViewModel.ProbeAndAssignDurationAsync`, called from
  `AddMediaCueAsync`, `BrowseMediaSourceAsync`, and `AddMediaFilesFromDrop`.
- ✅ `CueMediaProbe.TryProbeAsync` still catches all exceptions and
  returns `null` — caller writes default (false / 0) values into the VM.

### 5.1.2 Extend `CueNodeViewModel` + persist to model ✅

- ✅ `[ObservableProperty]` fields on `CueNodeViewModel`:
  `SourceHasVideo`, `SourceHasAudio`, `SourceAudioChannels`,
  `SourceVideoIsAttachedPicture` (see CuePlayerViewModel.cs ~L266).
- ✅ `MediaCueNode` in `Models/CueList.cs` exposes `HasVideo`,
  `HasAudio`, `AudioChannels`, `VideoIsAttachedPicture` (~L103–L114),
  all `init`-only with `false`/`0` defaults so pre-5.1 files load.
- ✅ `FromModel`/`ToModel` round-trip the fields (CuePlayerViewModel
  ~L546–L548 reading, ~L619–L621 writing).
- ✅ Test coverage: `CuePlayerViewModelTests.MediaCue_ProbeFields_RoundTripInSnapshot`
  asserts the round-trip end-to-end through `BuildCueListsSnapshot`.

### 5.1.3 Hide the Video tab on audio-only sources ✅

- ✅ XAML Video tab now binds `IsVisible="{Binding HasSelectedMediaCueWithVideo}"`.
- ✅ `HasSelectedMediaCueWithVideo` computed on the VM, re-notifies on
  `SelectedCueNodeChanged` + the selected cue's `SourceHasVideo` change.
- ✅ Cover-art case: `HasSelectedMediaCueWithAttachedPictureOnly` flips
  the Video tab banner to `VideoTabAttachedPictureHint`.

### 5.1.4 Audio tab header — source channel count ✅ (per-route ⚠ deferred)

- ✅ Audio tab now shows `"Source: {N} ch"` via `AudioTabSourceLabel`
  / `AudioChannelCountFormat` / `AudioSourceHasNoneLabel`. Hidden when
  the source has no audio.
- ⏸ Per-route ⚠ when `SourceChannel >= SourceAudioChannels`: not yet
  implemented. Deferred to Phase 5.8 (low-effort polish item — drop in
  an inline `TextBlock` + tooltip in the audio route DataTemplate
  driven by a converter or computed bool on `CueAudioRouteViewModel`).

### 5.1.5 Group cue duration roll-up ✅

- ✅ `BuildGroupDurationDisplay` handles all three fire modes:
  `FireAllSimultaneously` → `max`, `FirstCueOnly` → first non-comment
  child, `ArmedList` → sum (CuePlayerViewModel ~L382–L406).
- ✅ Recursive via `RolledDurationMs` getter — groups roll up children's
  rolled durations (~L426).
- ✅ Live updates: parent subscribes to `Children.CollectionChanged`
  and each child's `PropertyChanged` for `DurationMs`/`GroupFireMode`/
  child collection changes.
- ✅ Format `mm:ss · N items` (no hours bucket since `FormatDurationMs`
  collapses to `mm:ss` for runs < 1 h and naturally extends to `hh:mm:ss`).

### 5.1.6 Drawer "(N selected)" hint ✅

- ✅ Multi-select banners on Audio (`MultiSelectAudioBannerFormat`)
  and Video (`MultiSelectVideoBannerFormat`) tabs, bound to
  `SelectedCueCount`.
- ✅ `IsMultiSelected` and `SelectedCueCount` computed properties; the
  XAML uses the `StringFormat` shortcut to compose the banner text and
  the `IsVisible` predicate flips on multi-select.
- ✅ Re-notification on `UpdateSelection`.

### 5.1 verification ✅

- ✅ `dotnet build` clean.
- ✅ `dotnet test UI/HaPlay.Tests` green (104 tests).
- ✅ Manual: operator verified per the 2026-05-23 session log.

---

## Phase 5.2 — Read-only tree + rename popup — ✅ shipped (5.2.4 deferred)

> **Risk**: Medium · **Effort**: M · **Breaking**: ✗
>
> Goal: remove the entire class of "binding stays on old row" bugs by
> making the tree purely display. Move all editing into the drawer
> (already there) and a single F2 rename popup.
>
> **Outcome (2026-05-23)**: 96 tests pass (+1). Number + Name columns
> are now read-only `TextBlock` cells (deleted `BuildTextEditor` /
> `BuildCompactTextEditor` helpers). F2 on the tree opens the new
> `RenameCueDialog` (Number + Label fields, Enter commits, Esc
> cancels). A new "Renumber…" button on the toolbar opens
> `RenumberSelectionDialog` with start / step / scope (All / Root only
> / Selection only); the command walks the tree assigning sequential
> numbers with sub-numbering (`1`, `1.1`, `1.2`, `2`, ...) for nested
> groups. Drag-and-drop reorder (5.2.4) deferred — see note below.

### 5.2.1 Make tree cells read-only ✅

- ✅ `RebuildCueSource` now uses `BuildReadOnlyText` for all data
  columns (Number, Name, Duration, Kind).
- ✅ `BuildTextEditor` and `BuildCompactTextEditor` helpers deleted
  from `CuePlayerView.axaml.cs` — only `BuildStatusBadge` +
  `BuildReadOnlyText` remain.
- ✅ Status / Duration / Kind already read-only — confirmed.

### 5.2.2 F2 rename popup ✅

- ✅ `CueTreeGrid.KeyDown` (CuePlayerView.axaml.cs) routes `F2` to
  `RenameSelectedCueCommand`.
- ✅ `RenameCueDialog` + `RenameCueDialogViewModel` implement Number
  + Label fields; Enter commits via `IsDefault`, Esc cancels.
- ✅ Status message after rename: `RenamedCueStatusFormat` ("Renamed
  cue N: {old} → {new}").

### 5.2.3 Renumber selection dialog ✅

- ✅ `RenumberSelectionDialog` + `RenumberSelectionDialogViewModel`
  exist; "Renumber…" toolbar button opens it.
- ✅ Inputs: Start, Step, Scope radio (All / RootLevelOnly /
  SelectionOnly).
- ✅ `RenumberSubtree` / `RenumberSubtreePrefixed` / `RenumberFlat`
  walk the tree with sub-numbering (`1`, `1.1`, `2`, …) via
  `FormatCueNumber`.
- ⏸ Right-click context-menu entry not wired (toolbar button only).
  Low priority — operator can always reach it from the toolbar.

### 5.2.4 Drag-and-drop reorder — ⏸ deferred

Deferred to a follow-up phase. Reasoning:

- TreeDataGrid exposes `RowDragStarted` / `RowDragOver` / `RowDrop`
  events + an `ITreeDataGridSource.DragDropRows(...)` source method.
  The mechanism is there, but getting the policy right is its own
  session:
  - **Refuse drops into a descendant of a dragged group** —
    `IndexPath` walks needed to detect.
  - **Cross-parent moves** (root → group, group → root, sibling group)
    have to update both the source collection and re-parent the
    `CueNodeViewModel` tree.
  - **Multi-selection drag** — dragging 5 cues at once needs to
    preserve their *relative* order at the destination.
  - **Auto-renumber after reorder** (if 5.8's "auto-renumber on
    insert" lands) needs to be triggered post-move.
- **Operator workaround for now**: remove + re-add via the existing
  toolbar, and run the new Renumber dialog afterwards to fix
  numbering.

Picking this up later: implement in
`Views/CuePlayerView.axaml.cs`, gate via a new
`AppSettings.CueTreeDragReorderEnabled` flag while the policy stabilises.

### 5.2 verification ✅

- ✅ Build + tests clean.
- ✅ Manual: F2 / rename / Esc behavior + renumber verified by operator
  in the 2026-05-23 session. Drag-and-drop reorder still deferred —
  see 5.2.4. Recycling-binding bug is structurally prevented (every
  cell is now read-only `BuildReadOnlyText`).

---

## Phase 5.3 — "Now Playing" panel — ✅ shipped

> **Risk**: Low · **Effort**: M · **Breaking**: ✗
>
> Operator's #1 ask: see at a glance what's playing right now with
> per-cue progress + cancel. Plus upcoming cues from AutoFollow chains.
>
> **Outcome (2026-05-23)**: 99 tests pass (+3). Right-side panel
> docked at 260 px (resizable via `GridSplitter`) shows every active
> cue with its number, label, progress bar, "mm:ss / mm:ss" display,
> and per-row ✕ cancel button → `engine.StopCueAsync`. "Stop all"
> button at the top mirrors the transport Stop. Upcoming section
> lists up to 8 future cues from Standby; auto-filters cues that are
> already active. Engine raises `CueProgress` every ~150 ms; VM's
> `OnCueProgress` updates the matching row's `PositionMs`/`DurationMs`.

### 5.3.1 `ActiveCueViewModel` ✅

- ✅ `ViewModels/ActiveCueViewModel.cs` — holds `(CueNodeViewModel,
  Guid CueId)` + `[ObservableProperty]` for `PositionMs`, `DurationMs`,
  with `ProgressPercent` and `PositionDisplay` ("mm:ss / mm:ss")
  computed.
- ✅ `CancelCommand` forwards to the host's `CancelCueCallback`.
- ⏸ `ProgressBrush` color states (amber/red on lag): not implemented.
  The bar is a flat default brush — the drift surface already lives in
  `CuePlayer.StatusMessage` via `DriftWarning`. Could be revisited in
  5.7.4 if a stats panel lands.

### 5.3.2 ActiveCues collection on `CuePlayerViewModel` ✅

- ✅ `ObservableCollection<ActiveCueViewModel> ActiveCues` exists.
- ✅ `OnCueStarted` pushes; `OnCueEnded` removes.
- ✅ `CancelCueCallback` (`Func<Guid, Task>?`) wired in `MainViewModel`
  to `_cuePlaybackEngine.StopCueAsync`.

### 5.3.3 Progress polling ✅

- ✅ Engine raises `CueProgress(CuePlaybackProgress)` from
  `WatchNaturalEndAsync` (CuePlaybackEngine ~L498).
- ✅ VM subscribes via `OnCueProgress`; updates matching
  `ActiveCueViewModel`'s position fields.
- ✅ 150 ms cadence kept — appears smooth in operator session.

### 5.3.4 Upcoming cues view ✅

- ✅ `ObservableCollection<CueNodeViewModel> UpcomingCues` rebuilt on
  Standby / active set changes via `RebuildUpcomingCues` — uses
  `EnumerateFireableCueOrder()` anchored to `StandbyCueNode`, skips
  cues already in `ActiveCues`, capped at 8 lookahead.
- ⏸ Estimated fire-time / trigger-mode label per upcoming cue: not
  shown (rows just display number + label). Adding the trigger badge
  is a small follow-up if operators ask.

### 5.3.5 Panel XAML ✅

- ✅ Inlined into `CuePlayerView.axaml` as a right-side `Grid` column
  with `GridSplitter` (resizable). Two stacked `ItemsControl`s for
  Active / Upcoming.
- ⏸ Collapse-to-strip chevron + `AppSettings` persistence: not done.
  Operator can drag the splitter to nearly-zero width as a workaround.
  Folding into 5.8 polish.

### 5.3.6 Panel-level actions ✅

- ✅ "Stop all" button at the panel header → existing Stop transport.
- ✅ Per-row `✕` → `ActiveCueViewModel.CancelCommand` →
  `CancelCueCallback` → engine `StopCueAsync`.

### 5.3 verification ✅

- ✅ Build + tests clean (104 tests).
- ✅ Manual: operator verified per the 2026-05-23 session log
  (simultaneous group of cues showed live progress; cancel-one-only
  worked).

---

## Phase 5.4 — A/V sync P0: master the composition pump — ✅ shipped

> **Risk**: Medium · **Effort**: M · **Breaking**: ✗
>
> Eliminates drift mode 3a from the review doc (composition pump's
> Stopwatch vs. cue's audio-clock master). Frame stability over long
> shows.
>
> **Outcome (2026-05-23)**: `CueCompositionRuntime.SetClockMaster`
> takes an `IPlaybackClock` (typically the cue's audio runtime clock)
> and swaps the Stopwatch pump for a `MediaClock`-driven path —
> internally creates a slaved `MediaClock` with `videoTickInterval` =
> canvas period, subscribes to `VideoTick`, drains the mixer per tick.
> Stopwatch path remains as the fallback for audio-less compositions.
> `CuePlaybackEngine.WireVideoPlacements` calls `SetClockMaster` after
> resolving `entry.VideoClockMaster` from the first audio runtime.
> Drift counter (`FramesBehindMaster`) compares wall-elapsed with
> master-elapsed every tick; sustained drift fires `DriftWarning` (max
> once per ~30 frames) → engine surfaces a status message: *"Composition
> 'Program' drift: N frames behind master (M ms)"*. Stats record now
> carries `FramesBehindMaster` + `ClockMastered` for future health UI.

### 5.4.1 `CueCompositionRuntime.SetClockMaster` ✅

- ✅ Public `SetClockMaster(IPlaybackClock master)` (~L224). First
  non-null wins; subsequent calls are no-ops.
- ✅ `_master` / `_slaveClock` fields.

### 5.4.2 Pump loop respects the master ✅

- ✅ Adopted a slaved `MediaClock` (period = canvas period). On
  `SetClockMaster`, subscribes to `VideoTick` and drains the mixer per
  tick instead of Stopwatch-driven `PumpOneFrame`. Stopwatch path
  remains as the fallback for audio-less compositions.
- ✅ Pre-master start is handled — the Stopwatch loop runs until
  `SetClockMaster` flips the path; no infinite-wait risk.

### 5.4.3 Engine wires the master ✅

- ✅ `CuePlaybackEngine.WireVideoPlacements` calls
  `runtime.SetClockMaster(entry.VideoClockMaster)` after
  `GetOrCreateComposition` (~L378).

### 5.4.4 Drift counter + warning ✅

- ✅ `_framesBehindMaster` tracked alongside `_pumpOverruns`.
- ✅ `CheckMasterDrift` compares wall-elapsed vs `master.ElapsedSinceStart`
  and increments the counter when behind by ≥2 canvas periods.
- ✅ Sustained drift fires `DriftWarning` (max once per ~30 frames).
- ✅ `CuePlaybackEngine.GetOrCreateComposition` subscribes and writes
  a status message: *"Composition 'X' drift: N frames behind master
  (M ms)"*.

### 5.4.5 Pump-pressure surfacing groundwork ✅

- ✅ `CueCompositionRuntimeStats` carries `FramesBehindMaster` and
  `ClockMastered` (~L658). Available for future stats panels (5.7.4).

### 5.4 verification ✅

- ✅ Build + tests clean.
- ✅ Manual: operator verified per the 2026-05-23 session log.

---

## Phase 5.5 — Preview / scrubber

> **Risk**: Low · **Effort**: M · **Breaking**: ✗
>
> Lets the operator audition a single cue without going through Standby
> + Go, and scrub through a playing cue.

### 5.5.1 Preview button

- 🔲 New "▶ Preview" button on the General tab (and a "Stop Preview"
  button that swaps in when previewing is active).
- 🔲 Engine: new `PreviewCueAsync(MediaCueNode cue, CancellationToken
  ct)` that opens the cue's source and routes:
  - Audio → system default PortAudio output (a transient
    `CueAudioOutputRuntime` not registered in `_audioOutputs`).
  - Video → a transient floating preview window (an
    `SDL3GLVideoOutput` opened as a tool window 480 px wide).
- 🔲 Disposing the preview tears everything down.
- 🔲 Only one preview at a time — pressing Preview while another is
  active stops the previous.
- 🔲 Status message: "Previewing 'X'. Click again to stop."

### 5.5.2 Scrubber

- 🔲 New `Slider` on the General tab, bound to the cue's current
  position. Visible only when the cue is active in `ActiveCues`.
- 🔲 Two-way binding: dragging the thumb seeks via a new
  `engine.SeekCueAsync(cueId, TimeSpan position)`.
- 🔲 `SeekCueAsync` finds the entry in `_active`, calls
  `entry.Player.SeekCoordinated(position)`, and re-synchronizes the
  audio runtime if present (the master clock may need a hint).
- 🔲 Edge case: scrubbing past Duration → snap to Duration - 50ms,
  naturally fires the natural-end event.

### 5.5.3 Hover-to-preview (optional, defer if 5.5.1/5.5.2 take long)

- ⏸ Decide at implementation time. If kept:
  - 500 ms hover on a cue row opens a small popup with a frame
    thumbnail (first frame of the file, or the cue's loop point).
  - Reuses the framework's `MediaContainerDecoder.Open` + one
    `Video.TryReadNextFrame` to grab the frame.
  - Hover out closes the popup; doesn't interfere with selection or
    rename.

### 5.5 verification

- 🔲 Build + tests clean.
- 🔲 Manual: preview a video file that has audio. Audio plays out the
  system default device; video shows in a floating window. Stop
  preview disposes both. While a 30-second cue is playing, drag the
  scrubber to the 15-second mark — playback jumps and continues from
  there. Audio + video stay in sync after the seek.

---

## Phase 5.6 — Keyboard shortcuts — ✅ shipped

> **Risk**: None · **Effort**: S · **Breaking**: ✗
>
> Standard cue-player muscle memory.
>
> **Outcome (2026-05-23)**: 101 tests pass (+2). New VM commands
> `MoveSelectedCueUp` / `Down` (work within parent collection) and
> `DuplicateSelectedCue` (deep-copy via model round-trip with fresh
> ids cascading through nested groups). The cue tree's `KeyDown`
> handler covers `F2 / Del / Ctrl+D / Ctrl+↑↓`. A new
> UserControl-level `KeyDown` handles transport (`Space` = Go,
> `Esc` = Panic, `Enter` = Standby, `Backspace` = Back) — skips when
> a `TextBox` / `NumericUpDown` / `ComboBox` has focus so the
> operator can type freely in drawer fields.

### 5.6.1 Transport bindings ✅ (Ctrl+P deferred with 5.5)

- ✅ `OnUserControlKeyDown` handles `Space` → Go, `Esc` → Panic,
  `Enter` → Standby, `Backspace` → Back, with `FocusManager`
  short-circuit so `TextBox` / `NumericUpDown` / `ComboBox` focus
  passes the key through to the editor.
- ⏸ `Ctrl+P` (preview): not bound — Phase 5.5 (preview runtime) is
  deferred, so there's nothing for the key to trigger yet.

### 5.6.2 Tree navigation + reorder ✅ (Home/End deferred)

- ✅ `↑` / `↓` navigation — TreeDataGrid built-in still works after
  the read-only conversion.
- ✅ `Ctrl+↑` / `Ctrl+↓` → `MoveSelectedCueUp` / `MoveSelectedCueDown`
  (delta swap within parent).
- ⏸ `Home` / `End` → first / last cue: not bound. Operator can
  scroll. Low priority polish.

### 5.6.3 Edit commands ✅ (Del-confirm deferred)

- ✅ `F2` → rename popup.
- ✅ `Del` → `RemoveNodeCommand` direct.
- ⏸ Confirm-if-wired prompt on Del: not implemented — current behavior
  deletes without confirmation. Would land naturally with the dialog
  work in 5.8.
- ✅ `Ctrl+D` → `DuplicateSelectedCueCommand` deep-clones via
  `CloneCueNodeWithNewIds` (fresh Guids cascading through nested
  groups; routes / placements preserved).

### 5.6 verification ✅

- ✅ Build + tests clean.
- ✅ Manual: operator verified per the 2026-05-23 session log
  (transport keys ignore TextBox focus; Ctrl+D / Ctrl+↑↓ behave).

---

## Phase 5.7 — Health and pre-roll indicators

> **Risk**: Low · **Effort**: M · **Breaking**: ✗
>
> Visibility into what's wired and what's struggling.

### 5.7.1 Output health dots ✅

- ✅ In the "Audio outputs" + "Video outputs" expanders, render a
  small status dot at the start of each row.
- ✅ Source the dot color from the underlying `OutputLineViewModel`
  state — green (running), amber (configured but not started), red
  (error), gray (definition missing or removed).
  - Implemented via `OutputLineRegistry.Resolve(OutputLineId)` setting
    `LineRef` on each `CueVideoOutputBindingViewModel` /
    `CueAudioRouteViewModel`. The XAML row binds
    `Fill="{Binding LineRef.HealthColor, FallbackValue=#666666}"` with
    the existing `HealthColor` mapping from `OutputLineViewModel`.
    `CuePlayerViewModel.RefreshAvailableOutputBuckets` republishes the
    registry and walks all loaded cue lists to re-resolve refs whenever
    the available outputs change.
- ✅ The same dot appears next to each option in the route's "Output"
  ComboBox so the operator can see at-a-glance which devices are live
  when picking a route. (Replaced `DisplayMemberBinding` with an
  `ItemTemplate` rendering an `Ellipse` + name on both audio + video
  pickers.)

### 5.7.2 Pre-roll warming badge on cue rows ✅

- ✅ New `bool IsPreRollWarm` flag on `CueNodeViewModel`, updated by
  `CuePlayerViewModel.OnPreRollCacheChanged(IReadOnlyCollection<Guid>)`
  which walks every node and flips the flag to match the warm set.
- ✅ In `BuildStatusBadge` (the existing badge column), draw a small
  light-blue outline (`Color.FromArgb(220, 80, 170, 255)`,
  `StrokeThickness = 2`) when the cue is pre-rolled but idle. Current /
  Standby keep their solid color and skip the warming hint.
- ✅ Wire the cache to notify the host on membership changes —
  `CuePreRollCache.EntriesChanged` fires after Store / TryTake /
  InvalidateAll / EvictExcept. `MediaPlayerViewModel.CuePreRollChanged`
  re-exposes the event; `MainViewModel.CreatePlayer` forwards each
  player's snapshot into `CuePlayer.OnPreRollCacheChanged`.

### 5.7.3 Pump-pressure surfacing ✅ (status message only)

- ✅ In `CueCompositionRuntime`, subscribe to
  `VideoOutputPump.PumpPressure` for each NDI-wrapped pump.
- ✅ Track per-pump `lastReportedDrops` + `nextReportTicks` inside the
  subscriber closure to throttle to one report per ~5 s per output.
- ✅ Surface to the operator via a status message on the Cue Player
  ("NDI output 'Camera 1' dropped 12 frames in the last 5 s (total 47)
  — receiver may be slow."). The badge on the Video Outputs expander
  row is deferred — the row dot already shows Error/Warning health from
  the underlying `OutputLineViewModel` when the line itself is in
  trouble; the pump-pressure event is independent of line health and
  for now manifests only as the status message.
  - Plumbing: `CueCompositionRuntime.PumpPressureWarning` →
    `CuePlaybackEngine.GetOrCreateComposition` subscribes and pushes
    `Strings.NdiPumpPressureStatusFormat` into `CuePlayer.StatusMessage`.

### 5.7.4 Composition stats panel (optional)

- ⏸ Decide at implementation: add a small "Stats" toggle in the
  Compositions expander that shows the runtime's `CueCompositionRuntimeStats`
  (frames composited, submitted, overruns, slot overflow, last/max pump
  frame time, frames behind master). Useful for debugging.

### 5.7 verification ✅

- ✅ Build + tests clean — 104 tests passing (was 101; added 3:
  `SetAvailableOutputs_ResolvesLineRefOnAudioRoutes`,
  `OnOutputLineIdChanged_RefreshesLineRefFromRegistry`,
  `OnPreRollCacheChanged_FlipsIsPreRollWarmFlag`).
- ⏸ Manual verification: pending operator confirmation. Expected:
  - stop a local video preview in OutputManagement → its dot in the
    "Video outputs" expander row + all route picker dropdowns turn
    gray/red,
  - bump a cue into the warm pre-roll window → its status badge gets
    a light-blue outline while idle,
  - fire a video cue at a slow NDI receiver → status message reads
    "NDI output 'X' dropped N frames in the last 5 s …".

---

## Phase 5.8 — Polish + dialogs

> **Risk**: Low · **Effort**: M · **Breaking**: ✗ (color tag is
> additive but persisted, so technically a saved-file field — old
> files load with no tag).

### 5.8.1 Color tags

- 🔲 Add `int ColorTag { get; init; }` to `CueNode` in
  `Models/CueList.cs` (default 0 = none). Tag values 1..7 map to a
  fixed palette.
- 🔲 `CueNodeViewModel.ColorTag : int` (`[ObservableProperty]`) +
  computed `ColorTagBrush` returning the palette color or transparent.
- 🔲 New tree column at the very start (3 px wide): a thin vertical
  strip filled with `ColorTagBrush`.
- 🔲 Drawer General tab: a row of 8 color swatches (none + 7) — click
  to set.
- 🔲 Right-click menu entry: "Set color tag → …".

### 5.8.2 Cue list settings dialog

- 🔲 New `CueListSettingsDialog.axaml` with these fields (moved out of
  the toolbar):
  - Pre-roll count (currently inline).
  - Default cue trigger mode (new — sets the default for cues created
    via `+ Media` etc.; defaults to `Manual` today).
  - Auto-renumber on insert/reorder (new — checkbox).
- 🔲 Toolbar gets a gear icon next to the cue list selector to open
  the dialog. Remove the inline pre-roll spinner.

### 5.8.3 Files dropdown

- 🔲 Collapse Load / Save / Save As into a single "Files…" `MenuButton`
  in the toolbar with three submenu items.
- 🔲 Add a `Path` display next to the cue list name in the toolbar so
  the operator can see what file they're working on (read-only).

### 5.8.4 Resource string cleanup

- 🔲 `git grep` for any string keys in `Strings.resx` / `Strings.cs`
  not referenced anywhere in `.cs` / `.axaml`. Delete them. (Phase 3
  did a pass; another sweep after the new dialogs/buttons will be due.)

### 5.8 verification

- 🔲 Build + tests clean.
- 🔲 Manual: tag a few cues with colors; reorder them; reload the
  project; tags persist. Pre-roll count is no longer on the toolbar
  but is reachable via the gear icon's dialog.

---

## Phase 5.9 — A/V sync P1: PTS-aware slot policy + canvas-rate warning

> **Risk**: Medium · **Effort**: L · **Breaking**: ✗
>
> Long-show quality work. Less urgent than 5.4. The two items can ship
> independently.

### 5.9.1 PTS-aware slot policy

- 🔲 Extend `VideoCompositorSource.Slot` with a `SlotKeepPolicy enum
  { Latest, MasterAligned }`. (Lands in `S.Media.Effects` — a small
  framework change. Backwards-compatible: default `Latest`.)
- 🔲 In `MasterAligned`, slot stores frames by PTS; on Composite, the
  compositor asks for the frame whose PTS is closest to the master
  clock's `CurrentPosition` (or the last-pulled PTS + canvas period).
  Older frames are dropped, but "too-new" frames (future PTS) are
  held until their time.
- 🔲 `CueCompositionRuntime.AddLayer` opts each slot into
  `MasterAligned` when `_master` is non-null (set in 5.4.1).
- 🔲 Tests in `S.Media.Core.Tests` or a new
  `S.Media.Effects.Tests` covering: 30 fps source into 60 fps canvas
  with `MasterAligned` doesn't drop frames; 60 fps source into 30 fps
  canvas with `MasterAligned` drops every other source frame at the
  expected PTS.

### 5.9.2 Source fps probe + canvas-rate warning at add time

- 🔲 Extend `CueMediaProbe` from 5.1.1 to also report
  `Rational SourceFrameRate` (read from `decoder.Video.Format.FrameRate`).
- 🔲 Persist on `MediaCueNode` as `int SourceFpsNum`, `int SourceFpsDen`.
- 🔲 On the Video tab in the drawer, when the source's fps doesn't
  divide evenly into the placement's composition fps, show:

  > "Source 23.976 fps → 60 fps canvas — visible 3:2-pulldown judder
  > likely. Consider a 24 fps composition for cinema content."

- 🔲 No automatic fix — operator decides.

### 5.9.3 Cross-cue alignment for compositions

- ⏸ The hardest case: two cues with audio routes to *different*
  PortAudio devices (different crystals) both into one composition.
  Per-cue A/V is locked; cross-cue alignment isn't. No clean fix —
  the operator has to choose to share a device. Document this in
  `Doc/MediaFramework-Architecture.md` rather than try to engineer
  around it.

### 5.9 verification

- 🔲 Framework tests pass for the slot policy.
- 🔲 Build + cue tests clean.
- 🔲 Manual: play a 23.976 fps file into a 60 fps canvas — Video tab
  warns the operator. Switch the canvas to 24 fps — warning clears,
  playback is smooth.

---

## Cross-phase: documentation updates

- 🔲 After each phase ships, update
  `Doc/CuePlayer-Redesign-Checklist-2026-05-22.md` with the relevant
  Phase 5 cross-references (so the redesign doc points at this
  checklist's status for newer work).
- 🔲 Once 5.4 lands, update
  `Doc/CuePlayer-AVSync-And-UX-Review-2026-05-23.md` to mark drift
  mode 3a as resolved and note the remaining (3b, 3c) for 5.9 / docs
  respectively.
- 🔲 After 5.3 / 5.5 land, take screenshots and update
  `Doc/MediaFramework-Quickstart.md` with the new operator workflow.

---

## Cross-phase: testing infrastructure

A few test gaps to fill alongside the work above (low-priority but
keep them in mind):

- 🔲 An interaction test that fires a `FireAllSimultaneously` group of
  3 mock-execute media cues and asserts all 3 start within 50 ms of
  each other (regression cover for the Phase 4.10a fix).
- 🔲 An interaction test that adds + removes a cue from the tree and
  asserts no cell text recycles to stale data (regression cover for
  the binding-recycle fix in 4.10e — currently the bug class is
  prevented but there's no test for it).
- 🔲 A round-trip test for the new `MediaCueNode` fields landing in
  5.1.2 (HasVideo / HasAudio / AudioChannels) — already mentioned
  inline, repeated here so it isn't lost.

---

## What's intentionally NOT on this checklist

These are operator suggestions or ideas that landed in the review's
"don't do" section. Keep them off the list unless re-prioritized:

- ❌ Global "OBS-style" composition shared across all cue lists.
  Per-list compositions are the better model for portable show files.
- ❌ Full audio matrix UI on the cue side (the MediaPlayer's per-cell
  gain matrix). The current `CueAudioRoute` list is the right level.
- ❌ Retrigger modes (QLab's "fire even if already playing", "fire
  unless playing" etc.). The current "re-Go restarts" is simpler and
  less surprising.
- ❌ Show-file format versioning beyond what we already have (Schema
  v3). Each migration we've done so far has been
  load-time-only-with-sensible-defaults; no need for v4 just for new
  fields like `HasAudio` / `ColorTag` (additive, default-safe).
