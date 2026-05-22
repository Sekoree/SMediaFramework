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

- 🔲 `MediaFramework/Media/S.Media.FFmpeg/Video/VideoOutputRouter.cs` — already `[Obsolete]`, only self-referenced.
- 🔲 `MediaFramework/Media/S.Media.Core/Video/MetalIosurfaceNv12Interop.cs`
- 🔲 `MediaFramework/Media/S.Media.Core/Video/VulkanExternalNv12Interop.cs`
- 🔲 `MediaFramework/Media/S.Media.Core/Video/WindowsNv12D3D11TextureInterop.cs`
- 🔲 `MediaFramework/Media/S.Media.Core/Video/WindowsNv12SharedHandleInterop.cs`
- 🔲 `MediaFramework/Media/S.Media.Core/Video/LinuxDmabufNv12Interop.cs`
- 🔲 `MediaFramework/Media/S.Media.NDI/Clock/NDIEgressMuxPlayheadClock.cs` — only self-referenced (verify).
- 🔲 `MediaFramework/Media/S.Media.NDI/Clock/NDIAlignedRouterClock.cs` — only self-referenced (verify).
- 🔲 Verification: `dotnet build` clean; no warnings; existing tests pass.

### 0.2 Drop macOS scaffolding (Linux + Windows only)

