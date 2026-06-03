# HaPlay Cue Composition Timeline Semantics

Created: 2026-06-03

Captures the timing model cue video uses inside a composition, so future work
(timeline placement, show-time sync, multi-clip layers) starts from a documented
baseline instead of re-deriving it from the playback code.

## Current behavior: every cue layer is cue-relative (t=0 at fire)

When a media cue is fired into a composition:

1. The cue's `MediaPlayer` is seeked to `ClipWindow.Start` (the clip's start
   offset). Decoded frames therefore carry **source-timeline PTS** — a clip that
   starts 80 minutes into a file emits frames at ~80 min.
2. `CuePlaybackEngine.WireVideoPlacements` wraps the composition layer slot in
   `RetimingVideoOutput(ptsOffset = −ClipWindow.Start)` (a reusable
   `S.Media.Core.Video` wrapper). This subtracts the start offset, so frames reach
   the compositor slot at **cue-relative PTS** — the first visible frame is at t≈0.
3. The composition pump (`CueCompositionRuntime`) is slaved to the cue's audio
   playback clock (`PlaybackClockMaster`), which also starts at 0 on fire. Each
   layer slot uses `SlotKeepPolicy.MasterAligned`, picking the held frame closest
   to the master position and **withholding frames more than one canvas period in
   the future**.

The consequence — and the reason rebasing is mandatory — is that without step 2 a
clip-start frame at 80 min sits far beyond the master's `t + canvasPeriod` window,
so the slot withholds it and the layer renders transparent (the "black screen"
bug). Rebasing brings the clip into the master's window.

Regression coverage:

- Unit: `RetimingVideoOutputTests` (offset add/subtract, clamping + frame ownership).
- Integration: `CueStartOffsetCompositionTests` drives a source-PTS frame through
  the real `VideoCompositorSource` master-aligned slot and asserts it is visible
  with rebasing and withheld without it.

### Practical implications

- **Multiple cues in one composition share a t=0 origin.** Two layers fired at
  different wall-clock moments both start their own clip at local 0; they are
  *not* aligned to a shared show timeline. The first cue's clock becomes the
  composition master (`CueCompositionRuntime.SetClockMaster` keeps the first
  master and ignores later ones), and other layers ride that master's cadence.
- **Loop/seek stay cue-relative.** A loop restart re-seeks to `ClipWindow.Start`;
  the rebasing offset is fixed for the cue's lifetime, so the layer keeps
  presenting from local 0 again.
- **Audio and video share the clip window.** Both are trimmed by the same
  `CueClipWindow` (start/end offsets) and seek together.

## Open decision: cue-relative only, or future timeline placement?

The current model assumes **all cue layers are cue-relative**. That is correct
for the present soundboard/cue-stack use case (fire a clip, it plays from its
start). It does **not** yet support:

- **Show-time / timeline placement** — e.g. "this layer should appear at
  00:01:30 on a shared composition timeline" rather than at its own t=0.
- **Pre-rolled offset starts** — a layer that should enter mid-clip and stay in
  sync with a longer master that has already been running.

If timeline placement is wanted later, the recommended shape is to make the
rebasing offset a *placement property* rather than always `ClipWindow.Start`:

```
effectiveLayerPts = sourcePts - clipStart + timelinePlacementOffset
```

where `timelinePlacementOffset` defaults to 0 (today's behavior) and the master
clock becomes a composition-owned show clock instead of the first cue's audio
clock. This is also the natural home for the larger-backlog "first-class clip
window" and "RetimingVideoOutput" items — both would feed this same offset.

**Decision to record (owner):** keep cue-relative-only for now, and treat
timeline/show-time placement as a separate, opt-in feature layered on top — not a
change to the default. Until that feature exists, "all cue layers are
cue-relative" is the contract the playback path guarantees.
