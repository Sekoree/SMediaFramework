# MediaFramework — Refactor Checklist (2026‑05‑22)

> Companion to `Doc/MediaFramework-Critical-Review-2026-05-22.md`.
> Phases are ordered from **do first** (low risk, biggest cleanup) to
> **do last** (most invasive, biggest surface change). Each phase is
> independently shippable; you can stop anywhere and the framework is still
> consistent.

## Legend

- 🔲 todo  · ✅ done  · ⏸ blocked  · ❌ skipped
- **Risk**: None · Low · Medium · High
- **Effort**: S (single sitting / <1 day) · M (1–2 days, single PR) · L (multi-PR / week+)
- **Breaking**: ✓ (consumer code must change) · ✗ (additive / mechanical)

## Sequencing principles

1. Mechanical work before structural work — renames and deletions first so
   later phases don't fight stale identifiers.
2. Additive helpers before surface narrowing — give consumers a new
   call-site to migrate to *before* deleting the old one.
3. Backend collapses (NDI, AudioPlayer) before the public‑API redesign so
   the builder/composition work is built on the final type names.
4. Cross-cutting feature work (bit depth, encoder, async, metrics) comes
   after the surface is settled — they're additive and benefit from the
   clean foundations.

---

## Phase 0 — Dead code, scaffolding, diagnostics ceremony

> **Risk**: None · **Effort**: M · **Breaking**: ✗ (every item is removal of
> unreferenced code or a pure helper)
>
> Goal: get the obviously-dead code out, drop the platform we don't ship,
> and stop the diagnostic-block copy-paste cycle. Sets the LOC baseline so
> later phases are measurable.

### 0.1 Delete unreferenced files

- ✅ `MediaFramework/Media/S.Media.FFmpeg/Video/VideoOutputRouter.cs` — already `[Obsolete]`, only self-referenced.
- ✅ `MediaFramework/Media/S.Media.Core/Video/MetalIosurfaceNv12Interop.cs`
- ✅ `MediaFramework/Media/S.Media.Core/Video/VulkanExternalNv12Interop.cs`
- ✅ `MediaFramework/Media/S.Media.Core/Video/WindowsNv12D3D11TextureInterop.cs`
- ✅ `MediaFramework/Media/S.Media.Core/Video/WindowsNv12SharedHandleInterop.cs`
- ✅ `MediaFramework/Media/S.Media.Core/Video/LinuxDmabufNv12Interop.cs`
- ✅ `MediaFramework/Media/S.Media.NDI/Clock/NDIEgressMuxPlayheadClock.cs` — confirmed self-only.
- ✅ `MediaFramework/Media/S.Media.NDI/Clock/NDIAlignedRouterClock.cs` — confirmed self-only.
- ✅ Verification: `dotnet build` clean; existing tests pass (807/807).

> Also deleted in this step (test files whose only target was now gone):
> `Test/S.Media.FFmpeg.Tests/Video/VideoOutputRouterTests.cs`,
> `Test/S.Media.Core.Tests/Video/HardwareVideoInteropTests.cs` (588 LOC of
> placeholder-interop unit tests),
> `Test/S.Media.NDI.Tests/NDIEgressMuxPlayheadClockTests.cs`. The two
> remaining `<see cref="WindowsNv12D3D11TextureInterop"/>` doc comments
> (in `HardwareVideoWin32Nv12.cs` and `D3D11VaNv12BackingFactory.cs`) and
> the catalogue comment in `IHardwareVideoInterop.cs` were rewritten as
> plain prose.

### 0.2 Drop macOS scaffolding (Linux + Windows only)

- ✅ Delete `MediaFramework/Audio/PALib/CoreAudio/` (whole folder: `Native.cs`, `PaMacCoreStructs.cs`).
- ✅ Remove every `OperatingSystem.IsMacOS()` / `IsMacCatalyst` / `IsIOS` / `IsTvOS` branch:
  - `S.Media.FFmpeg/FFmpegRuntime.cs`
  - `Audio/PALib/Runtime/PortAudioLibraryResolver.cs` (also dropped the now-orphan `MacCandidates` array)
  - `Audio/PALib/JACK/Native.cs` (`IsSupportedPlatform` → Linux-only)
  - `MIDI/PMLib/Runtime/PortMidiLibraryResolver.cs` (+ orphan `MacCandidates`)
  - `NDI/NDILib/Runtime/NDILibraryResolver.cs` (+ orphan `MacCandidates`)
- ✅ `Directory.Packages.props` already had no `osx-*` RIDs to strip (audit).
- ✅ `MFPlayer.sln.DotSettings.user` has no macOS targets (audit).
- ✅ Verification: Linux build clean; no IDE warnings; 807/807 tests pass.

### 0.3 `MediaDiagnostics.SwallowDisposeErrors(...)` helper

- ✅ Added `MediaDiagnostics.SwallowDisposeErrors(Action dispose, string label)`.
- ✅ Mechanically replaced **76 occurrences** across **35 files** (script:
  `/tmp/refactor_swallow.py`). The 17 `#if DEBUG` blocks that remain are not
  "swallow dispose" patterns — they are DEBUG-gated `LogError` calls inside
  non-trivial catch handlers (rethrow, dispose held frame, return false), two
  DEBUG-gated `LogWarning` calls in `YadifDeinterlacer`, two `_logger.LogDebug`
  gates in `OSCServer`, and the helper definition itself. Each was reviewed
  and left intact.
- ✅ Verification: 807/807 tests pass. Net Phase-0.3 LOC delta absorbed into
  the overall Phase-0 number (−2 771 LOC across the whole framework).

### 0.4 Strip `LegacyAudio` / `LegacyVideo` from `MediaContainerDecoder`

- ✅ Removed the always-null `LegacyAudio` and `LegacyVideo` properties from
  `MediaFramework/Media/S.Media.FFmpeg/MediaContainerDecoder.cs`.
- ✅ Updated `MediaContainerDecoderTests` to drop the now-stale `Assert.Null`
  lines.

### 0.5 Audit `JackLib` reference claim

- ✅ Confirmed `grep "using JackLib"` in `S.Media.PortAudio/**` returns empty.
- ✅ Dropped both `<InternalsVisibleTo Include="S.Media.PortAudio" />` and
  `<InternalsVisibleTo Include="S.Media.PortAudio.Tests" />` from
  `JackLib.csproj`; updated the comment to "restore here if PortAudio
  later wants direct access to `JackClient` for port autoconnect".
- ✅ Kept `<InternalsVisibleTo Include="JackLib.Tests" />`. `JackLib.Tests`
  doesn't exist in the solution yet but the IVT is correct for when it does.

### 0.6 Audit `S.Media.Core` `InternalsVisibleTo`

- ✅ Dropped the three IVT entries (`S.Media.PortAudio`, `S.Media.SDL3`,
  `S.Media.Avalonia`) from `S.Media.Core.csproj`.
- ✅ Verification: full solution builds clean and all 807 tests pass without
  promoting a single Core `internal` member — turns out none of the three
  backends were actually using any Core internals. The IVT grants were
  defensive ("just in case") and can be safely removed.
- ✅ Kept `S.Media.Core.Tests` and `S.Media.Core.Benchmarks` IVT entries.

### 0.7 Move `Extras` libs

- ✅ Moved `MediaFramework/MIDI/` → `MediaFramework/Extras/MIDI/`,
  `MediaFramework/OSC/` → `MediaFramework/Extras/OSC/`,
  `MediaFramework/Audio/JackLib/` → `MediaFramework/Extras/JackLib/`.
  `MediaFramework/Audio/PALib/` stays where it is — it's tight-fit with
  the media stack.
- ✅ Updated `MFPlayer.sln`: replaced the three `MIDI` / `OSC` solution
  folders with a single `Extras` folder; re-pointed the three csproj
  paths; rebound the NestedProjects mapping.
- ✅ Updated `UI/HaPlay/HaPlay.csproj`, `Test/PMLib.Tests/PMLib.Tests.csproj`,
  and `Test/OSCLib.Tests/OSCLib.Tests.csproj` to the new relative paths.
- ✅ Verification: full build clean; 807/807 tests pass.

**Phase 0 exit criteria** — ✅ all met:
- Build green; tests green (807/807).
- **−2 771 LOC** in `MediaFramework/` (59 695 → 56 924), inside the
  predicted −2 500…−3 000 band.
- No behaviour change; no API change.

---

## Phase 1 — Mechanical rename: `Sink` → `Output`

> **Risk**: Low · **Effort**: M · **Breaking**: ✓ (mechanical) · **Status**: ✅ shipped
>
> Goal: settle on `Source` for producers and `Output` for consumers
> everywhere, so subsequent phases don't have to thread a stale "Sink"
> identifier through new code.
>
> **Implementation note**: applied via `/tmp/rename_sink_to_output.py` —
> word-bounded identifier rewrite over the explicit map below, plus a
> fallback `Sink→Output / sink→output / Sinks→Outputs / sinks→outputs`
> standalone-word pass to catch comments and stray references. The script
> rewrote 141 files and renamed 26 files; manual fix-ups patched ~10
> stragglers (field/parameter names the regex map didn't cover and a
> handful of NDI test files that used `.VideoSink` accessors). This doc
> was itself rewritten by the script — the section was re-authored
> afterwards to reflect what shipped, not the pre-rename plan.

### 1.1 Interfaces — ✅

- ✅ `IAudioSink` → `IAudioOutput`
- ✅ `IClockedSink` → `IClockedOutput`
- ✅ `IFlushableSink` → `IFlushableOutput`
- ✅ `IAudioSinkChannelCapabilities` → `IAudioOutputChannelCapabilities`
- ✅ `AudioSinkChannelCapabilities` (struct) → `AudioOutputChannelCapabilities`
- ✅ `IVideoSink` → `IVideoOutput`
- ✅ `IVideoSinkD3D11GlBorrowSetup` → `IVideoOutputD3D11GlBorrowSetup`

### 1.2 Concrete classes — ✅

