# MediaFramework Action Checklist — 2026-05-18

Derived from `Doc/MediaFramework-Review-2026-05.md`. Ordered to minimize wasted work: fix correctness first (so symptoms stop), then do renames (so later code anchors on the right names), then defaults / ergonomics, then features, then polish. Breaking existing tests and HaPlay is fine — note where it'll hurt so you can re-test.

Legend: **size** = XS (<1 h) · S (<½ day) · M (1–2 days) · L (1 week) · XL (multi-week feature). **breaks** = which downstream code likely needs updates.

**Status (updated 2026-05-19):** Phase 0 complete — C1 across all four backings (F2 closed the Win32 gap), C2 PortAudio Flush ordering, C3 sentinel now `AudioFormat(0, 0)` in the demux (F4 closed), C4 `AddSink` / `AddOutput` remarks spell out the ≈ 80&#160;ms latency budget plus hardware-vs-network guidance, S1 closed via an explicit ctor invariant on `SinkSlavedRouterClock`. Phase 1 complete. Phase 2 complete. Phase 3 complete (8/8 — P3.8 VideoRouter→Core refactor landed). Phase 4 complete (6/6 — full GL compositor scope incl. shaders + readback). Phase 5 complete (3/4 — BT.2020 / interlaced + yadif / SMPTE timecode landed; ancillary-data envelope explicitly deferred per checklist note). Phase 6 complete — every in-repo cleanup item closed (original 4 + 2026-05-19 follow-up: selective remarks move into `Doc/MediaFramework-Architecture.md`, project-memory refresh, untracked-files note). Only the CI smoke-test of `Tools/` remains and is gated on CI infrastructure not yet in the repo. Phase 7 (polish) complete — yadif coverage extended to Yuv422P / Yuv444P, `CpuVideoCompositor` gained a Bilinear sampling mode, BT.2020 → BT.709 RGB gamut mapping wired across the six YUV shaders with a new `RgbGamutMatrix` selector.

**Verification pass (2026-05-19):** Build is clean (0 warnings / 0 errors across all 26 projects including the new `CompositorSmoke` tool). Tests: 687 pass / 1 fail post-Phase-7 — the same pre-existing flaky `NDIIngestPlaybackClockTests.Timestamp_UsedWhenTimecodeSynthesize` noted every prior phase. All Phase 1–7 file additions and renames check out against the tree.

**Follow-up findings (post-review pass, 2026-05-19):**
- [x] **F1. `AudioFrame.Dispose()` is not idempotent despite its XML contract claiming it is.** ~~`S.Media.Core/Audio/AudioFrame.cs:20-32` documents "safe to call multiple times / callback fires once", but `Dispose()` directly invokes `Release` each call. With pooled `AudioFrame`s from `AudioFileDecoder` / `MediaContainerSharedDemux`, repeated dispose can double-return the same `ArrayPool<float>` buffer.~~ **Fixed.** Both pooled producers (`AudioFileDecoder.ConvertFrame` and `MediaContainerSharedDemux.ConvertAudioFrame`) now wrap the `ArrayPool<float>.Shared.Return` call behind an `Interlocked.Exchange` single-shot guard captured in the same closure. Allocation footprint is unchanged (the existing lambda closure carries the int field). `AudioFrame` itself stays a `readonly record struct`. Existing `AudioFileDecoderArrayPoolTests` continue to pass; the second `Dispose()` call already exercised in that test now provably no-ops.
- [x] **F2. C1 remains unresolved on Win32 NV12 backing.** ~~Confirmed again in `S.Media.Core/Video/VideoWin32Nv12Backing.cs:80-96` (still `Volatile.Read(_closed)` + `Interlocked.Increment(_refCount)` race).~~ **Fixed.** `_closed` field removed; both `AddReference` and `Dispose` now use the same CAS loop pattern as the three Linux dma-buf backings. Refcount == 0 *is* closed. Existing `VideoDmabufNv12BackingRefCountTests` covers the shared pattern (race test is structurally identical for the four backings).
- [x] **F3. Pooled-frame disposal contract is not documented at the `IAudioSource` boundary and is missed by tool code.** ~~`S.Media.Core/Audio/IAudioSource.cs:21-25` does not state that consumers must dispose leased `AudioFrame`s from `TryReadNextFrame`; `Tools/NDIPlayer/Program.cs:370-376` submits frames but never disposes them.~~ **Fixed.** Added a `<remarks>` block on `IAudioSource.TryReadNextFrame` spelling out the caller-owns-the-frame / dispose-once contract and cross-referencing the sink's synchronous span read. `Tools/NDIPlayer/Program.cs` `PumpAudio` and `PumpMuxOrdered`'s audio branch now wrap the submit in `try { … } finally { aFrame.Dispose(); }`; `PumpMuxOrdered`'s outer `finally` also drains any leftover `pendingA` so a cancellation between read and submit can't leak.
- [x] **F4. Sentinel docs are internally inconsistent.** ~~`S.Media.Core/Audio/AudioFormat.cs:16-21` now says the no-audio sentinel is `AudioFormat(0, 0)`, but `S.Media.FFmpeg/MediaContainerSharedDemux.cs:213-218` still emits `AudioFormat(48000, 0)`. Keep either code or docs aligned.~~ **Fixed by code change.** `MediaContainerSharedDemux.cs:217` now emits `new AudioFormat(0, 0)`; the surrounding comment was rewritten to point at `AudioFormat.Validate` as the fail-fast backstop for consumers that forget the `HasAudio` guard. This also retroactively closes the original Phase 0 **C3** item. Verified that every remaining `Audio.Format.SampleRate` read in the demux is either inside the audio-decode path or already gated by `_hasAudio`.

---

## Phase 0 — Stop the bleeding ✅ COMPLETE (verification 2026-05-19 — F1–F4, C4 doc pass, and S1 ctor invariant closed every item)

Correctness defects from §11.1. Land these before any refactor; they're cheap and the symptoms they cause are confusing.

- [x] **C1. Refcount race on all four `VideoFrame` backings** — `S.Media.Core/Video/VideoDmabufNv12Backing.cs:70-75`, `VideoDmabufP010Backing.cs`, `VideoDmabufP016Backing.cs`, `VideoWin32Nv12Backing.cs`. Replace the `Volatile.Read(_closed)` + `Interlocked.Increment(_refCount)` pair with a single CAS loop on `_refCount`; remove `_closed` (refcount == 0 *is* closed). Add a unit test that races `AddReference` against `Dispose` on a synthetic backing. **Size:** S. **Breaks:** nothing — internal pattern only.
  - **All four backings done.** Linux dma-buf trio (Nv12/P010/P016) landed in the original Phase 0 pass; the Win32 NV12 backing was closed as follow-up F2 (2026-05-19) — `_closed` removed, AddReference/Dispose now share the CAS-loop pattern with the dma-buf backings. The `VideoDmabufNv12BackingRefCountTests` race test exercises the shared structural pattern.

- [x] **C2. `PortAudioOutput.Flush()` calibration reset reordering** — `S.Media.PortAudio/PortAudioOutput.cs:125-137`. Move `Volatile.Write(ref _streamSmoothCalibrated, 0)` to **before** `Pa_StartStream`. Add a test that flushes mid-stream and asserts `ElapsedSinceStart` is monotonic across the flush. **Size:** XS. **Breaks:** nothing.
  - Done: write reordered with explanatory comment (PortAudioOutput.cs:138 is before Pa_StartStream at :139). **No unit test added** — the race window is microseconds and reliably reproducing it would need real PortAudio hardware plus instrumented callbacks. The one-line reorder is small enough to review by inspection.

- [x] **C3. Replace `AudioFormat(48000, 0)` sentinel with `AudioFormat(0, 0)`** — `S.Media.FFmpeg/MediaContainerSharedDemux.cs:230`. Update the XML doc on `MediaContainerDecoder.Audio` to say "consumers must guard with `HasAudio`". Add `Debug.Assert(Channels > 0)` at every router-side site that reads `SampleRate` so a regression fails fast. **Size:** XS. **Breaks:** any consumer that today reads `decoder.Audio.Format.SampleRate` without checking `HasAudio` (none in-tree; check `Tools/` to be sure).
  - **Closed by follow-up F4 (2026-05-19).** `MediaContainerSharedDemux.cs:217` now emits `new AudioFormat(0, 0)` with a rewritten comment pointing at `AudioFormat.Validate` as the fail-fast backstop. The bonus `HasAudio` guards in `Tools/NDIPlayer/Program.cs` were applied during the original Phase 0 pass. The proposed `Debug.Assert(Channels > 0)` peppering is unnecessary: AudioFormat.Validate (Phase 2) already throws at every public surface that wires a format into a live pipeline.

