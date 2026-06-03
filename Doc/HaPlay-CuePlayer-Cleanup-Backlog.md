# HaPlay Cue Player Cleanup Backlog

Created: 2026-06-03

Context: follow-up backlog after the cue-player seek/start-offset fixes, including
standby preparation, clip start/end offsets, and the cue-relative video PTS rebase
needed for composition slots.

## Cleanup Before Larger Feature Work

- [x] Remove the stale `deferPlay` argument from
  `CuePlaybackEngine.OpenCueEntryAsync`. *(Done — parameter deleted, both call
  sites updated; `ExecuteCoreAsync` keeps its own `deferPlay`.)*
  - Reason: start behavior is now controlled by route wiring plus paused audio
    sources. The argument no longer carries a meaningful transport decision.

- [x] Rename `ActiveCue.VideoClockMaster`. *(Done — renamed to
  `PlaybackClockMaster` with a doc comment explaining it is the audio-runtime
  clock used as both composition master and `Play(videoOnlyMaster:)`.)*
  - Suggested names: `PlaybackClockMaster`, `AudioClockMaster`, or
    `CompositionClockMaster`.
  - Reason: the value is sourced from the audio runtime playback clock, then used
    as the composition master. The current name reads as if the video path owns
    the clock.

- [x] Extract the repeated "next cues from standby" traversal in
  `CuePlayerViewModel`. *(Done — new `EnumeratePreRollWindow()` yields the
  fireable window from standby; the four methods now just apply their filter and
  cap matched targets.)*
  - Affected methods: `GetPreRollTargets`, `GetPreparedMediaCueTargets`,
    `GetNdiPreConnectTargets`, and `GetPortAudioPreConnectTargets`.
  - Suggested shape: one helper that enumerates the fireable pre-roll window
    from standby, with per-target filters layered on top.

- [x] Add a short local comment where `PtsRebasingVideoOutput` is wired into
  `WireVideoPlacements`. *(Done.)*
  - Reason: the class-level comment explains the wrapper, but the call site is
    the important black-screen fix. It should be obvious that cue start offsets
    require source PTS to be converted to cue-relative PTS before composition.

## Correctness-Adjacent Follow-Ups

- [x] Debounce and serialize `MainViewModel.RefreshCuePreRollAsync`. *(Done —
  latest-request-wins via a swapped `CancellationTokenSource` plus a
  `SemaphoreSlim(1,1)` serial gate; the token is threaded into
  `RefreshPreparedCuesAsync` and checked before the NDI/PortAudio pre-connect
  passes.)*
  - Current behavior: repeated pre-roll suggestions can overlap; failures are
    swallowed because pre-roll must not break transport.
  - Suggested behavior: a latest-request-wins refresh using a cancellation token
    source plus a small serial gate.

- [x] Trigger pre-roll refresh when existing route or placement properties
  change. *(Done — `WatchSelectedCueForPreRoll` subscribes to the selected media
  cue's offset/loop/end-behavior changes and to its audio-route / video-placement
  collection + item property changes, suggesting a refresh on edit-relevant
  fields. `LineRef` is excluded since it isn't part of the cache key.)*
  - Examples: audio output line, source channel, output channel, gain, video
    composition, layer index, opacity, start offset, and end offset.
  - Current protection: Go rebuilds the route plan and cache key, so playback
    should still recover.
  - Remaining issue: standby may no longer be warm after property edits.
  - Note: covers in-place edits on the *selected* cue (the only one the inspector
    exposes for editing); multi-select add already suggested a refresh.

- [x] Add an integration regression test for "start-offset cue enters
  composition at cue-relative t=0". *(Done — `CueStartOffsetCompositionTests`
  drives a source-PTS frame through the real `VideoCompositorSource`
  master-aligned slot: visible with `RetimingVideoOutput`, withheld (black)
  without it.)*
  - The current unit-level PTS rebase test is useful, but the black-screen bug
    happened at the `MediaPlayer -> VideoRouter -> CueCompositionRuntime` boundary.
  - Preferred test shape: fake or lightweight video frames submitted through the
    same layer-slot path used by cue playback.