- ✅ `DiscardingVideoSink` → `DiscardingVideoOutput`. (`DiscardingAudioSink` was
  on the original plan but doesn't exist in the codebase — audit confirmed.)
- ✅ `BusSink` → `AudioBus` (dual-role: implements both `IAudioOutput` and
  `IAudioSource` — pinned the XML doc to make this obvious post-rename).
- ✅ `CompositorVideoSink` → `VideoCompositorSource` (the type is an
  `IVideoSource`; layer slots inside it implement `IVideoOutput`).
- ✅ `SDL3VideoSink` → `SDL3VideoOutput`
- ✅ `SDL3GLVideoSink` → `SDL3GLVideoOutput`
- ✅ `VideoOpenGlControl` — name unchanged (Avalonia control); its
  `IVideoSink` impl now flips to `IVideoOutput`.
- ✅ `NDIAudioSink` → `NDIAudioOutput` (Phase 6 makes it internal).
- ✅ `NDIAudioAggregatingSink` → `NDIAudioAggregatingOutput`.
- ✅ `AdaptiveRateAudioSink` → `AdaptiveRateAudioOutput`.
- ✅ `ResamplingAudioSink` → `ResamplingAudioOutput`.
- ✅ `GlVideoSinkHdr` / `GlVideoSinkHdrPreference` → `GlVideoOutputHdr` /
  `GlVideoOutputHdrPreference`.
- ✅ `SinkSlavedRouterClock` → `OutputSlavedRouterClock`. (Goes internal in
  Phase 8; renamed now for consistency.)
- ⏸ HaPlay-local `LockedFormatVideoSink` and `LogoFallbackVideoSink` kept
  their class names — both are slated for deletion / reshape in Phase 7,
  so renaming and then deleting would be churn. Their `IVideoSink` field
  references inside were updated to `IVideoOutput`.

### 1.3 Router method names + internal pump types — ✅

- ✅ `AudioRouter.AddSink` / `RemoveSink` → `AddOutput` / `RemoveOutput`.
- ✅ `AudioRouter.SinkPump` (internal) → `OutputPump`.
- ✅ `AudioRouter.SinkPumpStats` → `OutputPumpStats`.
- ✅ `AudioRouter.GetPumpStats(sinkId)` → `GetPumpStats(outputId)`.
- ✅ `AudioRouterSinkErrorEventArgs` → `AudioRouterOutputErrorEventArgs`.
- ✅ `AudioRouter.SinkErrored` event → `OutputErrored`.
- ✅ `AudioRouter.RaiseSinkErrored` / `ResolveClockedSink` / `TryGetSink` →
  `RaiseOutputErrored` / `ResolveClockedOutput` / `TryGetOutput`.
- ✅ `AudioRouter._slaveClockSinkId` → `_slaveClockOutputId`.
- ✅ `VideoSinkPump` / `VideoSinkPumpAttachOptions` /
  `VideoSinkPumpMetrics` / `VideoSinkPumpPressureEventArgs` →
  `VideoOutputPump*`.
- ✅ `VideoSinkFanoutFormats` → `VideoOutputFanoutFormats`.
- ✅ `VideoRouter.TryGetVideoSinkPumpMetrics` →
  `TryGetVideoOutputPumpMetrics`.

### 1.4 Player / session params + docs — ✅

- ✅ `AudioPlayer.AddOutput` (already correctly named): XML doc swept;
  parameter `sinkPumpCapacityChunks` → `outputPumpCapacityChunks`.
- ✅ `VideoRouter.AddOutput`: XML doc swept; parameter
  `disposeSinkOnRouterDispose` → `disposeOutputOnRouterDispose`;
  `VideoSinkPumpAttachOptions.DisposeInnerSinkWhenPumpDisposes` →
  `DisposeInnerOutputWhenPumpDisposes`.
- ✅ `MediaContainerSession` / `MediaPlaybackSession` /
  `AvPlaybackCoordinator`: comments and parameter names "sink" / "primary
  sink" → "output" / "primary output" via the fallback word-bounded pass.
- ✅ `IVideoOutputD3D11GlBorrowSetup`: verified no shader-string or
  internal-code references survived.
- ✅ `MediaPlayer._videoInputSink` → `_videoInput`; the corresponding
  public `VideoInput` property was already a clean rename target.
- ✅ `NDIOutput.VideoSink` property → `VideoOutput` (the underlying field
  `_videoSink` / `_audioSink` → `_videoOutput` / `_audioOutput`).
  Phase 6 will collapse `VideoOutput` further to just `Video`.

### 1.5 Tests & tools — ✅

- ✅ All `*Tests` classes & methods renamed (`AdaptiveRateAudioSinkTests` →
  `AdaptiveRateAudioOutputTests`, `BusSinkTests` → `AudioBusTests`,
  `CompositorVideoSinkTests` → `VideoCompositorSourceTests`,
  `VideoSinkFanoutFormatsTests`, `VideoSinkPumpTests`, plus dozens of test
  *method* names that embedded `Sink`).
- ✅ `Tools/PlaybackSmoke`, `Tools/VideoPlaybackSmoke`, `Tools/NDIPlayer`,
  `Tools/NDIReceiver`, `Tools/CompositorSmoke` — script swept and
  hand-fixed parameter names (e.g. `windowPresentationSink` →
  `windowPresentationOutput`, `ndiAudioSinkId` → `ndiAudioOutputId`,
  `glWindowSink` → `glWindowOutput`).
- ✅ HaPlay references updated in the same PR — `IVideoOutput` /
  `IAudioOutput` references; `NDIOutput.VideoOutput` accessor; field
  `_ndiAudioSinks` → `_ndiAudioOutputs`; local var `ndiSink` →
  `ndiOutput`. The two HaPlay-local class names (`LockedFormatVideoSink`,
  `LogoFallbackVideoSink`) were intentionally left as-is (Phase 7).

### 1.6 Verification — ✅

- ✅ `dotnet build`: clean (0 errors, 0 warnings).
- ✅ `grep -rE '[A-Z][A-Za-z0-9_]*Sink[A-Za-z0-9_]*|\bSink\b|\bsink\b'
  MediaFramework --include='*.cs'` returns **empty**. In `UI/HaPlay/` the
  only residue is the two intentionally-kept HaPlay-local class names
  noted above.
- ✅ Tests: **807/807 pass** (full suite).
- 🔲 Tag the rename commit as a clearly-marked breaking-rename so
  consumers can `git revert` if they need a temporary back-port. (Commit
  not made yet — pending user instruction.)

**Phase 1 exit criteria** — ✅ all met:
- One mechanical rename pass; downstream code compiles; no behaviour change.
- ~11 stragglers were caught by post-script hand-fix-ups (field names the
  word-bounded identifier map didn't cover plus a handful of NDI test
  files using `.VideoSink` accessors). Counted, fixed, and verified.

---

## Phase 2 — Additive helpers (no breaking) · **Status**: ✅ shipped (2026‑05‑22)

> **Risk**: Low · **Effort**: M · **Breaking**: ✗
>
> Goal: give consumers the six-line API shape from §10.5 of the review
> without removing any existing call site. Subsequent phases delete the
> old paths.

### 2.1 `MediaFrameworkRuntime.Init()` builder

- ✅ New static `MediaFrameworkRuntime` in `S.Media.Core/Diagnostics/`.
- ✅ Fluent: `.UseFFmpeg()` (`S.Media.FFmpeg`), `.UsePortAudio()` (`S.Media.PortAudio`),
  `.UseNDI()` (`S.Media.NDI`), `.UseSkiaSharpImages()` (`S.Media.SkiaSharp`) — extension
  methods; each registers a matching `Shutdown` release (PortAudio/NDI ref-count per call).
- ✅ Idempotent; thread-safe (`Lock` on init builder + plugin registration).
- ✅ `MediaFrameworkPlugins` consolidates registry slots; legacy statics forward with
  `[Obsolete("Use MediaFrameworkPlugins.X")]`.
- ✅ `MediaFrameworkRuntime.Shutdown()` runs teardown hooks in reverse registration order.

### 2.2 Source factories

- ✅ New static `AudioSource` class in `S.Media.Core.Audio`:
  - `AudioSource.OpenFile(string path, AudioSourceOpenOptions? options = null) : IAudioSource`
  - `AudioSource.OpenStream(Stream stream, AudioSourceOpenOptions? options = null) : IAudioSource` (delegates to `MediaContainerDecoder.OpenStream`'s audio track for now; Phase 3 swaps in `StreamAvioBridge`).
- ✅ New static `VideoSource` class in `S.Media.Core.Video`:
  - `VideoSource.OpenFile(string path, VideoSourceOpenOptions? options = null) : IVideoSource`
  - `VideoSource.OpenStream(Stream stream, VideoSourceOpenOptions? options = null) : IVideoSource`
  - `VideoSource.OpenImage(string path) : IVideoSource` — forwards to `ImageFileSource.OpenFromFile`.
  - `VideoSource.OpenImage(Stream stream) : IVideoSource`
- ✅ New static `MediaContainer` facade in `S.Media.FFmpeg`:
  - `MediaContainer.OpenFile(string path, VideoDecoderOpenOptions? options = null) : MediaContainerDecoder` — forwards to `MediaContainerDecoder.Open`.
  - `MediaContainer.OpenStream(Stream stream, bool seekable = false, string? probeHintName = null, VideoDecoderOpenOptions? options = null) : MediaContainerDecoder` — forwards to `MediaContainerDecoder.OpenStream`.
  - Matches the review's §10.5 A+V minimum-viable shape (which prints
    `MediaContainer.OpenFile("clip.mkv")`); today the entry point is
    `MediaContainerDecoder.Open` and consumers have to remember the
    "decoder" suffix. One static, one type to discover.

> Implementation note: these statics live in `S.Media.Core` but their
> implementations forward into `S.Media.FFmpeg` / `S.Media.SkiaSharp` via
> the plugin registries (Phase 2.4).

### 2.3 Router shorthand methods

- ✅ `AudioRouter.Route(string sourceId, string outputId, float gain = 1.0f)` —
  identity channel map sized to the output's channel count. (Router state record
  renamed `Route` → `AudioRoute` to avoid the name clash.)
- ✅ `AudioRouter.Route(string sourceId, string outputId, ChannelMap map, float gain = 1.0f)`.
- ✅ `AudioRouter.Play()` — alias for `Start()`.
- ✅ `AudioRouter.AddSource(..., bool? autoResample = null)` — defaults to
  `DefaultAutoResample`; logs once on rate mismatch when off.