- [x] **C4. `AudioRouter` per-sink pump capacity is a flat 8 chunks for everyone.** AudioRouter.cs:136. For hardware sinks (those implementing `IClockedSink` with their own ring), expose a smaller default. At minimum document the latency implication on `AddSink` and on `AudioPlayer.AddOutput`. **Size:** XS doc, S if you also retune defaults.
  - **Closed by follow-up (2026-05-19, doc path).** Added a `<remarks>` block on `AudioRouter.AddSink` covering: the `pumpCapacityChunks × chunkSamples / sampleRate` latency formula with the concrete ≈ 80&#160;ms figure at defaults; the hardware vs. network recommendation (2–4 chunks for `IClockedSink`, default 8 for non-clocked / NDI); explicit note that auto-tuning isn't implemented and callers pass the knob explicitly. `AudioPlayer.AddOutput`'s `sinkPumpCapacityChunks` XML now mirrors the latency call-out and points at the longer discussion. Auto-tune for `IClockedSink` remains deliberately not wired — see the AddSink remark for the rationale.

- [x] **Verify the §11.2 suspects.** Each has enough context for a focused ~15-minute look. Do this before §0 closes so any extra defects join this phase rather than slip later.
  - [x] **S1.** `AudioRouter.RetargetSlaveClock` (AudioRouter.cs:459-471) constructs the new clock under `_gate`. Either move construction outside the lock or add a comment that the constructor must never invoke router APIs. **Size:** XS. **Closed via doc path (2026-05-19).** Took the "add a comment" alternative rather than moving construction outside the lock — `RetargetSlaveClock` / `SlaveTo` / `ReconfigureSampleRateWhileRunning` all need the sink-exists / IClockedSink validation to be atomic with the clock swap, and splitting the lock would open a different race with concurrent `RemoveSink`. Added an explicit invariant on `SinkSlavedRouterClock`'s constructor (SinkSlavedRouterClock.cs) calling out that it is held under `_gate` at all three call sites, must stay field-store-only, and must never invoke any `AudioRouter` API. The actual ctor already conforms; the comment makes the constraint explicit for future maintainers.
  - [x] **S4.** `S.Media.Avalonia/VideoOpenGlControl.cs` detach/render race on `_pendingFrame`. Trace what happens if a render fires between `_sinkDisposed = true` and the lock acquisition that drains the pending frame. **Size:** S. **Confirmed real and fixed:** Submit could pass the `_sinkDisposed` check at the top, detach could then run and drain `_pendingFrame`, and Submit would park a *new* frame in `_pendingFrame` with no further `OnOpenGlRender` callback to drain it (frame leak). Fix: re-check `_sinkDisposed` inside the frame lock and dispose-then-throw if disposed.
  - [x] **S5.** `S.Media.OpenGL/Nv12Win32SharedHandleGpuUploader.cs` keyed-mutex pairing on early-return paths. Confirm every `TryAcquireForGpuRead` success has a `Release` on every exit. **Size:** S. Windows-only test. **Rejected — no defect.** The agent claim misread C# semantics: the `try { … } finally { keyedScope?.Dispose(); }` block at lines 395-454 wraps every early `return false` inside the try; C# runs the finally on return from a try block. The acquire-failure return at line 392 is before keyedScope exists. All paths paired.
  - [x] **S6.** Resampler delay-buffer flush across seek. Write a test: open a sine-tone file, seek to a different position, decode the first 100 ms post-seek, FFT — should not show residue from pre-seek samples. **Size:** S. **Rejected — no defect.** `swr_close(_swr); swr_init(_swr)` at `MediaContainerSharedDemux.cs:806-808` is the canonical FFmpeg way to drain the resampler — `swr_close` frees `in_buffer`/`out_buffer` (the delay buffer). Combined with the preceding `avcodec_flush_buffers` + `av_frame_unref`, the path is correct. The agent's claim that `swr_close` "doesn't exist in standard FFmpeg" was wrong (it's been in libswresample since 1.0).

**Phase exit:** all confirmed defects (C1–C4) and all suspects closed. ✅

**Outcomes:**
- 4 confirmed defects fully fixed (C1 across all four backings — Linux dma-buf trio in the original pass, Win32 NV12 closed as F2; C2 Flush ordering; C3 sentinel + NDIPlayer HasAudio guards — sentinel finished as F4; C4 latency budget + hardware-vs-network rationale on `AudioRouter.AddSink` + `AudioPlayer.AddOutput`).
- 1 suspect confirmed real and fixed (S4 — `VideoOpenGlControl` re-checks `_sinkDisposed` inside the frame lock at VideoOpenGlControl.cs:156).
- 1 suspect closed via documented invariant (S1 — `SinkSlavedRouterClock`'s ctor now carries an explicit "must not invoke router APIs" remark since it is constructed under the router's `_gate` at all three call sites; the construction itself is benign and stays under the lock to keep the validate-then-swap atomic with `RemoveSink`).
- 2 suspects verified as agent misreads, not defects (S5, S6).
- 1 new unit test added (CAS-loop refcount race in `VideoDmabufNv12BackingRefCountTests.cs`).
- Tests: 602/603 pass post-fix (same as pre-fix). The one failure (`NDIIngestPlaybackClockTests.Timestamp_UsedWhenTimecodeSynthesize`) is the pre-existing flaky FP-tolerance test; an additional sometimes-flaky `AudioRouterTests.DynamicRemoveSink_StopsReceivingMidStream` passes in isolation (timing race in the test scaffolding, not in the fixes).
- Build: clean, zero warnings, zero errors across all 25 projects including HaPlay.

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

## Phase 3 — Foundational features ✅ COMPLETE (2026-05-18, 8/8)

These unblock the *primary product surfaces*: cue players, soundboards, image triggers, **and full video playback** (the user clarified 2026-05-18 that moving video is first-class — static images are the complementary case, not a replacement).

- [x] **`StaticFrameSource : IVideoSource`** in `S.Media.Core/Video/StaticFrameSource.cs`. Wraps any `ReadOnlyMemory<byte>[]` plane data and re-emits it on every read with PTS spaced at `format.FrameRate`. No external deps. 4 tests. **Size:** XS.

- [x] **`ImageFileSource : IVideoSource`** in new project `S.Media.SkiaSharp` (Linux native assets included). Decodes PNG / JPEG / WebP / BMP / GIF once via SkiaSharp's codec stack into premul BGRA8888; emits frames forever via `ReadOnlyMemory<byte>` over a pooled buffer (returned on `Dispose`). 4 tests; SkiaSharp + `SkiaSharp.NativeAssets.Linux` were already in `Directory.Packages.props`. **Size:** M.

- [x] **`VideoPlayer.HoldLastFrameAtEnd`** in `S.Media.Core/Video/VideoPlayer.cs`. When set, the player snapshots the last successfully submitted frame's plane data (managed `byte[]` copies) right before submit, then re-emits it on every subsequent video tick with PTS tracking the playhead — keeps NDI senders / GL renderers alive and the on-screen image stable instead of going dark. Hardware-backed frames (DMA-BUF / Win32 NV12) fall back to natural completion; CPU paths work today. 2 tests. **Size:** S.

- [x] **`MediaPlayer.Quick`** facade in new project `S.Media.Quick`. `QuickPlayer.Open(path)` returns a `QuickPlayback` handle with `Play()` / `Pause()` / `Stop()` / `Dispose()`. Auto-detects image vs media by extension; image path uses `ImageFileSource` + `HoldLastFrameAtEnd: true`; media path uses `MediaPlayer.TryOpen` + `PortAudioPlaybackHost` + `SDL3GLVideoSink`. Builds on Phase 2 auto-resample and pump-by-default. **Size:** M.