- [x] Decide and document cue composition timeline semantics. *(Done — see
  `Doc/HaPlay-Cue-Composition-Timeline-Semantics.md`: cue layers are cue-relative
  (t=0 at fire); timeline/show-time placement is recorded as a future opt-in
  feature, not a default change.)*
  - Current practical behavior: clip video is rebased so each cue starts at
    local t=0 inside the composition.
  - Decision to capture: whether all cue layers are always cue-relative, or
    whether future timeline/show-time placement should be supported separately.

## Larger Enhancements

- [x] Promote `PtsRebasingVideoOutput` or an equivalent retiming wrapper into
  the reusable media framework. *(Done — `S.Media.Core.Video.RetimingVideoOutput`
  (additive PTS offset + optional zero-clamp, zero-copy on hw/dmabuf backings)
  replaces the HaPlay-internal `PtsRebasingVideoOutput`, which was deleted. Cue
  wiring now uses `RetimingVideoOutput(layerOutput, -ClipWindow.Start)`. Unit
  coverage moved to `S.Media.Core.Tests/Video/RetimingVideoOutputTests`.)*
  - Suggested concept: `RetimingVideoOutput`, `VideoPtsOffsetOutput`, or a
    general clip/timeline wrapper in `S.Media.Core.Video`.
  - Reason: retiming is useful for cue players, soundboards with video,
    composition, loop regions, and media fragments, not only HaPlay.

