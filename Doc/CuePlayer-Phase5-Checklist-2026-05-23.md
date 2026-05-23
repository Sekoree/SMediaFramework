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

### 5.1.1 Probe `HasVideo` / `HasAudio` / source channel count on add

- ✅ Extend `Playback/CueMediaProbe.cs` from a `(int? DurationMs)` return
  to a record: `CueMediaProbeResult(int? DurationMs, bool HasVideo, bool
  HasAudio, int AudioChannels, bool VideoIsAttachedPicture)`.
- 🔲 Update all three call sites in
  `ViewModels/CuePlayerViewModel.cs` — `AddMediaCueAsync` (single +
  multi paths), `BrowseMediaSourceAsync`, `AddMediaFilesFromDrop` — to
  use the new return shape and assign onto the cue VM (see 5.1.2).
- 🔲 The probe must still catch + return null on any failure — same as
  today.

**Verification**: drag in a stereo `.mp3` with embedded cover-art and a
`.mp4` with 5.1 audio. Confirm probed values land on the cue's VM
fields (inspectable via debug or new drawer hint in 5.1.4).

### 5.1.2 Extend `CueNodeViewModel` + persist to model

- 🔲 Add `[ObservableProperty]` fields on `CueNodeViewModel`:
  `SourceHasVideo`, `SourceHasAudio`, `SourceAudioChannels`,
  `SourceVideoIsAttachedPicture`.
- 🔲 Add matching `init` fields on `MediaCueNode` in
  `Models/CueList.cs`: `bool HasVideo`, `bool HasAudio`, `int
  AudioChannels`, `bool VideoIsAttachedPicture`. Default values keep
  pre-Phase-5.1 files loading cleanly (false / 0).
- 🔲 Wire `FromModel` / `ToModel` for these fields.
- 🔲 Tests: add to `HaPlayProjectIOTests` — round-trip an `MediaCueNode`
  with the probed fields populated.

### 5.1.3 Hide the Video tab on audio-only sources

- 🔲 In `Views/CuePlayerView.axaml`, change the Video tab's `IsVisible`
  binding from `{Binding HasSelectedMediaCue}` to compute on both
  `HasSelectedMediaCue` and `SelectedCueNode.SourceHasVideo`.
  - Use a small multi-binding (or a new `HasSelectedMediaCueWithVideo`
    computed property on the VM; the latter is cleaner).
- 🔲 Add the computed property to `CuePlayerViewModel` that recomputes
  on `SelectedCueNodeChanged` and on the selected cue's
  `SourceHasVideo` change.
- 🔲 Cover-art case: if `VideoIsAttachedPicture` is true, **show** the
  Video tab but with a hint at the top: "Source is a still image
  (album art) — placement uses the attached picture as a static
  layer." (Cover art is a real PiP use case for "now playing" slates.)

### 5.1.4 Audio tab header — source channel count

- 🔲 Add a `TextBlock` above the routes ListBox: `"Source: {Binding
  SelectedCueNode.SourceAudioChannels} ch"`. Hide when channels = 0
  (audio not probed / source has no audio).
- 🔲 Per-route warning row: when a route's `SourceChannel >=
  SourceAudioChannels`, render an inline ⚠ icon + tooltip "source has
  N ch but this route reads ch X".

### 5.1.5 Group cue duration roll-up

- 🔲 In `CueNodeViewModel.DurationDisplay`, when `Kind == Group`:
  - For `FireMode == FirstCueOnly`: return the first fireable child's
    duration.
  - For `FireMode == FireAllSimultaneously`: return `max(children
    durations)` (treat the longest as the group's natural duration).
  - For `FireMode == ArmedList`: return the sum of all fireable
    children's durations (operator advances one-at-a-time, so total
    show length is the sum).
- 🔲 Recursive: a group inside a group rolls up correctly.
- 🔲 Subscribe to `Children.CollectionChanged` + each child's
  `DurationMs` `PropertyChanged` so the group's display updates live.
- 🔲 Format: `hh:mm:ss · N items` (or `mm:ss · N items` when < 1 h)
  so it's obviously a roll-up. Keep media cue display unchanged.

### 5.1.6 Drawer "(N selected)" hint

- 🔲 In `Views/CuePlayerView.axaml`, add a small banner at the top of
  the Audio tab and the Video tab: `"(N cues selected — '+ Route' /
  '+ Placement' will apply to all of them)"`.
- 🔲 Bind visibility to `SelectedCueNodes.Count > 1`. Hide when single.
- 🔲 Add a `int SelectedCueCount` computed property on the VM that
  notifies on `UpdateSelection`.