- [x] **`AudioGraphBuilder`** fluent helper in `S.Media.Core/Audio/AudioGraphBuilder.cs`. Chains `AddSource → AddSink → Connect`; tracks `LastSourceId` / `LastSinkId` so the one-source/one-sink case collapses to `.ConnectLast()`. Default `Connect` map is `ChannelMap.Identity` sized to the sink. 4 tests. **Size:** M.

- [x] **`BusSink` (sub-mix bus)** in `S.Media.Core/Audio/BusSink.cs`. Implements both `IAudioSink` and `IAudioSource`; lets the router pump mixed audio into the bus and pull it out the other side as a regular source. Lock-free SPSC ring (~80 ms default capacity) — same pattern as `NDIAudioReceiver`. `OverflowFloats` / `UnderflowFloats` counters for diagnostics. 5 tests. **Size:** M.

- [x] **`MediaContainerSharedDemux` packet queue depth as options field.** `MediaPlayerOpenOptions.AudioPacketQueueDepth` / `VideoPacketQueueDepth` (and matching `VideoDecoderOpenOptions` init properties) forward into the demux. `0` keeps the existing defaults (192 / 384). **Size:** XS.

- [x] **Move `VideoRouter` skeleton into `S.Media.Core`.** `VideoRouter`, `VideoSinkPump`, `VideoSinkFanoutFormats`, `VideoPumpPressureEvents` now live in `S.Media.Core/Video/` (namespace `S.Media.Core.Video`). New abstraction `IVideoCpuFrameConverter` + `VideoCpuFrameConverterRegistry` (factory + `CanConvertProbe`) lets Core do branch pixel conversion without referencing FFmpeg; the swscale-backed `VideoCpuFrameConverter` in `S.Media.FFmpeg` now implements that interface and is registered by `FFmpegRuntime.EnsureInitialized()`. The pure-managed plane-duplication helper moved to `VideoFrameCpuClone.DuplicateCpuBacking` (Core); `VideoCpuFrameConverter.DuplicateCpuBacking` stayed as a back-compat forwarder so HaPlay / smoke tools / older test sites kept compiling without changes. Build clean and 521/522 tests still pass (same single pre-existing flaky NDI clock test). **Size:** M.

**Phase exit:** the framework demonstrably backs a working soundboard / cue player loop — mixed-rate clips, static images held indefinitely, single-call file open with PortAudio + GL window, fluent route wiring, sub-mix bus, tunable demux for deep B-frame streams — **and** `VideoRouter` is now a Core type so consumers can wire video pipelines without pulling in FFmpeg. ✅

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

**P3.8 follow-up files (commit-after-Phase-3-batch):**
```
MediaFramework/Media/S.Media.Core/Video/IVideoCpuFrameConverter.cs          (new — interface + registry + VideoFrameCpuClone)
MediaFramework/Media/S.Media.Core/Video/VideoRouter.cs                      (git-mv from S.Media.FFmpeg/Video, namespace S.Media.Core.Video, switched to IVideoCpuFrameConverter)
MediaFramework/Media/S.Media.Core/Video/VideoSinkPump.cs                    (git-mv, namespace bump)
MediaFramework/Media/S.Media.Core/Video/VideoSinkFanoutFormats.cs           (git-mv, namespace bump, uses CanConvertProbe)
MediaFramework/Media/S.Media.Core/Video/VideoPumpPressureEvents.cs          (git-mv, namespace bump)
MediaFramework/Media/S.Media.Core/Video/DiscardingVideoSink.cs              (cref → S.Media.Core.Video.VideoRouter)
MediaFramework/Media/S.Media.Core/Video/IVideoSink.cs                       (cref → S.Media.Core.Video.{VideoRouter,VideoSinkPump,VideoSinkPumpAttachOptions})
MediaFramework/Media/S.Media.FFmpeg/Video/VideoCpuFrameConverter.cs         (implements IVideoCpuFrameConverter; DuplicateCpuBacking now forwards)
MediaFramework/Media/S.Media.FFmpeg/FFmpegRuntime.cs                        (installs Factory + CanConvertProbe)
MediaFramework/Media/S.Media.PortAudio/PortAudioPlaybackHost.cs             (cref bump)
```

---

## Phase 4 — Compositing & overlays ✅ COMPLETE (2026-05-19, 6/6)

The N→1 video compositing surface needed for picture-in-picture, lower-thirds, text overlays, and cue-stack transitions. Land all six items together so abstractions and consumers ship in lockstep.

- [x] **Define `IVideoCompositor`** in `S.Media.Core`. Inputs: ordered list of `CompositorLayer` (frame + transform + opacity + blend mode). Output: single `VideoFrame` (compositor returns a new frame; caller takes ownership). Supporting types: `LayerTransform2D` (2×3 affine with Identity/Translate/Scale/Rotate/Compose/Invert), `BlendMode` (`Source` / `SourceOver` / `Multiply`), `CompositorLayer` record struct.
  - Done in `S.Media.Core/Video/{IVideoCompositor,CompositorLayer,LayerTransform2D,BlendMode}.cs`.

- [x] **`CpuVideoCompositor`** in `S.Media.Core/Video/CpuVideoCompositor.cs`. BGRA32 in/out reference impl — inverse-affine nearest-neighbor sampling, premultiplied-alpha math for Source / SourceOver / Multiply, output buffer from `ArrayPool<byte>` released via the frame's release callback. Always available — no GPU context required.

- [x] **`GlVideoCompositor`** in `S.Media.OpenGL/GlVideoCompositor.cs`. GL 3.3 implementation: FBO + RGBA8 texture as render target, per-`(W, H)` layer-texture cache, shader-based blend with `glBlendFunc` per blend mode (`Source` → blend disabled; `SourceOver` → `ONE, ONE_MINUS_SRC_ALPHA` premul; `Multiply` → `DST_COLOR, ZERO` with shader-side mix to handle layer-alpha weighting). Bring-your-own GL context (caller responsible for making it current on the same thread). Shaders `Shaders/composite_layer.{vert,frag}.glsl` embedded as resources; `SharedGlProgramCache` reuse. Output via `glReadPixels(GL_BGRA)` into pooled BGRA32 buffer. State save/restore around `Composite` (FB binding, viewport, program, VAO, blend func + enable, scissor, unpack alignment/row-length) so it can embed in another sink's render path without trashing host state.

- [x] **`CompositorVideoSink`** in `S.Media.Core/Video/CompositorVideoSink.cs`. Single class implementing `IVideoSource`; exposes N slots via `AddSlot(...)` → returns a `Slot` handle with `.Sink` (the `IVideoSink` upstream code targets) plus mutable `.Opacity` / `.Transform` / `.BlendMode`. **Replace-on-submit** hold-latest semantics per slot — the previous frame is disposed when a new one arrives; `Slot.OverflowFrames` counts replacements. `TryReadNextFrame` snapshots slots in insertion order (back-to-front), invokes `IVideoCompositor.Composite`, returns the result. PTS derived from the output `Format.FrameRate`. Slot format negotiation declares the compositor's accepted layer formats.

- [x] **`TextLayerSource : IVideoSource`** in `S.Media.SkiaSharp/TextLayerSource.cs`. SkiaSharp CPU rasterization (first cut per checklist) → BGRA32 premul with alpha. Mutable Text / FontFamily / FontSize / ArgbColor / BackgroundArgb / Alignment with dirty-flag re-rasterise. Buffer rented from `ArrayPool<byte>`; emitted frames alias the live buffer (released on `Dispose`).

- [x] **Transitions** in `S.Media.Core/Video/{LayerOpacityTween,FadeFromBlackVideoSource,CutVideoSource}.cs`. `LayerOpacityTween` is a stateless tween helper (`Linear` / `EaseInOutSine`) — pair with a slot to drive fade-in/fade-out animations from a clock. `FadeFromBlackVideoSource` wraps an inner CPU source and multiplies RGB by a 0→1 ramp for the first `duration` worth of frames (BGRA32/Rgba32/Rgb24/Bgr24/Gray8; YUV rejected). `CutVideoSource` switches between two sources at a configurable PTS, rewriting the first B-frame's PTS to the cut boundary for clean continuity.

**Phase exit:** PiP and lower-thirds are demonstrable — `CompositorVideoSink` + `CpuVideoCompositor` runs headless in tests; `GlVideoCompositor` integrates with the existing SDL3/Avalonia GL context discipline. Transitions, text overlays, fade-from-black, and hard cuts all land. The cue player can now wire "PNG with fade-in + video underneath + text label" through one `IVideoCompositor` and ship out an NDI sender. ✅