- [x] Make clip windows a first-class framework concept. *(Done — promoted the
  HaPlay-internal `CueClipWindow` math to a reusable `S.Media.Core.ClipWindow`
  value type: source start/end, effective duration, `FromOffsets`, and
  source↔relative mapping + guarded end detection. HaPlay's `CueClipWindow` is now
  a thin adapter that builds one from a `MediaCueNode`; the same window already
  drives both audio and video seek in the cue engine. Covered by
  `S.Media.Core.Tests/ClipWindowTests`. The fuller "owns the shared seek
  transport" piece is folded into the cue/clip-API design below.)*
  - Include source start, source end, effective duration, relative timeline PTS,
    and shared audio/video seek behavior.
  - Goal: avoid keeping trim logic split across HaPlay view models, cue engine
    state, and output wrappers.

- [x] Add cue audio downmix presets or a per-cue matrix. *(Done for presets —
  `AudioDownmixPresets` (pass-through, mono→stereo, 5.1→stereo, drop-LFE) ship as a
  shared authoring quick-apply: the cue route editor buttons expand a preset into
  discrete `CueAudioRoute`s via `ApplyCueDownmixPreset`, honoring the 0-based source /
  1-based output channel convention; the same model backs the media-player matrix
  buttons. A stored per-cue matrix applied at mix time is split out below.)*
  - Useful presets: `5.1 -> stereo`, center-to-left/right, LFE drop or trim,
    duplicate mono to stereo, and direct channel pass-through.
  - Reason: the media player has matrix-style routing, but cue-player operators
    also need predictable channel mapping before playback starts.

- [ ] Stored per-cue downmix matrix (distinct from the authoring quick-apply above).
  *(Deferred as largely redundant — a cue's `AudioRoutes` already are a stored, sparse
  per-cue matrix (source→output + gain + mute) persisted on the cue and applied by the
  mixer at fire time. A parallel matrix representation would mostly duplicate that; revisit
  only if per-cell trim/level UI beyond the route list is actually wanted.)*
  - The shipped presets expand to plain routes at edit time; a stored matrix would
    live on the cue and be (re)applied by the mixer at fire time, surviving manual
    route edits and exposing per-cue trim/level cells.

- [x] Surface richer prepared-cue state in the UI. *(Done — `PreparedCueState`
  (Idle/Preparing/Ready/Stale/Failed) is surfaced per row as a color-coded status-dot
  outline plus a tooltip that includes the failure reason; engine raises
  `PreparedCueStatesChanged`, mapped onto each `CueNodeViewModel`. Follow-up: added the
  distinct `Stale` state — an in-place edit to the selected cue's transport / routes /
  placements flags its warm standby stale via `CuePlayerViewModel.CueStandbyInvalidated`
  → `CuePlaybackEngine.MarkPreparedCueStale` until the debounced refresh re-prepares it.)*
  - Suggested states: idle, preparing, seeked/ready, stale, failed.
  - Include the last failure reason somewhere operator-visible.
  - Reason: a binary warm marker is not enough for show-control workflows.

- [x] Add pre-roll resource policy controls. *(Done — the engine caps concurrently-held
  standby decoders and auto-evicts inactive entries (`EvictPreparedExceptAsync`), the
  per-cue `DisablePreRoll` opt-out is honored across the file-cache and the NDI/PortAudio
  pre-connect passes, and the cap is now operator-configurable per cue list
  (`CueList.MaxPreparedDecoders`, default 6, clamped [1,16], in the Cue List Settings
  dialog) — preparation stops at the cap rather than opening then evicting overflow.
  Deferred: a memory-estimate eviction policy (fuzzy; folded into the cue/clip API's
  `ClipStandbyPolicy` below).)*
  - Examples: maximum prepared decoders, maximum memory estimate, auto-evict
    inactive prepared entries, and per-cue opt-out.
  - Reason: keeping several long H.264 files opened and seeked can be expensive.

- [x] Consider a framework-level cue/clip API that can power HaPlay, soundboards,
  and cue-player hosts. *(Design RFC written and phase-1 framework surface implemented —
  `Doc/MediaFramework-Cue-Clip-API-RFC.md`. Found the framework already carries the cue
  vocabulary (`CueGraph`/`CueDefinition`/`CueShowFile` with `PreloadKey`/`NotReady`,
  `Soundboard`/`CueVoice`, `MediaGraph` topologies) and the clip primitives (`ClipWindow`,
  `RetimingVideoOutput`), but no standby engine — the five guarantees live only in HaPlay's
  `CuePlaybackEngine`. `S.Media.Playback.ClipStandbyEngine` now provides the
  UI-agnostic source lifecycle slice (`ClipSpec`, `ClipPreparationState`, `IPreparedClip`,
  `IArmedClip`, `IClipStandbyEngine`): builder-based open, `ClipWindow.Start` seek,
  cache-keyed non-consuming standby, decoder cap/window policy, explicit `Start()`, and
  grouped starts. HaPlay now delegates the file-cue source lifecycle to the standby
  engine; shared output-runtime ownership and video-capable soundboard remain follow-ups.)*
  - Desired guarantees: non-consuming standby, explicit start barrier,
    coordinated grouped starts, clip-relative audio/video timing, and clear
    output ownership.

## Review Notes On The Cue Items Above

- `deferPlay` removal (item 1): confirmed dead — `OpenCueEntryAsync` never reads the
  parameter; both call sites pass it through but the body only seeks to
  `ClipWindow.Start` and wires routes. The transport decision already lives in
  `ExecuteCoreAsync`. Safe pure-delete.
- `VideoClockMaster` rename (item 2): the field is assigned from
  `CueAudioOutputRuntime.PlaybackClock` and is also handed to
  `MediaPlayer.Play(videoOnlyMaster:)`, not only to the composition. Prefer
  `PlaybackClockMaster` so it reads correctly at both the audio-source and the
  `Play(...)` call sites.
- Pre-roll refresh on property edits (correctness item 2): note that
  `BuildPreparedCueKey` already hashes gain, channels, opacity, offsets, comp size,
  and output bindings — so a stale standby entry is correctly *rejected* at Go
  (key mismatch -> reopen). The only real loss is standby warmth, which matches
  the backlog's "remaining issue" framing. This is a warmth/UX fix, not a
  correctness fix.

## Regular Media Player — Fixes & Enhancements

Same desktop app, the non-cue `MediaPlayerViewModel` playout path. These are
independent of the cue work above.

### Fixes / Correctness-Adjacent

- [x] Tighten the auto-advance / loop boundary gap. *(Done — `OnLoopTimerTick`
  now adapts to a 60 ms poll within 1.5 s of a finite track's end and relaxes back
  to 500 ms otherwise; live/idle stay on the relaxed cadence.)*
  - Current: `OnLoopTimerTick` polls natural completion on a fixed 500 ms
    `DispatcherTimer`, so a track can run up to ~500 ms past its end before the
    next item (or loop restart) is triggered.
  - Suggested: shorten the interval as `CurrentPosition` approaches `Duration`,
    or drive the boundary from the router's natural-completion signal instead of
    a poll. Keep the 500 ms poll for the live reconnect/stats path.

- [x] Warm the next playlist item before auto-advance. *(Already present —
  `PlaylistDecoderCache` pre-opens the next decoder on play-start, consumed by
  `OpenOrReloadAsync`. Improved: `PreOpenAdjacentPlaylistItems` now follows the
  active playback tab so auto-advance reliably hits a warm decoder. Follow-up fix:
  now shuffle-aware — it warms the bag's actual next track via the non-consuming
  `PeekAutoAdvanceNext`, so shuffle auto-advance no longer opens a cold decoder; see
  the follow-up section below.)*
  - Current: cue mode has pre-roll, but normal playlist advance opens the next
    file cold inside `PlayPlaylistItemAsync`, adding a decoder-open stall at every
    boundary.
  - Suggested: a small "next item" prepared-decoder cache analogous to the cue
    pre-roll, invalidated on playlist/selection edits.

- [x] Confirm what `TransitionMode` / `TransitionDurationMs` actually do on a
  regular playlist advance. *(Done — investigated: `Fade` wraps the incoming clip
  in `FadeFromBlackVideoSource` (video fade-in-from-black) on every open including
  auto-advance, so it is NOT a no-op; it is not a cross-fade and does not fade the
  outgoing clip or audio. `IdleImage` is unwired (reserved). Resolved by scoping:
  accurate tooltip added + behavior documented; a true cross-fade is left as a
  separate larger feature.)*
  - `HaPlayFilePlaybackOptions` feeds these into cue fade timing, but it is not
    obvious they produce a real cross-fade/dip between two consecutive playlist
    items in plain player mode.
  - Decision to capture: either wire an actual transition on auto-advance, or
    scope the control so it isn't a silent no-op for the regular player.

- [x] Debounce slider scrubbing. *(Done — `SeekToSliderAsync` now coalesces
  rapid commits to latest-wins behind a small gate so a burst of releases runs one
  trailing seek instead of N queued arcs.)*
  - Current: every `SeekToSliderAsync` commit runs a full
    seek + prepare-outputs + play arc bounded at 3–5 s, serialized behind
    `WithPlaybackArcAsync`; fast drags on large files queue behind each other.
  - Suggested: latest-request-wins debounce, or commit on drag-release with a
    lightweight position preview while dragging.

### Enhancements

- [x] Playlist play modes beyond the current two. *(Done — added per-tab Shuffle
  (Fisher–Yates bag, each track once per cycle) and Repeat-all-list, persisted,
  applied to auto-advance; manual Next/Previous stay linear. Two new toggles.)*
  - Today only loop-current (`IsLooping`) and `AutoAdvance` exist.
  - Add shuffle and repeat-all-list (distinct from loop-single).

- [x] End-of-track low-time warning. *(Done — the middle clock turns red/semibold
  within 10 s of a finite track's end via a themed `lowtime` style class driven by
  `IsNearEndOfTrack`.)*
  - `RemainingTime` is already computed; color/flash the remaining readout under a
    configurable threshold (e.g. last 10 s) as a standard playout operator aid.

- [x] Structured player load/error state. *(Done — see implementation note in the
  Larger-Enhancements cross-reference; a sticky `LastLoadError` (with the failing
  file) plus a `PlayerLoadState` enum replace the transient-only status string.)*
  - Mirror the cue "idle / preparing / ready / failed" suggestion (cue item under
    "Surface richer prepared-cue state"): replace the transient `StatusMessage`
    string with a sticky last-error that names the failing file, alongside the
    existing `IsWaitingForSource` live-retry state.

- [x] Keyboard transport parity for the focused player. *(Done — added Home
  (seek-to-0) and `+`/`-` volume to the existing Space / `[` `]` / `,` `.` set;
  arrow keys deliberately left to list navigation. Hint strings updated.)*
  - Jog `,` / `.` exist; add space = play/pause, Home = seek-to-0, and
    volume up/down, then surface the set in a shortcuts list. (App-level bindings
    already live in `MainView.axaml`.)

- [x] Reuse the cue audio downmix presets (see "Add cue audio downmix presets")
  in the player's audio matrix. *(Done — the shared `AudioDownmixPresets` quick-apply
  buttons appear in both the cue route editor and the player matrix.)*
  - The player already exposes a matrix; expose the same `5.1 -> stereo`,
    `mono -> stereo`, and LFE-drop presets as quick-apply buttons so operators
    don't hand-enter cells.