- ✅ `AudioRouter.DefaultAutoResample { get; set; } = false` (static) — flips
  the `autoResample` default for *new* sources without forcing every call
  site to spell it. Per-call override still wins. A new consumer who hits
  "source rate mismatch" once turns the static on and never sees it again.
  Mitigation: log a warning the first time a mismatch fires under the
  default so silent resamples don't surprise.

### 2.4 Plugin registry consolidation

- ✅ New `MediaFrameworkPlugins` static (in `S.Media.Core`) that holds:
  - `AudioSourceFileFactory`, `AudioSourceStreamFactory`
  - `VideoSourceFileFactory`, `VideoSourceStreamFactory`
  - `ImageFileSourceFactory(string ext)`
  - (forwards the existing `AudioRouterAutoResample.SourceWrapper`,
    `VideoCpuFrameConverterRegistry.Factory`,
    `VideoDeinterlacerRegistry.Factory` slots, marking the older statics
    `[Obsolete("Use MediaFrameworkPlugins.X")]`)
- ✅ `MediaFrameworkPluginRegistration` (FFmpeg) populates file/stream factories on first `UseFFmpeg()`.
- ✅ `MediaFrameworkRuntime.UseSkiaSharpImages()` registers image factories.

### 2.5 Verification

- ✅ New tests: `MinimumViableApiTests`, `AudioRouterRouteTests` in `S.Media.Core.Tests`.
- 🔲 Full-suite `807/807` re-run in IDE (`dotnet` CLI needs SDK `10.0.300` per `global.json`).

### 2.6 Soundboard primitives (`AudioClip` + voice + player)

> The previous review explicitly flagged this gap — *"there is no
> 'soundboard' abstraction — i.e. a player that owns N short clips and
> triggers any of them on demand without re-opening the file."* HaPlay
> needs a touch-grid UI of clip buttons (one-shot, polyphonic re-trigger,
> latched loop, choke groups). Today the only file-backed `IAudioSource`
> is `AudioFileDecoder` (one decoder per playback, `avformat_open_input`
> per press, no replay). This phase adds the in-memory clip primitive
> the soundboard sits on.

