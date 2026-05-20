# Media Framework Findings - 2026-05-20

This review covers the media framework as a whole, not only the recent refactor
surface. It includes `MediaFramework`, `UI/HaPlay`, tools, tests, and the current
documentation in `Doc/`. Generated `bin` and `obj` output was ignored.

The working tree already had local changes in `MFPlayer.sln` and
`MediaFramework/Tools/CompositorSmoke/Program.cs` before this document was
written; this review did not modify them.

## Executive Summary

The framework is in a much better shape than the older May review implies. The
core media contracts are small, the router abstractions are now in the right
layer, format support is documented, and most focused test projects pass.

The highest-priority follow-ups are:

1. Fix `MFPlayer.sln`; it currently contains `CompositorSmoke.csproj` twice and
   cannot be listed or built as a solution.
2. Fix `QuickPlayer` audio startup. The helper wires PortAudio but does not
   start the hardware output path, so audio playback through the quick API is
   likely silent or incomplete.
3. Fix HaPlay duration and seeking for video-only files. The UI currently reads
   duration only from the audio decoder.
4. Stabilize the known NDI timestamp test by controlling time in the test or by
   relaxing the assertion around wall-clock extrapolation.
5. Clean up stale documentation from the previous review so users do not follow
   obsolete class names and fixed recommendations.

## Verification Snapshot

Commands run during this review:

| Area | Result |
| --- | --- |
| `dotnet sln MFPlayer.sln list` | Fails: duplicate `MediaFramework/Tools/CompositorSmoke/CompositorSmoke.csproj` entry. |
| `dotnet build MFPlayer.sln --no-restore` | Fails with no compiler diagnostics because the solution is invalid. |
| `S.Media.Core` build | Passed. |
| `S.Media.FFmpeg` build | Passed. |
| `S.Media.OpenGL` build | Passed. |
| `S.Media.SkiaSharp` build | Passed. |
| `PALib`, `JackLib`, `NDILib`, `OSCLib`, `PMLib` builds | Passed when built individually; one `PMLib` run exited 139, rerun passed. |
| `S.Media.Avalonia`, `S.Media.SDL3`, `S.Media.PortAudio`, `S.Media.NDI`, `HaPlay`, `HaPlay.Desktop` builds | Local CLI runs failed silently after restore/build startup. Reproduce after the solution duplicate is fixed so the actual failure can be surfaced. |
| `S.Media.Core.Tests` | Passed: 392 tests. |
| `S.Media.FFmpeg.Tests` | Passed: 116 tests. |
| `S.Media.SkiaSharp.Tests` | Passed: 8 tests. |
| `S.Media.OpenGL.Tests` | Passed: 38 tests. |
| `S.Media.PortAudio.Tests` | Passed: 21 tests. |
| `S.Media.Playback.Tests` | Passed: 3 tests. |
| `S.Media.NDI.Tests` | Failed: 1 known timing assertion, 24 passed. |

The test runner needed permission to open local sockets in this environment. The
failures above are therefore test results from an elevated `dotnet test` run, not
from the default sandboxed run.

## Findings

### 1. Solution file is invalid

`MFPlayer.sln` contains two project entries for
`MediaFramework/Tools/CompositorSmoke/CompositorSmoke.csproj`. As a result,
`dotnet sln MFPlayer.sln list` fails with:

```text
Duplicate item 'MediaFramework/Tools/CompositorSmoke/CompositorSmoke.csproj' of type 'Project'
```

Impact:

- The top-level solution cannot be used as the normal developer entry point.
- CI that builds the solution will fail before reaching useful compile errors.
- IDE project load behavior may depend on which duplicate entry is interpreted.

Recommendation:

- Remove the duplicate `CompositorSmoke` project entry and its nested project
  mapping from `MFPlayer.sln`.
- Keep only the existing valid project identity and place it under the Tools
  solution folder.
- Add a CI step that runs `dotnet sln MFPlayer.sln list` before build/test.

Priority: P0.

### 2. `QuickPlayer` wires PortAudio but does not start output

`MediaFramework/Media/S.Media.Quick/QuickPlayer.cs` creates a
`PortAudioPlaybackHost` through `TryWirePortAudioMainForPlayer`, but
`QuickPlayback.Play()` only calls:

