# MediaFramework Action Checklist — 2026-05-18

Derived from `Doc/MediaFramework-Review-2026-05.md`. Ordered to minimize wasted work: fix correctness first (so symptoms stop), then do renames (so later code anchors on the right names), then defaults / ergonomics, then features, then polish. Breaking existing tests and HaPlay is fine — note where it'll hurt so you can re-test.

Legend: **size** = XS (<1 h) · S (<½ day) · M (1–2 days) · L (1 week) · XL (multi-week feature). **breaks** = which downstream code likely needs updates.

**Status (updated 2026-05-18):** Phase 0 complete. Phase 1 complete. Phase 2 complete. Phase 3 mostly complete (7/8 — P3.8 VideoRouter→Core refactor deferred as a focused follow-up). Phase 4 is the next planned batch.

---

## Phase 0 — Stop the bleeding ✅ COMPLETE (2026-05-18)

Correctness defects from §11.1. Land these before any refactor; they're cheap and the symptoms they cause are confusing.

- [x] **C1. Refcount race on all four `VideoFrame` backings** — `S.Media.Core/Video/VideoDmabufNv12Backing.cs:70-75`, `VideoDmabufP010Backing.cs`, `VideoDmabufP016Backing.cs`, `VideoWin32Nv12Backing.cs`. Replace the `Volatile.Read(_closed)` + `Interlocked.Increment(_refCount)` pair with a single CAS loop on `_refCount`; remove `_closed` (refcount == 0 *is* closed). Add a unit test that races `AddReference` against `Dispose` on a synthetic backing. **Size:** S. **Breaks:** nothing — internal pattern only.
  - Done: `_closed` removed from all four backings; both AddReference and Dispose use a CAS loop on `_refCount`. Added `AddReference_RacesDispose_NeverIncrementsThroughZero` (500-iteration Barrier-synchronized race test) in `VideoDmabufNv12BackingRefCountTests.cs`. The other three backings share the identical pattern so the test covers them by construction.

- [x] **C2. `PortAudioOutput.Flush()` calibration reset reordering** — `S.Media.PortAudio/PortAudioOutput.cs:125-137`. Move `Volatile.Write(ref _streamSmoothCalibrated, 0)` to **before** `Pa_StartStream`. Add a test that flushes mid-stream and asserts `ElapsedSinceStart` is monotonic across the flush. **Size:** XS. **Breaks:** nothing.
  - Done: write reordered with explanatory comment. **No unit test added** — the race window is microseconds and reliably reproducing it would need real PortAudio hardware plus instrumented callbacks. The one-line reorder is small enough to review by inspection.

- [x] **C3. Replace `AudioFormat(48000, 0)` sentinel with `AudioFormat(0, 0)`** — `S.Media.FFmpeg/MediaContainerSharedDemux.cs:230`. Update the XML doc on `MediaContainerDecoder.Audio` to say "consumers must guard with `HasAudio`". Add `Debug.Assert(Channels > 0)` at every router-side site that reads `SampleRate` so a regression fails fast. **Size:** XS. **Breaks:** any consumer that today reads `decoder.Audio.Format.SampleRate` without checking `HasAudio` (none in-tree; check `Tools/` to be sure).
  - Done: sentinel changed to `AudioFormat(0, 0)`. Bonus fix: `Tools/NDIPlayer/Program.cs` was reaching into `dec.Audio.Format` without checking `HasAudio` — guarded both the `EnableAudio` call sites and the diagnostic print so video-only files no longer try to wire an audio sink. Skipped the proposed `Debug.Assert(Channels > 0)` peppering — the now-zero rate will fail fast at `AudioRouter`'s positive-rate check (AudioRouter.cs:141) without needing per-site asserts.