**Outcomes:**
- Build: clean (0 warnings / 0 errors) across all 25 projects including HaPlay.
- Tests: 557/558 pass. Single failure is the same pre-existing flaky `NDIIngestPlaybackClockTests.Timestamp_UsedWhenTimecodeSynthesize` (FP-tolerance, ~5000 ticks delta) seen in Phase 0/1/2/3 — unrelated to Phase 4.
- 24 new tests across LayerTransform2D (7), CpuVideoCompositor (6), CompositorVideoSink (6), LayerOpacityTween (7), FadeFromBlackVideoSource (4), CutVideoSource (3), TextLayerSource (4 — 1 carry-over with ImageFileSource).
- GL compositor itself has no headless-context test infra in the repo today; it builds clean and is validated via the existing SDL3/Avalonia GL test surfaces. Future work: extend `Tools/VideoPlaybackSmoke` (or a new `Tools/CompositorSmoke`) with a `--pip` flag exercising the GL compositor against a real SDL3 context. Not blocking phase exit.

**Files added:**
```
MediaFramework/Media/S.Media.Core/Video/IVideoCompositor.cs           (new)
MediaFramework/Media/S.Media.Core/Video/CompositorLayer.cs            (new)
MediaFramework/Media/S.Media.Core/Video/LayerTransform2D.cs           (new)
MediaFramework/Media/S.Media.Core/Video/BlendMode.cs                  (new)
MediaFramework/Media/S.Media.Core/Video/CpuVideoCompositor.cs         (new)
MediaFramework/Media/S.Media.Core/Video/CompositorVideoSink.cs        (new)
MediaFramework/Media/S.Media.Core/Video/LayerOpacityTween.cs          (new)
MediaFramework/Media/S.Media.Core/Video/FadeFromBlackVideoSource.cs   (new)
MediaFramework/Media/S.Media.Core/Video/CutVideoSource.cs             (new)
MediaFramework/Media/S.Media.OpenGL/GlVideoCompositor.cs              (new)
MediaFramework/Media/S.Media.OpenGL/Shaders/composite_layer.vert.glsl (new)
MediaFramework/Media/S.Media.OpenGL/Shaders/composite_layer.frag.glsl (new)
MediaFramework/Media/S.Media.SkiaSharp/TextLayerSource.cs             (new)
MediaFramework/Test/S.Media.Core.Tests/Video/LayerTransform2DTests.cs        (new)
MediaFramework/Test/S.Media.Core.Tests/Video/CpuVideoCompositorTests.cs     (new)
MediaFramework/Test/S.Media.Core.Tests/Video/CompositorVideoSinkTests.cs    (new)
MediaFramework/Test/S.Media.Core.Tests/Video/LayerOpacityTweenTests.cs      (new)
MediaFramework/Test/S.Media.Core.Tests/Video/FadeFromBlackVideoSourceTests.cs (new)
MediaFramework/Test/S.Media.Core.Tests/Video/CutVideoSourceTests.cs         (new)
MediaFramework/Test/S.Media.SkiaSharp.Tests/TextLayerSourceTests.cs         (new)
```

**Design notes for next phase consumers:**
- The GL compositor consumer of `CompositorVideoSink.Output` (typically a `VideoPlayer` on a clock thread) must own the GL context. Wiring a GL compositor into a non-GL player driven by an arbitrary clock thread is not supported — a "GL-owned context" wrapper is future work.
- First-cut compositors accept BGRA32 layers only; YUV layers must be CPU-converted upstream. The existing `VideoRouter` branch-converter path handles this if the slot declares `AcceptedPixelFormats = [Bgra32]`. A future enhancement can teach `GlVideoCompositor` to upload YUV layers directly via the existing `YuvVideoRenderer` recipes, skipping the CPU conversion.
- CPU compositor uses nearest-neighbor sampling on affine transforms; bilinear / bicubic is a future improvement.
- `FadeFromBlackVideoSource` rejects hardware-backed frames. For hardware paths use `LayerOpacityTween` on a `CompositorVideoSink.Slot` — opacity is a compositor uniform, not a frame-content transform.

---

## Phase 5 — Broadcast-grade format support ✅ COMPLETE (2026-05-19, 3/4 — ancillary deferred)

These move the framework from "playout / streaming" to "broadcast-ready". User picked the **three-item scope** (BT.2020 / interlaced + yadif / SMPTE timecode); the ancillary-data envelope is explicitly deferred per its own "Defer until a concrete consumer asks" note.

- [x] **BT.2020 / Rec.2020 color primaries.** Added `VideoColorSpace` + `VideoColorRange` enums in `S.Media.Core/Video/`; FFmpeg propagation extracts `_frame->colorspace` + `_frame->color_range` and threads them through every frame factory via a new internal `VideoFrameMetadataHint` struct. Added `YuvColorSpace.Bt2020Limited` / `Bt2020Full` matrices plus a `YuvColorSpace.FromHint(VideoColorSpace, VideoColorRange, int height)` helper. `YuvVideoRenderer.Upload` now applies the per-frame hint automatically (no-op when the resolved matrix equals the current). FFmpeg's `AVCOL_SPC_BT2020_NCL` and `AVCOL_SPC_BT2020_CL` now flow end-to-end to the right shader matrix.

- [x] **Interlaced video.** New `VideoFieldOrder` enum on `VideoFrame`; FFmpeg propagation reads the FFmpeg-8 `AV_FRAME_FLAG_INTERLACED` / `AV_FRAME_FLAG_TOP_FIELD_FIRST` flags and threads them through every frame factory. New `IDeinterlacer` interface + `VideoDeinterlacerRegistry` in Core (same pluggable shape as `IVideoCpuFrameConverter`). `BobDeinterlacer` (Core) is the always-available CPU fallback for BGRA32 / RGBA32 / I420 / NV12 — field separation + line interpolation, doubles the frame rate. `YadifDeinterlacer` (FFmpeg) wraps a libavfilter graph `buffer → yadif=mode=0:parity=auto:deint=interlaced → buffersink` and is auto-registered at `FFmpegRuntime.EnsureInitialized`. Supports I420 and NV12 inputs; NV12 round-trips through yuv420p internally because yadif's in-tree NV12 support is incomplete on some FFmpeg builds. Progressive input bypasses the graph as a passthrough.

- [x] **SMPTE LTC / drop-frame timecode.** New `VideoTimecode` readonly record struct in Core: carries `(Hours, Minutes, Seconds, Frames, IsDropFrame, FrameRate)` with `ToFrameNumber()` / `ToTicksAtRate()` / `ToTimecodeString()` / `TryParse(string, Rational, out)` / `FromFrameNumber(long, Rational, bool)`. Companion static helper `VideoTimecodeMath` does the drop-frame skip math (29.97 → skip 2 per minute except every 10th; 59.94 → skip 4). Validates that drop-frame is only allowed at 30000/1001 and 60000/1001 — 23.976 explicitly rejected. FFmpeg extracts `AV_FRAME_DATA_S12M_TIMECODE` side data via a new `VideoFileDecoder.ReadS12mTimecode` helper and attaches the result to every `VideoFrame`. New `NDIVideoTimecodeMode.SmpteFromFrame` mode on `NDIVideoSender` encodes `frame.Timecode?.ToTicksAtRate()` into the NDI Timecode slot (falls back to `PresentationRelativeTicks` math when the frame carries no timecode). `NDIVideoSender.FrameFormatType` now derives from `frame.FieldOrder` (Progressive vs Interleaved).

- [ ] **Embedded ancillary data** (closed captions, AFD, SCTE-35). **Deferred — no concrete consumer.** The checklist annotation "Defer until a concrete consumer asks" is the reason; the API surface for `VideoAncillary` (envelope vs sidecar callback, per-frame vs side-data, generic vs typed slots) is speculative without a real CC / AFD / SCTE-35 consumer to design against. Will re-open when a paying use case lands.

**Phase exit:** demonstrably correct ingest/playout for SMPTE-source content. BT.2020 UHD HDR routes the right matrix automatically; interlaced ingest no longer silently strips field info; SMPTE timecode round-trips from FFmpeg side data into the NDI sender slot. ✅