- `_mediaPlayer.Audio?.Play()`
- `_mediaPlayer.Video.Play()`

The working playback smoke path uses a different sequence:

- `AudioHost.PrefillMainOutputDirectFromDecoder(...)`
- `AudioHost.StartHardwareOutput()`
- `Session.Play()`

Impact:

- The advertised quick API can successfully open media and attach a PortAudio
  host while never starting the actual PortAudio stream.
- End users trying the simplest API path may get video with no audible audio.
- This is especially damaging because `QuickPlayer` is the API most likely to
  be copied from examples.

Recommendation:

- Give `QuickPlayback` ownership of the returned `PortAudioPlaybackHost`.
- On `Play()`, prefill once and call `StartHardwareOutput()` before starting the
  clocked media session.
- On pause/stop/dispose, mirror the lifecycle used by the playback smoke session
  so the hardware stream and media graph cannot drift apart.
- Add an integration-style test around a fake or test audio host that verifies
  `Play()` starts output.

Priority: P0.

### 3. HaPlay duration uses only the audio decoder

`UI/HaPlay/ViewModels/MediaPlayerViewModel.cs` sets player duration from:

```csharp
created.Player.Decoder.Audio is ISeekableSource a ? a.Duration : TimeSpan.Zero
```

The video decoder is also seekable, and the shared demux layer exposes video
track duration. For video-only files, the UI therefore reports zero duration and
seek behavior becomes degraded or unavailable.

Impact:

- Video-only files appear as zero-length media in the UI.
- Seeking and progress display can be wrong even though the decoder has enough
  information.
- This leaks low-level decoder details into the UI.

Recommendation:

- Add a `Duration` facade on `MediaContainerDecoder` or `MediaPlayer` that
  returns the best available container duration.
- Prefer audio duration when audio exists, video duration for video-only files,
  and container duration when both streams are present but stream durations
  differ slightly.
- Update HaPlay to consume that single public property.
- Add a video-only UI or view-model test.

Priority: P1.

### 4. NDI timestamp test is still time-sensitive

`S.Media.NDI.Tests.Clock.NDIIngestPlaybackClockTests.Timestamp_UsedWhenTimecodeSynthesize`
expects the elapsed clock time after a timestamped frame to be within 512 ticks.
`NDIIngestPlaybackClock.ElapsedSinceStart` also extrapolates from wall-clock time
since the last frame notification, so reading immediately after `NotifyAudioFrame`
can include scheduler noise. In this review the observed value was about
0.0102843 seconds when the test expected 0.0100000 seconds.

Impact:

- The implementation can be correct while the test fails on a busy or slow
  runner.
- CI reliability will suffer because the test depends on host scheduling.

Recommendation:

- Inject a time provider or stopwatch into `NDIIngestPlaybackClock` so tests can
  freeze elapsed wall time.
- Alternatively split the test into "timestamp accepted" and "wall-clock
  extrapolation" cases with wider tolerance only where extrapolation is under
  test.

Priority: P1.

### 5. Audio resampler ownership contract is ambiguous

The automatic FFmpeg resampler factory used by the router sets
`disposeInnerWhenDisposed: false`, which is the right choice for router-managed
sources. However:

- `ResamplingAudioSource` documentation still describes wrapper ownership as
  the default behavior.
- `AudioRouter.AddSource` documentation says the wrapper takes ownership of the
  original source.
- The actual ownership depends on the registered compatibility factory.

Impact:

- Public XML docs can lead consumers to dispose the wrong object or retain an
  object they expected the router to own.
- This matters because routers and wrappers sit on hot paths that many sample
  apps will copy.

Recommendation:

- Update the XML docs to describe the current router/factory ownership model.
- Consider making ownership explicit in the factory delegate type or wrapping
  result object, rather than implicit in each factory implementation.
- Add a small test that verifies disposing a router-installed resampler does not
  dispose the original source.

Priority: P1.

### 6. Legacy `VideoOutputRouter` duplicates the Core `VideoRouter`