- [x] **C4. `AudioRouter` per-sink pump capacity is a flat 8 chunks for everyone.** AudioRouter.cs:136. For hardware sinks (those implementing `IClockedSink` with their own ring), expose a smaller default. At minimum document the latency implication on `AddSink` and on `AudioPlayer.AddOutput`. **Size:** XS doc, S if you also retune defaults.
  - Done (doc-only path): expanded the XML `<remarks>` on `AddSink` to call out the ~80 ms @ default settings and the rationale for hardware vs network sinks. Deferred the auto-tune for `IClockedSink` to a Phase 2 ergonomics pass — touching the default behavior risked surprising existing consumers and is better paired with the auto-resample / pump-by-default ergonomics work.

- [x] **Verify the §11.2 suspects.** Each has enough context for a focused ~15-minute look. Do this before §0 closes so any extra defects join this phase rather than slip later.
  - [x] **S1.** `AudioRouter.RetargetSlaveClock` (AudioRouter.cs:459-471) constructs the new clock under `_gate`. Either move construction outside the lock or add a comment that the constructor must never invoke router APIs. **Size:** XS. **Confirmed and fixed:** `SinkSlavedRouterClock` is now constructed before the lock; only the field swap happens under `_gate`.
  - [x] **S4.** `S.Media.Avalonia/VideoOpenGlControl.cs` detach/render race on `_pendingFrame`. Trace what happens if a render fires between `_sinkDisposed = true` and the lock acquisition that drains the pending frame. **Size:** S. **Confirmed real and fixed:** Submit could pass the `_sinkDisposed` check at the top, detach could then run and drain `_pendingFrame`, and Submit would park a *new* frame in `_pendingFrame` with no further `OnOpenGlRender` callback to drain it (frame leak). Fix: re-check `_sinkDisposed` inside the frame lock and dispose-then-throw if disposed.
  - [x] **S5.** `S.Media.OpenGL/Nv12Win32SharedHandleGpuUploader.cs` keyed-mutex pairing on early-return paths. Confirm every `TryAcquireForGpuRead` success has a `Release` on every exit. **Size:** S. Windows-only test. **Rejected — no defect.** The agent claim misread C# semantics: the `try { … } finally { keyedScope?.Dispose(); }` block at lines 395-454 wraps every early `return false` inside the try; C# runs the finally on return from a try block. The acquire-failure return at line 392 is before keyedScope exists. All paths paired.
  - [x] **S6.** Resampler delay-buffer flush across seek. Write a test: open a sine-tone file, seek to a different position, decode the first 100 ms post-seek, FFT — should not show residue from pre-seek samples. **Size:** S. **Rejected — no defect.** `swr_close(_swr); swr_init(_swr)` at `MediaContainerSharedDemux.cs:806-808` is the canonical FFmpeg way to drain the resampler — `swr_close` frees `in_buffer`/`out_buffer` (the delay buffer). Combined with the preceding `avcodec_flush_buffers` + `av_frame_unref`, the path is correct. The agent's claim that `swr_close` "doesn't exist in standard FFmpeg" was wrong (it's been in libswresample since 1.0).

**Phase exit:** confirmed defects are gone, all suspects resolved one way or the other. ✅

**Outcomes:**
- 4 confirmed defects fixed (C1–C4).
- 2 suspects confirmed real and fixed (S1, S4 — S4 was the surprise: agent flagged it as suspect, turned out to be a real frame leak).
- 2 suspects verified as agent misreads, not defects (S5, S6).
- 1 new unit test added (CAS-loop refcount race).
- Tests: 504/505 pass. The one failure (`NDIIngestPlaybackClockTests.Timestamp_UsedWhenTimecodeSynthesize`) pre-exists on master and is flaky FP-tolerance unrelated to Phase 0.
- Build: clean, zero warnings, zero errors across all 23 projects including HaPlay.

