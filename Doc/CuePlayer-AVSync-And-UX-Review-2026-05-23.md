# Cue Player — A/V Sync & UX Review (2026‑05‑23)

> Deep dive on the composition pipeline's clocking, plus operator UX ideas.
> Written after Phase 4.10's audio mixer + GPU compositor work landed, with
> a working PiP scenario and 11-cue-mixed audio.

## Part A — A/V Sync Deep Dive

### 1. The clocks in play

Each active cue ends up tied to **three** logical clocks. Understanding
where they meet (and where they don't) is the entire A/V sync story:

| Clock | Driven by | Used for |
|---|---|---|
| **PortAudio device clock** | The audio interface's sample clock (real-time-stable to ppm) | `CueAudioOutputRuntime.AudioRouter` master; pulls source samples at hardware rate; drives PortAudio submit cadence |
| **MediaPlayer freerun clock** | `MediaClock` ticking from wall-clock + `IPlaybackClock.SetMaster` slaving (engine sets it to the audio runtime's clock when the cue has audio routes) | The cue's `MediaContainerSession` video pump; decoder advance + frame submit timing |
| **Composition pump tick** | `Stopwatch.GetTimestamp()` in `CueCompositionRuntime.PumpLoop`, fixed period from `canvasFormat.FrameRate` | Calls `VideoCompositorSource.TryReadNextFrame` at canvas fps, fans the composed frame to bound outputs |

The engine connects 1↔2 explicitly. **It does not connect 3 to either**,
and that's the seam this review is about.

### 2. What the engine does today (the good)

In `CuePlaybackEngine.WireAudioRoutes` (≈line 334):

```csharp
if (runtime.PlaybackClock is { } playbackClock)
    entry.VideoClockMaster ??= playbackClock;
```

…and at `player.Play(videoOnlyMaster: entry.VideoClockMaster)`. So when a
cue has audio routes:

- `CueAudioOutputRuntime` creates its router with the PortAudio
  device's sample clock as master.
- The MediaPlayer's video pump is slaved to that same clock via
  `videoOnlyMaster`, so the cue's video frames are timed from the
  audio clock (not freerun wall time).
- A 2-hour cue's audio + video stay locked to the device clock, no
  drift between them.

For pure-video cues (no audio routes, `IncludeAudioRouter: true`):

- `MediaPlayer` creates its own internal `AudioRouter` + audio clock
  from the file's audio stream. Audio samples are pulled at internal
  rate, discarded (no output bound). Video pump runs from the same
  internal audio clock.
- Audio + video are locked within the cue, even though the audio
  never reaches a physical device.

So **per-cue A/V sync is correct.** The frames the cue submits to its
compositor slot are time-correct relative to the cue's audio.

### 3. Where it can drift (the bad)

**The composition pump is wall-clock-paced.** It calls
`_mixer.TryReadNextFrame` every `1/canvasFps` Stopwatch ticks. The
slot's contents come from the cue's video pump, which runs on the
cue's *audio master clock*.

Three drift modes:

#### 3a. Two-clock mismatch (composition pump vs. cue's master) — ✅ resolved (Phase 5.4)

- **Was**: The composition pump and the cue's audio master were sourced
  differently (Stopwatch vs. PortAudio sample clock).
- **Now (5.4)**: `CueCompositionRuntime` masters the composition pump to
  the first cue's `PlaybackClock` when a clock master is set. Drift mode
  3a is eliminated for normal single-master compositions.
- **Residual**: Long-run drift counters surface remaining slop for operator
  visibility (5.4.2).

**Severity (historical)**: low for typical show lengths (< 1 hour). No
longer the primary sync risk after 5.4.

#### 3b. Source-rate vs. canvas-rate mismatch — partially addressed (Phase 5.9)

- **Was**: The slot is a "latest wins" buffer with no PTS check.
- **Now (5.9.1)**: Slots opt into `SlotKeepPolicy.MasterAligned` when the
  composition runtime has a clock master — frames are chosen by PTS relative
  to the master instead of blind overwrite.
- **Now (5.9.2)**: Source fps is probed at add time; the Video tab warns when
  source rate doesn't divide evenly into the canvas rate.
- Operator still picks canvas fps; the engine reports mismatches rather than
  silently smoothing 3:2 pulldown.

#### 3c. Multiple cues, multiple masters, one composition

A `FireAllSimultaneously` group that puts two cues (each with its own
audio route, each with its own audio master clock) into the same
composition has *two* masters slaving *two* video pumps.

- Cue A's video pump runs on its audio master (PortAudio out 1).
- Cue B's video pump runs on its audio master (PortAudio out 2).
- The composition pump ticks at wall clock.

If PortAudio out 1 and out 2 are different physical devices (different
crystals), cue A and cue B will drift relative to each other inside
the composition. Per-cue A/V stays locked; cross-cue alignment doesn't.

**Severity**: invisible until two videos need to align (split-screen,
overlay timed to background, beat-sync, …). For "background +
alpha foreground" with the alpha cue's audio routed to the same device
as the background, both pumps share a master and stay aligned.

### 4. Other timing seams worth knowing

#### 4a. NDI sender pacing vs. composition pump

`NDIVideoSender.PaceBeforePack` enforces a `_minimumSubmitSpacing` —
inline sleeps inside `Submit` to honor the carrier's framerate. The
runtime now wraps NDI sinks in `VideoOutputPump(maxQueuedFrames=8)`
(Phase 4.10's NDI fix), so the composition pump never blocks on NDI's
sleep. **But** the pump's drop-oldest behavior means an NDI receiver
that falls behind for >133 ms drops a whole multi-frame burst — the
operator sees a hitch on the NDI side that doesn't appear on the Local
output sharing the same composition.

**Possible improvement**: when one output's pump is dropping, signal
the operator (the existing `VideoOutputPump.PumpPressure` event can
feed a UI indicator).

#### 4b. Latency stack-up

End-to-end latency for a video cue's frame from decoder to receiver:

| Stage | Latency added | Notes |
|---|---|---|
| Decoder → `VideoRouter` input | ≈ 1 frame | decoder queue depth |
| Router → `BgraConvertingVideoOutput` (swscale per branch) | ≈ 1 frame | CPU swscale; GL backend has a similar 1-frame upload step |
| Wrapper → compositor slot | ~0 | direct Submit |
| Compositor pump pulls | up to 1 canvas-frame period | Stopwatch-driven |
| Compositor → `VideoOutputPump` queue | 0–8 frames | NDI only; Local is synchronous |
| Local / NDI render | 1 frame | GL upload + present / NDI pack |

So Local outputs lag the source by 3–4 frames typically; NDI 3–12 frames
under load. The Phase 4.10 GPU compositor (`SDL3GLVideoCompositor`)
cut the per-frame composite cost from ~6 ms (CPU) to ~1 ms (GL) — pump
overruns dropped accordingly.

#### 4c. Source-side multi-rate fan-out

For a cue with audio routes to *two* different output devices,
`CuePlaybackEngine` uses `PausableAudioSource.CreateBranch()` to tee
the decoder's audio. Each branch is pulled by a different runtime's
router. If the two routers have different sample clocks (two physical
PortAudio devices), the tee's frames are consumed at different rates —
the framework's tee handles back-pressure but the cue's video pump
follows *one* master (the first one we encountered), so the other
output gradually drifts from the video.

**Severity**: same as 3c; an issue only when the two devices have to
stay synchronized visually.

### 5. Concrete recommendations

> All P0 items would visibly help operators today; P1 is for hardening.

#### P0 — Master the composition pump to a cue's clock

Replace the Stopwatch loop with a clock-mastered tick:

- `CueCompositionRuntime.SetClockMaster(IPlaybackClock master)` —
  called by the engine right after the first cue's `WireAudioRoutes`
  resolves a `playbackClock`.
- The pump becomes `clock.WaitForVideoTick(_canvasPeriod, ct)` (or
  equivalent — `MediaClock.AcquireRenderHandle` is the existing
  framework primitive); no Stopwatch.
- For audio-less compositions (no cue ever provided a master), fall
  back to the current Stopwatch path.

Removes 3a entirely and fixes the worst case of 3c for cues that
share an audio master.

#### P0 — Drift detector + operator warning

`CueCompositionRuntime.PumpLoop` already tracks `_framesComposited`
vs. `_framesSubmitted` and `_pumpOverruns`. Add:

- `_slotsBehindMaster` count — increments when a slot's last-Submit
  PTS is more than 2 canvas-periods behind the master's `CurrentPosition`.
- A periodic `StatusMessage` (every ~5 s) when this counter is
  growing, naming the laggy cue.

Makes 3a/3b/3c visible to the operator instead of silent stutter.

#### P1 — Per-cue PTS-aware slot policy

`VideoCompositorSource.Slot` keeps "latest wins" semantics. For 3b
(source-rate ≠ canvas-rate), expose a per-slot policy:

- `Latest` (current default) — what we have.
- `MasterAligned` — slot keeps the frame whose PTS is closest to the
  master's CurrentPosition at compositor tick time; older frames are
  dropped but never *too-new* frames.

Eliminates the silent frame-drop for over-rate sources.

#### P1 — Surface `VideoOutputPump.PumpPressure` to the operator

Each composition's NDI branch's pump fires `PumpPressure` when it
drops the oldest queued frame. Bubble that up to a per-output health
indicator (small badge on the Video Outputs expander row). Persistent
pressure = the operator's network can't keep up with this
composition's framerate.

#### P1 — Composition fps vs. source fps validation at add-time

When the operator adds a media cue with a `FilePlaylistItem`, we
already probe duration. Probe **frame rate** at the same time, store
on the cue. The Video tab in the drawer can then warn "source 23.976
fps into 60 fps canvas — visible 3:2 pulldown judder; consider a
24 fps composition".

#### P2 — Optional GenLock-style master selection

Right now `entry.VideoClockMaster ??= playbackClock` takes the first
audio runtime encountered as the cue's master. For a multi-output cue
where the operator cares about a specific master (e.g. "lock to NDI
out 1's wall-clock genlock"), expose a per-cue `VideoMasterOutputId`
that the engine prefers when resolving the master.

---

## Part B — UX Improvements

Operator-requested + my own observations after using the Cue Player
for the recent test scenarios. Organized by surface, with effort
estimates: **S** = single sitting, **M** = 1–2 days, **L** = multi-day.

### 1. Cue tree (TreeDataGrid) — collapse editable cells

**Today**: every cell is an editable `TextBox` / `ComboBox` / spinner.
The grid is dense, easy to misclick, and the Name column re-binds on
recycle (the "stale label" bug we fixed in 4.10e is a symptom of this
class of issue — bindings that don't re-bind on recycle).

**Proposal** (M):

- All cells become **read-only `TextBlock`** rendering of the cue's
  data. No inline editing.
- Double-click a row → expand the bottom drawer, focus its first
  editable field. Single-click → just select.
- Rename: F2 on the selected row → opens an inline rename popup over
  the Name cell only. Esc cancels, Enter commits. (Mirrors typical
  file-tree rename UX.)
- The Number column shows the cue number as plain text. Renumber
  via right-click → "Renumber selection…" dialog.

Removes the recycling-binding class of bugs entirely and makes the
tree feel like a list, not a spreadsheet.

### 2. Drawer — kind-aware tabs (and hide what doesn't apply)

**Today**: Media cues show General / Audio / Video / Action / Comment / Group
tabs visibility-filtered by cue kind. The Video tab is shown for
every Media cue including audio-only files.

**Proposals** (S each, ship together):

- **Hide the Video tab when the cue's source has no video stream.**
  We already probe duration on add; extend that probe to cache
  `HasVideo` + `HasAudio` + `SourceChannelCount` on the cue node, then
  drop the Video tab visibility binding from `HasSelectedMediaCue` to
  `HasSelectedMediaCue && SelectedCueNode.SourceHasVideo`. (Cover-art
  files like MP3s with embedded artwork report `HasVideo == true` but
  `VideoIsAttachedPicture == true` — surface that too so an attached
  picture can still place into a composition for a "now playing"
  slate.)

- **Audio tab shows source channel count and lays out routes per-channel.**
  Header: "Source: 2 channels". Routes ListBox already exists; add a
  hint when SourceChannel exceeds source channels: "source has 2 ch
  but route targets ch 3 (out of range)".

- **General tab gets the cue number editor** (currently the tree
  edits Number; if §1 lands, the tree is read-only and Number editing
  moves here).

- **Notes** stays in General as today.

### 3. Group cue — total duration

**Today**: the Group tab in the drawer only shows fire mode. The tree
shows `—` for the duration of group rows.

**Proposal** (S):

- `CueNodeViewModel.DurationDisplay` for group rows: sum of children
  durations (recursively for nested groups). For `FireAllSimultaneously`,
  display `max(children durations)`. For `FirstCueOnly`, display
  `(first cue's duration)`. For `AutoFollow`/`AutoContinue` chains,
  display the cumulative timeline.
- Updates when a child's duration changes (subscribe to
  `Children.PropertyChanged` for media nodes' `DurationMs`).

Format the display as `hh:mm:ss · N items` so it's obviously a roll-up.

### 4. Per-cue preview / seek

**Today**: Standby → Go is the only path to "see" a cue. No quick
"audition this" gesture, no scrubbing.

**Proposals**:

- **Preview button on the General tab** (S): "▶ Preview" plays the
  cue *without* committing the engine to the cue list's outputs —
  routes audio to the system default device and video to a temporary
  preview window (or just the General tab's panel). One-cue-at-a-time
  preview. Disposes when the operator clicks Stop Preview or moves to
  another cue.

- **Scrubber on the General tab** (M): a slider bound to the cue's
  current `MediaPlayer.PlayClock.CurrentPosition`. Drag to seek.
  Updates live during playback. Hidden when nothing's playing.

- **Hover-to-preview** (M, optional): hovering a cue in the tree for
  500 ms opens a tiny popup with the first frame (or 30 s of audio
  scrub-thumbnail). Cancellable.

### 5. Right-side "Now Playing" panel

**Operator's exact ask**: a panel listing currently-playing cues with
duration, remaining time, position, and per-cue cancel button. Also
show *upcoming* cues triggered by AutoFollow / AutoContinue.

**Proposal** (M):

- New `Views/Controls/ActiveCuesPanel.axaml` docked on the right side
  of the Cue Player tab. Width ~280 px, collapsible.
- Bound to `CuePlayerViewModel.ActiveCues` — a new
  `ObservableCollection<ActiveCueViewModel>` populated from the
  engine's `CueStarted` / `CueEnded` events.
- Each row shows:
  - Status dot (matches the tree's dot color)
  - Cue number + label
  - Progress bar (position / duration)
  - "mm:ss / mm:ss" text
  - Per-cue "✕" button → calls `engine.StopCueAsync(cueId)`
- Below: an "Upcoming" section listing cues that will fire from
  AutoFollow / AutoContinue chains within the next N seconds. The
  engine already builds a trigger plan; expose it via a
  `Future` collection on the VM and surface it here.
- Optional: a "Pause all" / "Stop all" button at the top of the panel.

Persistent UI value here is high — right now if 11 audio cues are
mixing, the operator has *no* per-cue indicator beyond the tree row
badge (and can't cancel just one).

### 6. Cue numbering — auto-renumber + insert-between

**Today**: Number is a free-form text field; the operator types it.
`+ Media` etc. generate `(count + 1)` as the new number string.

**Proposals** (S each):

- **Auto-renumber command**: right-click cue list → "Renumber 1.0,
  2.0, 3.0…" (root-level cues) and nested groups get e.g. `1.1`,
  `1.2`. Stable until next renumber.
- **Insert between**: when inserting between cue 2 and cue 3, default
  the new cue's number to `2.5`. (Operators reorder live; new
  insertions shouldn't force a renumber.)
- **Drag-and-drop reorder**: pull a cue between two others, drop;
  numbers update if auto-renumber is on, else stay.

### 7. Output health and pre-roll dots

**Today**: the operator sees output lines in the Output Management
view; status (running / errored / not started) is shown there.

**Proposal** (S):

- A small dot next to each Video Outputs row in the Cue Player tab,
  green/amber/red based on the underlying line's health (same data
  shown in Output Management). Hover for tooltip.
- Same for Audio outputs (the dropdown in the route picker can color
  entries by health).
- Pre-roll status on cue rows: the engine pre-opens decoders for the
  next N cues; show a small "warming" badge on those rows. (We
  already have `CuePreRollCache`; just need the indicator binding.)

### 8. Cue properties that have grown into duplicates

A few overlaps to clean up:

- **Trigger mode** appears in the General tab. Old grid had a Trigger
  column; that's gone now ✅. Just confirm no remaining reference in
  the resx.
- **Cue number** is editable in two places once the §1 rename popup
  exists (rename popup + drawer field). Pick one: rename popup is
  the one operators reach for; the drawer field becomes read-only
  (or shows only when you arrive there from "Renumber selection…").
- **`SelectedCueNode` vs. `SelectedCueNodes`**: the multi-select
  changes meant the drawer still binds to the singular primary. Fine
  in concept but the drawer should *show* "(N cues selected — adds
  apply to all)" when N > 1 so the operator knows the `+ Route`
  button isn't single-cue.

### 9. Keyboard shortcuts (operator quality of life)

Standard cue-player shortcuts that don't exist yet:

| Key | Action |
|---|---|
| `Space` | Go |
| `Esc` | Panic |
| `Enter` | Standby selected |
| `Backspace` | Back |
| `↑` / `↓` | Move selection |
| `Ctrl+↑` / `Ctrl+↓` | Move cue up/down in the list |
| `F2` | Rename selected cue |
| `Del` | Remove selected cue (confirm if it has routes/placements) |
| `Ctrl+D` | Duplicate selected cue |
| `Ctrl+Click` / `Shift+Click` | Multi-select (works already as of 4.10) |

**Effort**: S (Avalonia `KeyBindings` on the cue player view).

### 10. Color tags / labels (QLab parity, optional)

QLab and similar give the operator a per-cue color label (8 swatches
+ "no color"). Useful for visually grouping cues by act/scene/intent
without nesting them into groups.

**Proposal** (M):
- `CueNode.ColorTag : int` (0 = none, 1..7 = palette index).
- Per-row left edge gets a 4 px vertical color stripe.
- Drawer General tab gets a color picker.
- Color tags are purely visual — no behavior.

### 11. Status / log surface

**Today**: One `StatusMessage` string scrolls past in the toolbar.
Errors disappear after the next status update.

**Proposal** (M):
- A persistent log panel (collapsed by default at the bottom of the
  Cue Player, separate from the drawer) showing the last 200 status
  messages with timestamps.
- Errors stay until acknowledged or cleared.
- Useful for post-mortem: "the cue at 14:32 reported 'NDI line in use'"
  is gone right now after the next status update overwrites.

### 12. Move advanced or one-time config into dialogs

A few things on the main view that the operator touches rarely could
move into dialogs to reclaim screen space:

- **Cue list management** (Add / Remove / Load / Save / Save As) is
  one row of buttons in the toolbar. Collapse Load/Save/Save-As into
  a single "Files…" dropdown. Add/Remove stay.
- **Pre-roll count** is a numeric in the toolbar; it's a "set once
  per show" config. Move to a "Cue list settings…" dialog accessible
  from a gear icon in the toolbar.
- **Compositions** expander: keeps its place (operator references it
  often). Add a "Composition…" dialog (opened from the row) for the
  truly advanced options the operator's eyeing (color space, output
  precision, pixel format) when they exist.

---

## Part C — Suggested implementation order

> Picked so each phase ships independently and the operator visibly
> benefits each time.

| Phase | Scope | Effort | Why ship now |
|---|---|---|---|
| **5.1** | UX: hide Video tab for audio-only sources; show source channel count in Audio tab; group duration roll-up; drawer "(N selected)" hint | M | Pure operator clarity; no engine changes. |
| **5.2** | Read-only tree cells + F2 rename popup + drag-reorder | M | Removes the recycling-binding bug class entirely. |
| **5.3** | Right-side "Now Playing" panel | M | Operator's #1 ask; closes the multi-cue feedback gap. |
| **5.4** | A/V sync P0: master the composition pump to a cue's clock + drift counter | M | ✅ Shipped — eliminates 3a. |
| **5.5** | Preview / scrubber on General tab | M | ✅ Shipped — audition without transport. |
| **5.6** | Keyboard shortcuts + duplicate / move-up-down commands | S | ✅ Shipped. |
| **5.7** | Output health dots; pre-roll warming badges; `VideoOutputPump.PumpPressure` surfaced | M | ✅ Shipped. |
| **5.8** | Color tags; renumber dialog; cue list settings dialog | M | ✅ Mostly shipped (string cleanup deferred). |
| **5.9** | A/V sync P1: PTS-aware slot policy; source-fps vs. canvas-fps warning | L | ✅ Shipped — 3b mitigated; 3c documented. |

## Part D — What I'm not recommending

Worth noting explicitly:

- **A single global composition like OBS Studio**: tempting, but the
  per-cue-list compositions are a more flexible model for show files
  (each list ships its own production). Keep it.
- **Per-frame GPU-side cue compositing across compositions**: the
  current "one compositor per composition, N cues feed slots" is the
  right separation. Don't try to put cross-composition mixing in the
  engine.
- **Audio matrix UI on the cue side**: the per-cell channel matrix
  the MediaPlayer side has (`AudioMatrixViewModel`) is overkill for
  cue routing. The current `CueAudioRoute` list (one route per
  source-channel → output-channel pair) is the right abstraction.
- **Re-running the cue while it's already playing as a "retrigger"
  policy**: QLab has this; HaPlay's current "re-Go stops + restarts"
  is simpler and less surprising. Don't add modes.

## Appendix — Quick reference: where each clock lives

```
PortAudio device clock
    └── CueAudioOutputRuntime._router (AudioRouter w/ AttachMasterClock)
            ├── Pulls IAudioSource samples at sample rate
            └── PublishedAs:  PlaybackClock (engine reads this)

CuePlaybackEngine.WireAudioRoutes
    └── entry.VideoClockMaster ??= playbackClock

CuePlaybackEngine.Play
    └── player.Play(videoOnlyMaster: entry.VideoClockMaster)
            └── MediaContainerSession.Play → AvPlaybackCoordinator.Play
                    └── video.Clock.SetMaster(videoOnlyMaster)
                            └── Cue's video pump now ticks from the audio clock

CueCompositionRuntime.PumpLoop
    └── Master clock when SetClockMaster is called (Phase 5.4)
            └── _master.ElapsedSinceStart drives pump period
            └── _mixer.TryReadNextFrame(masterPts) with MasterAligned slots (5.9)
                    └── Fans composed frame to acquired outputs
```

Phase 5.4 added the arrow from the audio clock back to the composition
pump's tick. Phase 5.9 passes master PTS into slot acquisition.