### 5.1 verification

- 🔲 `dotnet build` clean.
- 🔲 `dotnet test UI/HaPlay.Tests` green.
- 🔲 Manual: add an MP3 (no video, no cover) → no Video tab. Add MP3
  with cover art → Video tab shows the hint. Add a video file → Video
  tab as today. Audio tab on a 5.1 file shows "Source: 6 ch". Group
  with 3 media cues totaling 2 min shows the rolled-up duration.

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

### 5.2.1 Make tree cells read-only

- 🔲 In `Views/CuePlayerView.axaml.cs.RebuildCueSource`, replace
  `BuildCompactTextEditor` (used for Number) and `BuildTextEditor`
  (used for Name) with `BuildReadOnlyText`.
- 🔲 Drop the no-longer-used `BuildTextEditor` and
  `BuildCompactTextEditor` methods from the file.
- 🔲 The Status / Duration / Kind columns are already read-only — no
  change.

### 5.2.2 F2 rename popup

- 🔲 Add `KeyBindings` on the `TreeDataGrid` in the XAML: F2 →
  `RenameSelectedCueCommand`.
- 🔲 New `RenameSelectedCueAsync` `RelayCommand` on `CuePlayerViewModel`:
  - Opens a small `RenameCueDialog.axaml` (new) with two fields:
    Number (`TextBox`) and Label (`TextBox` with `IsDefault = true` so
    Enter commits).
  - Esc cancels (no change). Enter / "OK" writes back to the cue VM.
- 🔲 Status message after rename: "Renamed cue N: {old} → {new}".

### 5.2.3 Renumber selection dialog

- 🔲 New `RenumberSelectionDialog.axaml` accessible from the cue list
  toolbar (a `Renumber…` button) and from the tree's right-click menu.
- 🔲 Inputs: "Start number" (default `1`), "Step" (default `1.0`),
  "Apply to" (radio: All / Root level only / Selected only).
- 🔲 Walks the tree, assigns sequential numbers `start`, `start+step`,
  `start+2*step`, ... Nested groups recurse with sub-numbers
  (`1.1`, `1.2`, ... under `1`).

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

### 5.2 verification

- 🔲 Build + tests clean.
- 🔲 Manual: with two cues in the tree, swap them via drag-drop. F2 on
  a cue opens the rename popup; Enter commits. Type non-default Number
  + Label, press Esc — no change. Tree never shows a stale label after
  remove+add (the recycling-binding bug).

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

### 5.3.1 `ActiveCueViewModel`

- 🔲 New `ViewModels/ActiveCueViewModel.cs`:
  - Holds a reference to the `CueNodeViewModel` and the engine's cue id.
  - `[ObservableProperty]` for `PositionMs`, `DurationMs`, `ProgressPercent`.
  - `Display` computed: `"01:23 / 03:45"`.
  - `ProgressBrush` computed: green normal, amber when behind, red when
    error.
  - `CancelCommand` → calls back into the engine (via a host callback
    similar to `StopPlaybackCallback`).
- 🔲 No model changes — this is a UI VM.

### 5.3.2 ActiveCues collection on `CuePlayerViewModel`

- 🔲 New `ObservableCollection<ActiveCueViewModel> ActiveCues` on the
  cue VM.
- 🔲 Update the existing `OnCueStarted(Guid)` (already exists) to also
  push an `ActiveCueViewModel` into the collection.
- 🔲 Update `OnCueEnded(Guid)` to remove the matching entry.
- 🔲 New `CancelActiveCue(Guid)` method on `CuePlayerViewModel` that
  forwards to a new `Func<Guid, Task>? CancelCueCallback` host
  callback. `MainViewModel` wires this to `engine.StopCueAsync`.

### 5.3.3 Progress polling

- 🔲 The engine already has a `WatchNaturalEndAsync` task per cue that
  polls `PlayClock.CurrentPosition` every 150 ms. Extend it to also
  raise `CueProgress(Guid, TimeSpan Position, TimeSpan Duration)`
  through a new event.
- 🔲 Cue VM subscribes via a new `OnCueProgress` host callback; updates
  the matching `ActiveCueViewModel`'s position fields.
- 🔲 Verify the 150 ms cadence is fine for a progress bar; bump to 100
  ms if it visibly stutters.

### 5.3.4 Upcoming cues view

- 🔲 Add a `BuildUpcomingPlan(int lookaheadSeconds)` method to
  `CuePlayerViewModel` that returns the next N cues from
  `EnumerateFireableCueOrder()` starting at `StandbyCueNode`, plus
  any cues queued via the active trigger plan (currently lives in
  `RunTriggerPlanAsync`).