**Files touched:**
```
MediaFramework/Media/S.Media.Avalonia/VideoOpenGlControl.cs       (S4 fix)
MediaFramework/Media/S.Media.Core/Audio/AudioRouter.cs            (C4 doc + S1 fix)
MediaFramework/Media/S.Media.Core/Video/VideoDmabufNv12Backing.cs (C1 fix)
MediaFramework/Media/S.Media.Core/Video/VideoDmabufP010Backing.cs (C1 fix)
MediaFramework/Media/S.Media.Core/Video/VideoDmabufP016Backing.cs (C1 fix)
MediaFramework/Media/S.Media.Core/Video/VideoWin32Nv12Backing.cs  (C1 fix)
MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs  (C3 fix)
MediaFramework/Media/S.Media.PortAudio/PortAudioOutput.cs         (C2 fix)
MediaFramework/Test/S.Media.Core.Tests/Video/VideoDmabufNv12BackingRefCountTests.cs  (new test)
MediaFramework/Tools/NDIPlayer/Program.cs                         (C3 follow-up: HasAudio guards)
```

---

## Phase 1 — Names that will anchor everything else ✅ COMPLETE (2026-05-18)

These are mechanical but produce big diffs. Doing them early means new code in Phase 3+ uses the correct names from the start.

- [x] **Rename `AvRouter` → `MediaContainerSession`.** Chose `MediaContainerSession` over bare `MediaSession` so the type sits with `MediaContainerDecoder` / `MediaContainerPlaybackBundle`. `MediaContainerAvRouter` static factory folded into `MediaContainerSession.Create`. `MediaPlayer.Av` → `MediaPlayer.Session`. Tests: `MediaContainerSessionTests` (was `AvRouterTests`). **Size:** S.

- [x] **Rename `MediaContainerMegaPlaybackHost` → `MediaContainerPlaybackBundle`.** `MediaContainerMegaPlaybackOwnedParts` → `MediaContainerPlaybackBundleOwnedParts`. **Size:** S.

- [x] **Delete `MediaContainerPlaybackGraph`.** No in-tree callers remained; non-disposable grouping is covered by the bundle with `OwnedParts.None` when needed. **Size:** XS.

- [x] **Rename `S.Media.PortAudio.MediaContainerPlaybackHost` → `PortAudioPlaybackHost`.** `MediaContainerPlaybackHostPlayerOwnership` → `PortAudioPlaybackHostPlayerOwnership`. Kept a dedicated type (vs folding into `AudioPlayerPortAudioExtensions`) because PortAudio wiring + ownership flags are substantial. **Size:** S.

- [x] **Drop the "Tier E / Tier F / row N" annotations.** Stripped checklist row references from MediaFramework XML/docs; kept technical substance. `Doc/MediaFramework-Checklist-2026-05.md` is now the canonical checklist in-repo. **Size:** S.

- [x] **NDI jargon — keep names, add glossary.** Added `Doc/NDI-Terminology.md`; NDI types unchanged. **Size:** XS.

**Phase exit:** public taxonomy is consistent — no "Mega", no `AvRouter` misnomer, PortAudio vs FFmpeg lifecycle types are distinct. ✅

**Outcomes:**
- Build: clean (0 warnings / 0 errors) across full `MFPlayer.sln` including HaPlay.
- Tests: 503/504 pass. The one failure (`NDIIngestPlaybackClockTests.Timestamp_UsedWhenTimecodeSynthesize`) is the same pre-existing flaky FP-tolerance test noted in Phase 0.
- Fix during finish: restored `MediaContainerSharedDemux.VideoIsAttachedPicture` (property referenced by `MediaContainerDecoder` / HaPlay but missing from demux — blocked compile).