## Follow-Up Review Fixes (2026-06-03)

A second review pass over the completed items above (full build + test suite green)
found three gaps and one stale checkbox; all are now resolved.

- **Shuffle defeated the warm-decoder pre-open.** `PreOpenAdjacentPlaylistItems`
  warmed only the *linear* neighbours (`idx ± 1`), but with Shuffle on, auto-advance
  picks the next track from the shuffle bag — so every shuffle advance opened a cold
  decoder, defeating the "warm next item" fix whenever the new shuffle mode was on.
  Fixed: a non-consuming `PeekAutoAdvanceNext` returns the item auto-advance will
  actually choose (shuffle bag, or linear/repeat-all), and the pre-open warms that plus
  the linear neighbours for manual Next/Previous (deduped; the cache still caps at
  `MaxEntries = 3`). The *first* shuffle advance can still be cold because the bag is
  built lazily on that advance — a deliberate trade so the pre-warm path doesn't commit
  the random order.

- **`DisablePreRoll` was only half-honored.** The per-cue "Disable pre-roll" checkbox
  was respected by the engine's file open/seek/route cache
  (`GetPreparedMediaCueTargets`) but ignored by the NDI and PortAudio pre-connect passes
  (`GetNdiPreConnectTargets`, `GetPortAudioPreConnectTargets`) — both consumed in
  production by `MainViewModel.RefreshCuePreRollAsync`. Fixed: both pre-connect filters
  now skip cues with `DisablePreRoll` set, so the checkbox is a uniform opt-out. (The
  broader resource-policy item under Larger Enhancements is still open.)