- 🔲 Delete `MediaFramework/Audio/PALib/CoreAudio/` (whole folder: `Native.cs`, `PaMacCoreStructs.cs`).
- 🔲 Remove every `OperatingSystem.IsMacOS()` branch
  - `S.Media.FFmpeg/FFmpegRuntime.cs` (`if (… IsLinux || IsMacOS) ffmpeg.RootPath = "";` → `if (IsLinux) …`)
  - `S.Media.Core/Video/VideoFrame.cs` (Win32-shared static factory's macOS PNS check is dead — keep only the platform guard for Windows-only).
  - any others surfaced by grep.
- 🔲 Strip `osx-*` RIDs from `Directory.Packages.props` and any
  `PackageReference Condition=` that targets macOS.
- 🔲 Strip macOS targets from `MFPlayer.sln.DotSettings.user` if present.
- 🔲 Verification: build on Linux + Windows; no IDE warnings about missing targets.

### 0.3 `MediaDiagnostics.SwallowDisposeErrors(...)` helper

- 🔲 Add `MediaDiagnostics.SwallowDisposeErrors(Action dispose, string label)` —
  collapses the 4–6-line `try / #if DEBUG / catch / #else / catch { }` pattern
  to one line per call site.
- 🔲 Mechanically replace the ~70 occurrences across the framework
  (`grep -rn "#if DEBUG" --include="*.cs" | grep MediaDiagnostics.LogError`).
  Tighter sub-count: `grep -rn "#if DEBUG" --include="*.cs" MediaFramework | grep MediaDiagnostics.LogError`
  returns ~23 exact-match lines; the broader `#if DEBUG` ceremony (Dispose,
  Cancel, swallowed `ObjectDisposedException`) covers the remaining ~50 to
  reach ~70 total.
- 🔲 Verification: LOC reduction ~250–350 lines; tests still green. Pin the
  final exact-match count in the commit message so future readers don't
  re-debate the math.

### 0.4 Strip `LegacyAudio` / `LegacyVideo` from `MediaContainerDecoder`

- 🔲 Remove the always-null `LegacyAudio` and `LegacyVideo` properties from
  `MediaFramework/Media/S.Media.FFmpeg/MediaContainerDecoder.cs`.
- 🔲 Verification: nothing in the solution references them — confirm with a
  grep before deleting.

### 0.5 Audit `JackLib` reference claim

- 🔲 `MediaFramework/Audio/JackLib/JackLib.csproj` has
  `<InternalsVisibleTo Include="S.Media.PortAudio" />` plus a comment that
  "S.Media.PortAudio uses JackLib for JACK port/connection management" —
  but `grep "using JackLib" S.Media.PortAudio/**` is empty.
- 🔲 Decision: **either** wire it up (PortAudio's JACK host could call
  `JackClient` for port autoconnect) **or** drop the IVT and the comment.
  Default: drop the IVT — autoconnect can be added when needed.
- 🔲 Verification: `JackLib` is genuinely standalone (still useful for
  external tooling); the project ref story is honest.

### 0.6 Audit `S.Media.Core` `InternalsVisibleTo`

- 🔲 Today: PortAudio, SDL3, Avalonia all have IVT into Core.
- 🔲 For each backend, grep for which `internal` Core members it touches.
- 🔲 Promote those few members to `public` (or `internal` + small explicit
  helper class) and drop the IVT entries (keep Tests/Benchmarks).
- 🔲 Verification: backends compile without the IVT; Core no longer leaks
  internals to host packages.

### 0.7 Move `Extras` libs

- 🔲 Move `MediaFramework/MIDI/PMLib/`, `MediaFramework/OSC/OSCLib/`, and
  `MediaFramework/Audio/JackLib/` under `MediaFramework/Extras/` so the
  dependency story at solution level shows them as ancillary.
- 🔲 Update `MFPlayer.sln` project paths; verify all references resolve.
- 🔲 Verification: solution builds; HaPlay (uses PMLib, OSCLib) still links.

**Phase 0 exit criteria**: build green, tests green, ~2 500–3 000 LOC removed,
no behavior change, no API change.

---

## Phase 1 — Mechanical rename: `Sink` → `Output`

> **Risk**: Low · **Effort**: M · **Breaking**: ✓ (mechanical, Rider-driven)
>
> Goal: settle on `Source` for producers and `Output` for consumers
> everywhere, so subsequent phases don't have to thread a stale "Sink"
> identifier through new code.

### 1.1 Interfaces

- 🔲 `IAudioSink` → `IAudioOutput`
- 🔲 `IClockedSink` → `IClockedOutput`
- 🔲 `IFlushableSink` → `IFlushableOutput`
- 🔲 `IAudioSinkChannelCapabilities` → `IAudioOutputChannelCapabilities`
- 🔲 `AudioSinkChannelCapabilities` (struct) → `AudioOutputChannelCapabilities`
- 🔲 `IVideoSink` → `IVideoOutput`
- 🔲 `IVideoSinkD3D11GlBorrowSetup` → `IVideoOutputD3D11GlBorrowSetup`

### 1.2 Concrete classes

- 🔲 `DiscardingAudioSink` → `DiscardingAudioOutput`
- 🔲 `DiscardingVideoSink` → `DiscardingVideoOutput`
- 🔲 `BusSink` → `AudioBus` (it's both a source and a sink — `Bus` describes the role better than `Sink`)
- 🔲 After rename, sanity-check `AudioBus` XML doc to make the dual-role
  obvious: `IAudioOutput` (consumer-facing) and `IAudioSource` (router-facing)
  on the same instance. `AudioBus` is the only such type in the framework —
  pin the doc comment so the next reader doesn't assume it's just an output.
- 🔲 `CompositorVideoSink` → `VideoCompositorSource` (it's an `IVideoSource`; layer inputs become outputs internally)
- 🔲 `SDL3VideoSink` → `SDL3VideoOutput`
- 🔲 `SDL3GLVideoSink` → `SDL3GLVideoOutput`
- 🔲 `VideoOpenGlControl` — keep name (Avalonia control naming) but its
  `IVideoSink` impl flips to `IVideoOutput`.
- 🔲 `NDIAudioSink` → `NDIAudioOutput` (will go internal in Phase 6, but rename first for consistency)
- 🔲 `NDIAudioAggregatingSink` → `NDIAudioAggregatingOutput` (or delete in Phase 6)
- 🔲 `AdaptiveRateAudioSink` → `AdaptiveRateAudioOutput`
- 🔲 `ResamplingAudioSink` → `ResamplingAudioOutput`

### 1.3 Router method names + internal pump types

- 🔲 `AudioRouter.AddSink/RemoveSink` → `AddOutput/RemoveOutput`
- 🔲 `AudioRouter.SinkPump` (internal) → `OutputPump`
- 🔲 `AudioRouter.SinkPumpStats` → `OutputPumpStats`
- 🔲 `AudioRouter.GetPumpStats(sinkId)` → `GetPumpStats(outputId)` (param + doc rename)
- 🔲 `AudioRouterSinkErrorEventArgs` → `AudioRouterOutputErrorEventArgs`
- 🔲 `AudioRouter.SinkErrored` event → `OutputErrored`
- 🔲 `VideoSinkPump` → `VideoOutputPump`
- 🔲 `VideoSinkPumpAttachOptions` → `VideoOutputPumpAttachOptions`
- 🔲 `VideoSinkPumpMetrics` → `VideoOutputPumpMetrics`
- 🔲 `VideoSinkPumpPressureEventArgs` → `VideoOutputPumpPressureEventArgs`
- 🔲 `VideoSinkFanoutFormats` → `VideoOutputFanoutFormats`

### 1.4 Player / session params + docs

- 🔲 `AudioPlayer.AddOutput` (already named correctly — verify XML doc still
  says "sink"; replace).
- 🔲 `VideoRouter.AddOutput` (already named correctly — same XML pass).
- 🔲 `MediaContainerSession`, `MediaPlaybackSession`, `AvPlaybackCoordinator`:
  comments and param names referring to "sink" / "primary sink" → "output" / "primary output".
- 🔲 `IVideoSinkD3D11GlBorrowSetup` (already renamed in 1.1 — verify
  no missed usages in shader/internal code).

### 1.5 Tests & tools

- 🔲 Update `*Tests` projects — Rider's symbol rename handles 95%; sweep
  XML test-name strings if any.
- 🔲 Update `Tools/PlaybackSmoke`, `Tools/VideoPlaybackSmoke`, `Tools/NDIPlayer`,
  `Tools/NDIReceiver`, `Tools/CompositorSmoke`.
- 🔲 **Update `HaPlay` references in the same PR**. Direct assembly references
  mean the rename can't ship in isolation — coordinate the HaPlay-side
  compile with the framework-side rename so the working tree never has a
  half-renamed state. (Outside the framework review's *design* scope but
  inside its *merge* scope.)

### 1.6 Verification

- 🔲 `dotnet build` solution: clean.
- 🔲 `grep -rn "Sink" MediaFramework/Media/**/*.cs | grep -v Test` returns
  zero hits (or only hits in shader-string comments — flag them).
- 🔲 Tag the commit as a clear breaking-rename so consumers can `git revert`
  if they need a temporary back-port.

**Phase 1 exit criteria**: one big rename commit; downstream code compiles
after a Rider symbol rename; no behavior change.

---

## Phase 2 — Additive helpers (no breaking)

> **Risk**: Low · **Effort**: M · **Breaking**: ✗
>
> Goal: give consumers the six-line API shape from §10.5 of the review
> without removing any existing call site. Subsequent phases delete the
> old paths.

### 2.1 `MediaFrameworkRuntime.Init()` builder

- 🔲 New static `MediaFrameworkRuntime` in `S.Media.Core/Diagnostics/`
  (or top-level `S.Media.Core/`).
- 🔲 Fluent: `.UseFFmpeg()`, `.UsePortAudio()`, `.UseNDI()` — each calls
  the existing init/runtime (FFmpegRuntime.EnsureInitialized,
  PortAudioRuntime.Acquire, NDIRuntime.Create+release on dispose).
- 🔲 Idempotent; thread-safe.
- 🔲 Existing static slots (`AudioRouterAutoResample.SourceWrapper`,
  `VideoCpuFrameConverterRegistry.Factory`, `VideoDeinterlacerRegistry.Factory`)
  remain as backing storage — `UseFFmpeg` populates them.
- 🔲 `MediaFrameworkRuntime.Shutdown(TimeSpan? gracePeriod = null)` — drains
  the matched ref-count releases in reverse order (NDI → PortAudio →
  FFmpeg). Idempotent. Lets hosted processes (long-running cue servers,
  unit-test harnesses running hundreds of clips) tear down deterministically
  instead of relying on process exit.

### 2.2 Source factories

- 🔲 New static `AudioSource` class in `S.Media.Core.Audio`:
  - `AudioSource.OpenFile(string path, AudioSourceOpenOptions? options = null) : IAudioSource`
  - `AudioSource.OpenStream(Stream stream, AudioSourceOpenOptions? options = null) : IAudioSource` (delegates to `MediaContainerDecoder.OpenStream`'s audio track for now; Phase 3 swaps in `StreamAvioBridge`).
- 🔲 New static `VideoSource` class in `S.Media.Core.Video`:
  - `VideoSource.OpenFile(string path, VideoSourceOpenOptions? options = null) : IVideoSource`
  - `VideoSource.OpenStream(Stream stream, VideoSourceOpenOptions? options = null) : IVideoSource`
  - `VideoSource.OpenImage(string path) : IVideoSource` — forwards to `ImageFileSource.OpenFromFile`.
  - `VideoSource.OpenImage(Stream stream) : IVideoSource`
- 🔲 New static `MediaContainer` facade in `S.Media.FFmpeg`:
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

- 🔲 `AudioRouter.Route(string sourceId, string outputId, float gain = 1.0f)` —
  identity channel map sized to the output's channel count.
- 🔲 `AudioRouter.Route(string sourceId, string outputId, ChannelMap map, float gain = 1.0f)` —
  full overload, same as `AddRoute` but with the rename.
- 🔲 `AudioRouter.Play()` — alias for `Start()`.
- 🔲 `AudioRouter.AddSource(IAudioSource source, string? id = null, bool autoResample = false)` — already exists; verify XML doc.
- 🔲 `AudioRouter.DefaultAutoResample { get; set; } = false` (static) — flips
  the `autoResample` default for *new* sources without forcing every call
  site to spell it. Per-call override still wins. A new consumer who hits
  "source rate mismatch" once turns the static on and never sees it again.
  Mitigation: log a warning the first time a mismatch fires under the
  default so silent resamples don't surprise.

### 2.4 Plugin registry consolidation

- 🔲 New `MediaFrameworkPlugins` static (in `S.Media.Core`) that holds:
  - `AudioSourceFileFactory`, `AudioSourceStreamFactory`
  - `VideoSourceFileFactory`, `VideoSourceStreamFactory`
  - `ImageFileSourceFactory(string ext)`
  - (forwards the existing `AudioRouterAutoResample.SourceWrapper`,
    `VideoCpuFrameConverterRegistry.Factory`,
    `VideoDeinterlacerRegistry.Factory` slots, marking the older statics
    `[Obsolete("Use MediaFrameworkPlugins.X")]`)
- 🔲 `FFmpegRuntime.EnsureInitialized()` populates the file/stream source
  factories (so `AudioSource.OpenFile` works after `UseFFmpeg()` is called).
- 🔲 `S.Media.SkiaSharp` adds a static-init hook for the image factory; or
  `MediaFrameworkRuntime.UseSkiaSharpImages()` does it explicitly.

### 2.5 Verification

- 🔲 New tests in `S.Media.Core.Tests`: minimum-viable audio playback flow
  (`MediaFrameworkRuntime.Init().UseFFmpeg().UsePortAudio()` → six-line wire-up).
- 🔲 Old `MediaPlayer.TryOpen` and `AudioPlayer.AddOutput` paths still pass.

### 2.6 Soundboard primitives (`AudioClip` + voice + player)

> The previous review explicitly flagged this gap — *"there is no
> 'soundboard' abstraction — i.e. a player that owns N short clips and
> triggers any of them on demand without re-opening the file."* HaPlay
> needs a touch-grid UI of clip buttons (one-shot, polyphonic re-trigger,
> latched loop, choke groups). Today the only file-backed `IAudioSource`
> is `AudioFileDecoder` (one decoder per playback, `avformat_open_input`
> per press, no replay). This phase adds the in-memory clip primitive
> the soundboard sits on.

- 🔲 New `AudioClip` class in `S.Media.Core.Audio`:
  - `AudioFormat Format { get; }`, `TimeSpan Duration { get; }`,
    `int SamplesPerChannel { get; }`.
  - `static AudioClip OpenFile(string path, int? targetSampleRate = null, ChannelMap? mixdown = null)`.
  - `static AudioClip OpenStream(Stream stream, int? targetSampleRate = null, ChannelMap? mixdown = null)` (uses Phase 3's `StreamAvioBridge`).
  - `static AudioClip FromSamples(AudioFormat format, ReadOnlyMemory<float> interleaved)`.
  - `AudioClipVoice CreateVoice(AudioClipVoiceOptions? options = null)`.
- 🔲 New `AudioClipVoice : IAudioSource, IDisposable`:
  - One PCM buffer is shared across all voices of a clip; each voice owns
    only a cursor + ramp state. **Zero allocation per `ReadInto`** —
    `Buffer.BlockCopy` from the clip buffer into the router scratch.
  - `void Stop()` — triggers a click-free release fade (`ReleaseFade` from
    options); `IsExhausted` becomes true after the fade plays out, the
    router naturally drops the route on next state-snapshot.
  - `bool Loop { get; set; }` — flippable mid-play. Latched-loop UX: press
    1 ⇒ `Loop = true`; press 2 ⇒ `Loop = false; Stop();`.
  - `TimeSpan Position { get; }` for UI scrubbers.
- 🔲 New `AudioClipVoiceOptions` record struct:
  `(bool Loop, double StartOffsetSec, float StartGain, TimeSpan? AttackFade, TimeSpan? ReleaseFade)`.
  Click-free attack/release ramps use the same gain math `AudioRouter`
  already runs for `SetRouteGain` — keep policy in one place.
- 🔲 New `AudioClipPlayer` (per-pad helper):
  - Owns one `AudioClip` and mints voices via `Fire(router, outputId, map?, gain?, options?)`.
  - `AudioClipPlayerMode { Polyphonic, MonoRetrigger, OneShot, LatchedLoop }`
    governs what `Fire` does when a voice is already live.
  - `MaxPolyphony { get; set; } = 8` — bounded voice count; new fires past
    the limit reuse the oldest voice (stop-then-fresh).
  - `ChokeGroup { get; set; }` — pads in the same group stop each other.
  - `IReadOnlyList<AudioClipVoice> ActiveVoices { get; }` for UI.
  - `void StopAll()` — used by choke groups and by the operator's "panic" stop.
- 🔲 New `AudioRouter.RegisterChokeGroup(string label, IAudioSource voice)` /
  `UnregisterChokeGroup(string label, IAudioSource voice)`. The choke
  contract belongs in the router so any `IAudioSource` participates, not
  just clip voices.
- 🔲 New `Tools/SoundboardSmoke`: 32 voices of an 1-second clip firing at
  random offsets over 5 seconds with one PortAudio output. Asserts:
  - zero allocations on the router thread (after warm-up; measured via
    GC alloc tracking).
  - zero pump drops.
  - click-free joins (RMS at voice-start/voice-stop boundaries under
    threshold).
- 🔲 Decide on `AudioGraphBuilder` once `AudioClipPlayer.Fire` is in. The
  builder's `ConnectLast` is the soundboard wiring pattern; keep the
  builder **or** lift `ConnectLast` onto `AudioRouter.RouteLast()` and
  delete the builder. Either path keeps the soundboard ergonomic; pick
  one and ship.
- 🔲 Verification: HaPlay can build a 4×4 / 8×8 soundboard view-model on
  top of `AudioClipPlayer` with no framework patches needed.

**Phase 2 exit criteria**: §10.5 minimum-viable API works end-to-end on the
existing solution; **soundboard primitives ship and a 32-voice smoke
asserts allocation-free playback**; no consumer breakage; old API stays.

---

## Phase 3 — Stream IO via `StreamAvioBridge`

> **Risk**: Medium · **Effort**: M · **Breaking**: ✗ (additive; old
> temp-file path is renamed but retained)
>
> Goal: replace the temp-file spool with a real `AVIOContext` so finite and
> moderately-long streams play without disk overhead.

### 3.1 Implement `StreamAvioBridge`

- 🔲 New `S.Media.FFmpeg/Internal/StreamAvioBridge.cs` (~80 LOC).
- 🔲 Allocates a libav `AVIOContext` via `avio_alloc_context` with:
  - `read_packet` → `Stream.Read` (returns -1 on EOF).
  - `seek` → `Stream.Position` (when `isSeekable: true`); else null.
- 🔲 Manages buffer pinning; `Dispose` frees the context and the I/O buffer.
- 🔲 Caller owns the underlying `Stream`; the bridge does not dispose it.

### 3.2 Wire into `MediaContainerDecoder`

- 🔲 New entry point:

  ```csharp
  public static MediaContainerDecoder OpenStream(
      Stream stream,
      bool isSeekable = false,
      string? probeHintName = null,
      VideoDecoderOpenOptions? options = null);
  ```

- 🔲 Existing temp-file `OpenStream` renamed to `OpenStreamSpooled`
  (kept for the cases where libav can't probe format incrementally).
- 🔲 Update `MediaPlayer.TryOpenStream` to use the new path; keep the
  spooled fallback behind an `OpenStreamOptions.SpoolToDisk` flag.

### 3.3 `AudioSource` / `VideoSource` stream shortcut

- 🔲 `AudioSource.OpenStream(Stream, AudioSourceOpenOptions)` → `MediaContainerDecoder.OpenStream` audio track wrapper.
- 🔲 `VideoSource.OpenStream(Stream, VideoSourceOpenOptions)` → same for video.

### 3.4 Verification

- 🔲 Test: open an `MemoryStream` containing an MP3, play out via `DiscardingAudioOutput`, assert exhausts.
- 🔲 Test: open a non-seekable `Stream` (e.g. a `NetworkStream`-like wrapper); verify forward-only decode works and seek attempts throw cleanly.
- 🔲 Test: confirm temp file is NOT created for the new path (assert no
  `mf_stream_*` entries in `Path.GetTempPath()` afterwards).

**Phase 3 exit criteria**: `Stream`-based media plays without disk
spooling; legacy spooled path still selectable.

---

## Phase 4 — Player façade collapse

> **Risk**: Medium · **Effort**: M · **Breaking**: ✓
>
> Goal: cut the redundant wrappers (`AudioPlayer`, `AudioGraphBuilder`,
> `IAvPlaybackSession`, `MediaPlaybackSession`) so `AudioRouter` and
> `VideoRouter` (+ `VideoPlayer` for the clock-paced helper) are the
> production surface. `MediaPlayer` remains as the bundled all-in-one.

### 4.1 Fold `AudioPlayer` into `AudioRouter` + `MediaPlayer`

- 🔲 Move `AudioPlayer`'s auto-wire-primary behaviour into `AudioRouter`:
  - `AudioRouter.AutoSlaveTo(IClockedOutput)` selector logic.
  - `AudioRouter.AttachMasterClock(IPlaybackClock)` for `IPlaybackClock`-implementing outputs.
- 🔲 `AudioRouter.AddOwnedSource(IAudioSource)` for the "router disposes
  source on dispose" pattern (used to live on AudioPlayer).
- 🔲 `AudioRouter.AddOutputAndAutoWirePrimary(IAudioOutput, ...)` shorthand.
- 🔲 `MediaPlayer.Audio` → expose `AudioRouter` directly (currently `AudioPlayer`).
- 🔲 Delete `MediaFramework/Media/S.Media.Core/Audio/AudioPlayer.cs`.

### 4.2 Delete `AudioGraphBuilder`

- 🔲 Confirm `AudioRouter.Route(...)` (Phase 2.3) covers the use cases.
- 🔲 Delete `MediaFramework/Media/S.Media.Core/Audio/AudioGraphBuilder.cs`.
- 🔲 Update HaPlay if it uses it (out of scope for this review but flag the PR).

### 4.3 Internalise / drop session wrappers

- 🔲 Make `IAvPlaybackSession` `internal` (still implemented by
  `MediaPlaybackSession` but no longer a public contract).
- 🔲 Delete `MediaPlaybackSession` (or make `internal`).
- 🔲 Move `AvPlaybackCoordinator`'s methods to `internal` static helpers
  used by `MediaPlayer`.
- 🔲 Surface `Play / Pause / Seek / SeekCoordinated` on `MediaPlayer`
  directly (the methods already exist via the session; just promote).

### 4.4 `flushSharedMuxAfterPause` cleanup

- 🔲 Replace the `Action? flushSharedMuxAfterPause = null` parameter
  throughout `MediaPlayer` / `MediaContainerSession` with an enum:

  ```csharp
  public enum PauseFlushPolicy { FlushCodecPipelines, SkipFlush }
  ```

- 🔲 Custom delegate cases use the explicit `PauseWithFlushAction(Action)`
  helper (rare — call it out in XML doc).
- 🔲 Internalise `MediaContainerSession` while we're here.

### 4.5 Verification

- 🔲 All existing tests pass after fixing references.
- 🔲 New test: end-to-end `MediaPlayer.Open(path)…OpenAsync()` smoke
  (audio+video, audio only, image).
- 🔲 LOC reduction: ~600 lines (AudioPlayer + AudioGraphBuilder +
  MediaPlaybackSession + Coordinator partially internalised).

### 4.6 Pre-resolved routes — kill 4 dictionary lookups per route per chunk

> `AudioRouter.RunLoop` currently does, for every route, every chunk:
>
> ```csharp
> if (!snapshot.Sources.TryGetValue(route.SourceId, out var src)) continue;
> if (!snapshot.Sinks.TryGetValue(route.SinkId, out var sink)) continue;
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

- 🔲 `Route` record gains private resolved-entry fields:
  - `SourceEntry SourceEntryRef`
  - `SinkEntry SinkEntryRef`
  - `RouteGainSlot GainSlot` (small mutable single-slot class with
    `float Target` + `float Current`).
- 🔲 Resolved refs are populated in `AddRoute` and re-bound whenever
  `AddSource` / `RemoveSource` / `AddSink` / `RemoveSink` changes the
  state. Mutation pass walks routes once; chunks read the fields directly.
- 🔲 Remove `_currentGains` and `_routeTargetGains` `ConcurrentDictionary`
  instances. `SetRouteGain` / `SetRouteGainById` now flip
  `GainSlot.Target` — still lock-free, still click-free (run loop reads
  the same slot field).
- 🔲 Run loop becomes four field reads per route, no hashing.
- 🔲 Verification: existing router tests still pass (gain ramps, hot
  add/remove, slave-clock retarget). Soundboard smoke from Phase 2.6
  shows the expected drop in router-thread CPU under high route counts.
  Roughly 30% reduction in `RunLoop` CPU at 4096 routes is the rough
  target; benchmark and pin the number.

### 4.7 Stop / pause paths — replace LINQ array materialisation

- 🔲 `AudioRouter.StopInternal` and `FinishRunLoopThreadLifetime`
  currently do `activePumps = [.. _state.Sinks.Values.Select(e => e.Pump)]`
  (and the same for sinks-for-flush). Replace with a hand-written loop
  into a pre-sized `SinkPump[]`. Skip the allocation entirely when
  `_state.Sinks.Count == 0`.
- 🔲 Soundboard scenarios stop voices constantly (one press → one
  `RemoveSource`); making the stop path allocation-free is small but
  measurable.

### 4.8 Move `AudioPrefill` next to its only consumer

- 🔲 `S.Media.Core.Audio.AudioPrefill` is consumed only by
  `S.Media.PortAudio.AudioPlayerPortAudioExtensions`. After 4.1's
  `AudioPlayer` removal it has no Core caller. Move to
  `S.Media.PortAudio.Internal.AudioPrefill` (or inline into the extension
  if usage is single-site).

**Phase 4 exit criteria**: only `AudioRouter` + `VideoRouter` +
`VideoPlayer` + `MediaPlayer` remain as consumer entry points; router
hot path uses field reads, not dictionary lookups.

---

## Phase 5 — `MediaPlayer.Open(...)` builder API

> **Risk**: Medium · **Effort**: M · **Breaking**: ✓
>
> Goal: replace the 9 static `TryOpen*` overloads with one builder per entry
> verb. Old statics forward to the builder for one release, then go away.

### 5.1 Builder primitives

- 🔲 New `MediaPlayer.OpenBuilder` (file/uri/stream variants share most of it).
- 🔲 Static entry verbs (return a typed builder):

  ```csharp
  MediaPlayer.Open(string filePath)        → OpenFileBuilder
  MediaPlayer.OpenUri(Uri uri)             → OpenUriBuilder
  MediaPlayer.OpenStream(Stream s)         → OpenStreamBuilder
  MediaPlayer.OpenLive(IAudioSource?, IVideoSource?) → OpenLiveBuilder
  ```

- 🔲 Fluent surface on every builder:
  - `.WithOptions(MediaPlayerOpenOptions)` — or `.WithOptions(o => o with { … })`
  - `.WithVideoLead(IVideoOutput, bool dispose = false)`
  - `.WithPortAudio(deviceLatencyMs?: …, channels?: …)`
  - `.WithNDIOutput(NDIOutput, bool disposeOnPlayerDispose = false)`
  - `.WithDecoderOwnership(MediaPlayerDecoderOwnership)`
  - `.TryBuild(out MediaPlayer? player, out string? error)`
  - `.OpenAsync(CancellationToken ct = default) : Task<MediaPlayer>` (throws on failure)

### 5.2 Migrate old statics

- 🔲 Keep the old `MediaPlayer.TryOpen*` overloads, mark `[Obsolete("Use MediaPlayer.Open(...)")]`, have them forward to the builder.
- 🔲 Update every tool (`Tools/PlaybackSmoke`, `Tools/VideoPlaybackSmoke`, etc.) to use the builder.
- 🔲 Cut the `[Obsolete]` overloads in the next major.

### 5.3 Verification

- 🔲 New `MediaPlayer.Tests` smoke project: each canonical scenario in §5
  of the review has a test.
- 🔲 Old `TryOpen*` still compiles + works for one release.

### 5.4 Delete `S.Media.Quick`

- 🔲 After the builder ships, delete `MediaFramework/Media/S.Media.Quick/`
  outright (the previous plan was to rename it to `S.Media.SoundboardQuick`).
  The builder + `AudioClipPlayer` cover the "open and play" surface
  `QuickPlayer` exposed; the docstrings move into the quickstart page
  (Phase 13.2.3). One fewer assembly to publish.
- 🔲 Archive `S.Media.Quick`'s smoke under the new `MediaPlayer.Tests`
  smoke project.

### 5.5 Reduce option-record copying for downstream consumers

- 🔲 `MediaPlayerOpenOptions` becomes either an `init`-only `record` with
  `with`-friendly mutators **or** an `IMediaPlayerOpenOptions` read-only
  interface implementations can derive from. HaPlay currently translates
  `HaPlayFilePlaybackOptions → MediaPlayerOpenOptions` per open; after
  this consumers can stop carrying that translation layer.

**Phase 5 exit criteria**: `MediaPlayer.Open(...).WithVideoLead(...).OpenAsync()`
is the documented path; 9 statics collapse to 4 + builder; `S.Media.Quick`
is deleted.

---

## Phase 6 — NDI surface collapse

> **Risk**: Medium · **Effort**: L · **Breaking**: ✓
>
> Goal: one `NDISource` (combined A+V receiver), one `NDIOutput` (combined
> A+V sender). Per-stream types go internal; pull-mode session deleted.

### 6.1 `NDISource`

- 🔲 New class `NDISource : IDisposable` in `S.Media.NDI`.
- 🔲 Static `NDISource.Find(TimeSpan timeout, NDIFindOptions? options = null) : IReadOnlyList<NDIDiscoveredSource>`.
- 🔲 Static `NDISource.Open(NDIDiscoveredSource source, NDISourceOptions? options = null) : NDISource`.
- 🔲 Properties:
  - `IAudioSource Audio { get; }` — always non-null (silent when sender has no audio).
  - `IVideoSource Video { get; }` — always non-null (black/exhausted when sender has no video).
  - `IPlaybackClock IngestClock { get; }`
  - `bool ReceiveAudio { get; set; }` / `ReceiveVideo { get; set; }`
  - `NDIConnectionState State { get; }`
- 🔲 Implementation: lift `NDILiveReceiver` into `NDISource` (rename + small surface tidy).

### 6.2 Internalise per-stream receivers

- 🔲 `NDIVideoReceiver` → `internal sealed class` inside `S.Media.NDI`.
- 🔲 `NDIAudioReceiver` → `internal sealed class`.
- 🔲 Both keep their existing logic; only the access modifier and
  references-from-outside change.

### 6.3 Delete pull-mode + redundant clocks

- 🔲 Delete `NdiFrameSyncSession`, `NdiFrameSyncAudioSource`, `NdiFrameSyncVideoSource`, `NdiAudioFrameConverter`.
- 🔲 Delete `NDIEgressMuxPlayheadClock` and `NDIAlignedRouterClock` if still unreferenced after Phase 0.

### 6.4 `NDIOutput` tidy

- 🔲 Internalise `NDIVideoSender`, `NDIAudioSink` (now `NDIAudioOutput`).
- 🔲 Surface only via `NDIOutput.Audio` (returns `IAudioOutput`) and
  `NDIOutput.Video` (returns `IVideoOutput`).
- 🔲 Fold `NDIMonitorReceiverPumpFusion`, `NDIFusionPlaybackHints`,
  `NDIEgressPresentationTimeline`, `NDIFrameTiming`, `NDIOutputExtensions`
  into `NDIOutput` as nested types / extension methods.

### 6.5 Verification

- 🔲 NDI receive smoke (live NDI source on local network → discard
  outputs; assert frame count > 0 within 2 seconds).
- 🔲 NDI send smoke (already covered by existing `Tools/NDIPlayer`).
- 🔲 LOC reduction: ~1500 lines in `S.Media.NDI` (4031 → ~2400).

**Phase 6 exit criteria**: NDI consumer code is `NDISource.Find(...)` →
`NDISource.Open(...)` → done; everything else is implementation detail.

---

## Phase 7 — `S.Media.Effects` extraction + `VideoCompositor` API

> **Risk**: Medium · **Effort**: L · **Breaking**: ✓
>
> Goal: video effects move out of Core; consumers drive composition via a
> declarative `LayerConfig` + `Transition` API.

### 7.1 New project

- 🔲 Create `MediaFramework/Media/S.Media.Effects/S.Media.Effects.csproj`.
- 🔲 References: `S.Media.Core`, `S.Media.OpenGL` (for the GL backend), `S.Media.FFmpeg` (for CPU converters via the registry).

### 7.2 Move existing primitives

- 🔲 Move from `S.Media.Core/Video/` → `S.Media.Effects/`:
  - `CompositorLayer.cs`, `CompositorSamplingMode.cs`, `CpuVideoCompositor.cs`
  - `BlendMode.cs`, `LayerOpacityTween.cs`, `LayerTransform2D.cs`
  - `VideoCpuOpacity.cs`
  - `FadeFromBlackVideoSource.cs`, `CutVideoSource.cs`, `StaticFrameSource.cs`, `PixelFormatConvertingVideoSource.cs`
  - `IVideoCompositor.cs`
  - `CompositorVideoSink.cs` → renamed to `VideoCompositorSource` (per Phase 1).
- 🔲 Move from `S.Media.OpenGL/` → `S.Media.Effects.OpenGL/` (sub-folder
  inside the same project, or a separate `S.Media.Effects.Gl` project):
  - `GlVideoCompositor.cs` and the GL composite shader resources.

### 7.3 Public `VideoCompositor` API (§10.6 of review)

- 🔲 `public sealed class VideoCompositor : IVideoSource, IDisposable`
  - `static Create(VideoFormat output, VideoCompositorBackend backend = Auto, VideoCompositorOptions? options = null)`
  - `LayerHandle AddLayer(IVideoSource source, LayerConfig config)`
  - `bool RemoveLayer(LayerHandle handle)`
  - `IReadOnlyList<LayerHandle> Layers { get; }`
  - `IPlaybackClock? Clock { get; set; }`
- 🔲 `public sealed class LayerHandle`
  - `IVideoSource Source { get; }`
  - `LayerConfig CurrentConfig { get; }`
  - `void SetConfig(LayerConfig)` (instant jump)
  - `void AddTransition(TimeSpan at, Transition)`
  - `void ClearTransitions()`
- 🔲 `public readonly record struct LayerConfig`
  - `LayerPosition Position`
  - `float Scale`, `float Opacity`, `float Rotation`
  - `BlendMode Blend`
  - `LayerAnchor ScaleAnchor`
  - Static presets: `Background`, `CenteredHalf`.
- 🔲 `public abstract record LayerPosition` with statics:
  `Cover`, `Center`, `Anchored(LayerAnchor, marginX, marginY)`,
  `AbsolutePixels(x, y)`, `NormalizedXY(x01, y01)`.
- 🔲 `public abstract record Transition` with statics:
  `FadeTo`, `MoveTo`, `ScaleTo`, `Cut`, `Combo(params Transition[])`, `Sequence(params Transition[])`.
- 🔲 `public enum LayerAnchor` (9 positions).
- 🔲 `public enum VideoCompositorBackend { Auto, Cpu, Gl }`.

### 7.4 Transition evaluation

- 🔲 Once-per-output-frame inside `VideoCompositor.TryReadNextFrame`.
- 🔲 Resolves the *current* `LayerConfig` from the attached
  `IPlaybackClock` (or wall clock if none attached).
- 🔲 Easing: `Linear`, `EaseInOutSine`, `EaseInOutCubic` defaults.

### 7.5 Verification

- 🔲 Move `CompositorSmoke` tool to use the new API; existing fixtures
  remain correct.
- 🔲 Replace HaPlay's `LogoFallbackVideoSink` use of `VideoCpuOpacity` with
  the new compositor layer config.
- 🔲 Tests: cross-fade between two clips via two layers + opacity transitions
  (replaces `FadeFromBlackVideoSource` + `CutVideoSource`).

**Phase 7 exit criteria**: `S.Media.Core` no longer contains video effects;
composition is declarative.

---

## Phase 8 — Clock surface narrowing

> **Risk**: Low · **Effort**: S · **Breaking**: ✓ (small surface)
>
> Goal: collapse 13 named clock concepts to 4 public ones; the rest go
> internal.

### 8.1 Merge read views

- 🔲 Collapse `IPlaybackTimeline` + `IPlaybackPlayhead` → single `IPlayhead`.
- 🔲 Keep `IPlaybackClock` (master input).
- 🔲 Keep `IMediaClock` (driver) — or rename `IPlaybackController` to fit
  alongside `IPlayhead`.

### 8.2 Internalise router clocks

- 🔲 `IRouterClock`, `WallClockRouterClock`, `SinkSlavedRouterClock` →
  `internal`. `AudioRouter` keeps `SlaveTo(outputId)` / `RetargetSlaveClock(outputId)` as the public surface.
- 🔲 `IngestSlavedRouterClock` (NDI) → `internal` (NDIRouter slave logic
  becomes a method like `AudioRouter.SlaveToNdi(NDIIngestPlaybackClock)`).

### 8.3 Document the four public clocks

- 🔲 `MediaClock` — default driver.
- 🔲 `CompositePlaybackClock` — priority-merge of multiple masters.
- 🔲 `VideoPtsClock` — PTS-derived playback clock.
- 🔲 `NDIIngestPlaybackClock` — NDI receive timeline.

### 8.4 Verification

- 🔲 Backwards-compat aliases: `using IPlaybackTimeline = IPlayhead;` for one release.
- 🔲 No consumer-visible behavior change.

**Phase 8 exit criteria**: clock vocabulary shrinks from 13 to 4 public
names; `internal` surface stays unchanged.

---

## Phase 9 — `VideoFrame` redesign

> **Risk**: High · **Effort**: L · **Breaking**: ✓ (every frame producer)
>
> Goal: single ctor + discriminated hardware backing, instead of 10
> parameters with 6 mutually-exclusive HW backings hand-checked at runtime.

### 9.1 Discriminated backing

- 🔲 New abstract `VideoFrameHardwareBacking : IDisposable` with concrete
  subclasses (or use a sealed record union):
  - `DmabufNv12Backing`, `DmabufP010Backing`, `DmabufP016Backing`
  - `Win32SharedNv12Backing`
  - (`None` is just `null`)

### 9.2 `VideoFrame` ctor collapse

- 🔲 New canonical ctor:

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

- 🔲 Mutual-exclusion check collapses to a type check on `backing` (or `null`).
- 🔲 Convenience single-plane overload retained.

### 9.3 Named factories on the backing types

- 🔲 `DmabufNv12Backing.CreateFrame(pts, format, backing, metadata, release)`
- 🔲 `DmabufP010Backing.CreateFrame(...)`, `DmabufP016Backing.CreateFrame(...)`
- 🔲 `Win32SharedNv12Backing.CreateFrame(...)`
- 🔲 Each backing's `CreateSharedReference(VideoFrame source)` static moves
  here too.

### 9.4 Release strategy unification

- 🔲 Drop `Action? release` + `IDisposable? disposableRelease` duality;
  ship only `IDisposable? release`. `Action.Wrap(Action a) : IDisposable`
  helper if a closure is genuinely needed.
- 🔲 Audit `AudioFrame.Release` (`Action?` today, `AudioFrame.cs:29`):
  same closure-captures-pool-buffer pattern as `VideoFrame`. Ship the
  `Action → IDisposable` change for `AudioFrame` in the same PR — keeps
  the framework's "one release strategy" rule consistent across
  audio/video.

### 9.5 Update producers

- 🔲 `S.Media.FFmpeg`: `MediaContainerSharedDemux.VideoTrack`, hardware
  decode paths, swscale paths.
- 🔲 `S.Media.NDI`: `NDIVideoFrameUnpack`, `NDIVideoReceiver` (internal),
  `NdiFrameSyncVideoSource` (or its replacement).
- 🔲 `S.Media.SkiaSharp`: `ImageFileSource`, `TextLayerSource`.
- 🔲 `S.Media.Core`: held-frame ctor in `VideoPlayer.TrySubmitHeldFrame`.
- 🔲 Hardware backings (`VideoDmabufNv12Backing`, etc.) — rename or alias to the new `DmabufNv12Backing` names.
- 🔲 `VideoFramePool` (per `VideoRouter`) for fan-out — `VideoFrame` is
  heap-allocated per submit today; a 60 fps fan-out to 4 outputs allocates
  240 frames/sec. Pooled frames carry back-references so `Release`
  returns them to the pool. Bench-validated: `Tools/VideoPlaybackSmoke
  --4-outputs` allocates ~0 bytes/sec after warm-up.

### 9.6 Verification

- 🔲 Existing video tests pass — extensive set already covers HW frame
  ref-count behavior.
- 🔲 Smoke tools all play HW-decoded content correctly.

**Phase 9 exit criteria**: one `VideoFrame` ctor, one mutual-exclusion
check (type), no `disposableRelease` duality.

---

## Phase 10 — Bit-depth completeness (additive)

> **Risk**: Medium · **Effort**: M · **Breaking**: ✗
>
> Goal: high-bit-depth content survives end-to-end on supported hardware.
> See §10.11 of the review for the full background.

### 10.1 New pixel formats

- 🔲 `PixelFormat.Rgba16` — 16-bit unsigned-normalized packed RGBA.
- 🔲 `PixelFormat.Rgba16F` — 16-bit IEEE half-float packed RGBA.
- 🔲 `PixelFormatInfo` entries (8 bytes/pixel each).
- 🔲 `S.Media.FFmpeg.Video.VideoCpuFrameConverter`: swscale recipes for
  `Rgba16` (`AV_PIX_FMT_RGBA64LE`) and the FP16 path.

### 10.2 10-bit GL swapchain

- 🔲 `GlOutputBitDepth` enum (`Eight`, `Ten`, `Auto`).
- 🔲 `SDL3GLVideoOutput` ctor accepts `swapchainBitDepth: GlOutputBitDepth.Auto`.
  - Windows DXGI: requests `R10G10B10A2_UNORM` via SDL GL attribute
    `SDL_GL_RED_SIZE=10/GREEN_SIZE=10/BLUE_SIZE=10/ALPHA_SIZE=2`.
  - Linux EGL: same attributes; Wayland HDR-capable compositors honour.
  - Fallback to 8-bit + single warning log when 10-bit config unavailable.
- 🔲 `VideoOpenGlControl` (Avalonia) gets the same option (Avalonia
  exposes framebuffer config on some backends; document the matrix).

### 10.3 `GlVideoCompositor` output precision

- 🔲 `GlCompositorOutputPrecision { Rgba8, Rgba16, Rgba16F }` enum.
- 🔲 `GlVideoCompositor` ctor accepts it; `RecreateFbo` picks the right
  internal format (`Rgba8` / `Rgba16` / `Rgba16f`).
- 🔲 Final `glReadPixels` switches between `GL_UNSIGNED_BYTE`,
  `GL_UNSIGNED_SHORT`, `GL_HALF_FLOAT` and emits the matching `PixelFormat`.

### 10.4 NDI sender 16-bit FourCCs

- 🔲 `NDIVideoSender.AcceptedPixelFormats` gains `P216` (16-bit 4:2:2 YUV) and `PA16` (P216 + 16-bit alpha plane).
- 🔲 `NDIVideoSender.Submit` plumbs the FourCC + the right plane layout
  for `P216` / `PA16` frames.
- 🔲 Smoke: render `Yuv422P10Le` clip through `VideoCompositor` (precision:
  `Rgba16`) into an `NDIOutput` configured for `P216`; receive on NDI Studio
  Monitor and confirm 10-bit colour ramp.

### 10.5 `VideoCompositor` precision option

- 🔲 `VideoCompositor.Create(..., options: new VideoCompositorOptions { OutputPrecision = ...})`.
- 🔲 Default `Rgba8` (matches today); opt-in `Rgba16` / `Rgba16F` for HDR
  / chained composite scenarios.

### 10.6 Verification

- 🔲 Composite a 10-bit YUV source over a black background with
  `Rgba16` precision; assert output `VideoFrame.Format.PixelFormat ==
  Rgba16`; eyeball-test gradient banding gone.
- 🔲 Render 10-bit content on a 10-bit display via `SDL3GLVideoOutput`
  with `swapchainBitDepth: Ten`; manual visual check.

**Phase 10 exit criteria**: 10/12/16-bit content survives end-to-end on
opt-in code paths; default behaviour unchanged.

---

## Phase 11 — Async, metrics, extensibility (additive)

> **Risk**: Low · **Effort**: M · **Breaking**: ✗
>
> Goal: round off the consumer surface with the small ergonomic wins.

### 11.1 Async open

- 🔲 `MediaPlayer.Open(...).OpenAsync(CancellationToken)` already added in
  Phase 5 — confirm it actually runs the blocking native init on a worker
  thread, doesn't block the caller's `SynchronizationContext`.

### 11.2 Aggregated metrics

- 🔲 `MediaPlayer.GetMetrics() : MediaPlayerMetrics` — one snapshot:
  - Audio router pump stats + per-output drops/processed.
  - Video router per-output pump metrics.
  - `VideoPlayer.DecodedCount / DisplayedCount / DroppedLate / DroppedDrain`.
  - `PortAudioOutput.PlayedSamples / UnderrunSamples / DroppedSamples` (if PortAudio in graph).
  - `NDISource.OverflowSamples / VideoOverflowFrames` (if NDI in graph).
  - `MediaClock.CurrentPosition / Master.GetType().Name`.

### 11.3 Image-extension factory

- 🔲 `MediaFrameworkExtensionRegistry`: `(string ext) → IVideoSource factory`.
- 🔲 `S.Media.SkiaSharp` registers PNG/JPEG/WebP/BMP/GIF.
- 🔲 `VideoSource.OpenImage(path)` reads ext, dispatches via the registry,
  fails clearly when unregistered.

### 11.4 Optional auto-adapt

- 🔲 `AudioRouter.EnableAdaptiveRateOnNonMasterOutputs(maxAbsPpm: …)` —
  one-line wrapper that wraps every non-master `IAudioOutput` in
  `AdaptiveRateAudioOutput` on add.

### 11.5 `TriggerBus` — scriptable control surface (OSC / MIDI / Mond)

> Forward-looking: the user's scripting layer is Mond (NativeAOT-friendly
> Lua-ish), with OSC and MIDI as the wire protocols. None of those bindings
> belong in `S.Media.Core`, but the framework needs **one stable,
> allocation-free trigger surface** that script glue and protocol adapters
> can target. Without it, scripts will reach into `AudioClipPlayer.Fire`,
> `AudioRouter.SetRouteGainById`, `MediaPlayer.Seek`, `VideoCompositor`
> layer config, etc. via reflection — slow at startup and brittle to refactor.

- 🔲 New `S.Media.Core.Triggers.TriggerBus`:
  - `void Register(string triggerId, TriggerHandler handler)`
  - `bool Unregister(string triggerId)`
  - `bool Fire(string triggerId, in TriggerPayload payload = default)` —
    returns false when no handler is registered (non-throwing; scripts
    discover what's wired up cleanly).
  - `IReadOnlyCollection<string> RegisteredIds { get; }`.
- 🔲 New `public delegate void TriggerHandler(in TriggerPayload payload);`
- 🔲 New `public readonly record struct TriggerPayload(TriggerValueKind Kind, double NumericValue, ReadOnlyMemory<char> TextValue)` —
  tagged-union sized for the 90% case (MIDI CC 7-bit, OSC float, short
  address tail). Allocation-free at the call site; larger payloads can
  follow if a real workload needs them.
- 🔲 `public enum TriggerValueKind { None, Numeric, Text }`.
- 🔲 Usability sugar: `S.Media.Core.Triggers.Audio.RegisterAudioClipPlayer(TriggerBus bus, string id, AudioClipPlayer player)`
  binds `<id>.fire`, `<id>.stop`, `<id>.stopAll`, `<id>.loop` so a script
  does `mf.fire("pad.kick.fire")` without four manual lambdas.
- 🔲 OSC adapter in `MediaFramework/Extras/OSCLib/` (post Phase 0.7):
  `OscTriggerBridge(TriggerBus, OscReceiver)` — subscribes and calls
  `bus.Fire("<address>", payload)` per OSC message. Small (~80 LOC).
- 🔲 MIDI adapter in `MediaFramework/Extras/PMLib/`:
  `MidiTriggerBridge(TriggerBus, PMLib.MidiInput, MidiTriggerProfile)` —
  configurable NoteOn / CC / PgmChg mapping to bus ids. Small (~100 LOC).
- 🔲 Document the **id naming convention** in
  `Doc/MediaFramework-Triggers.md` (`pad.<name>.fire`,
  `pad.<name>.stop`, `loop.<group>.toggle`, `out.<id>.gain`, …) so
  protocol adapters and Mond scripts converge on the same vocabulary.
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

**Phase 11 exit criteria**: async open works; one-call metrics snapshot;
extension-driven image registry; trigger bus + OSC/MIDI adapters available.

---

## Phase 12 — Encoder sinks (`S.Media.FFmpeg.Encode`)

> **Risk**: Medium · **Effort**: L · **Breaking**: ✗ (purely additive)
>
> Goal: framework can record/transcode. Naturally consumes the 10/12-bit
> output formats from Phase 10.

### 12.1 New project

- 🔲 `MediaFramework/Media/S.Media.FFmpeg.Encode/` referencing FFmpeg
  bindings + Core.

### 12.2 Implementations

- 🔲 `FFmpegVideoFileOutput : IVideoOutput, IDisposable` — wraps
  libavformat mux + libavcodec encoder for a given codec.
  - Accepted formats: `Yuv420P10Le`, `P010`, `Yuv444P12Le`, `Yuva444P12Le`, plus 8-bit fallbacks.
  - Config: container (mp4/mkv/mov), codec (h264/hevc/prores), bitrate, GOP.
- 🔲 `FFmpegAudioFileOutput : IAudioOutput, IDisposable` — same shape for
  aac/opus/flac.
- 🔲 `FFmpegMuxFileOutput` — combined A+V mux that holds both above and
  multiplexes packets.

### 12.3 Smoke

- 🔲 `Tools/EncoderSmoke`: decode + recompose + reencode round-trip.

**Phase 12 exit criteria**: framework can encode A+V into a file.

---

## Phase 13 — Public-API tests + docs sweep

> **Risk**: Low · **Effort**: M · **Breaking**: ✗
>
> Goal: lock in the surface so future regressions are caught at the public
> contract, not at internal refactors.

### 13.1 `MediaPlayer.Tests` smoke project

- 🔲 Open file (audio+video), play 2 s, seek to 1 s, play 2 s.
- 🔲 Open file (audio only), play to end.
- 🔲 Open image, hold last frame.
- 🔲 Open URI (skip in CI if no network).
- 🔲 Open Stream (in-memory MP3).
- 🔲 Open live (mock `IAudioSource` + `IVideoSource`).
- 🔲 Mid-play sink swap (add a second `IAudioOutput`, route to it, remove first).
- 🔲 Assert no temp file created (Stream-mode).
- 🔲 Assert `MediaPlayer.GetMetrics()` advances during playback.
- 🔲 **Allocation contract test** (BenchmarkDotNet `MemoryDiagnoser`): for
  each shipped `IPlaybackClock` (`MediaClock`, `CompositePlaybackClock`,
  `VideoPtsClock`, `NDIIngestPlaybackClock`), assert zero managed-bytes
  allocation per time-read call. If a future implementation accidentally
  allocates per read it will show up as periodic GC on the playback
  thread — catch it at CI time, not in production.

### 13.2 Architecture doc sweep

- 🔲 Update `Doc/MediaFramework-Architecture.md` to match the post-refactor
  type names + entry verbs.
- 🔲 Update `Doc/MediaFramework-Format-Support.md` table for new pixel
  formats (`Rgba16`, `Rgba16F`).
- 🔲 New `Doc/MediaFramework-Quickstart.md` walking through the six-line
  minimum-viable API (§10.5 of the review).
- 🔲 New `Doc/MediaFramework-PublicAPI.md` — one-page enumeration of
  every public type in the post-refactor framework, grouped by
  namespace, with its phase-of-introduction column and deprecation
  status. Replaces "is X still around in v2?" greps for consumers.
- 🔲 New `Doc/MediaFramework-Triggers.md` (paired with Phase 11.5) —
  documents the `TriggerBus` id-naming convention so OSC / MIDI / Mond
  bindings converge on the same vocabulary.
- 🔲 Archive the older checklists under `Doc/Archive/2026-05/`.
- 🔲 Fix the now-stale claims in `Doc/MediaFramework-Critical-Review-2026-05-22.md`:
  - §10.5 prints `MediaContainer.OpenFile("clip.mkv")` — type didn't exist
    at the time of writing; after Phase 2.2 it does. No edit needed
    once 2.2 ships, but update the §10.5 prose to acknowledge the new
    facade rather than imply the existing `MediaContainerDecoder.Open`.
  - §10.5 note "no `autoResample` overload on `AudioRouter.AddSource`" is
    stale (`AudioRouter.cs:158` already has it). Re-phrase as "no
    rate-inference convenience" — Phase 2.3's `DefaultAutoResample`
    static is what closes that gap.
  - §3.4 "first-class `ITimedAudioSource`/`ITimedVideoSource` with a
    `Position` getter" — `ISeekableSource` already has `Position` +
    `Duration`. Re-phrase the §3.4 ask as "frame-step API on top of the
    existing `ISeekableSource`" to make the new contract explicit.
  - §2.4 "AudioPlayer ... forwards to router" reads as if the type is
    already gone; today it's at `AudioPlayer.cs`. Phrase as "currently
    three ways" until Phase 4.1 lands.

### 13.3 Cleanup

- 🔲 Delete `[Obsolete]` shims from Phases 4 / 5 (planned for next major).
- 🔲 Final LOC + public-type count snapshot.

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
| `ResamplingAudioSink`          |  14 | Sample-rate adapters |
| `AudioRouter`                  |  13 | Hot output add / per-cell matrix |
| `VideoSinkPump`                |  10 | NDI fan-out + logo wrapping |
| `CompositorVideoSink`          |   9 | Used by `OutputPresetVideoSource`, `LockedFormatVideoSink` |
| `CpuVideoCompositor`           |   7 | Same |
| `MediaContainerSession`        |   6 | Only for `Play/SeekCoordinated*` — Phase 4 absorbs |
| `SDL3GLVideoSink`              |   4 | The GL variant only |
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
| `BusSink` | ❌ (rename to `AudioBus` per §10.1; HaPlay didn't need a bus) |
| `AudioGraphBuilder` | ❌ (Phase 4 deletes) |
| `PortAudioPlaybackHost` | ❌ (HaPlay handles its own wiring) |
| `AdaptiveRateAudioSink` | ❌ (the primitive is fine to keep, just not auto-wired) |
| `ResamplingAudioSource` | ❌ (only sink-side resampler ever used) |
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
and/or output resolution on an `IVideoSink`, regardless of what the source
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
  compositor into a sink wrapper so consumers can lock format/resolution on
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
`IVideoSink` and can:
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
configure sink → add router output → add router route → install audio
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
- 🔲 `AudioRouter.AddRoute(srcId, sinkId, routeId, map, gain)` and
  `SetRouteGainById` are kept. The legacy `AddRoute(srcId, sinkId, map, gain)`
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
| `BusSink` (rename to `AudioBus` in Phase 1) | 0 | ✓ (could also be deleted entirely — HaPlay never instantiates one) |
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