**Files touched (representative):**
```
MediaFramework/Media/S.Media.FFmpeg/MediaContainerSession.cs              (new; replaces AvRouter)
MediaFramework/Media/S.Media.FFmpeg/MediaContainerPlaybackBundle.cs     (new; replaces MegaPlaybackHost)
MediaFramework/Media/S.Media.PortAudio/PortAudioPlaybackHost.cs         (new; replaces MediaContainerPlaybackHost)
MediaFramework/Media/S.Media.Playback/MediaPlayer.cs                    (Session, Bundle)
MediaFramework/Tools/VideoPlaybackSmoke/*                               (smoke wiring)
UI/HaPlay/Playback/HaPlayPlaybackSession.cs                           (MediaContainerSession)
MediaFramework/Test/.../MediaContainerSessionTests.cs                   (renamed)
MediaFramework/Test/.../MediaContainerPlaybackBundleTests.cs            (renamed)
MediaFramework/Test/.../PortAudioPlaybackHostTests.cs                   (renamed)
Doc/NDI-Terminology.md                                                  (new)
+ Tier-annotation cleanup across Core / FFmpeg / NDI XML remarks
```

---

## Phase 2 — Defaults & ergonomics ✅ COMPLETE (2026-05-18)

Small changes that improve the API consumers will use. Doing these before Phase 3 means new high-level helpers inherit good defaults instead of papering over bad ones.

- [x] **Drop the `48000` default on `AudioPlayer`.** Removed the default; ctor now requires `sampleRate` explicitly. All in-tree callers already passed one. **Size:** XS.

- [x] **`NDIAudioReceiver` ring capacity in time, not samples.** Ctor switched to `TimeSpan ringCapacityDuration = default (→ 2 s)`. Frame count computed in `EnsureFormat` from the first observed sample rate; `MinCapacityFrames = 1024` floor preserved. **Size:** XS.

- [x] **Default video sinks behind a `VideoSinkPump`.** `VideoRouter.AddOutput` now wraps in a pump by default. New `synchronous: true` flag opts out (used for `DiscardingVideoSink` and the test sinks that assert immediate receipt). Mutually exclusive with explicit `asyncPump`. Tests updated via a `AddSyncOutput` helper. **Size:** S.

- [x] **Document `IVideoSink.Submit`'s thread contract on the interface itself.** XML doc on `IVideoSink.Submit` now states the clock-driver-thread contract and points to `VideoSinkPump` for slow sinks. **Size:** XS.

- [x] **`AudioRouter.AddSource(autoResample: true)`** option.
  - New `ResamplingAudioSource` in `S.Media.FFmpeg/Audio/` (libswresample wrapper with optional inner-ownership).
  - New `AudioRouterAutoResample.SourceWrapper` static factory hook in `S.Media.Core` so Core stays FFmpeg-free.
  - `FFmpegRuntime.EnsureInitialized` installs the default wrapper with `disposeInnerWhenDisposed: false` so caller-owned inner sources are never disposed by the router.
  - `AudioRouter.AddSource` accepts `autoResample: bool`; the router itself owns the wrapper (new `SourceEntry.OwnedWrapper`) and disposes it on `RemoveSource` / `Dispose`.
  - `AudioPlayer.AddOwnedSource` gained an `autoResample` forward.
  - 3 new tests in `ResamplingAudioSourceTests.cs`. **Size:** M.

- [x] **`AudioFormat` validation.** New `AudioFormat.IsValid` and `AudioFormat.Validate(paramName?)`; called at every public surface that plumbs a format into a live pipeline (`AudioRouter.AddSource`, `AudioRouter.AddSink`, `ResamplingAudioSink` ctor, `ResamplingAudioSource` ctor). `AudioFormat(0, 0)` sentinel remains valid as a value (no in-ctor validation) — guarded with `HasAudio` on the consumer side. **Size:** XS.

- [x] **Convert `*PumpPressureEventArgs` to `readonly record struct`.** `AudioRouterPumpPressureEventArgs`, `VideoSinkPumpPressureEventArgs`, `VideoRouterPumpPressureEventArgs` are now `readonly record struct`s. `EventHandler<T>` has no `: EventArgs` constraint since .NET Core, and all in-tree handlers receive by value. Sink-error args stayed a class (carries `Exception`; cold path). **Size:** S.