- 🔲 New `ObservableCollection<UpcomingCueViewModel> UpcomingCues` on
  the cue VM, rebuilt whenever `StandbyCueNode` or active plan changes.
- 🔲 Each `UpcomingCueViewModel`: cue label + trigger mode label +
  estimated fire time (cue's own pre-wait offset + chain delay).

### 5.3.5 Panel XAML

- 🔲 New `Views/Controls/ActiveCuesPanel.axaml` — a docked control
  with two `ItemsControl`s (Active above, Upcoming below) bound to
  the collections.
- 🔲 In `Views/CuePlayerView.axaml`, dock the panel to the right of
  the cue tree with width = 280 px. Wrap the existing cue tree +
  drawer DockPanel + panel in a `Grid` with two star columns (3 *
  cuetree / drawer + 1 * panel? — operator-tunable; start with
  `*,280`).
- 🔲 Add a header toggle (▶/◀ chevron) that collapses the panel to a
  thin strip when the operator doesn't need it. Persist the collapsed
  state in `AppSettings`.

### 5.3.6 Panel-level actions

- 🔲 "Stop all" button at the top of the Active section — calls
  `CuePlayer.StopCommand` (same as the toolbar's Stop).
- 🔲 Per-row `✕` button — `CancelActiveCue(cueId)`.

### 5.3 verification

- 🔲 Build + tests clean.
- 🔲 Manual: fire a group of 3 cues with `FireAllSimultaneously`.
  Active panel shows 3 rows with live progress bars. Click `✕` on one
  → that one stops, the other two keep playing. Set up an
  `AutoFollow` chain of 3 cues → Upcoming section shows the next two
  with estimated delays.

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

### 5.4.1 `CueCompositionRuntime.SetClockMaster`

- 🔲 Add public method `void SetClockMaster(IPlaybackClock master)` on
  `CueCompositionRuntime`. Stores the master; safe to call multiple
  times (only takes the *first* non-null — multiple cues into the
  same composition shouldn't fight for the master).
- 🔲 Field: `IPlaybackClock? _master` initially null.

### 5.4.2 Pump loop respects the master

- 🔲 In `PumpLoop`, replace the Stopwatch-period loop with a hybrid:
  - If `_master is not null`, await `_master.WaitForVideoTickAsync(ct,
    canvasPeriod)`. (If the framework's existing tick API has a
    different name, find it in `S.Media.Core.Clock` — there are
    helpers like `MediaClock.AcquireRenderHandle` for this pattern.)
  - Else fall back to the existing Stopwatch path (audio-less
    composition).
- 🔲 The first call to `_master` should not block forever if the master
  isn't started yet — guard with a 100 ms timeout and emit one log
  per second while waiting.

### 5.4.3 Engine wires the master

- 🔲 In `CuePlaybackEngine.WireVideoPlacements`, right after
  `GetOrCreateComposition`:

  ```csharp
  if (entry.VideoClockMaster is { } master)
      runtime.SetClockMaster(master);
  ```

- 🔲 `WireAudioRoutes` already resolves `entry.VideoClockMaster`; the
  composition runtime now picks it up the moment the cue's wire-up
  completes.

### 5.4.4 Drift counter + warning

- 🔲 Add `_framesBehindMaster` field on `CueCompositionRuntime`
  alongside the existing `_pumpOverruns` counter.
- 🔲 Inside `PumpLoop`, after `TryReadNextFrame`, compare each slot's
  last-Submit `PresentationTime` against `_master.CurrentPosition`.
  Increment `_framesBehindMaster` when any slot is more than two
  canvas periods behind.
- 🔲 When the counter rises by >30 over 5 seconds, raise a new event
  `event EventHandler<CueCompositionRuntimeWarning>? Warning` carrying
  the composition id, the laggy cue's id (slot's owner), and the lag
  magnitude.
- 🔲 `CuePlaybackEngine` subscribes and translates into a status message
  surfaced on the cue VM ("Composition 'Program' is 280 ms behind cue
  'Background' — check pump pressure").

### 5.4.5 Pump-pressure surfacing groundwork

- 🔲 No UI yet (that lands in 5.7), but expose
  `CueCompositionRuntimeStats.FramesBehindMaster` on the stats struct
  so future telemetry / health badges can read it.

### 5.4 verification

- 🔲 Build + tests clean.
- 🔲 Manual: fire a 5-minute video cue with audio routed externally.
  Confirm video timestamps stay aligned to audio (no PTS-vs-wall drift)
  by stopping at the end — the natural-end event should fire within
  ±30 ms of the audio actually ending. Compare with a Stopwatch-pump
  baseline build to verify the improvement.

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

### 5.6.1 Transport bindings

- 🔲 In `Views/CuePlayerView.axaml`, attach `KeyBindings` at the
  `UserControl` level:

  | Key | Command |
  |---|---|
  | `Space` | `GoCommand` |
  | `Esc` | `PanicCommand` |
  | `Enter` | `StandbySelectedCommand` |
  | `Backspace` | `BackCommand` |
  | `Ctrl+P` | (5.5.1) Toggle preview on selected |

- 🔲 Ensure no key conflicts with text input — when a `TextBox` has
  focus (e.g. inside the drawer), keys should NOT trigger transport.
  Use `KeyDown` handlers that check `FocusManager.GetFocusedElement()`.

### 5.6.2 Tree navigation + reorder

- 🔲 `↑` / `↓` → move selection (built-in to TreeDataGrid; verify it
  works after Phase 5.2's read-only conversion).
- 🔲 `Ctrl+↑` / `Ctrl+↓` → move the selected cue up / down within its
  parent. New `MoveSelectedCueUpCommand` / `…DownCommand`.
- 🔲 `Home` / `End` → first / last cue.

### 5.6.3 Edit commands

- 🔲 `F2` → rename popup (5.2.2 hook).
- 🔲 `Del` → `RemoveNodeCommand`. Confirm if the cue has wired routes
  or placements: "Cue X has 3 audio routes and 1 video placement.
  Delete anyway?"
- 🔲 `Ctrl+D` → `DuplicateSelectedCueCommand`. Deep-copies the
  `CueNodeViewModel` (new GUID, same fields, same routes/placements),
  inserts immediately after the original.

### 5.6 verification

- 🔲 Build + tests clean.
- 🔲 Manual: navigate the tree with arrow keys, press Space → Go fires.
  Click into a drawer TextBox, type space → text contains a space, Go
  does NOT fire. Ctrl+D duplicates; Del with routes shows the confirm.

---

## Phase 5.7 — Health and pre-roll indicators

> **Risk**: Low · **Effort**: M · **Breaking**: ✗
>
> Visibility into what's wired and what's struggling.

### 5.7.1 Output health dots

- 🔲 In the "Audio outputs" + "Video outputs" expanders, render a
  small status dot at the start of each row.
- 🔲 Source the dot color from the underlying `OutputLineViewModel`
  state — green (running), amber (configured but not started), red
  (error), gray (definition missing or removed).
- 🔲 The same dot appears next to each option in the route's "Output"
  ComboBox so the operator can see at-a-glance which devices are live
  when picking a route.

### 5.7.2 Pre-roll warming badge on cue rows

- 🔲 New `bool IsPreRollWarm` flag on `CueNodeViewModel`, updated by
  `CuePlayerViewModel` reading from the engine's pre-roll cache
  (`CuePreRollCache`).
- 🔲 In `BuildStatusBadge` (the existing badge column), draw a small
  outline + dot when the cue is pre-rolled but idle (e.g. light blue
  outline). Keep current/standby coloring.
- 🔲 Wire the engine to notify the VM on pre-roll cache changes — add
  a new `event EventHandler? PreRollCacheChanged` on the engine, fire
  it from `CuePreRollCache` hooks.

### 5.7.3 Pump-pressure surfacing

- 🔲 In `CueCompositionRuntime`, subscribe to
  `VideoOutputPump.PumpPressure` for each NDI-wrapped pump.
- 🔲 Track per-output `LastPressureTicks` + `DroppedFrames`.
- 🔲 Surface to the operator via a small badge on the affected Video
  Outputs expander row + a status message: "NDI output 'Camera 1'
  dropped 12 frames in the last 5 seconds — receiver may be slow."

### 5.7.4 Composition stats panel (optional)

- ⏸ Decide at implementation: add a small "Stats" toggle in the
  Compositions expander that shows the runtime's `CueCompositionRuntimeStats`
  (frames composited, submitted, overruns, slot overflow, last/max pump
  frame time, frames behind master). Useful for debugging.

### 5.7 verification

- 🔲 Build + tests clean.
- 🔲 Manual: stop the local video preview in OutputManagement → its
  dot in the Cue Player's "Video outputs" expander turns gray, and
  any route picker dropdown shows it grayed. Fire a video cue at a
  composition that has a slow NDI receiver → pressure badge lights up
  amber within 5 seconds.

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