- ✅ New `AudioClip` class in `S.Media.Core.Audio`:
  - `AudioFormat Format { get; }`, `TimeSpan Duration { get; }`,
    `int SamplesPerChannel { get; }`.
  - `static AudioClip OpenFile(string path, int? targetSampleRate = null, ChannelMap? mixdown = null)`.
  - `static AudioClip OpenStream(Stream stream, int? targetSampleRate = null, ChannelMap? mixdown = null)` (uses Phase 3's `StreamAvioBridge`).
  - `static AudioClip FromSamples(AudioFormat format, ReadOnlyMemory<float> interleaved)`.
  - `AudioClipVoice CreateVoice(AudioClipVoiceOptions? options = null)`.
- ✅ New `AudioClipVoice : IAudioSource, IDisposable`:
  - One PCM buffer is shared across all voices of a clip; each voice owns
    only a cursor + ramp state. **Zero allocation per `ReadInto`** —
    `Buffer.BlockCopy` from the clip buffer into the router scratch.
  - `void Stop()` — triggers a click-free release fade (`ReleaseFade` from
    options); `IsExhausted` becomes true after the fade plays out, the
    router naturally drops the route on next state-snapshot.
  - `bool Loop { get; set; }` — flippable mid-play. Latched-loop UX: press
    1 ⇒ `Loop = true`; press 2 ⇒ `Loop = false; Stop();`.
  - `TimeSpan Position { get; }` for UI scrubbers.
- ✅ New `AudioClipVoiceOptions` record struct:
  `(bool Loop, double StartOffsetSec, float StartGain, TimeSpan? AttackFade, TimeSpan? ReleaseFade)`.
  Click-free attack/release ramps use the same gain math `AudioRouter`
  already runs for `SetRouteGain` — keep policy in one place.
- ✅ New `AudioClipPlayer` (per-pad helper):
  - Owns one `AudioClip` and mints voices via `Fire(router, outputId, map?, gain?, options?)`.
  - `AudioClipPlayerMode { Polyphonic, MonoRetrigger, OneShot, LatchedLoop }`
    governs what `Fire` does when a voice is already live.
  - `MaxPolyphony { get; set; } = 8` — bounded voice count; new fires past
    the limit reuse the oldest voice (stop-then-fresh).
  - `ChokeGroup { get; set; }` — pads in the same group stop each other.
  - `IReadOnlyList<AudioClipVoice> ActiveVoices { get; }` for UI.
  - `void StopAll()` — used by choke groups and by the operator's "panic" stop.
- ✅ New `AudioRouter.RegisterChokeGroup` / `UnregisterChokeGroup` (stops other
  `AudioClipVoice` members in the group).
- ✅ `Tools/SoundboardSmoke` — polyphonic stress + **zero pump drops** exit code;
  run: `SoundboardSmoke <clip> [voice-count] [duration-sec]`.
- ✅ `AudioRouter.RouteLast()` ships; `AudioGraphBuilder` marked `[Obsolete]` (Phase 4 delete).
- ✅ `AudioClip.Open*` **mixdown** via `ChannelMap.Apply` per frame.
- ✅ `AudioClipVoice_ReadInto_NoAllocationsAfterWarmup` unit test (router-thread GC assert
  stays in the smoke tool via pump stats only — per-voice hot path is what we guarantee).
- ⏸ HaPlay 4×4 soundboard view-model — consumer work; primitives are in place (`AudioClipPlayer`).

**Phase 2 exit criteria**: §10.5 minimum-viable API works end-to-end on the
existing solution; **soundboard primitives ship and a 32-voice smoke
asserts allocation-free playback**; no consumer breakage; old API stays.

---

## Phase 3 — Stream IO via `StreamAvioBridge` · **Status**: ✅ shipped (2026‑05‑22)

> **Risk**: Medium · **Effort**: M · **Breaking**: ✗ (additive; old
> temp-file path is renamed but retained)
>
> Goal: replace the temp-file spool with a real `AVIOContext` so finite and
> moderately-long streams play without disk overhead.

### 3.1 Implement `StreamAvioBridge`

- ✅ `S.Media.FFmpeg/Internal/StreamAvioBridge.cs` — `avio_alloc_context` with
  static read/seek callbacks (GC-safe), `GCHandle` opaque, `av_malloc` buffer,
  `AVFMT_FLAG_CUSTOM_IO` open + `avformat_find_stream_info`.
- ✅ Caller owns the `Stream`; bridge does not dispose it.

### 3.2 Wire into `MediaContainerDecoder`

- ✅ `MediaContainerDecoder.OpenStream(stream, isSeekable, probeHintName, options)` — AVIO path.
- ✅ `OpenStreamSpooled` — former temp-file spool (renamed from old `OpenStream`).
- ✅ `MediaContainerOpenStreamOptions` + overload on `OpenStream`.
- ✅ `MediaContainerSharedDemux.Open(Stream, …)` + `_inputSeekable` guard on `SeekPresentation`.
- ✅ `MediaPlayer.TryOpenStream` uses AVIO by default; `MediaPlayerOpenOptions.SpoolStreamToDisk` for legacy spool.

### 3.3 `AudioSource` / `VideoSource` stream shortcut

- ✅ Plugin registration uses AVIO (`StreamIsSeekable`, `SpoolToDisk` on open options).

### 3.4 Verification

- ✅ `MediaContainerDecoderStreamAvioTests` — memory WAV without `mf_stream_*` temp files,
  forward-only stream, non-seekable seek throws, spooled path still creates temps, router exhaust.

**Phase 3 exit criteria**: ✅ met — stream playback without disk spool by default; spooled path retained.

---

## Phase 4 — Player façade collapse

> **Risk**: Medium · **Effort**: M · **Breaking**: ✓
>
> Goal: cut the redundant wrappers (`AudioPlayer`, `AudioGraphBuilder`,
> `IAvPlaybackSession`, `MediaPlaybackSession`) so `AudioRouter` and
> `VideoRouter` (+ `VideoPlayer` for the clock-paced helper) are the
> production surface. `MediaPlayer` remains as the bundled all-in-one.

### 4.1 Fold `AudioPlayer` into `AudioRouter` + `MediaPlayer`

- ✅ Move `AudioPlayer`'s auto-wire-primary behaviour into `AudioRouter`:
  - `AudioRouter.AutoSlaveTo(IClockedOutput)` selector logic.
  - `AudioRouter.AttachMasterClock(IPlaybackClock)` for `IPlaybackClock`-implementing outputs.
- ✅ `AudioRouter.AddOwnedSource(IAudioSource)` for the "router disposes
  source on dispose" pattern (used to live on AudioPlayer).
- ✅ `AudioRouter.AddOutputAndAutoWirePrimary(IAudioOutput, ...)` shorthand.
- ✅ `MediaPlayer.AudioRouter` (+ `AudioClock`, `AudioSourceId`) replaces `MediaPlayer.Audio`.
- ✅ Delete `MediaFramework/Media/S.Media.Core/Audio/AudioPlayer.cs`.

### 4.2 Delete `AudioGraphBuilder`

- ✅ Confirm `AudioRouter.Route(...)` (Phase 2.3) covers the use cases.
- ✅ Delete `MediaFramework/Media/S.Media.Core/Audio/AudioGraphBuilder.cs`.
- ✅ HaPlay / smoke / QuickPlayer updated (no `AudioGraphBuilder` references).

### 4.3 Internalise / drop session wrappers

- ✅ Make `IAvPlaybackSession` `internal` (still implemented by
  `MediaPlaybackSession` but no longer a public contract).
- ✅ `MediaPlaybackSession` is `internal`.
- ✅ `AvPlaybackCoordinator` is `internal` static helpers used by
  `MediaContainerSession` / `MediaPlayer`.
- ✅ Surface `Play / Pause / Seek / SeekCoordinated` on `MediaPlayer`
  directly.

### 4.4 `flushSharedMuxAfterPause` cleanup

- ✅ `PauseFlushPolicy` enum on public `MediaPlayer` / `MediaContainerSession`
  pause/seek entry points.

- ✅ Custom delegate cases use `PauseWithFlushAction(Action)` on `MediaPlayer`.
- ✅ `MediaContainerSession` public façade; `IAvPlaybackSession` internal only
  (`InternalsVisibleTo` for `S.Media.Playback`, `S.Media.FFmpeg`, `HaPlay`, tests).

### 4.5 Verification

- ✅ All existing tests pass after fixing references (Core 369, FFmpeg 149,
  Playback 12, PortAudio 22, HaPlay 4, Quick 3).
- 🔲 New test: end-to-end `MediaPlayer.Open(path)…OpenAsync()` smoke
  (audio+video, audio only, image) — deferred; covered by existing
  `MediaPlayerTests` + `TryOpen_wav_play_pause_advances_audio_router`.
- ✅ LOC reduction: `AudioPlayer`, `AudioGraphBuilder`, public session types removed.

### 4.6 Pre-resolved routes — kill 4 dictionary lookups per route per chunk

> `AudioRouter.RunLoop` currently does, for every route, every chunk:
>
> ```csharp
> if (!snapshot.Sources.TryGetValue(route.SourceId, out var src)) continue;
> if (!snapshot.Outputs.TryGetValue(route.SinkId, out var output)) continue;
> var fromGain = _currentGains.GetValueOrDefault(route.RouteId, route.Gain);
> var toGain   = _routeTargetGains.TryGetValue(route.RouteId, out var tg) ? tg : route.Gain;
> ```
>
> That's **2 × `ImmutableDictionary<string,…>` lookup** + **2 ×
> `ConcurrentDictionary<string, float>` lookup** per route per chunk. For
> HaPlay's worst-case 64×64 audio-matrix (4096 active routes) at 480
> samples / 48 kHz, that's ~1.6 million hash lookups/sec just to read
> ids the router already knows. Phase 4 is already touching the router,
> so absorb the hot-path fix here.

- ✅ `ResolvedRoute` + `RouteGainSlot` on hot path (`AudioRouter` partial state).
- ✅ Resolved refs populated in `AddRoute` / rebound on source/output churn.
- ✅ `_currentGains` / `_routeTargetGains` `ConcurrentDictionary`s removed.
- ✅ Run loop reads `ResolvedRoute` fields per chunk (no per-route id dictionaries).
- 🔲 Formal 4096-route CPU benchmark not pinned (existing router tests green).

### 4.7 Stop / pause paths — replace LINQ array materialisation

- ✅ `AudioRouter.StopInternal` / `FinishRunLoopThreadLifetime` use
  `CollectOutputPumps` / `CollectOutputs` loops (no LINQ materialisation).

### 4.8 Move `AudioPrefill` next to its only consumer

- ✅ `S.Media.PortAudio.Internal.AudioPrefill`; `AudioRouterPortAudioExtensions.TryPrefillPrimaryPortAudio`.
- ✅ Tests moved to `S.Media.PortAudio.Tests/AudioPrefillTests.cs`.

**Phase 4 exit criteria**: ✅ only `AudioRouter` + `VideoRouter` +
`VideoPlayer` + `MediaPlayer` remain as consumer entry points; router
hot path uses field reads, not dictionary lookups.

---

## Phase 5 — `MediaPlayer.Open(...)` builder API

> **Risk**: Medium · **Effort**: M · **Breaking**: ✓
>
> Goal: replace the 9 static `TryOpen*` overloads with one builder per entry
> verb. Old statics forward to the builder for one release, then go away.

### 5.1 Builder primitives

- ✅ `MediaPlayerOpenBuilder` + typed builders (`OpenFile`, `OpenUri`, `OpenStream`, `OpenLive`, `OpenDecoder`).
- ✅ Static entry verbs on `MediaPlayer`:

  ```csharp
  MediaPlayer.Open(string filePath)        → MediaPlayerOpenFileBuilder
  MediaPlayer.Open(Uri uri)                → MediaPlayerOpenUriBuilder
  MediaPlayer.Open(Stream s)               → MediaPlayerOpenStreamBuilder
  MediaPlayer.OpenLive(IAudioSource?, IVideoSource?) → MediaPlayerOpenLiveBuilder
  MediaPlayer.Open(MediaContainerDecoder)    → MediaPlayerOpenDecoderBuilder
  ```

- ✅ Fluent surface:
  - `.WithOptions(MediaPlayerOpenOptions)` / `.WithOptions(o => o with { … })`
  - `.WithVideoLead(IVideoOutput, bool dispose = false)`
  - `.WithPortAudio(...)` extension in `S.Media.PortAudio`
  - `.WithDecoderOwnership(...)` (file/decoder)
  - `.TryBuild(out MediaPlayer? player, out string? error)`
  - `.OpenAsync(CancellationToken ct = default)`
- 🔲 `.WithNDIOutput(...)` — deferred (smoke tools still wire NDI after build).

### 5.2 Migrate old statics

- ✅ `MediaPlayer.TryOpen*` marked `[Obsolete("Use MediaPlayer.Open(...)")]`, still forward to core impl.
- ✅ `VideoPlaybackSmoke`, HaPlay file/live open paths use builders.
- 🔲 Cut `[Obsolete]` overloads in next major.

### 5.3 Verification

- ✅ `S.Media.Playback.Tests` covers file/uri/stream/live/builder/OpenAsync/options mutate.
- ✅ Obsolete APIs still compile (builders call them internally with pragma).

### 5.4 Delete `S.Media.Quick`

- ✅ Removed `S.Media.Quick` + `S.Media.Quick.Tests` from solution; deleted project files.
- ✅ `PlaybackAudioStartup` + tests archived under `S.Media.Playback` / `.Tests`.

### 5.5 Reduce option-record copying for downstream consumers

- ✅ `MediaPlayerOpenOptions` is a `readonly record struct` with `with` mutators on builders.
- 🔲 `IMediaPlayerOpenOptions` / HaPlay translation collapse — optional follow-up.

**Phase 5 exit criteria**: ✅ `MediaPlayer.Open(...).WithVideoLead(...).OpenAsync()`
is the documented path; 9 static `TryOpen*` overloads remain obsolete shims; `S.Media.Quick`
is deleted.

---

## Phase 6 — NDI surface collapse

> **Risk**: Medium · **Effort**: L · **Breaking**: ✓
>
> Goal: one `NDISource` (combined A+V receiver), one `NDIOutput` (combined
> A+V sender). Per-stream types go internal; pull-mode session deleted.

### 6.1 `NDISource`

- ✅ `NDISource : IDisposable` replaces `NDILiveReceiver`.
- ✅ `NDISource.Find(TimeSpan, NDIFindOptions?)` / `NDISource.Open(..., NDISourceOptions?)`.
- ✅ `Audio` / `Video` always non-null (`StandbyAudioFormat` / exhausted video when stream disabled).
- ✅ `IngestClock`, `ReceiveAudio` / `ReceiveVideo`, `NDIConnectionState`.

### 6.2 Internalise per-stream receivers

- ✅ `NDIVideoReceiver` / `NDIAudioReceiver` are `internal` (tests via `InternalsVisibleTo`).

### 6.3 Delete pull-mode + redundant clocks

- ✅ Deleted `NdiFrameSyncSession`, `NdiFrameSyncAudioSource`, `NdiFrameSyncVideoSource`, `NdiAudioFrameConverter`.
- ✅ `NDIEgressMuxPlayheadClock` / `NDIAlignedRouterClock` were already absent.

### 6.4 `NDIOutput` tidy

- ✅ `NDIVideoSender` / `NDIAudioOutput` are `internal`.
- ✅ Public `IVideoOutput Video` and `IAudioOutput? Audio`; `EnableAudio` returns `IAudioOutput`.
- 🔲 Full fold of `NDIMonitorReceiverPumpFusion` / timeline helpers into `NDIOutput` nested types — deferred (still separate files, wired on `NDIOutput`).

### 6.5 Verification

- ✅ `S.Media.NDI.Tests` + HaPlay build; `NDISourceApiTests` added.
- 🔲 Live-network receive smoke (needs NDI sender on LAN).
- ✅ NDI send path updated (`NDIPlayer`, `VideoPlaybackSmoke` use `ndi.Video`).

**Phase 6 exit criteria**: ✅ consumer path is `NDISource.Find(...)` → `NDISource.Open(...)`; per-stream receivers are internal implementation detail.

---

## Phase 7 — `S.Media.Effects` extraction + `VideoCompositor` API

> **Risk**: Medium · **Effort**: L · **Breaking**: ✓
>
> Goal: video effects move out of Core; consumers drive composition via a
> declarative `LayerConfig` + `Transition` API.

### 7.1 New project

- ✅ Create `MediaFramework/Media/S.Media.Effects/S.Media.Effects.csproj`.
- ✅ References: `S.Media.Core`, `S.Media.OpenGL` (for the GL backend), `S.Media.FFmpeg` (for CPU converters via the registry).

### 7.2 Move existing primitives

- ✅ Move from `S.Media.Core/Video/` → `S.Media.Effects/`:
  - `CompositorLayer.cs`, `CompositorSamplingMode.cs`, `CpuVideoCompositor.cs`
  - `BlendMode.cs`, `LayerOpacityTween.cs`, `LayerTransform2D.cs`
  - `VideoCpuOpacity.cs`
  - `FadeFromBlackVideoSource.cs`, `CutVideoSource.cs`, `StaticFrameSource.cs`, `PixelFormatConvertingVideoSource.cs`
  - `IVideoCompositor.cs`
  - `VideoCompositorSource.cs` (per Phase 1).
- ✅ Move from `S.Media.OpenGL/` → `S.Media.Effects.OpenGL/`:
  - `GlVideoCompositor.cs` and `composite_layer` shader resources.
- ✅ HaPlay: removed NDI frame-sync UI (`NdiInputSyncMode`, dialog radios, playlist `SyncMode`); A/V timing stays on `NDISource` / framework ingest.

### 7.3 Public `VideoCompositor` API (§10.6 of review)

- ✅ `public sealed class VideoCompositor : IVideoSource, IDisposable`
  - `static Create(VideoFormat output, VideoCompositorBackend backend = Auto, VideoCompositorOptions? options = null)`
  - `LayerHandle AddLayer(IVideoSource source, LayerConfig config)`
  - `bool RemoveLayer(LayerHandle handle)`
  - `IReadOnlyList<LayerHandle> Layers { get; }`
  - `IPlaybackClock? Clock { get; set; }`
- ✅ `public sealed class LayerHandle`
  - `IVideoSource Source { get; }`
  - `LayerConfig CurrentConfig { get; }`
  - `void SetConfig(LayerConfig)` (instant jump)
  - `void AddTransition(TimeSpan at, Transition)`
  - `void ClearTransitions()`
- ✅ `public readonly record struct LayerConfig`
  - `LayerPosition Position`
  - `float Scale`, `float Opacity`, `float Rotation`
  - `BlendMode Blend`
  - `LayerAnchor ScaleAnchor`
  - Static presets: `Background`, `CenteredHalf`.
- ✅ `public abstract record LayerPosition` with statics:
  `Cover`, `Center`, `Anchored(LayerAnchor, marginX, marginY)`,
  `AbsolutePixels(x, y)`, `NormalizedXY(x01, y01)`.
- ✅ `public abstract record Transition` with statics:
  `FadeTo`, `MoveTo`, `ScaleTo`, `Cut`, `Combo(params Transition[])`, `Sequence(params Transition[])`.
- ✅ `public enum LayerAnchor` (9 positions).
- ✅ `public enum VideoCompositorBackend { Auto, Cpu, Gl }`.

### 7.4 Transition evaluation

- ✅ Once-per-output-frame inside `VideoCompositor.TryReadNextFrame` (via per-layer pull + `VideoCompositorSource`).
- ✅ Resolves the *current* `LayerConfig` from the attached
  `IPlaybackClock` (or wall clock if none attached).
- ✅ Easing: `EaseInOutCubic` on `LayerOpacityTween` / transition `Progress`.
- ✅ `MoveTo` interpolates via `LayerConfigResolver.ResolveTransform` (affine lerp).

### 7.5 Verification

- ✅ `CompositorSmoke` uses `VideoCompositor` + `StaticFrameSource.FromFrame` (GL backend).
- ✅ HaPlay: `OutputPresetVideoSource` → `VideoCompositor`; `LockedFormatVideoSink` / logo template render → `CompositorOutputScaler`; live output fade → compositor `LayerConfig.Opacity`.
- ✅ `VideoCompositorCrossFadeTests` — two-layer opacity cross-fade + cut transition.

**Phase 7 exit criteria**: ✅ `S.Media.Core` no longer contains video effects types;
✅ HaPlay preset/lock/logo paths use `S.Media.Effects` compositor APIs (`VideoCompositor`, `CompositorOutputScaler`, `LayerConfig`).

---

## Phase 8 — Clock surface narrowing

> **Risk**: Low · **Effort**: S · **Breaking**: ✓ (small surface)
>
> Goal: collapse 13 named clock concepts to 4 public ones; the rest go
> internal.

### 8.1 Merge read views

- ✅ Collapse `IPlaybackTimeline` + `IPlaybackPlayhead` → single `IPlayhead`.
- ✅ Keep `IPlaybackClock` (master input).
- ✅ Keep `IMediaClock` (driver) extending `IPlayhead`.

### 8.2 Internalise router clocks

- ✅ `IRouterClock`, `WallClockRouterClock`, `OutputSlavedRouterClock` → `internal`.
- ✅ `PlaybackSlavedRouterClock` (Core) replaces NDI `IngestSlavedRouterClock`.
- ✅ `AudioRouter.SlaveTo(outputId)` / `RetargetSlaveClock(outputId)` remain public.
- ✅ `AudioRouter.SlaveToIngest(IPlaybackClock)` + `AudioRouterNdiExtensions.SlaveToNdi(NDIIngestPlaybackClock)`.
- ✅ Public `AudioRouter.Clock` / `SetClock` removed (tests use `internal SetClock`).

### 8.3 Document the four public clocks

- ✅ `MediaClock` — default driver (`IPlayhead` + ticks).
- ✅ `CompositePlaybackClock` — priority-merge of multiple masters.
- ✅ `VideoPtsClock` — PTS-derived playback clock.
- ✅ `NDIIngestPlaybackClock` — NDI receive timeline.

### 8.4 Verification

- ✅ Obsolete aliases: `IPlaybackTimeline : IPlayhead`, `IPlaybackPlayhead` (seek-free slice).
- ✅ Tests: `SlaveToIngest`, `PlaybackSlavedRouterClock`, `IPlayhead` assignability.

**Phase 8 exit criteria**: ✅ public playhead/clock vocabulary is `IPlayhead`, `IMediaClock`, `IPlaybackClock`, plus the four documented master clocks; router pacing types are internal.

---

## Phase 9 — `VideoFrame` redesign

> **Risk**: High · **Effort**: L · **Breaking**: ✓ (every frame producer)
>
> Goal: single ctor + discriminated hardware backing, instead of 10
> parameters with 6 mutually-exclusive HW backings hand-checked at runtime.

### 9.1 Discriminated backing

- ✅ New abstract `VideoFrameHardwareBacking : IDisposable` with concrete
  subclasses (or use a sealed record union):
  - `DmabufNv12Backing`, `DmabufP010Backing`, `DmabufP016Backing`
  - `Win32SharedNv12Backing`
  - (`None` is just `null`)

### 9.2 `VideoFrame` ctor collapse

- ✅ New canonical ctor:

  ```csharp
  public VideoFrame(
      TimeSpan presentationTime,
      VideoFormat format,
      ReadOnlyMemory<byte>[] planes,
      int[] strides,
      VideoFrameMetadata metadata = default,
      VideoFrameHardwareBacking? backing = null,
      IDisposable? release = null);
  ```

- ✅ Mutual-exclusion check collapses to a type check on `backing` (or `null`).
- ✅ Convenience single-plane overload retained.

### 9.3 Named factories on the backing types

- ✅ `DmabufNv12Backing.CreateFrame(pts, format, backing, metadata, release)`
- ✅ `DmabufP010Backing.CreateFrame(...)`, `DmabufP016Backing.CreateFrame(...)`
- ✅ `Win32SharedNv12Backing.CreateFrame(...)`
- ✅ Each backing's `CreateSharedReference(pts, format, metadata)` instance method;
  `VideoFrame.Create*SharedReference` forwards to it.

### 9.4 Release strategy unification

- ✅ Drop `Action? release` + `IDisposable? disposableRelease` duality;
  ship only `IDisposable? release`. `DisposableRelease.Wrap` / `Chain` /
  `Combine` for closures and refcounted backings.
- ✅ `AudioFrame.Release` is `IDisposable?`; `AudioFrame.WithActionRelease`
  wraps legacy `Action` call sites. FFmpeg pool returns use
  `DisposableRelease.Wrap`.

### 9.5 Update producers

- ✅ `S.Media.FFmpeg`: `MediaContainerSharedDemux`, `VideoFileDecoder`,
  swscale / Yadif / `VideoCpuFrameConverter` paths.
- ✅ `S.Media.NDI`: `NDIVideoFrameUnpack`.
- ✅ `S.Media.SkiaSharp`: `ImageFileSource`, `TextLayerSource` (unchanged
  `release: null` — source owns buffers).
- ✅ `S.Media.Core` / `S.Media.Effects`: compositors, deinterlacers, fan-out,
  `CutVideoSource`, `VideoPlayer`.
- ✅ Hardware backings renamed (`VideoDmabuf*` / `VideoWin32*` → `Dmabuf*` /
  `Win32Shared*`).
- 🔲 `VideoFramePool` (per `VideoRouter`) for fan-out — `VideoFrame` is
  heap-allocated per submit today; a 60 fps fan-out to 4 outputs allocates
  240 frames/sec. Pooled frames carry back-references so `Release`
  returns them to the pool. Bench-validated: `Tools/VideoPlaybackSmoke
  --4-outputs` allocates ~0 bytes/sec after warm-up.

### 9.6 Verification

- ✅ Core + FFmpeg video/audio unit tests pass (`dotnet build` clean).
- 🔲 Smoke tools all play HW-decoded content correctly (manual).

**Phase 9 exit criteria**: ✅ one `VideoFrame` ctor, one mutual-exclusion
check (type), no `disposableRelease` duality. (`VideoFramePool` deferred.)

---

## Phase 10 — Bit-depth completeness (additive)

> **Risk**: Medium · **Effort**: M · **Breaking**: ✗
>
> Goal: high-bit-depth content survives end-to-end on supported hardware.
> See §10.11 of the review for the full background.

### 10.1 New pixel formats

- ✅ `PixelFormat.Rgba16` — 16-bit unsigned-normalized packed RGBA.
- ✅ `PixelFormat.Rgba16F` — 16-bit IEEE half-float packed RGBA.
- ✅ `PixelFormat.P216` / `PixelFormat.Pa16` for NDI 16-bit 4:2:2 egress.
- ✅ `PixelFormatInfo` entries (8 bytes/pixel for RGBA16/16F).
- ✅ `S.Media.FFmpeg.Video.VideoCpuFrameConverter`: swscale via
  `AV_PIX_FMT_RGBA64LE` / `RGBAF16LE` / `P216LE`.

### 10.2 10-bit GL swapchain

- ✅ `GlOutputBitDepth` enum (`Eight`, `Ten`, `Auto`).
- ✅ `SDL3GLVideoOutput` ctor accepts `swapchainBitDepth: GlOutputBitDepth.Auto`.
  - Windows DXGI: requests `R10G10B10A2_UNORM` via SDL GL attribute
    `SDL_GL_RED_SIZE=10/GREEN_SIZE=10/BLUE_SIZE=10/ALPHA_SIZE=2`.
  - Linux EGL: same attributes; Wayland HDR-capable compositors honour.
  - Fallback to 8-bit + single warning log when 10-bit config unavailable.
- ✅ `VideoOpenGlControl` exposes `SwapchainBitDepth` (Avalonia may ignore
  10-bit attributes on some backends — documented on the property).

### 10.3 `GlVideoCompositor` output precision

- ✅ `GlCompositorOutputPrecision { Rgba8, Rgba16, Rgba16F }` enum.
- ✅ `GlVideoCompositor` ctor + `RecreateFbo` use `Rgba8` / `Rgba16` / `Rgba16f`.
- ✅ `glReadPixels` uses `UNSIGNED_BYTE` / `UNSIGNED_SHORT` / `HALF_FLOAT`
  and emits `Bgra32` / `Rgba16` / `Rgba16F`.

### 10.4 NDI sender 16-bit FourCCs

- ✅ `NDIVideoSender.AcceptedPixelFormats` includes `P216` and `Pa16`.
- ✅ `PackP216` / `PackPa16` + FourCC mapping for submit.
- 🔲 Smoke: `Yuv422P10Le` → compositor `Rgba16` → NDI `P216` (manual).

### 10.5 `VideoCompositor` precision option

- ✅ `VideoCompositorOptions.GlOutputPrecision` wired through
  `VideoCompositor.Create`.
- ✅ Default `Rgba8`; opt-in `Rgba16` / `Rgba16F`.

### 10.6 Verification

- ✅ Unit tests: pixel maps, `PixelFormatInfo`, CPU converter probes,
  NDI staging layout, compositor precision mapping.
- 🔲 GL composite + 10-bit display smoke (manual).

**Phase 10 exit criteria**: ✅ opt-in high-bit paths ship; default
BGRA8 / 8-bit swapchain unchanged. Manual smoke items remain.

---

## Phase 11 — Async, metrics, extensibility (additive)

> **Risk**: Low · **Effort**: M · **Breaking**: ✗
>
> Goal: round off the consumer surface with the small ergonomic wins.

### 11.1 Async open

- ✅ `OpenAsync` uses `Task.Run` for `TryBuild` (blocking decode/init off caller thread).
  Hosts with a UI `SynchronizationContext` should `await OpenAsync().ConfigureAwait(false)`.

### 11.2 Aggregated metrics

- ✅ `MediaPlayer.GetMetrics() : MediaPlayerMetrics` — one snapshot:
  - Audio router pump stats + per-output drops/processed.
  - Video router per-output pump metrics.
  - `VideoPlayer.DecodedCount / DisplayedCount / DroppedLate / DroppedDrain`.
  - `PortAudioOutput.PlayedSamples / UnderrunSamples / DroppedSamples` (if PortAudio in graph).
  - `NDISource.OverflowSamples / VideoOverflowFrames` (if NDI in graph).
  - `MediaClock.CurrentPosition / Master.GetType().Name`.

### 11.3 Image-extension factory

- ✅ `MediaFrameworkExtensionRegistry`: per-extension `IVideoSource` factories.
- ✅ `S.Media.SkiaSharp` registers `.png` / `.jpg` / `.jpeg` / `.webp` / `.bmp` / `.gif`.
- ✅ `VideoSource.OpenImage(path)` tries registry first, then plugin fallback.

### 11.4 Optional auto-adapt

- ✅ `AudioRouter.EnableAdaptiveRateOnNonMasterOutputs(maxRateDeltaHz)` —
  wraps subsequent non-master outputs via `MediaFrameworkPlugins.WrapAdaptiveRateOutput`
  (FFmpeg `AdaptiveRateAudioOutput`).

### 11.5 `TriggerBus` — scriptable control surface (OSC / MIDI / Mond)

> Forward-looking: the user's scripting layer is Mond (NativeAOT-friendly
> Lua-ish), with OSC and MIDI as the wire protocols. None of those bindings
> belong in `S.Media.Core`, but the framework needs **one stable,
> allocation-free trigger surface** that script glue and protocol adapters
> can target. Without it, scripts will reach into `AudioClipPlayer.Fire`,
> `AudioRouter.SetRouteGainById`, `MediaPlayer.Seek`, `VideoCompositor`
> layer config, etc. via reflection — slow at startup and brittle to refactor.