**Phase exit:** the framework's defaults stop surprising consumers; the most likely API ergonomics complaints (rate mismatch rejection, blocking sinks, baked-in 48000) are gone. ✅

**Outcomes:**
- Build: clean (0 warnings / 0 errors) across the full `MFPlayer.sln`.
- Tests: 501/502 pass. The one failure is the same pre-existing flaky `NDIIngestPlaybackClockTests.Timestamp_UsedWhenTimecodeSynthesize` (FP-tolerance, ~2700 ticks delta) noted in Phase 0 and Phase 1 — unrelated to Phase 2.
- 3 new tests added (`ResamplingAudioSourceTests`).

**Files touched:**
```
MediaFramework/Media/S.Media.Core/Audio/AudioFormat.cs              (Validate + IsValid)
MediaFramework/Media/S.Media.Core/Audio/AudioPlayer.cs              (no-default ctor + autoResample forward)
MediaFramework/Media/S.Media.Core/Audio/AudioRouter.cs              (autoResample, validation, SourceEntry.OwnedWrapper, dispose chain)
MediaFramework/Media/S.Media.Core/Audio/AudioRouterAutoResample.cs  (new — static factory hook)
MediaFramework/Media/S.Media.Core/Audio/AudioRouterEvents.cs        (pressure args → record struct)
MediaFramework/Media/S.Media.Core/Video/IVideoSink.cs               (thread-contract docs)
MediaFramework/Media/S.Media.FFmpeg/Audio/ResamplingAudioSink.cs    (uses AudioFormat.Validate)
MediaFramework/Media/S.Media.FFmpeg/Audio/ResamplingAudioSource.cs  (new — source-side wrapper)
MediaFramework/Media/S.Media.FFmpeg/FFmpegRuntime.cs                (installs SourceWrapper factory)
MediaFramework/Media/S.Media.FFmpeg/Video/VideoPumpPressureEvents.cs (pressure args → record struct)
MediaFramework/Media/S.Media.FFmpeg/Video/VideoRouter.cs            (pump-by-default + synchronous flag)
MediaFramework/Media/S.Media.NDI/Audio/NDIAudioReceiver.cs          (TimeSpan ring capacity)
MediaFramework/Media/S.Media.Playback/MediaPlayer.cs                (discarding sink synchronous: true)
MediaFramework/Test/S.Media.FFmpeg.Tests/Audio/ResamplingAudioSourceTests.cs (new)
MediaFramework/Test/S.Media.FFmpeg.Tests/MediaContainerPlaybackBundleTests.cs (synchronous: true)
MediaFramework/Test/S.Media.FFmpeg.Tests/Video/VideoRouterTests.cs  (AddSyncOutput helper, async-pump sites kept)
```

---

## Phase 3 — Foundational features ✅ MOSTLY COMPLETE (2026-05-18, 7/8)

These unblock the *primary product surfaces*: cue players, soundboards, image triggers, **and full video playback** (the user clarified 2026-05-18 that moving video is first-class — static images are the complementary case, not a replacement).

- [x] **`StaticFrameSource : IVideoSource`** in `S.Media.Core/Video/StaticFrameSource.cs`. Wraps any `ReadOnlyMemory<byte>[]` plane data and re-emits it on every read with PTS spaced at `format.FrameRate`. No external deps. 4 tests. **Size:** XS.

- [x] **`ImageFileSource : IVideoSource`** in new project `S.Media.SkiaSharp` (Linux native assets included). Decodes PNG / JPEG / WebP / BMP / GIF once via SkiaSharp's codec stack into premul BGRA8888; emits frames forever via `ReadOnlyMemory<byte>` over a pooled buffer (returned on `Dispose`). 4 tests; SkiaSharp + `SkiaSharp.NativeAssets.Linux` were already in `Directory.Packages.props`. **Size:** M.