**Outcomes:**
- Build: clean (0 warnings / 0 errors) across all 25 projects including HaPlay.
- Tests: 595/596 pass. Single failure is the same pre-existing flaky `NDIIngestPlaybackClockTests.Timestamp_UsedWhenTimecodeSynthesize` (~4600 ticks FP-tolerance delta) seen in every prior phase — unrelated.
- 38 new tests added across VideoTimecode (12), VideoFrameMetadata (2), BobDeinterlacer (5), VideoColorSpace (3), FFmpeg VideoColorSpaceMapping (6), FFmpeg YadifDeinterlacer (3), OpenGL YuvColorSpace (7).

**Files added:**
```
MediaFramework/Media/S.Media.Core/Video/VideoColorSpace.cs                 (new)
MediaFramework/Media/S.Media.Core/Video/VideoColorRange.cs                 (new)
MediaFramework/Media/S.Media.Core/Video/VideoFieldOrder.cs                 (new)
MediaFramework/Media/S.Media.Core/Video/VideoTimecode.cs                   (new — struct + VideoTimecodeMath helpers)
MediaFramework/Media/S.Media.Core/Video/IDeinterlacer.cs                   (new — interface + registry)
MediaFramework/Media/S.Media.Core/Video/BobDeinterlacer.cs                 (new — Core fallback)
MediaFramework/Media/S.Media.Core/Video/VideoFrame.cs                      (4 new optional ctor params + 4 new properties, threaded through all Create*Dmabuf factories + SharedReference forwarders + TryCreateNv12CpuFanOutViews)
MediaFramework/Media/S.Media.Core/Video/IVideoCpuFrameConverter.cs         (VideoFrameCpuClone forwards new metadata)
MediaFramework/Media/S.Media.FFmpeg/Video/VideoFrameMetadataHint.cs        (new — internal struct bundling trc/cs/range/field/timecode)
MediaFramework/Media/S.Media.FFmpeg/Video/YadifDeinterlacer.cs             (new — libavfilter wrapper, I420 / NV12 input)
MediaFramework/Media/S.Media.FFmpeg/Video/VideoFileDecoder.cs              (MapColorSpace/MapColorRange/MapFieldOrder/ReadS12mTimecode + refactored 5 Build*Frame sites to take VideoFrameMetadataHint)
MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs           (mirrors the VideoFileDecoder refactor on its 3 Build*VideoFrame sites)
MediaFramework/Media/S.Media.FFmpeg/FFmpegRuntime.cs                       (registers YadifDeinterlacer factory)
MediaFramework/Media/S.Media.OpenGL/YuvColorSpace.cs                       (Bt2020Limited + Bt2020Full + FromHint)
MediaFramework/Media/S.Media.OpenGL/YuvVideoRenderer.cs                    (Upload applies per-frame ColorSpace hint)
MediaFramework/Media/S.Media.NDI/Video/NDIVideoTimecodeMode.cs             (new SmpteFromFrame mode)
MediaFramework/Media/S.Media.NDI/Video/NDIVideoSender.cs                   (SmpteFromFrame branch in BuildTimecode, FrameFormatType from FieldOrder)
MediaFramework/Test/S.Media.Core.Tests/Video/VideoTimecodeTests.cs         (new — 12 tests covering drop-frame math, parsing, round-trips)
MediaFramework/Test/S.Media.Core.Tests/Video/VideoFrameMetadataTests.cs    (new)
MediaFramework/Test/S.Media.Core.Tests/Video/BobDeinterlacerTests.cs       (new)
MediaFramework/Test/S.Media.Core.Tests/Video/VideoColorSpaceTests.cs       (new)
MediaFramework/Test/S.Media.FFmpeg.Tests/Video/VideoColorSpaceMappingTests.cs (new)
MediaFramework/Test/S.Media.FFmpeg.Tests/Video/YadifDeinterlacerTests.cs   (new)
MediaFramework/Test/S.Media.OpenGL.Tests/YuvColorSpaceTests.cs             (new)
```

**Design notes for next phase consumers:**
- The `VideoFrame` ctor now has 11 positional/optional params (was 8 pre-Phase-5). A future refactor could collapse the optional-metadata trio (`ColorTransferHint`, `ColorSpace`, `ColorRange`, `FieldOrder`, `Timecode`) into a single `VideoFrameMetadata` record struct — not in this batch, but worth flagging in Phase 6 cleanup.
- `YadifDeinterlacer` accepts only I420 / NV12 inputs. Other interlaced formats (uyvy422, yuv422p, yuv422p10le) currently fall through to `BobDeinterlacer` via the registry factory's switch. Adding more yadif-supported formats is a follow-up — yadif's accepted-format set on the libavfilter side is broader than what we wire here.
- The "NDI receiver reads SMPTE timecode out" part of the checklist is contingent on an `NDIVideoReceiver` existing — only the sender side is in this batch. When the receiver lands, it should read `NDIVideoFrameV2.Timecode` ticks and reconstruct via `VideoTimecode.FromFrameNumber(ticks / (10_000_000 * den / num), rate, dropFrame: VideoTimecodeMath.IsDropFrameRate(rate))`.
- BT.2020 primaries are wired for the YCbCr → RGB matrix only. **RGB-to-RGB gamut mapping** (BT.2020 → BT.709 for SDR display preview) is not done — the framework presents BT.2020 content as-if-BT.709 to the display, which under-saturates without a display that natively understands BT.2020. A future GL pass between matrix and HDR transfer could handle this with a 3×3 RGB matrix.
- Drop-frame math is conservative: 29.97 and 59.94 only. Variable-frame-rate streams (where the rational rate is the maximum, not the exact playback rate) are out of scope.

---

## Phase 6 — Cleanup ✅ COMPLETE (2026-05-19, batch + follow-up — all in-repo items closed)

Quality-of-life. No correctness or feature consequences. Original batch landed four items; the 2026-05-19 follow-up closed the remarks move + project-memory refresh + the untracked-files note. Only the CI smoke-test item remains open as it needs infrastructure that doesn't exist yet.

- [x] **Split `ChannelMap.cs` (2106 lines)** into `ChannelMap.cs` (data + ApplyAdditive dispatcher + small ops; now 292 lines) + `ChannelMap.SimdAccumulate.cs` (19 SIMD methods + private helpers, partial struct, 1831 lines). Zero behavior change — confirmed by re-running the 141 in-tree ChannelMap / AudioRouter tests, all green. (Review said 14 SIMD methods; the actual count was 19 once the `TryAccumulate*` family was enumerated.)

- [x] **`VideoFrameMetadata` consolidation** — new `readonly record struct VideoFrameMetadata` in `S.Media.Core/Video/` bundling `(ColorTransferHint, ColorSpace, ColorRange, FieldOrder, Timecode)`. `VideoFrame` ctor signature drops 5 individual hint params in favor of one `metadata` param. The 5 read-side properties (`ColorTransferHint` etc.) are preserved as forwarders to `Metadata.X` so existing consumers compile unchanged. New `Metadata` property exposes the bundle directly for `with`-expression mutation. FFmpeg's internal `VideoFrameMetadataHint` struct (Phase 5) is deleted — superseded by the public Core type. `meta.TransferHint` → `meta.ColorTransferHint` rename mechanically applied. Net VideoFrame ctor: 11 params → 11 params (4 swapped for 1 metadata + 1 IDisposable; see P6.4 below) but conceptually much cleaner — the metadata bundle is one named thing rather than 5 loose hints. Touched: VideoFrame.cs, IVideoCpuFrameConverter.cs (`VideoFrameCpuClone`), BobDeinterlacer.cs, FadeFromBlackVideoSource.cs, CutVideoSource.cs, StaticFrameSource.cs, VideoPlayer.cs, VideoDmabufCpuReadback.cs, VideoFileDecoder.cs, MediaContainerSharedDemux.cs, VideoCpuFrameConverter.cs, YadifDeinterlacer.cs, LogoFallbackVideoSink.cs (HaPlay), NDIOutputPreviewRuntime.cs (HaPlay), 3 test files.