`MediaFramework/Media/S.Media.FFmpeg/Video/VideoOutputRouter.cs` still exists
after video routing moved into Core. Its current usage appears to be limited to
its tests and old documentation references.

Impact:

- New consumers can pick the FFmpeg-specific router by mistake.
- Two router concepts make examples and docs harder to follow.
- Maintenance effort is split across a legacy path and the Core path.

Recommendation:

- Mark `VideoOutputRouter` obsolete and point users to Core `VideoRouter`.
- Remove it after any remaining callers are migrated.
- Keep compatibility only if a real downstream consumer still depends on the
  exact old API.

Priority: P2.

### 7. Documentation has stale review content

`Doc/MediaFramework-Review-2026-05.md` still references older names and older
gaps that the checklist says have since been fixed, including:

- `AvRouter`
- `MediaContainerMegaPlaybackHost`
- old 48000 Hz defaults
- previous `VideoRouter` placement concerns

Impact:

- A new user can read the old review and receive contradictory guidance from the
  newer checklist and format-support document.
- The old review makes the framework look less complete than it is.

Recommendation:

- Add a clear "superseded by" note at the top of the older review, or move it to
  an archive section.
- Keep `MediaFramework-Checklist-2026-05.md` as the status document and this file
  as the follow-up findings document.
- Update class names and example snippets in any public-facing docs.

Priority: P2.

### 8. Some build failures are not actionable enough

Several package/UI/native-adapter projects failed locally with no useful
diagnostic output after build or restore startup, while others built cleanly.
This may be a local SDK/MSBuild/toolchain issue, but silent failure is still a
developer-experience problem.

Impact:

- Contributors cannot tell whether they are missing a workload, a native
  dependency, a package source, or a code fix.
- The working build command for the repository is unclear.

Recommendation:

- Fix the invalid solution first.
- Add a `global.json` to pin the expected .NET SDK, or document the minimum and
  tested SDK versions.
- Add a root build script or documented command that builds supported projects in
  a known order.
- Make native dependency requirements explicit for PortAudio, NDI, SDL3,
  Avalonia, SkiaSharp, JACK, and PipeWire.

Priority: P2.

### 9. HaPlay still exposes development noise and lacks automated coverage

`MediaPlayerViewModel.AddFilesToPlaylistAsync` emits several `Console.WriteLine`
messages during normal file-add flow. HaPlay also has no automated tests despite
owning transport state, route acquisition/release, hold-image behavior, playlist
logic, and output mode selection.

Impact:

- End-user logs are noisy.
- Complex UI state can regress without any test signal.

Recommendation:

- Replace ad-hoc console output with structured debug logging or remove it.
- Add view-model tests for file add, play/pause/stop, seek, loop, route
  acquisition, and video-only duration.
- Keep UI tests at the view-model level first; full Avalonia UI automation can
  come later.

Priority: P2.

### 10. Auto output routing should be user-configurable

HaPlay's automatic route choice prioritizes NDI when NDI is available. That is a
reasonable broadcast default, but it is not always the best first-run behavior
for an end-user player.

Impact:

- Users with NDI installed may unintentionally route output to the network
  instead of local preview and speakers.
- First-run behavior can feel surprising.

Recommendation:

- Add an output preference setting: local, NDI, or last used.
- Persist the selected audio device, video output mode, NDI source name, and
  preview/window preference.
- Surface the selected route clearly in the transport or output panel.

Priority: P2.

## Useful Feature Opportunities

### Output health panel

The framework already tracks useful pressure indicators in several places:
audio pump drops, video pump drops, PortAudio underruns/dropped samples, and NDI
sender pressure. HaPlay should surface these as per-output health indicators.

This would make end-user troubleshooting much easier: users could distinguish a
bad file, an overloaded renderer, an audio device underrun, and an NDI network
backpressure issue without attaching a debugger.

### Output presets and session profiles

Add user-facing profiles for common setups:

- Local preview and speakers.
- NDI program output.
- Preview plus NDI program.
- Composited output with a hold image.
- Named audio device plus channel mapping.

The framework already has most of the primitives. Persisting and recalling them
would make the app easier to use in real sessions.

### Volume, mute, and per-route gain in HaPlay

