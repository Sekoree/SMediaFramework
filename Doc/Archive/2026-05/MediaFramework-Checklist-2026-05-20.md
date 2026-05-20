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

### [x] P1-01 Expose best-effort media duration from decoder/player API

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

Status Notes:

- Added `MediaContainerDecoder.Duration` and `MediaPlayer.Duration`.
- Added FFmpeg coverage for video-only duration fallback.

### [ ] P1-02 Update HaPlay to use unified duration source

Deferred:

- HaPlay/UI work is deferred for the planned larger UI refactor.

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

### [x] P1-03 Stabilize NDI timestamp synthesis test

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

Status Notes:

- `NDIIngestPlaybackClock` now accepts an internal timestamp provider for
  deterministic tests while keeping the public constructor unchanged.
- Timestamp/timecode tests freeze wall extrapolation instead of depending on
  scheduler timing.

### [x] P1-04 Clarify and test resampler ownership contract

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

Status Notes:

- Updated `AudioRouter.AddSource` and `ResamplingAudioSource` docs to describe
  wrapper ownership and original-source ownership separately.
- Added router-dispose coverage for auto-resampled sources.

## P2 - Maintainability and End-User Polish

### [x] P2-01 Decommission legacy `VideoOutputRouter`

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

Status Notes:

- `VideoOutputRouter` is marked obsolete with migration guidance to Core
  `VideoRouter`.
- Its focused legacy tests remain in place with obsolete warnings suppressed so
  the compatibility path is covered until removal.

### [x] P2-02 Archive stale review/checklist docs

Scope:

- Archive old files:
  `Doc/MediaFramework-Review-2026-05.md`,
  `Doc/MediaFramework-Checklist-2026-05.md`.

Status:

- Archived to `Doc/Archive/2026-05/`.

### [x] P2-03 Build and environment hygiene

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

Status Notes:

- Added `global.json` pinned to SDK `10.0.300` with feature-band roll-forward.
- Added `Doc/Build-Environment.md` with build/test commands, package groups,
  native runtime expectations, and a CI baseline.

### [ ] P2-04 Replace ad-hoc HaPlay console logging

Deferred:

- HaPlay/UI work is deferred for the planned larger UI refactor.

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

Deferred:

- HaPlay/UI work is deferred for the planned larger UI refactor.

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

Deferred:

- HaPlay/UI work is deferred for the planned larger UI refactor.

Scope:

- Surface audio/video pump drops, PortAudio underruns, and NDI sender pressure
  in HaPlay UI.

Acceptance Criteria:

- Operator can diagnose likely bottleneck without debugger logs.

### [ ] P3-02 Session profiles/presets

Deferred:

- HaPlay/UI workflow and persistence UX are deferred for the planned larger UI refactor.

Scope:

- Save/load named output setups:
  local, NDI, preview+program, composited hold image, audio device/channel map.

Acceptance Criteria:

- One-click recall of full output setup.

### [ ] P3-03 Volume/mute/per-route gain controls

Deferred:

- HaPlay/UI exposure is deferred for the planned larger UI refactor.

Scope:

- Expose master volume, mute, and route gain controls in HaPlay using existing
  router capabilities.

Acceptance Criteria:

- Gain/mute control is available without custom code by end users.

### [ ] P3-04 Preview/program and cue-stack workflow

Deferred:

- HaPlay/UI workflow is deferred for the planned larger UI refactor.

Scope:

- Add preview/program switching, take, hold frame/image, and transition flow.

Acceptance Criteria:

- Practical show-control workflow possible in HaPlay.

### [ ] P3-05 File-output/record sink

Scope:

- Add FFmpeg-backed sink to record final output program to file.

Acceptance Criteria:

- User can capture output audio+video without external recorder.

Status Notes:

- Left pending. The current FFmpeg package has decode/demux infrastructure but no encoder/muxer layer yet; implementing this cleanly should introduce a deliberate recording package/API rather than a narrow sink-only shortcut.

### [ ] P3-06 Optional broadcast hardware I/O package

Scope:

- Explore DeckLink (or equivalent) package integration as optional adapter.

Acceptance Criteria:

- Hardware I/O integration can be enabled without core API churn.

Status Notes:

- Left pending. This should remain an optional adapter package so Core/Playback APIs do not take a hard dependency on vendor SDKs.

### [x] P3-07 Headless compositor CI smoke

Scope:

- Add offscreen compositor smoke test with deterministic frame checks.

Acceptance Criteria:

- CI validates compositor render path at least on one representative setup.

Status Notes:

- Added `HeadlessCompositorSmokeTests` covering deterministic CPU compositor output without a display, SDL, or GL context.

### [x] P3-08 URI/stream open API helpers

Scope:

- Add explicit helpers such as `TryOpenFile`, `TryOpenUri`, `TryOpenStream`.

Acceptance Criteria:

- Network/stream use cases are first-class without file-path ambiguity.

Status Notes:

- Added `MediaContainerDecoder.OpenFile`, `OpenUri`, and finite `OpenStream`.
- Added `MediaPlayer.TryOpenFile`, `TryOpenUri`, and finite `TryOpenStream`.
- File URIs validate local files; non-file absolute URIs are passed to FFmpeg protocol I/O; finite streams are spooled to decoder-owned temporary files.

### [x] P3-09 OSC timetag scheduling

Scope:

- Implement `OSCServerOptions.IgnoreTimeTagScheduling` behavior and scheduler.

Acceptance Criteria:

- OSC bundle timetags are handled predictably for show-control workflows.

Status Notes:

- `IgnoreTimeTagScheduling = true` keeps immediate dispatch behavior.
- `IgnoreTimeTagScheduling = false` now delays future-dated bundles until their OSC/NTP timetag while dispatching immediate and past timetags without delay.
- Added OSC timetag conversion and scheduler coverage.

## Coverage Backlog (Tests)

### [x] T-01 Add `QuickPlayer` integration test

Goal:

- Verify quick API starts audio output and drives media clock correctly.

Status:

- Added `S.Media.Quick.Tests` coverage for the audio startup gate used by `QuickPlayback.Play`, including prefill/start ordering, single hardware start, and retry behavior after prefill failure.
- Full SDL3/PortAudio end-to-end smoke coverage remains better suited to a hardware/display smoke runner.

### [ ] T-02 Add HaPlay view-model test project

Goal:

- Cover add/open/play/pause/stop/seek/loop and route acquisition/release.

Status:

- Deferred with the rest of HaPlay/UI work until the planned UI refactor.

### [x] T-03 Add OSCLib test project

Goal:

- Cover codec, bundle parsing, route matching, and timetag behavior.

Status:

- Added `OSCLib.Tests` coverage for message/bundle encode-decode, OSC address pattern matching, and strict nested bundle timetag validation.

### [x] T-04 Add PMLib/MIDI parser test project

Goal:

- Cover channel messages, SysEx accumulation, and edge framing cases.

Status:

- Added `PMLib.Tests` coverage for MIDI message parsing, high-resolution CC/NRPN accumulation, and SysEx fragment/realtime edge cases.
- Extracted SysEx accumulation from `MIDIInputDevice` into an internal testable accumulator while preserving the input-device event surface.

## Recommended Execution Order

1. Complete `P0-01` and `P0-02`.
2. Complete `P1-01` to `P1-04`.
3. Complete `P2-03` to `P2-05`.
4. Complete `T-02` after the HaPlay/UI refactor scope is ready.