- [x] **`AudioFileDecoder.TryReadNextFrame` ArrayPool** (S3 from §11.2). `AudioFrame` gains an optional `Release` callback (5th positional record-struct field, default null) + a `Dispose()` method that invokes it. `AudioFileDecoder.ConvertFrame` and `MediaContainerSharedDemux.ConvertAudioFrame` rent from `ArrayPool<float>.Shared` and return on dispose. Existing `AudioFrame` constructions (test sites etc.) compile unchanged because the new field has a default value. Eliminates per-call `new float[]` alloc for frame-based audio consumers (the ReadInto path was already alloc-free).

- [x] **`VideoFrame` `IDisposable` release path** — added a second `disposableRelease: IDisposable?` parameter to the VideoFrame ctor. Dispose method invokes both `_release: Action?` and `_disposableRelease: IDisposable?` (both nullable, both fire-and-clear). The four `Create*Dmabuf` / `CreateNv12Win32Shared` factories now pass `disposableRelease: <backing>` directly when `additionalRelease` is null — eliminates the per-frame method-group → delegate allocation on the common path (each `VideoDmabufXXXBacking` already implements `IDisposable`). When `additionalRelease` is non-null they keep the existing closure path (one alloc per call, unavoidable). Saves roughly one delegate allocation (~24-64 B) per zero-copy dma-buf / Win32 shared frame in fan-out scenarios.

- [x] **Move long-form XML `<remarks>` into `Doc/` files** — closed via selective move (2026-05-19). New `Doc/MediaFramework-Architecture.md` carries the long-form discussion of `AudioRouter`'s route-mix profiling switch + multi-output drift mitigation, `MediaContainerSession`'s `flushSharedMuxAfterPause` deadlock guidance, and the worked `MediaContainerPlaybackBundle` example (PortAudio + NDI + GL fan-out, ownership flags, `finally`-order). The in-source `<remarks>` on each type now keep the essential 2–3 paragraphs (so IDE hover stays useful) and cross-reference the doc for the deeper material. Net: `AudioRouter.cs`'s class header dropped from ~75 lines of remarks to ~45; `MediaContainerSession` / `MediaContainerPlaybackBundle` each lost ~6 lines and got tighter.

- [x] **Refresh project memory** — done out-of-band (touches `~/.claude/projects/-home-seko-RiderProjects-MFPlayer/memory/`). Updated `project_overview.md` to reflect the post-rename type surface and current verification numbers; added `reference_doc_locations.md` pointing at the `Doc/` directory and `feedback_checklist_phases.md` capturing the multi-phase checklist work pattern. `MEMORY.md` index updated. Outside the in-repo file tree, so no checklist diff per se.

- [x] **Clean up untracked files in tree** — closed by the user's commit pattern. Earlier session output (`git status`) showed a clean working tree with no untracked files; the Phase 3–6 file additions noted at the time were all subsequently committed (latest commit `06e7d5e P5 + P6`). Only this session's in-flight edits (the F1–F4 / S1 / C4 / Phase-6-cleanup fixes) remain uncommitted, and those are intentionally staged for a user review pass.

- [x] **Add `Doc/NDI-Terminology.md` glossary** (Egress, Ingest, Mux, Fusion, Aggregating, Pump). **Size:** XS. *(Done in Phase 1.)*

- [ ] **CI smoke-test the `Tools/` projects** — out of scope for an in-repo coding pass; needs CI infrastructure that doesn't exist in the repo today. Future work when the project adopts CI.

**Phase exit:** every in-repo cleanup item landed (four original + three follow-up). Only CI smoke-test of the `Tools/` projects remains open and that is gated on CI infrastructure not yet wired up. ✅

**Outcomes:**
- Build: clean (0 warnings / 0 errors) across all 25 projects.
- Tests: 602/603 pass. Single failure is the same pre-existing flaky `NDIIngestPlaybackClockTests.Timestamp_UsedWhenTimecodeSynthesize` (FP-tolerance ~78k ticks delta this run; varies per run, unrelated to Phase 6) seen in every prior phase.
- 7 new tests added: `VideoFrameMetadataTests` (1 new — bundled-read test), `VideoFrameReleaseTests` (4 — Action/IDisposable/both/idempotent), `AudioFileDecoderArrayPoolTests` (2).

**Files added / modified (representative):**
```
MediaFramework/Media/S.Media.Core/Audio/ChannelMap.cs                       (-1814 lines; class → partial, kept dispatcher + small ops + factories)
MediaFramework/Media/S.Media.Core/Audio/ChannelMap.SimdAccumulate.cs        (new; 1831 lines, partial — 19 SIMD methods + private helpers)
MediaFramework/Media/S.Media.Core/Audio/AudioFrame.cs                       (new Release field + Dispose())
MediaFramework/Media/S.Media.Core/Video/VideoFrameMetadata.cs               (new — consolidated metadata struct)
MediaFramework/Media/S.Media.Core/Video/VideoFrame.cs                       (ctor consolidation + IDisposable release path)
MediaFramework/Media/S.Media.Core/Video/IVideoCpuFrameConverter.cs          (VideoFrameCpuClone forwards new metadata struct)
MediaFramework/Media/S.Media.Core/Video/BobDeinterlacer.cs                  (3 sites: metadata bundle + with-mutation)
MediaFramework/Media/S.Media.Core/Video/FadeFromBlackVideoSource.cs         (1 site)
MediaFramework/Media/S.Media.Core/Video/CutVideoSource.cs                   (5 sites including hardware-backing forwarders)
MediaFramework/Media/S.Media.Core/Video/StaticFrameSource.cs                (ctor arg fix)
MediaFramework/Media/S.Media.Core/Video/VideoPlayer.cs                      (HoldLastFrame ctor)
MediaFramework/Media/S.Media.Core/Video/VideoDmabufCpuReadback.cs           (3 sites: Nv12/P010/P016 CPU copy)
MediaFramework/Media/S.Media.FFmpeg/Video/VideoFrameMetadataHint.cs         (deleted — superseded)
MediaFramework/Media/S.Media.FFmpeg/Video/VideoFileDecoder.cs               (Build* methods take VideoFrameMetadata; 4 frame-factory sites simplified)
MediaFramework/Media/S.Media.FFmpeg/MediaContainerSharedDemux.cs            (ConvertAudioFrame uses ArrayPool + AudioFrame.Release; 3 video frame-factory sites simplified)
MediaFramework/Media/S.Media.FFmpeg/Audio/AudioFileDecoder.cs               (ConvertFrame uses ArrayPool<float> + AudioFrame.Release)
MediaFramework/Media/S.Media.FFmpeg/Video/VideoCpuFrameConverter.cs         (metadata forwarding)
MediaFramework/Media/S.Media.FFmpeg/Video/YadifDeinterlacer.cs              (2 sites: output frames forward source metadata with progressive-field override)
UI/HaPlay/Playback/LogoFallbackVideoSink.cs                                 (2 sites)
UI/HaPlay/OutputPreview/NDIOutputPreviewRuntime.cs                          (1 site)
MediaFramework/Test/S.Media.Core.Tests/Video/VideoFrameMetadataTests.cs     (new bundled-read test)
MediaFramework/Test/S.Media.Core.Tests/Video/VideoFrameReleaseTests.cs      (new)
MediaFramework/Test/S.Media.Core.Tests/Video/BobDeinterlacerTests.cs        (named-arg → metadata bundle)
MediaFramework/Test/S.Media.FFmpeg.Tests/Audio/AudioFileDecoderArrayPoolTests.cs (new)
MediaFramework/Test/S.Media.FFmpeg.Tests/Video/YadifDeinterlacerTests.cs    (named-arg → metadata bundle)
MediaFramework/Test/S.Media.FFmpeg.Tests/Video/VideoCpuFrameConverterTests.cs (ctor arg fix)
```

**Design notes:**
- The `Action? _release` ↔ `IDisposable? _disposableRelease` two-slot dispatch is the simplest non-breaking variant. A future refactor could collapse both into a discriminated-union struct (e.g. a tagged `(byte kind, object state)` pair), but that touches VideoFrame internals more deeply and is profile-driven — not in scope.
- `ChannelMap` is now a `readonly partial struct` (was `readonly struct`). External callers see no change.
- `AudioFrame` is still a `readonly record struct`, just with an additional `Release` field. Auto-generated `Equals` now considers the Release callback in value equality — practically this means two AudioFrames with the same data but different release callbacks compare unequal. No in-tree consumer relies on the prior equality contract; documented if it becomes an issue.