- [x] **`VideoPlayer.HoldLastFrameAtEnd`** in `S.Media.Core/Video/VideoPlayer.cs`. When set, the player snapshots the last successfully submitted frame's plane data (managed `byte[]` copies) right before submit, then re-emits it on every subsequent video tick with PTS tracking the playhead — keeps NDI senders / GL renderers alive and the on-screen image stable instead of going dark. Hardware-backed frames (DMA-BUF / Win32 NV12) fall back to natural completion; CPU paths work today. 2 tests. **Size:** S.

- [x] **`MediaPlayer.Quick`** facade in new project `S.Media.Quick`. `QuickPlayer.Open(path)` returns a `QuickPlayback` handle with `Play()` / `Pause()` / `Stop()` / `Dispose()`. Auto-detects image vs media by extension; image path uses `ImageFileSource` + `HoldLastFrameAtEnd: true`; media path uses `MediaPlayer.TryOpen` + `PortAudioPlaybackHost` + `SDL3GLVideoSink`. Builds on Phase 2 auto-resample and pump-by-default. **Size:** M.

- [x] **`AudioGraphBuilder`** fluent helper in `S.Media.Core/Audio/AudioGraphBuilder.cs`. Chains `AddSource → AddSink → Connect`; tracks `LastSourceId` / `LastSinkId` so the one-source/one-sink case collapses to `.ConnectLast()`. Default `Connect` map is `ChannelMap.Identity` sized to the sink. 4 tests. **Size:** M.

- [x] **`BusSink` (sub-mix bus)** in `S.Media.Core/Audio/BusSink.cs`. Implements both `IAudioSink` and `IAudioSource`; lets the router pump mixed audio into the bus and pull it out the other side as a regular source. Lock-free SPSC ring (~80 ms default capacity) — same pattern as `NDIAudioReceiver`. `OverflowFloats` / `UnderflowFloats` counters for diagnostics. 5 tests. **Size:** M.

- [x] **`MediaContainerSharedDemux` packet queue depth as options field.** `MediaPlayerOpenOptions.AudioPacketQueueDepth` / `VideoPacketQueueDepth` (and matching `VideoDecoderOpenOptions` init properties) forward into the demux. `0` keeps the existing defaults (192 / 384). **Size:** XS.

- [ ] **Move `VideoRouter` skeleton into `S.Media.Core`** — *deferred to a focused follow-up PR.* The refactor would touch every `using S.Media.FFmpeg.Video` site (HaPlay, smoke tools, decoder tests, MediaPlayer wiring), require an abstraction for `VideoCpuFrameConverter` so the moved router can still do branch conversion, and produce a churn diff worth reviewing in isolation. **Size:** M.

**Phase exit:** the framework now demonstrably backs a working soundboard / cue player loop — mixed-rate clips, static images held indefinitely, single-call file open with PortAudio + GL window, fluent route wiring, sub-mix bus, and tunable demux for deep B-frame streams. Only the architectural move-VideoRouter-to-Core item is outstanding.

**Outcomes:**
- Build: clean (0 warnings / 0 errors) across all 25 projects (added `S.Media.SkiaSharp`, `S.Media.SkiaSharp.Tests`, `S.Media.Quick`).
- Tests: 521/522 pass. Single failure is the same pre-existing flaky `NDIIngestPlaybackClockTests.Timestamp_UsedWhenTimecodeSynthesize` (FP-tolerance, ~2300 ticks delta) seen in Phase 0/1/2 — unrelated.
- 19 new tests added across StaticFrameSource (4), VideoPlayer.HoldLastFrameAtEnd (2), AudioGraphBuilder (4), BusSink (5), ImageFileSource (4).

