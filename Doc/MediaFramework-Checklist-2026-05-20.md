# Media Framework Checklist - 2026-05-20

This checklist replaces the archived May review/checklist and is based on:

- `Doc/MediaFramework-Findings-2026-05-20.md`
- `Doc/MediaFramework-Format-Support.md`

## Status Legend

- `[ ]` not started
- `[~]` in progress
- `[x]` done
- Priority: `P0` block release/developer workflow, `P1` correctness/reliability,
  `P2` maintainability/polish, `P3` product expansion

## P0 - Immediate Blockers

### [x] P0-01 Fix invalid solution file (`MFPlayer.sln`)

Scope:

- Remove duplicate `CompositorSmoke.csproj` project entry.
- Remove duplicate nested-project mapping.
- Keep one canonical `CompositorSmoke` GUID entry under the Tools folder.

Acceptance Criteria:

- `dotnet sln MFPlayer.sln list` succeeds.
- `dotnet build MFPlayer.sln --no-restore` no longer fails due to duplicate
  project metadata.
- CI includes a solution integrity check before build/test.

Verification:

```bash
dotnet sln MFPlayer.sln list
dotnet build MFPlayer.sln --no-restore
```

Status Notes:

- `dotnet sln MFPlayer.sln list` now succeeds and lists one `CompositorSmoke`
  project path.
- `dotnet restore MFPlayer.sln` succeeds.
- `dotnet build MFPlayer.sln --no-restore /nr:false` succeeds with 0 warnings
  and 0 errors.

### [x] P0-02 Fix `QuickPlayer` audio output startup path

Scope:

- Ensure `QuickPlayback.Play()` starts the PortAudio hardware stream when the
  quick helper wires a `PortAudioPlaybackHost`.
- Mirror the smoke-path lifecycle sequence:
  prefill -> start hardware output -> start media session.
- Ensure pause/stop/dispose cleanly handle host lifecycle.

Acceptance Criteria:

- Audio plays through the quick API for files with audio streams.
- No regression for video-only playback.
- No repeated-start exceptions when toggling play/pause/stop.

Verification:

```bash
dotnet test MediaFramework/Test/S.Media.Playback.Tests/S.Media.Playback.Tests.csproj
dotnet run --project MediaFramework/Tools/VideoPlaybackSmoke/VideoPlaybackSmoke.csproj
```

Status Notes:

- `QuickPlayback.Play()` now routes media playback through the existing
  `MediaContainerSession.Play(...)` path with PortAudio prefill and hardware
  start callbacks.
- `S.Media.Quick` builds successfully.
- `S.Media.Playback.Tests` passed: 3/3.

## P1 - Correctness and Reliability

### [ ] P1-01 Expose best-effort media duration from decoder/player API

Scope:

- Add `Duration` on `MediaContainerDecoder` or `MediaPlayer` as a stable UI/API
  surface.
- Use best available duration:
  audio duration when audio exists, video duration for video-only content, and
  container-level fallback where needed.

Acceptance Criteria:

- Video-only files report non-zero duration where demux data provides it.
- Existing audio+video duration behavior remains unchanged or intentionally
  improved with tests.

Verification:

```bash
dotnet test MediaFramework/Test/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj
```

### [ ] P1-02 Update HaPlay to use unified duration source

Scope:

- Replace direct audio-decoder duration reads in
  `UI/HaPlay/ViewModels/MediaPlayerViewModel.cs`.
- Keep seeking and progress UI behavior coherent for audio-only, video-only, and
  audio+video media.

Acceptance Criteria:

- Video-only files show expected duration and seek bar range.
- Seeking behavior remains functional for all media types.

Verification:

```bash
dotnet build UI/HaPlay/HaPlay.csproj
```

### [ ] P1-03 Stabilize NDI timestamp synthesis test

Scope:

- Refactor clock test to avoid strict dependence on host scheduler jitter.
- Prefer injectable time source in `NDIIngestPlaybackClock` tests.
- Alternatively isolate wall-clock extrapolation checks with wider tolerance.

Acceptance Criteria:

- `Timestamp_UsedWhenTimecodeSynthesize` is deterministic across repeated runs.
- No false negatives under moderate system load.

Verification:

```bash
dotnet test MediaFramework/Test/S.Media.NDI.Tests/S.Media.NDI.Tests.csproj --filter "FullyQualifiedName~NDIIngestPlaybackClockTests"
```

### [ ] P1-04 Clarify and test resampler ownership contract

Scope:

- Align docs/comments for `ResamplingAudioSource`, `AudioRouter.AddSource`, and
  FFmpeg compatibility factory behavior.
- Add test coverage to prove the intended disposal semantics.

Acceptance Criteria:

- Public docs match runtime behavior.
- Disposal tests enforce no unintended inner-source disposal for router-managed
  wrappers.

Verification:

```bash
dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj
dotnet test MediaFramework/Test/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj
```

## P2 - Maintainability and End-User Polish

### [ ] P2-01 Decommission legacy `VideoOutputRouter`

Scope:

- Mark `S.Media.FFmpeg.VideoOutputRouter` obsolete with migration guidance.
- Migrate remaining internal usage (if any) to Core `VideoRouter`.
- Remove legacy path once no consumers remain.

Acceptance Criteria:

- No ambiguity between FFmpeg legacy router and Core router for new code.
- Test suite still passes after migration.

Verification:

```bash
dotnet test MediaFramework/Test/S.Media.FFmpeg.Tests/S.Media.FFmpeg.Tests.csproj
```

### [x] P2-02 Archive stale review/checklist docs

Scope:

- Archive old files:
  `Doc/MediaFramework-Review-2026-05.md`,
  `Doc/MediaFramework-Checklist-2026-05.md`.

Status:

- Archived to `Doc/Archive/2026-05/`.

### [ ] P2-03 Build and environment hygiene

Scope:

- Add `global.json` or documented supported SDK matrix.
- Add a root build flow document/script with supported project order.
- Document native prerequisites for PortAudio, JACK, SDL3, NDI, Avalonia,
  SkiaSharp, PipeWire.

Acceptance Criteria:

- New contributors can run a documented build path without guesswork.
- Build failures produce actionable diagnostics.

Verification:

```bash
dotnet --info
dotnet restore
dotnet build MFPlayer.sln
```

### [ ] P2-04 Replace ad-hoc HaPlay console logging

Scope:

- Remove `Console.WriteLine` noise from normal playlist/file add workflows.
- Replace with structured debug logging where needed.

Acceptance Criteria:

- User-facing run output is clean by default.
- Diagnostic logs remain available in debug mode.

Verification:

```bash
dotnet build UI/HaPlay/HaPlay.csproj
```

### [ ] P2-05 HaPlay output-route preference

Scope:

- Add user setting for route preference:
  local preview/speakers, NDI first, or last-used route.
- Persist selected audio device/video mode/NDI source.

Acceptance Criteria:

- First-run behavior is predictable and user-configurable.
- Selected route persists across sessions.

Verification:

```bash
dotnet build UI/HaPlay/HaPlay.csproj
```

## P3 - Feature Expansion Backlog

### [ ] P3-01 Output health dashboard

Scope:

- Surface audio/video pump drops, PortAudio underruns, and NDI sender pressure
  in HaPlay UI.

Acceptance Criteria:

- Operator can diagnose likely bottleneck without debugger logs.

### [ ] P3-02 Session profiles/presets

Scope:

- Save/load named output setups:
  local, NDI, preview+program, composited hold image, audio device/channel map.

Acceptance Criteria:

- One-click recall of full output setup.

### [ ] P3-03 Volume/mute/per-route gain controls

Scope:

- Expose master volume, mute, and route gain controls in HaPlay using existing
  router capabilities.

Acceptance Criteria:

- Gain/mute control is available without custom code by end users.

### [ ] P3-04 Preview/program and cue-stack workflow

Scope:

- Add preview/program switching, take, hold frame/image, and transition flow.

Acceptance Criteria:

- Practical show-control workflow possible in HaPlay.

### [ ] P3-05 File-output/record sink

Scope:

- Add FFmpeg-backed sink to record final output program to file.

Acceptance Criteria:

- User can capture output audio+video without external recorder.

### [ ] P3-06 Optional broadcast hardware I/O package

Scope:

- Explore DeckLink (or equivalent) package integration as optional adapter.

Acceptance Criteria:

- Hardware I/O integration can be enabled without core API churn.

### [ ] P3-07 Headless compositor CI smoke

Scope:

- Add offscreen compositor smoke test with deterministic frame checks.

Acceptance Criteria:

- CI validates compositor render path at least on one representative setup.

### [ ] P3-08 URI/stream open API helpers

Scope:

- Add explicit helpers such as `TryOpenFile`, `TryOpenUri`, `TryOpenStream`.

Acceptance Criteria:

- Network/stream use cases are first-class without file-path ambiguity.

### [ ] P3-09 OSC timetag scheduling

Scope:

- Implement `OSCServerOptions.IgnoreTimeTagScheduling` behavior and scheduler.

Acceptance Criteria:

- OSC bundle timetags are handled predictably for show-control workflows.

## Coverage Backlog (Tests)

### [ ] T-01 Add `QuickPlayer` integration test

Goal:

- Verify quick API starts audio output and drives media clock correctly.

### [ ] T-02 Add HaPlay view-model test project

Goal:

- Cover add/open/play/pause/stop/seek/loop and route acquisition/release.

### [ ] T-03 Add OSCLib test project

Goal:

- Cover codec, bundle parsing, route matching, and timetag behavior.

### [ ] T-04 Add PMLib/MIDI parser test project

Goal:

- Cover channel messages, SysEx accumulation, and edge framing cases.

## Recommended Execution Order

1. Complete `P0-01` and `P0-02`.
2. Complete `P1-01` to `P1-04`.
3. Complete `P2-03` to `P2-05`.
4. Start `T-01` and `T-02` before major `P3` feature work.