- **Removed orphaned `GetPreRollTargets`.** This `CuePlayerViewModel` method (and its
  lone test) had no production caller — the engine-side `GetPreparedMediaCueTargets` is
  the live cue file pre-roll path. Deleted as stale, in keeping with the `deferPlay`
  cleanup theme.

- **Reconciled the cue-downmix checkbox.** "Add cue audio downmix presets or a per-cue
  matrix" was left unchecked even though the presets shipped — and the player-side reuse
  item, which depends on them, was already checked. Marked the presets done and split the
  still-open *stored per-cue matrix* into its own item.

### Known scope trims (not bugs)

- The end-of-track low-time warning uses a hard-coded 10 s threshold
  (`LowTimeWarningThreshold`); the original note suggested a configurable threshold.
- Keyboard transport is surfaced via control tooltips / hint strings rather than a
  dedicated shortcuts list.

## Larger-Enhancements Pass (2026-06-03)

Worked the remaining Larger Enhancements. Most were already implemented in code with
stale checkboxes; the genuine gaps were closed and the framework cue/clip item now has
both an RFC and a phase-1 playback implementation.

- **Richer prepared-cue state — reconciled + `Stale` added.** The state badge
  (Idle/Preparing/Ready/Failed + failure-reason tooltip) already existed. Added the
  distinct `Stale` state: an in-place edit to the selected cue marks its warm standby
  stale (`CuePlayerViewModel.CueStandbyInvalidated` → `CuePlaybackEngine.MarkPreparedCueStale`)
  until the debounced refresh re-prepares it. New glyph/tooltip/colour for `Stale`.
- **Pre-roll resource policy — reconciled + configurable cap.** The decoder cap +
  auto-evict + per-cue opt-out already existed; made the cap operator-configurable per
  cue list (`CueList.MaxPreparedDecoders`, default 6, clamped [1,16], in the Cue List
  Settings dialog) and made preparation stop at the cap instead of opening-then-evicting.
  Memory-estimate eviction deferred.
- **Stored per-cue downmix matrix — deferred** as largely redundant with existing
  `AudioRoutes` (see the item note).
- **Framework cue/clip API — RFC + phase-1 implementation.** RFC at
  `Doc/MediaFramework-Cue-Clip-API-RFC.md`; implementation in
  `S.Media.Playback.ClipStandbyEngine` with focused playback tests. HaPlay file-cue
  source lifecycle now delegates to the framework standby engine; shared output-runtime
  ownership and video-capable soundboard are the next extraction steps.

Coverage: added `MaxPreparedDecoders` round-trip/default tests and `Stale`-state
glyph/tooltip/mapping tests. Full suite green (142 tests).
Additional phase-1 cue/clip API coverage: `ClipStandbyEngineTests` verifies standby
reuse, cache-key replacement, failed status reporting, policy caps, and grouped starts.

## Verification To Run After Cleanup Changes

- `dotnet build MFPlayer.sln -m:1 --no-restore -v:m`
- `dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --no-build --logger "console;verbosity=minimal"`
- `git diff --check`