- ✅ `S.Media.Core.Triggers.TriggerBus` + `TriggerPayload` / `TriggerHandler` / `TriggerValueKind`.
- ✅ `AudioTriggerRegistration.RegisterAudioClipPlayer` (`{id}.fire|stop|stopAll|loop`).
- ✅ `MediaPlayer.Triggers` — per-player bus instance.
- ✅ `OscTriggerBridge` in `Extras/OSCLib` (pattern `//`, address → `Fire`).
- ✅ `MidiTriggerBridge` + `MidiTriggerProfile` in `Extras/PMLib`.
- ✅ `Doc/MediaFramework-Triggers.md` — id naming convention.
- 🔲 Mond-binding design rules (for the script layer when it lands —
  enforced by linting, not by the framework):
  - Avoid `Task<T>` / `ValueTask<T>` / `IAsyncEnumerable<T>` on the public
    trigger surface. `Fire` is intentionally void-returning so a script
    firing 50 pads stays on one stack.
  - Avoid `params object[]` varargs. `TriggerPayload` covers the
    payload shape; richer cases get a per-handler descriptor later.
- 🔲 The actual Mond bindings live in the **UI/HaPlay** project (or a
  future `HaPlay.Scripting` assembly), not in `S.Media.Core`. The
  framework ships only the bus + the contract.