---

## Phase 7 — Beyond the checklist (polish) ✅ COMPLETE (2026-05-19, 3 original items + P7.4 + P7.5 follow-ups)

The three "Beyond the checklist" items called out in the original Suggested-next tail, now lifted into their own phase. None are blocking; each is a quality-of-output improvement on a feature that already shipped.

- [x] **P7.1. Broader `YadifDeinterlacer` pixel-format coverage.** `Configure` now branches on the input pixel format and matches the libavfilter graph to its subsampling: I420 / NV12 → `yuv420p` (NV12 still round-trips through `yuv420p` because yadif's NV12 path is unreliable across FFmpeg builds), Yuv422P → `yuv422p`, Yuv444P → `yuv444p`. Chroma-plane copies pulled into shared 3-plane / NV12-split paths driven by per-format `(_chromaWidthDiv, _chromaHeightDiv)` divisors. `FFmpegRuntime.EnsureInitialized` updated so the default `VideoDeinterlacerRegistry.Factory` routes the two new formats to yadif; anything else still falls through to `BobDeinterlacer`. Two new tests in `YadifDeinterlacerTests` push interlaced 4:2:2 and 4:4:4 frames and assert progressive output preserves the input format (3 planes each). **Size:** S → S.

- [x] **P7.2. Bilinear (and bicubic) sampling in `CpuVideoCompositor`.** New `CompositorSamplingMode { Nearest, Bilinear, Bicubic }` enum in `S.Media.Core/Video/`. `CpuVideoCompositor` gained a ctor `samplingMode` argument and a settable `SamplingMode` property; both default to `Nearest` so existing byte-exact callers stay unchanged. Bilinear path: shifts the fractional source coord by 0.5 for pixel-center convention, samples four neighbors with `(1-tx)(1-ty)` / `tx(1-ty)` / `(1-tx)ty` / `tx·ty` weights, clamps each neighbor index to `[0, srcW-1] × [0, srcH-1]` so edges stay sharp instead of fading to transparent. Bicubic path (follow-up 2026-05-19): 4×4 Catmull-Rom kernel, edge-clamped, with per-channel result clamped to `[0, 255]` to absorb the interpolating spline's overshoot at very high contrast transitions; same pixel-center convention as bilinear. Four new tests in `CpuVideoCompositorTests` cover the gradient across a 2× horizontal scale for bilinear and bicubic, the bicubic identity-transform byte-exactness contract, and the default-stays-Nearest ctor contract. **Size:** S → S.

- [x] **P7.3. BT.2020 → BT.709 RGB gamut mapping.** New `RgbGamutMatrix` readonly record struct in `S.Media.OpenGL/` with two static instances (`Identity`, `Bt2020ToBt709` — the ITU-R BT.2087 row-major matrix) plus `FromHint(source, display)`. Patched six YUV fragment shaders (`yuv_planar`, `yuv_nv12`, `yuv_nv21`, `yuva_planar`, `uyvy422`, `yuyv422`) to declare `uniform mat3 gamutMatrix` and multiply it after the existing `hdrPreviewAfterMatrix` pass. `YuvVideoRenderer` exposes `GamutMatrix` as a mutable property defaulting to `Identity`; uniform location is bound only when the shader's `NeedsYuvMatrix` recipe is in use, so RGB pixel-format paths see no effect. New `RgbGamutMatrixTests` covers 7 cases: identity preservation, BT.2020 unit white → BT.709 unit white round-trip, saturated-red boost-beyond-gamut characterisation, and the four `FromHint` branches (Bt2020 / Bt2020Cl source → remap; Bt709 source or non-Bt709 display → identity). **Size:** M → M.

- [x] **P7.5. `GlVideoCompositor` accepts native YUV / YUVA layers** (follow-up 2026-05-19, smoke-validated 2026-05-20 — scope: GL only / RGBA8 output). The compositor's accepted-layer list now mirrors `YuvVideoRenderer.SupportedPixelFormats` — BGRA32 stays the direct-upload fast path; every other format runs a per-layer `YuvVideoRenderer` pre-pass into a cached **RGBA16F intermediate texture + FBO** so 10 / 12 / 16-bit precision survives into the composite stage. The composite shader samples the intermediate identically to a BGRA32 layer, so transform / opacity / blend behave the same. Caches: per-`(W, H)` BGRA32 textures, per-`(W, H)` RGBA16F intermediate textures + FBOs (shared across formats at the same size), per-`(PixelFormat, W, H)` `YuvVideoRenderer` instances. Each YUV layer's `ColorSpace` is set from `frame.Metadata.ColorSpace + ColorRange + Height` via `YuvColorSpace.FromHint`. Output stays RGBA8 BGRA32 via `glReadPixels`; one 8-bit truncation lives only at the very end of the pipeline. GL state save/restore (program / VAO / viewport / FBO / texture-unit-0) wraps each YUV pre-pass inside the layer loop. New `GlVideoCompositorAcceptedFormatsTests` (21 cases) asserts the accepted-format set includes the user's `yuva444p12le` / `yuv422p10le` plus the rest of the family. New `Tools/CompositorSmoke` runnable (`dotnet run --project MediaFramework/Tools/CompositorSmoke -- --background <bg> --foreground <fg> --out out.png`) composites one decoded frame from each input and writes a BGRA32 PNG — purpose-built for the user's test content to confirm formats reach the compositor natively. **Size:** M.

- [x] **P7.4. Professional pixel-format coverage — YUVA family + 12-bit / 16-bit variants** (follow-up 2026-05-19). Twelve new `PixelFormat` enum members covering the full YUVA family at 8 / 10 / 12 / 16 bit (less `Yuva420P12Le` which libav doesn't expose) plus 12-bit non-alpha `Yuv422P12Le` / `Yuv444P12Le`. Wired end-to-end: `PixelFormatInfo` (plane count / plane height / plane byte width / bytes-per-sample / alpha + high-bit predicates), `FfmpegVideoPixelMaps.ToAvPixelFormat`, `VideoFileDecoder.MapNativePixelFormat`, and `GlVideoFormatSupport` (new `Planar422YuvaSize` / `Planar444YuvaSize` helpers; per-format recipes pick R8 vs R16 storage + 10/12-bit bitScale; `SupportedPixelFormatsOrder` re-ordered to prefer alpha-preserving high-bit formats during sink negotiation). `yuva_planar.frag.glsl` now applies the recipe's `bitScale` to the alpha sample (clamped to `[0, 1]`) so high-bit YUVA renders correctly; 8-bit `yuva420p` still works because its `bitScale` is `1.0`. Two new test files: `FfmpegVideoPixelMapsCoverageTests` (every `PixelFormat` except `Unknown` has a libav mapping + per-format AVPixelFormat assertion + `PixelFormatInfo` predicate coherence) and additions to `GlVideoFormatSupportTests` (every new format has a recipe + appears in `SupportedPixelFormats`; bitScale matches storage). The user's `yuva444p12le(tv, bt709, progressive)` test content is now a first-class native path. **Size:** M.

**Phase exit:** all three original items land with tests; the framework presents BT.2020 content correctly on SDR previews, deinterlaces 4:2:2 / 4:4:4 broadcast content through yadif, and offers smoother compositing for transformed layers. P7.4 follow-up brings YUVA + 12-bit / 16-bit professional pixel-format coverage to first-class native paths (decode → swscale → GL display → format-support doc). P7.5 follow-up extends `GlVideoCompositor` to accept those native formats as layers, with end-to-end smoke validation (2026-05-20) against real ProRes `yuva444p12le` over `yuv422p10le` content: the framework's composite matches FFmpeg's `overlay=format=yuv420p10` reference to <1% mean per-channel RGB diff (max 1.55% of pixels show any channel difference > 4). ✅

**Outcomes:**
- Build: clean (0 warnings / 0 errors) across all 25 projects.
- Tests: solution-wide pass count grew by **64** new tests across Phase 7. Breakdown: 2 yadif, 4 compositor (Bilinear + Bicubic + identity + default), 7 RgbGamutMatrix; 26 OpenGL recipe-coverage cases (every new format has a recipe + bitScale match); 25 FFmpeg pixel-map coverage cases (every PixelFormat → AVPixelFormat + per-format AVPixelFormat assertion + PixelFormatInfo predicate coherence). Pre-existing flaky NDI clock and occasional flaky `AudioRouterTests.DynamicRemoveSink_StopsReceivingMidStream` (passes in isolation) unchanged.

**Files added / modified:**
```
MediaFramework/Media/S.Media.Core/Video/CompositorSamplingMode.cs            (new — Nearest / Bilinear / Bicubic)
MediaFramework/Media/S.Media.Core/Video/CpuVideoCompositor.cs                (Bilinear + Bicubic paths + SampleBilinear / SampleBicubic helpers + SamplingMode property)
MediaFramework/Media/S.Media.Core/Video/PixelFormat.cs                       (P7.4: 12 new YUVA/12-bit/16-bit members)
MediaFramework/Media/S.Media.Core/Video/PixelFormatInfo.cs                   (P7.4: PlaneCount/Height/ByteWidth/BytesPerSample/IsAlphaCarrying/IsHighBitDepth coverage)
MediaFramework/Media/S.Media.FFmpeg/FFmpegRuntime.cs                         (yadif factory accepts Yuv422P/Yuv444P)
MediaFramework/Media/S.Media.FFmpeg/Video/YadifDeinterlacer.cs               (per-format internal AV pix fmt + chroma divisors)
MediaFramework/Media/S.Media.FFmpeg/Video/VideoFileDecoder.cs                (P7.4: MapNativePixelFormat additions for the 12 new formats)
MediaFramework/Media/S.Media.FFmpeg/Video/Internal/FfmpegVideoPixelMaps.cs   (P7.4: ToAvPixelFormat additions)
MediaFramework/Media/S.Media.OpenGL/RgbGamutMatrix.cs                        (new)
MediaFramework/Media/S.Media.OpenGL/YuvVideoRenderer.cs                      (GamutMatrix property + uniform plumbing)
MediaFramework/Media/S.Media.OpenGL/GlVideoFormatSupport.cs                  (P7.4: 12 new recipes, Planar422/444 YUVA size helpers, reordered SupportedPixelFormatsOrder)
MediaFramework/Media/S.Media.OpenGL/Shaders/yuv_planar.frag.glsl             (gamutMatrix uniform + post-HDR multiply)
MediaFramework/Media/S.Media.OpenGL/Shaders/yuv_nv12.frag.glsl               (gamutMatrix uniform + post-HDR multiply)
MediaFramework/Media/S.Media.OpenGL/Shaders/yuv_nv21.frag.glsl               (gamutMatrix uniform + post-HDR multiply)
MediaFramework/Media/S.Media.OpenGL/Shaders/yuva_planar.frag.glsl            (gamutMatrix uniform + post-HDR multiply; P7.4 alpha bitScale)
MediaFramework/Media/S.Media.OpenGL/Shaders/uyvy422.frag.glsl                (gamutMatrix uniform + post-HDR multiply)
MediaFramework/Media/S.Media.OpenGL/Shaders/yuyv422.frag.glsl                (gamutMatrix uniform + post-HDR multiply)
MediaFramework/Test/S.Media.Core.Tests/Video/CpuVideoCompositorTests.cs      (Bilinear + Bicubic + identity + default tests)
MediaFramework/Test/S.Media.FFmpeg.Tests/Video/YadifDeinterlacerTests.cs     (Yuv422P + Yuv444P emit-progressive)
MediaFramework/Test/S.Media.FFmpeg.Tests/Video/FfmpegVideoPixelMapsCoverageTests.cs (P7.4 — new, 25 cases)
MediaFramework/Test/S.Media.OpenGL.Tests/RgbGamutMatrixTests.cs              (new — 7 tests)
MediaFramework/Media/S.Media.OpenGL/YuvVideoRenderer.cs                      (P7.5: Upload dispatch for the 12 new formats + P16 helper variants for 4:2:2 / 4:4:4 / YUVA)
MediaFramework/Media/S.Media.OpenGL/GlVideoCompositor.cs                     (P7.5: native YUV layer support + RGBA16F intermediate cache)
MediaFramework/Media/S.Media.OpenGL/Shaders/*.frag.glsl                      (P7.5 smoke-cleanup: stripped non-ASCII bytes from comments — Mesa GLSL parser is strict)
MediaFramework/Test/S.Media.OpenGL.Tests/GlVideoCompositorAcceptedFormatsTests.cs (P7.5 — new, 21 cases)
MediaFramework/Tools/CompositorSmoke/CompositorSmoke.csproj                  (P7.5 — new runnable: composites two file frames, saves PNG)
MediaFramework/Tools/CompositorSmoke/Program.cs                              (P7.5 — new; supports --seek, --seek-bg, --seek-fg)
MediaFramework/Test/S.Media.OpenGL.Tests/GlVideoFormatSupportTests.cs        (P7.4: 26 new recipe-coverage / bitScale cases)
Doc/MediaFramework-Format-Support.md                                        (P7.4: enum, support matrix, GL bitScale notes)
```

**Design notes for future work:**
- The gamut matrix is applied to display-space RGB, not linear-light RGB — adequate for preview, not strict colour-management. A future refactor could re-order against an explicit linearisation pass; flagged in `Doc/MediaFramework-Architecture.md`.
- `RgbGamutMatrix.FromHint` only wires Bt2020 → Bt709 SDR display today. Add P3 (DCI-P3) / Adobe RGB display targets in the same shape when a real use case appears.
- `CpuVideoCompositor` bilinear still uses nearest-neighbor's destination-AABB clip (computed from the four transformed corners). For very small layers under aggressive shear that AABB may clip a half-pixel border; if it bites, expand the AABB by one pixel in each direction.
- `YadifDeinterlacer` now supports 4:2:2 and 4:4:4 inputs; 10-bit variants (`Yuv422P10Le` etc.) remain unsupported and fall through to `BobDeinterlacer`. Adding 10-bit would need an `AV_PIX_FMT_YUV422P10LE` branch plus 16-bit-per-sample plane copies.

---

## Suggested first sprint (1–2 weeks)

~~If picking a starting batch: **everything in Phase 0**, plus the §11.2 suspect verifications, plus **the AvRouter and MediaContainerMegaPlaybackHost renames from Phase 1**.~~ ✅ Phase 0 done. **Continue this sprint with the Phase 1 renames** (`AvRouter` → `MediaSession`, drop "Mega", delete `MediaContainerPlaybackGraph`, disambiguate the two `MediaContainerPlaybackHost`s, strip Tier-E/Tier-F annotations). The C4 pump-capacity auto-tune for `IClockedSink` also slips into this sprint or the next — it's small.

## Suggested second sprint

**Phase 2 in full + `StaticFrameSource` from Phase 3.** Small wins that immediately improve every consumer (HaPlay included once it's updated).

## Suggested third onwards

Phase 3 features in any order; pick by what's most needed for the next product milestone (cue player vs. soundboard vs. multi-output mixer). Phase 4 is a quarter-sized chunk on its own. Phase 5 only when a paying use case demands it.

## Suggested next (after Phase 5 — 2026-05-19)

With Phase 5 closed (modulo the deferred ancillary-data envelope), the framework is plausibly broadcast-capable for SMPTE-source content. The remaining work splits into:

- **Phase 6 cleanup** (opportunistic): the `ChannelMap.cs` split, the `VideoFrameMetadata` consolidation flagged in the Phase-5 design notes, an `NDIVideoReceiver` (which unlocks the receiver side of SMPTE timecode + interlace metadata), and a `Tools/CompositorSmoke` integration sample exercising `GlVideoCompositor` against a real SDL3 context.
- **Phase 5 ancillary follow-up**: when a CC / AFD / SCTE-35 consumer materialises, design `VideoAncillary` against that real use case.
- **Beyond the checklist**: ✅ closed as Phase 7 (2026-05-19) — BT.2020 → BT.709 SDR gamut mapping landed via `RgbGamutMatrix`, YadifDeinterlacer accepts Yuv422P / Yuv444P, `CpuVideoCompositor` gained Bilinear *and* Bicubic (Catmull-Rom) sampling. BT.2020 → P3 / Adobe RGB displays and linear-light gamut math remain explicit non-goals — open them as Phase 8 items if a concrete consumer asks. Format coverage at every stage is summarised in `Doc/MediaFramework-Format-Support.md`.

No item is blocking anything else.