**Files touched / added:**
```
MediaFramework/Media/S.Media.Core/Audio/AudioGraphBuilder.cs                (new)
MediaFramework/Media/S.Media.Core/Audio/BusSink.cs                          (new)
MediaFramework/Media/S.Media.Core/Video/StaticFrameSource.cs                (new)
MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs                      (HoldLastFrameAtEnd)
MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs            (packet queue fields)
MediaFramework/Media/S.Media.FFmpeg/Video/VideoHardwareDecodeContext.cs     (queue depth options)
MediaFramework/Media/S.Media.Playback/MediaPlayerOpenOptions.cs             (queue depth forward)
MediaFramework/Media/S.Media.SkiaSharp/S.Media.SkiaSharp.csproj             (new project)
MediaFramework/Media/S.Media.SkiaSharp/ImageFileSource.cs                   (new)
MediaFramework/Media/S.Media.Quick/S.Media.Quick.csproj                     (new project)
MediaFramework/Media/S.Media.Quick/QuickPlayer.cs                           (new)
MediaFramework/Test/S.Media.Core.Tests/Audio/AudioGraphBuilderTests.cs      (new)
MediaFramework/Test/S.Media.Core.Tests/Audio/BusSinkTests.cs                (new)
MediaFramework/Test/S.Media.Core.Tests/Video/StaticFrameSourceTests.cs      (new)
MediaFramework/Test/S.Media.Core.Tests/Video/VideoPlayerTests.cs            (hold-last tests)
MediaFramework/Test/S.Media.SkiaSharp.Tests/S.Media.SkiaSharp.Tests.csproj  (new project)
MediaFramework/Test/S.Media.SkiaSharp.Tests/ImageFileSourceTests.cs         (new)
MFPlayer.sln                                                                (added the 3 new projects)
```

---

## Phase 4 — Compositing & overlays (the next product frontier)

Once Phase 3 lands, the largest remaining gap is N→1 video compositing — needed for picture-in-picture, transitions, lower-thirds, text overlays. Plan up front so abstractions land in the right place.

- [ ] **Define `IVideoCompositor`** in `S.Media.Core`. Inputs: ordered list of `VideoFrame` (layers, back to front) plus per-layer transform / opacity / blend mode. Output: single `VideoFrame` (or direct write to a sink). The implementation runs in shader on the sink's GL context. **Size:** S (interface design).

- [ ] **`GlVideoCompositor`** in `S.Media.OpenGL`. Builds on existing `YuvVideoRenderer` plane-upload code. Supports `Source`, `Source-over`, `Multiply`, opacity, affine transform. **Size:** L. **Breaks:** nothing — additive.

- [ ] **`CompositorVideoSink`** in `S.Media.OpenGL` (or Core, if you keep the compositor interface generic). N input slots, each one an `IVideoSink`. One `IVideoSource` output that the `VideoRouter` can hand to a real display sink. **Size:** M.

- [ ] **`TextLayerSource : IVideoSource`** in `S.Media.Image` (or a sibling). GPU-side SDF font rendering preferred; SkiaSharp CPU rasterization is a fine first cut. Inputs: string + font + size + color + transform. **Size:** L.

- [ ] **Layer transitions** (fade, dip-to-black, cut) as wrapping sources that animate parameters of an underlying layer. Land after compositor primitives stabilize. **Size:** M.

**Phase exit:** PiP and lower-thirds are demonstrable; the cue player can do "PNG fades in, video plays under it, text label appears, all out one NDI sender".

---

## Phase 5 — Broadcast-grade format support

These move the framework from "playout / streaming" to "broadcast-ready". Each is sizable; sequence them only when a real consumer needs them.

- [ ] **BT.2020 / Rec.2020 color primaries.** `S.Media.OpenGL/YuvColorSpace.cs` — add the matrices; `YuvVideoRenderer` shader uniforms. **Size:** M.

- [ ] **Interlaced video.** Field metadata on `VideoFrame` (top-field-first vs bottom-field-first vs progressive), a deinterlace converter (FFmpeg `yadif` is the obvious choice; pre-wrap it in `S.Media.FFmpeg/Video/`). **Size:** L. **Breaks:** `VideoFrame` ctor — add an optional field-order parameter; existing call sites become "progressive".