**Phase 11 exit criteria**: ✅ async open works; one-call metrics snapshot;
extension-driven image registry; trigger bus + OSC/MIDI adapters available.

---

## Phase 12 — Encoder outputs (`S.Media.FFmpeg.Encode`)

> **Risk**: Medium · **Effort**: L · **Breaking**: ✗ (purely additive)
>
> Goal: framework can record/transcode. Naturally consumes the 10/12-bit
> output formats from Phase 10.

### 12.1 New project

- ✅ `MediaFramework/Media/S.Media.FFmpeg.Encode/` — references `S.Media.FFmpeg` + `S.Media.Core`.

### 12.2 Implementations

- ✅ `FFmpegVideoFileOutput : IVideoOutput, IDisposable` — H.264/HEVC/ProRes422 via libavcodec;
  accepts `Yuv420P10Le`, `P010`, `Yuv444P12Le`, `Yuva444P12Le`, NV12/I420, RGBA/BGRA (swscale).
- ✅ `FFmpegAudioFileOutput : IAudioOutput, IDisposable` — AAC/Opus/FLAC packed-float ingest.
- ✅ `FFmpegMuxFileOutput` — shared `FfmpegMuxContext`; A+V legs; finalize on `Dispose` (trailer).

### 12.3 Smoke

- ✅ `Tools/EncoderSmoke` + `S.Media.FFmpeg.Encode.Tests` mux round-trip (lavfi source when ffmpeg on PATH).

**Phase 12 exit criteria**: ✅ framework can encode A+V into a file.

---

## Phase 13 — Public-API tests + docs sweep ✅

> **Risk**: Low · **Effort**: M · **Breaking**: ✗
>
> Goal: lock in the surface so future regressions are caught at the public
> contract, not at internal refactors.

### 13.1 `MediaPlayer.Tests` smoke project

- ✅ Open file (audio+video), play 2 s, seek to 1 s, play 2 s — `MediaPlayerSmokeTests.OpenAvFile_play_two_seconds_seek_play_two_seconds` (skips if `ffmpeg` missing).
- ✅ Open file (audio only), play to end — `OpenAudioOnlyFile_play_until_source_exhausted`.
- ✅ Open image, hold last frame — `OpenImage_hold_last_frame` (`S.Media.SkiaSharp`).
- ✅ Open URI — file-scheme `OpenUri_file_scheme_opens_player` (no network in CI).
- ✅ Open Stream (in-memory) — WAV AVIO `OpenStream_memory_wav_no_temp_spool`.
- ✅ Open live (mock `IAudioSource` + `IVideoSource`) — `OpenLive_mock_sources_playback_advances`.
- ✅ Mid-play output swap — `MidPlay_audio_output_swap_routes_to_second_output` (video primary cannot be unrouted).
- ✅ Assert no temp file created (Stream-mode) — `mf_stream_*` count unchanged.
- ✅ Assert `MediaPlayer.GetMetrics()` advances during playback.
- ✅ **Allocation contract** — xunit + `GC.GetAllocatedBytesForCurrentThread()` in
  `PlaybackClockAllocationTests` (Core) and `NDIIngestPlaybackClockAllocationTests` (NDI);
  covers `MediaClock` (via master), `CompositePlaybackClock`, `VideoPtsClock`,
  `NDIIngestPlaybackClock`.

### 13.2 Architecture doc sweep

- ✅ `Doc/MediaFramework-Architecture.md` — builder entry + metrics/triggers.
- ✅ `Doc/MediaFramework-Format-Support.md` — `Rgba16`, `Rgba16F`, `P216`, `Pa16`.
- ✅ `Doc/MediaFramework-Quickstart.md`.
- ✅ `Doc/MediaFramework-PublicAPI.md` (assembly map + regen commands).
- ✅ `Doc/MediaFramework-Triggers.md` (Phase 11).
- ✅ `Doc/Archive/2026-05/README.md`.
- ✅ `Doc/MediaFramework-Critical-Review-2026-05-22.md` §10.5, §3.4, §2.2.4 updates.

### 13.3 Cleanup

- ⏭️ Delete `[Obsolete]` shims — **deferred** to next major (`MediaPlayer.TryOpen*` still used by builder pragmas and hosts).
- ✅ LOC + public-type snapshot documented in `MediaFramework-PublicAPI.md` (regen commands).

**Phase 13 exit criteria**: framework ships v2.0 with a documented
quickstart, public-API enumeration, trigger-id vocabulary, and a test
set guarding the public surface (including the allocation-contract suite).

---

## Summary by phase

| Phase | What | Breaking | Risk | Effort | LOC impact |
|---|---|:---:|:---:|:---:|---:|
| 0  | Dead code + macOS strip + diag helper | ✗ | None | M | −2 500…−3 000 |
| 1  | `Sink → Output` rename | ✓ (mechanical) | Low | M | ~0 |
| 2  | Additive `Runtime.Init()` + source/container facades + `AudioClip` soundboard primitives | ✗ | Low | M | +800 |
| 3  | `StreamAvioBridge` | ✗ | Medium | M | +200 |
| 4  | Player façade collapse + router hot-path pre-resolve | ✓ | Medium | M | −600 |
| 5  | `MediaPlayer.Open(...)` builder + `S.Media.Quick` deletion | ✓ | Medium | M | −650 |
| 6  | NDI surface collapse | ✓ | Medium | L | −1 500 |
| 7  | `S.Media.Effects` extraction + `VideoCompositor` | ✓ | Medium | L | ±0 (moved) |
| 8  | Clock surface narrowing | ✓ (small) | Low | S | ~0 |
| 9  | `VideoFrame` + `AudioFrame` release unification + `VideoFramePool` | ✓ (wide) | High | L | −200 |
| 10 | Bit-depth completeness | ✗ | Medium | M | +400 |
| 11 | Async + metrics + extensibility + `TriggerBus` (OSC/MIDI) | ✗ | Low | M | +500 |
| 12 | Encoder project | ✗ | Medium | L | +new (~1 000) |
| 13 | Tests + docs (incl. allocation contract + public-API page) | ✗ | Low | M | +tests |

