# HaPlay Cue Player Cleanup Backlog

Created: 2026-06-03

Context: follow-up backlog after the cue-player seek/start-offset fixes, including
standby preparation, clip start/end offsets, and the cue-relative video PTS rebase
needed for composition slots.

## Cleanup Before Larger Feature Work

- [ ] Remove the stale `deferPlay` argument from
  `CuePlaybackEngine.OpenCueEntryAsync`.
  - Reason: start behavior is now controlled by route wiring plus paused audio
    sources. The argument no longer carries a meaningful transport decision.

- [ ] Rename `ActiveCue.VideoClockMaster`.
  - Suggested names: `PlaybackClockMaster`, `AudioClockMaster`, or
    `CompositionClockMaster`.
  - Reason: the value is sourced from the audio runtime playback clock, then used
    as the composition master. The current name reads as if the video path owns
    the clock.

- [ ] Extract the repeated "next cues from standby" traversal in
  `CuePlayerViewModel`.
  - Affected methods: `GetPreRollTargets`, `GetPreparedMediaCueTargets`,
    `GetNdiPreConnectTargets`, and `GetPortAudioPreConnectTargets`.
  - Suggested shape: one helper that enumerates the fireable pre-roll window
    from standby, with per-target filters layered on top.

- [ ] Add a short local comment where `PtsRebasingVideoOutput` is wired into
  `WireVideoPlacements`.
  - Reason: the class-level comment explains the wrapper, but the call site is
    the important black-screen fix. It should be obvious that cue start offsets
    require source PTS to be converted to cue-relative PTS before composition.

## Correctness-Adjacent Follow-Ups

- [ ] Debounce and serialize `MainViewModel.RefreshCuePreRollAsync`.
  - Current behavior: repeated pre-roll suggestions can overlap; failures are
    swallowed because pre-roll must not break transport.
  - Suggested behavior: a latest-request-wins refresh using a cancellation token
    source plus a small serial gate.

- [ ] Trigger pre-roll refresh when existing route or placement properties
  change.
  - Examples: audio output line, source channel, output channel, gain, video
    composition, layer index, opacity, start offset, and end offset.
  - Current protection: Go rebuilds the route plan and cache key, so playback
    should still recover.
  - Remaining issue: standby may no longer be warm after property edits.

- [ ] Add an integration regression test for "start-offset cue enters
  composition at cue-relative t=0".
  - The current unit-level PTS rebase test is useful, but the black-screen bug
    happened at the `MediaPlayer -> VideoRouter -> CueCompositionRuntime` boundary.
  - Preferred test shape: fake or lightweight video frames submitted through the
    same layer-slot path used by cue playback.

- [ ] Decide and document cue composition timeline semantics.
  - Current practical behavior: clip video is rebased so each cue starts at
    local t=0 inside the composition.
  - Decision to capture: whether all cue layers are always cue-relative, or
    whether future timeline/show-time placement should be supported separately.

## Larger Enhancements

- [ ] Promote `PtsRebasingVideoOutput` or an equivalent retiming wrapper into
  the reusable media framework.
  - Suggested concept: `RetimingVideoOutput`, `VideoPtsOffsetOutput`, or a
    general clip/timeline wrapper in `S.Media.Core.Video`.
  - Reason: retiming is useful for cue players, soundboards with video,
    composition, loop regions, and media fragments, not only HaPlay.

- [ ] Make clip windows a first-class framework concept.
  - Include source start, source end, effective duration, relative timeline PTS,
    and shared audio/video seek behavior.
  - Goal: avoid keeping trim logic split across HaPlay view models, cue engine
    state, and output wrappers.

- [ ] Add cue audio downmix presets or a per-cue matrix.
  - Useful presets: `5.1 -> stereo`, center-to-left/right, LFE drop or trim,
    duplicate mono to stereo, and direct channel pass-through.
  - Reason: the media player has matrix-style routing, but cue-player operators
    also need predictable channel mapping before playback starts.

- [ ] Surface richer prepared-cue state in the UI.
  - Suggested states: idle, preparing, seeked/ready, stale, failed.
  - Include the last failure reason somewhere operator-visible.
  - Reason: a binary warm marker is not enough for show-control workflows.

- [ ] Add pre-roll resource policy controls.
  - Examples: maximum prepared decoders, maximum memory estimate, auto-evict
    inactive prepared entries, and per-cue opt-out.
  - Reason: keeping several long H.264 files opened and seeked can be expensive.

- [ ] Consider a framework-level cue/clip API that can power HaPlay, soundboards,
  and cue-player hosts.
  - Desired guarantees: non-consuming standby, explicit start barrier,
    coordinated grouped starts, clip-relative audio/video timing, and clear
    output ownership.

## Verification To Run After Cleanup Changes

- `dotnet build MFPlayer.sln -m:1 --no-restore -v:m`
- `dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --no-build --logger "console;verbosity=minimal"`
- `git diff --check`