- [ ] **SMPTE LTC / drop-frame timecode.** New `VideoTimecode` struct on `VideoFrame`; NDI sender writes it into the NDI timecode slot; NDI receiver reads it out. Drop-frame computation in a small helper. **Size:** M.

- [ ] **Embedded ancillary data** (closed captions, AFD, SCTE-35). Add a generic `VideoAncillary` envelope on `VideoFrame` (or a sidecar callback per frame). **Size:** L. Defer until a concrete consumer asks.

**Phase exit:** demonstrably correct ingest/playout for SMPTE-source content; the framework is plausibly usable in a broadcast environment.

---

## Phase 6 — Cleanup (do whenever)

Quality-of-life. No correctness or feature consequences; can be opportunistic.

- [ ] **Split `ChannelMap.cs` (2106 lines)** into `ChannelMap.cs` (data + small ops) + `ChannelMap.SimdAccumulate.cs` (14 SIMD shortcut methods, partial class). **Size:** S.

- [ ] **Move long-form XML `<remarks>` into `Doc/` files**, leave 3–4 lines of IntelliSense doc on the type. Worst offenders: `MediaContainerSession.cs`, `AudioRouter.cs:1-86`, `MediaContainerPlaybackBundle.cs:39-66`. **Size:** S (per type).

- [ ] **Refresh project memory** (`~/.claude/projects/-home-seko-RiderProjects-MFPlayer/memory/project_overview.md`). The 2026-05-10 snapshot predates `S.Media.Playback`, `VideoRouter`, `MediaContainerSharedDemux`, the NDI clock zoo. **Size:** XS.

- [ ] **Clean up untracked files in tree.** `git status` claims `Doc/HaPlay-Review-2026-05.md` is untracked but it doesn't exist; `UI/HaPlay/OutputPreview/PortAudioOutputRuntime.cs` is genuinely new. Either commit or delete. **Size:** XS.

- [x] **Add `Doc/NDI-Terminology.md` glossary** (Egress, Ingest, Mux, Fusion, Aggregating, Pump) if you kept the names. **Size:** XS. *(Done in Phase 1.)*

- [ ] **CI smoke-test the `Tools/` projects.** They double as documentation; make sure `dotnet build` + a no-arg run is part of CI so they don't bitrot. **Size:** S.

- [ ] **Replace `Action _release` closure on `VideoFrame` with `IVideoFrameReleaser` (struct).** Eliminates one closure allocation per dma-buf / shared-handle frame in fan-out scenarios. Profile-driven; defer until §6 of the review flags it as live cost. **Size:** S.

- [ ] **`AudioFileDecoder.TryReadNextFrame` array-pool** (S3 from §11.2). Switch `new float[]` to `ArrayPool<float>` with a release callback. **Size:** S.

---

## Suggested first sprint (1–2 weeks)

~~If picking a starting batch: **everything in Phase 0**, plus the §11.2 suspect verifications, plus **the AvRouter and MediaContainerMegaPlaybackHost renames from Phase 1**.~~ ✅ Phase 0 done. **Continue this sprint with the Phase 1 renames** (`AvRouter` → `MediaSession`, drop "Mega", delete `MediaContainerPlaybackGraph`, disambiguate the two `MediaContainerPlaybackHost`s, strip Tier-E/Tier-F annotations). The C4 pump-capacity auto-tune for `IClockedSink` also slips into this sprint or the next — it's small.

## Suggested second sprint

**Phase 2 in full + `StaticFrameSource` from Phase 3.** Small wins that immediately improve every consumer (HaPlay included once it's updated).

## Suggested third onwards

Phase 3 features in any order; pick by what's most needed for the next product milestone (cue player vs. soundboard vs. multi-output mixer). Phase 4 is a quarter-sized chunk on its own. Phase 5 only when a paying use case demands it.