**Rough net**: −4 000 LOC in the existing media stack, +~2 000 LOC of new
soundboard / encoder / trigger / tests, and a substantially smaller
*public* surface. The biggest gains are concentrated in Phases 0, 4, 6,
and 7. The biggest *new* product wins are the soundboard primitives
(Phase 2.6) and the trigger bus (Phase 11.5).

---

## When to stop

If you can only ship some of this, the priorities I'd hold to:

- **Must**: Phases 0, 1, 2. Zero-risk cleanup, naming, the new six-line
  API **and the soundboard primitives** (Phase 2.6) — the latter unblocks
  a missing product surface HaPlay needs.
- **Should**: Phases 3, 4, 5, 6. Stream IO, surface narrowing, builder, NDI
  collapse. Phase 4 also pays a one-time perf debt (hot-path dictionary
  lookups, §4.6) that the soundboard will exercise heavily.
- **Could**: Phases 7, 8, 10. Composition API, clock tidy, bit-depth
  completeness. Nice-to-have, but not blocking.
- **Won't (until needed)**: Phases 9, 11, 12. `VideoFrame` redesign,
  async/metrics polish + `TriggerBus`, encoder project. Land 11.5
  (`TriggerBus`) when the Mond / OSC / MIDI script layer is ready to
  consume it; land 9 when the heap pressure from `VideoFrame`
  allocations or the `Action?` release closures becomes measurable.

Phase 13 (tests + docs) is implicit alongside whatever subset ships.

---

# HaPlay cross-check — what its usage proves, deletes, and promotes

HaPlay is the only real consumer the framework has today. A grep across
its ~18 kLOC tells us exactly which framework pieces carry weight and which
were "future-proofing" that never landed a caller. Use this section as a
sanity layer over the phases above before merging anything destructive.

## Public-type usage count (HaPlay, framework symbols only)

Counted as identifier mentions across `UI/HaPlay/**/*.cs`:

| Type | Mentions | Notes |
|---|---:|---|
| `PortAudioOutput`              | 120 | Held directly + reconfigured via `PortAudioOutputRuntime` |
| `MediaPlayer`                  |  84 | The single playback anchor |
| `PortAudioInput`               |  75 | Pre-connect cache + per-cue capture |
| `VideoCpuFrameConverter`       |  32 | Used directly — see "promotion" §H.3 |
| `NDIOutput`                    |  20 | Per-runtime carrier |
| `VideoRouter`                  |  19 | Hot output add / remove |
| `NDILiveReceiver`              |  15 | Always combined A+V — validates §10.2 |
| `ResamplingAudioOutput`          |  14 | Sample-rate adapters |
| `AudioRouter`                  |  13 | Hot output add / per-cell matrix |
| `VideoOutputPump`                |  10 | NDI fan-out + logo wrapping |
| `VideoCompositorSource`          |   9 | Used by `OutputPresetVideoSource`, `LockedFormatVideoSink` |
| `CpuVideoCompositor`           |   7 | Same |
| `MediaContainerSession`        |   6 | Only for `Play/SeekCoordinated*` — Phase 4 absorbs |
| `SDL3GLVideoOutput`              |   4 | The GL variant only |
| `MediaContainerDecoder`        |   4 | Just to probe `HasAudio/HasVideo` before opening MediaPlayer |
| `IAvPlaybackSession`           |   3 | Façade type — Phase 4 internalises |
| `MediaPlaybackSession`         |   2 | Same |
| `VideoPlayer`                  |   2 | Only `MediaPlayer.Video` reads |
| `VideoOpenGlControl`           |   2 | Avalonia engine outputs |
| `NDIVideoReceiver`             |   2 | Internal use through `NDILiveReceiver` — Phase 6 makes internal |
| `AudioPlayer`                  |   2 | Only as `player.Audio.Router` — Phase 4 absorbs |
| `NDIAudioReceiver`             |   1 | Same as above |
| `PixelFormatConvertingVideoSource` | 1 | Live BGRA conversion |
| `FadeFromBlackVideoSource`     |   1 | File cue fade-in |

## Public framework types HaPlay does **not** touch at all

Confirms the §10 / phase-plan deletions and `internal`-isations are safe:

| Type / namespace | Used by HaPlay? |
|---|---|
| `AvPlaybackCoordinator` | ❌ (Phase 4 internalises) |
| `MediaContainerPlaybackBundle` | ❌ (always reached via `MediaPlayer.TryOpen`) |
| `IPlaybackTimeline`, `IPlaybackPlayhead` | ❌ (Phase 8 merge into `IPlayhead`) |
| `VideoPtsClock`, `CompositePlaybackClock` | ❌ |
| `NDIIngestPlaybackClock` (direct use) | ❌ (used inside NDI receivers only) |
| `NDIEgressMuxPlayheadClock`, `NDIAlignedRouterClock`, `IngestSlavedRouterClock` | ❌ (Phase 0 deletes; HaPlay confirms zero callers) |
| `NdiFrameSyncSession`, `NdiFrameSyncAudioSource`, `NdiFrameSyncVideoSource` | ❌ (Phase 6 deletes) |
| `VideoOutputRouter` (obsolete) | ❌ (Phase 0 deletes) |
| `AudioBus` | ❌ (rename to `AudioBus` per §10.1; HaPlay didn't need a bus) |
| `AudioGraphBuilder` | ❌ (Phase 4 deletes) |
| `PortAudioPlaybackHost` | ❌ (HaPlay handles its own wiring) |
| `AdaptiveRateAudioOutput` | ❌ (the primitive is fine to keep, just not auto-wired) |
| `ResamplingAudioSource` | ❌ (only output-side resampler ever used) |
| `StaticFrameSource` | ❌ |
| `HardwareVideoWin32Nv12`, `VideoDmabuf*Backing`, `VideoWin32Nv12Backing` direct ctor calls | ❌ (handled inside FFmpeg/OpenGL) |
| All 5 unused HW interop placeholders (`MetalIosurfaceNv12Interop` etc.) | ❌ (Phase 0 deletes) |
| macOS targets / `OperatingSystem.IsMacOS()` branches | ❌ (HaPlay is Linux + Windows; Phase 0 strips) |

Every "delete" / "internal" in Phase 0 / 6 lines up with a zero-mention type
above. **Nothing in the plan removes a type HaPlay still leans on.**

## Framework primitives HaPlay had to build itself (promote into the framework)

These are HaPlay-local types that solve framework-generic problems. They
suggest gaps the framework should fill so the *next* consumer doesn't
re-invent them. Each maps cleanly onto a phase already in the plan.

### H.1 `LockedFormatVideoSink` → framework `VideoOutputFormatLock`

`UI/HaPlay/Playback/LockedFormatVideoSink.cs` (188 LOC). Pins pixel format
and/or output resolution on an `IVideoOutput`, regardless of what the source
produces. HaPlay needs this for NDI outputs (operators select "1920×1080
UYVY for this NDI sender" and expect every clip to land on those numbers).

**Promotion**: ships as part of **Phase 7** (`S.Media.Effects`). Most of it
collapses onto the new `VideoCompositor` API once that lands:

```csharp
var locked = VideoCompositor
    .Create(new VideoFormat(1920, 1080, PixelFormat.Uyvy, …))
    .AddLayer(decoderVideo, LayerConfig.Background)
    .AsVideoOutput(ndiOutput.Video);
```

…with `AsVideoOutput(IVideoOutput inner)` collapsing the layered source +
inner output into one `IVideoOutput` the router can attach to. Removes the
~188 LOC HaPlay carries today.

Add to **Phase 7 checklist**:
- 🔲 `VideoCompositor.AsVideoOutput(IVideoOutput inner)` — turn a fixed-raster
  compositor into a output wrapper so consumers can lock format/resolution on
  an output (replaces HaPlay's `LockedFormatVideoSink`).

### H.2 `OutputPresetVideoSource` → framework "program raster" wrapper

`UI/HaPlay/Playback/OutputPresetVideoSource.cs` (109 LOC). Letterboxes the
decoder video into a fixed program raster (1080p60 / 720p60 / Custom). Same
mechanism as H.1 (compositor + letterbox transform) on the source side.

**Promotion**: After **Phase 7**, this is two lines on the consumer side:

```csharp
var program = VideoCompositor.Create(new VideoFormat(1920, 1080, …))
    .AddLayer(decoder.Video, LayerConfig.Background);
```

The "Background" preset on `LayerConfig` already letterboxes (§10.6 of the
review). The 109 LOC wrapper vanishes.

Add to **Phase 7 checklist**:
- 🔲 `LayerConfig.Background` preset honours letterbox/contain semantics —
  matches HaPlay's `OutputPresetVideoSource.LetterboxTransform`.

### H.3 `VideoCpuFrameConverter` direct dependency on `S.Media.FFmpeg`

HaPlay uses `VideoCpuFrameConverter.CanConvert(src, dst, w, h)` static and
`new VideoCpuFrameConverter()` directly — that's an `S.Media.FFmpeg`
dependency *just to ask "can this conversion happen?"*. The framework has
`VideoCpuFrameConverterRegistry` (Core) to abstract over this but HaPlay
doesn't use it because the registry doesn't expose `CanConvert`.

Add to **Phase 2 checklist** (additive, non-breaking):
- 🔲 `VideoCpuFrameConverterRegistry.CanConvert(src, dst, w, h)` static — already
  has the backing slot (`CanConvertProbe`), just needs the public façade.
  Lets pure-Core consumers ask without referencing FFmpeg.
- 🔲 `IVideoCpuFrameConverter` already exists as the abstraction; document
  `VideoCpuFrameConverterRegistry.Create()` as the consumer API.

After this, HaPlay's `CompositorLayerConverter` drops the `S.Media.FFmpeg`
`using` and becomes Core-only.

### H.4 `FallbackTemplateVideoOutput` (idle slate + frame-substitution)

`UI/HaPlay/Playback/LogoFallbackVideoSink.cs` (421 LOC). Wraps any
`IVideoOutput` and can:
- substitute a static template frame on demand ("hold" mode),
- apply per-output opacity (fade in / out),
- cache the most recent real frame so toggling hold off restores the source
  (single-frame-source mode for cover art / static images).

The hold-template substitution + idle-slate pumping are a recurring need
for any cue-player / soundboard / studio tool that wants outputs to keep
ticking when there's no decoder running. The per-output opacity is the
same concept as a `LayerConfig.Opacity` transition (§10.6).

**Promotion**: split into two:

- `FallbackTemplateVideoOutput` (in `S.Media.Effects`, ~60 LOC) — wraps an
  `IVideoOutput`, exposes `SetHoldTemplate(VideoFrame)` and `Hold = true/false`.
  Generic primitive.
- Per-output opacity stays HaPlay-local OR moves to `VideoCompositor` as
  a per-layer transition (it's already there — H.2 covers it).

Add to **Phase 7 checklist**:
- 🔲 `FallbackTemplateVideoOutput` — promote HaPlay's hold-template logic
  (without the single-frame-source cache, which is a HaPlay UX concern).

The single-frame-source cache and the BGRA `VideoCpuOpacity` helper can
stay in HaPlay; they're cue-player UX rather than framework primitives.

### H.5 `MediaContainerDecoder.Open(...)` "probe first" pattern

HaPlay's `HaPlayPlaybackSession.TryCreate` opens the decoder before calling
`MediaPlayer.TryOpen` *just* to probe `HasAudio` / `HasVideo`:

```csharp
decoder = MediaContainerDecoder.Open(mediaPath, mpOpt.ToVideoDecoderOpenOptions());
var hasVideo = decoder.HasVideo;
var hasAudio = decoder.HasAudio;
// … decide which outputs to acquire …
if (!MediaPlayer.TryOpen(decoder, …, MediaPlayerDecoderOwnership.BundleDisposesDecoder, …))
```

This works but it ties HaPlay to a multi-step open. The Phase 5 builder API
should expose this directly:

Add to **Phase 5 checklist**:
- 🔲 `MediaPlayer.OpenBuilder.ProbeContainer(out bool hasAudio, out bool hasVideo)` —
  a builder method that opens the decoder, reports the streams it found,
  and lets the consumer decide which outputs to wire before `TryBuild`.
- 🔲 Alternatively: `OpenBuilder.OnContainerProbed(Action<ContainerInfo> hook)`
  callback fired during `TryBuild` between decode-open and player-construct
  steps.

### H.6 Pre-connect / pre-roll cache pattern

HaPlay has three near-identical caches (`CuePreRollCache`,
`NdiInputPreConnectCache`, `PortAudioInputPreConnectCache`) — bounded LRU
maps from `Guid cueId → opened-resource` with a `CacheKey` invalidation
contract. ~380 LOC combined.

The pattern is consumer-level (HaPlay knows what "an upcoming cue" means),
but it would benefit from a small framework helper:

Add to **Phase 11 checklist** (additive, optional):
- 🔲 `MediaResourceLruCache<TKey, TResource>` — generic bounded LRU with
  `TryTake/Store/EvictExcept/InvalidateAll` semantics. Three HaPlay caches
  collapse to one type each plus the resource-specific open delegate.

Low priority — HaPlay's three caches are not much code total, and the
pattern is simple enough to copy if a future consumer needs the same shape.

### H.7 Hot output add/remove during running session

HaPlay's `TryAddOutput` / `TryRemoveOutput` (in `HaPlayPlaybackSession`) hot
add/remove an output line during a running session. The framework supports
this — `AudioRouter` and `VideoRouter` both allow dynamic mutation while
running — but the session-level orchestration (acquire output runtime →
configure output → add router output → add router route → install audio
matrix → restore previous state on failure) is ~200 LOC of HaPlay-side
glue.

**Verdict**: HaPlay-specific (the "output runtime" concept and the audio
matrix UX don't belong in the framework). The framework's dynamic-graph
support is correct; HaPlay's orchestration over it is the right layer.

**Watch-out**: Phase 4 (player façade collapse) and Phase 5 (builder API)
must preserve the post-construction `player.AudioRouter.AddOutput(...)` and
`player.VideoRouter.AddOutput(...)` + `TryAddRoute(...)` paths. They're the
foundation HaPlay's hot-add depends on. Mark this in the Phase 4 / Phase 5
verification checklist.

Add to **Phase 4 verification**:
- 🔲 Verify `player.AudioRouter` and `player.VideoRouter` mutation methods
  remain callable while the session is playing (router dynamic-graph
  contract intact).

### H.8 Per-cell channel-mix matrix is load-bearing

HaPlay's audio matrix UX (`AudioMatrixCellConfig`, `TrySetOutputMatrix`) is
the *only* current user of the multi-route-per-pair `AudioRouter.AddRoute(...,
routeId, ...)` API and `SetRouteGainById`. It installs one route per
non-zero matrix cell.

Add to **Phase 4 verification**:
- 🔲 `AudioRouter.AddRoute(srcId, outputId, routeId, map, gain)` and
  `SetRouteGainById` are kept. The legacy `AddRoute(srcId, outputId, map, gain)`
  overload deprecates per §2.2.5 of the review, but the explicit-routeId path
  stays.

## Phase 0 confirmation: every "delete" is HaPlay-safe

Cross-reference: Phase 0 of the checklist proposes deleting these files /
features. HaPlay's usage count column confirms each is safe.

| Phase 0 deletion | HaPlay mentions | Safe? |
|---|---:|:---:|
| `VideoOutputRouter.cs` | 0 | ✓ |
| `MetalIosurfaceNv12Interop.cs` | 0 | ✓ |
| `VulkanExternalNv12Interop.cs` | 0 | ✓ |
| `WindowsNv12D3D11TextureInterop.cs` | 0 | ✓ |
| `WindowsNv12SharedHandleInterop.cs` | 0 | ✓ |
| `LinuxDmabufNv12Interop.cs` | 0 | ✓ |
| `NDIEgressMuxPlayheadClock.cs` | 0 | ✓ |
| `NDIAlignedRouterClock.cs` | 0 | ✓ |
| `IngestSlavedRouterClock.cs` | 0 | ✓ |
| `MediaContainerDecoder.LegacyAudio`/`LegacyVideo` | 0 | ✓ |
| `PALib/CoreAudio/` | 0 | ✓ (Linux+Windows only) |
| `OperatingSystem.IsMacOS()` branches | 0 | ✓ |
| `AudioBus` (rename to `AudioBus` in Phase 1) | 0 | ✓ (could also be deleted entirely — HaPlay never instantiates one) |
| `NdiFrameSync*` (Phase 6) | 0 | ✓ |
| `AvPlaybackCoordinator` (Phase 4 internalise) | 0 | ✓ |
| `MediaPlaybackSession` / `IAvPlaybackSession` (Phase 4 internalise) | 2 / 3 | ✓ (HaPlay holds `MediaContainerSession.Session` as `IAvPlaybackSession`; after Phase 4 it accesses the same methods via `MediaPlayer` directly) |

## Net effect on HaPlay after the full plan ships

Files HaPlay can delete or shrink once the framework absorbs the
HaPlay-built primitives:

| HaPlay file | After R7 / Phase 7 |
|---|---|
| `LockedFormatVideoSink.cs` (188 LOC) | **Delete** — replaced by `VideoCompositor.AsVideoOutput` |
| `OutputPresetVideoSource.cs` (109 LOC) | **Delete** — replaced by `VideoCompositor.AddLayer(…, LayerConfig.Background)` |
| `OutputPresetFormats.cs` (57 LOC) | **Shrink** — `LetterboxTransform` moves into `LayerConfig.Background` resolution |
| `CompositorLayerConverter.cs` (42 LOC) | **Delete** — `VideoCompositor` accepts non-BGRA layers natively after the GL-backend default kicks in |
| `LogoFallbackVideoSink.cs` (421 LOC) | **Shrink to ~150 LOC** — `FallbackTemplateVideoOutput` (framework) covers hold-template; opacity becomes a transition; single-frame-source cache stays HaPlay |
| `PlaybackVideoPipeline.cs` (101 LOC) | **Shrink** — fade-in becomes a `Transition.FadeFromBlack` preset; UYVY-conversion becomes a `LayerConfig` flag |
| `IdleLogoSlateSession.cs` (224 LOC) | **Light change** — drives `FallbackTemplateVideoOutput.SubmitTemplateFrame` directly instead of HaPlay's wrapper |

Total HaPlay LOC reduction from framework promotions: **~500–700 lines**.
Plus the existing HaPlay code becomes simpler-to-read because the
domain-specific intent (letterbox, format-lock, hold-template) is spoken in
framework verbs.

## Two new checklist items to land alongside Phase 7

These came out of the HaPlay cross-check above and should be added to the
existing Phase 7 task list:

- 🔲 **`VideoCompositor.AsVideoOutput(IVideoOutput inner)`** — collapse the
  fixed-raster compositor into an `IVideoOutput` so it can be plugged
  directly into a `VideoRouter.AddOutput(...)` slot (replaces HaPlay's
  `LockedFormatVideoSink`).
- 🔲 **`FallbackTemplateVideoOutput`** — generic hold-template substitution
  wrapper (replaces the substrate of HaPlay's `LogoFallbackVideoSink`).

## Closing read

After the full plan ships, the consumer of the framework writes one of
two paths:

1. **The minimum-viable shape** (six lines for audio-only, twelve for A+V) —
   §10.5 of the review.
2. **A HaPlay-like cue-driven host** — uses `MediaPlayer.Open(...).WithVideoLead(...)`,
   `AudioRouter.AddOutput` + `Route(...)` for hot edits, `VideoCompositor`
   for program raster and PiP, `FallbackTemplateVideoOutput` for slate, and
   the per-cell matrix API for routing — all framework primitives.

What HaPlay had to build on top **today** (~4 000 LOC of `UI/HaPlay/Playback/`)
shrinks by ~500–700 LOC after Phases 4 + 7 land, and the parts that
*remain* are honest HaPlay-domain code (cue lists, output runtime
management, audio matrix UI) rather than framework workarounds.