`AudioRouter` supports route-level gain and channel maps, but HaPlay should make
basic gain controls visible:

- Master volume.
- Mute.
- Per-output gain.
- Optional simple stereo balance or channel map preset.

This is a high-value user feature because it does not require new media backend
work.

### Preview/program and cue-stack workflow

The compositor, image source, text overlay source, output router, and route
layers make a simple show-control workflow realistic:

- Preview current/next item.
- Take to program.
- Hold frame/image when media stops.
- Fade between playlist items.
- Optional lower-third or slate overlay.

This would move HaPlay from a media test harness toward a practical playback
tool.

### Recording or file-output sink

Add an FFmpeg-backed sink that records the final audio/video output to a file.
This is useful for rehearsals, test captures, and support repros. It also gives
the framework a clear "what actually went to output" artifact.

### DeckLink or other broadcast I/O

NDI is already covered. A DeckLink output/input package would make the framework
more useful in broadcast and live-event environments. Keep this optional and in a
separate package so the core API remains small.

### Headless compositor smoke test

The OpenGL compositor currently relies on smoke tools and focused unit tests.
Add a headless or offscreen compositor smoke test in CI that renders a few frames
with:

- one video/image layer,
- one text layer,
- opacity tween,
- transform/crop,
- readback validation.

This would protect one of the more valuable refactor outcomes.

### URI and stream opening helpers

`MediaPlayer.TryOpen(string)` currently behaves like a file-oriented API. FFmpeg
can handle many URI and stream sources, so consider adding explicit helpers:

- `TryOpenFile(path)`
- `TryOpenUri(uri)`
- `TryOpenStream(stream, options)`

This keeps the simple API clear while enabling network and embedded use cases.

### OSC timetag scheduling

`OSCServerOptions.IgnoreTimeTagScheduling` is documented as currently not
consumed. Implementing OSC bundle timetag scheduling would be useful for
show-control and cue workflows, especially if HaPlay grows cue-stack behavior.

## Test Coverage Gaps

The strongest current coverage is around Core, FFmpeg, OpenGL, PortAudio,
Playback, SkiaSharp, and NDI. The main gaps are:

- No visible OSCLib test project.
- No visible PMLib/MIDI parser test project.
- No HaPlay view-model tests.
- No integration test around `QuickPlayer` as the easiest public entry point.
- No CI-grade compositor smoke test that validates rendered output.

Recommended near-term tests:

1. `QuickPlayer.Play()` starts PortAudio output when audio is present.
2. Video-only files report non-zero duration through HaPlay.
3. Router-installed resampler disposal does not dispose the wrapped source.
4. OSC route matching, bundle parsing, and timetag scheduling.
5. MIDI message parsing, SysEx accumulation, and device hot-plug behavior.
6. NDI clock tests with injected time.

## Suggested Priority Backlog

### P0 - Fix before relying on the repo as a product entry point

- Remove the duplicate `CompositorSmoke` entry from `MFPlayer.sln`.
- Fix `QuickPlayer` so audio hardware output starts through the quick API.

### P1 - Fix before expanding public examples

- Add a media/container duration facade and update HaPlay duration handling.
- Stabilize the NDI clock test.
- Clarify resampler/router ownership documentation and tests.

### P2 - Improve maintainability and end-user polish

- Obsolete or remove legacy `VideoOutputRouter`.
- Mark stale May review content as superseded.
- Replace HaPlay console logging with proper debug logging.
- Add HaPlay view-model coverage.
- Document or pin the supported SDK and native dependency set.
- Make automatic output routing user-configurable.

### P3 - Product features

- Output health panel.
- Output/session presets.
- Volume, mute, and per-route gain controls.
- Preview/program and cue-stack workflow.
- Recording/file-output sink.
- DeckLink package.
- Headless compositor smoke test.
- URI and stream opening helpers.
- OSC timetag scheduling.

## Closing Notes

The main architecture now looks coherent: Core owns common media contracts and
routing, package-specific projects provide adapters, and the newer docs describe
format support clearly. The remaining work is less about another large refactor
and more about making the public entry points reliable, making the UI friendlier
for real users, and removing stale paths that obscure the current design.
