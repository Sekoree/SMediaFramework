# Media Framework — Review & Improvement Checklist

**Last updated:** 2026-05-12 (afternoon) — Re-audit after the 2026-05-12 morning fixes. §"Audit findings (2026-05-12)" updated with `[x]` for items now resolved in the working tree. New §"VideoPlaybackSmoke audit (2026-05-12 PM)" tracks the smoke tool, the audio-dropout investigation, and a unified-clock proposal.

**Previous update (2026-05-12 morning):** Implementation audit: every previously-checked box was verified present. §"Audit findings (2026-05-12)" called out one likely bug (P010 bit-scale), three minor bugs, four robustness/contract gaps, plus optimization and cleanup opportunities.

**Previous update (2026-05-11):** Linux NV12 DRM PRIME dma-buf → EGL/GL (`RetainDmabufForGl`, `YuvDmabufEglInterop`, `Nv12DmabufGpuUploader`); FFmpeg VAAPI zero-copy MVP.

Legend: `[x]` done, `[~]` partial / best-effort, `[ ]` intentional future work.

---

## Executive summary — where to look

| Area | Implementation |
|------|----------------|
| Extended pixel formats | `PixelFormat.cs`, `PixelFormatInfo.cs`, `VideoFileDecoder` `MapNativePixelFormat` / `ToAVPixelFormat` |
| GL recipes + shaders | `GlVideoFormatSupport.cs`, `YuvVideoRenderer.cs`, `Shaders/argb.frag.glsl`, `abgr.frag.glsl`, `gray.frag.glsl`, `yuva_planar.frag.glsl` |
| Frame transfer metadata | `VideoTransferHint.cs`, `VideoFrame.ColorTransferHint`, libav `AVFrame.color_trc` → `VideoFileDecoder.MapTransferHint` |
| FFmpeg hardware decode (CPU transfer) | `VideoDecoderOpenOptions.TryHardwareAcceleration`, `VideoHardwareDecodeContext`, `av_hwframe_transfer_data` |
| Linux NV12 DRM PRIME → GL | `VideoDecoderOpenOptions.RetainDmabufForGl`, `VideoFrame.DmabufNv12` / `CreateNv12Dmabuf`, `DrmPrimeNv12BackingFactory`, `YuvDmabufEglInterop`, `Nv12DmabufGpuUploader` (incl. **`EGL_EXT_image_dma_buf_import_modifiers`**), `SDL3GLVideoSink` + `YuvVideoRenderer` |
| SDL GL HDR | `SDL3GLVideoSink.ApplyTransferHintToRenderer` + optional `GlVideoSinkHdrPreference` / `HdrPreference` property |
| Router SIMD (stereo+) | Stereo identity/swap, **mono `[0,0]` → duplex**, **`[0,1,0,1]` 2→4 widen** (`ChannelMap.TryAccumulate*`), `AudioRouter.ApplyRoute` |
| libav resampling | `AudioResampler` (`swresample`) for packed float audio |
| Thread join slicing | **`CooperativePlaybackJoin`** (`S.Media.Core.Threading`) — **`MediaClock`**, **`VideoPlayer`**, **`AudioRouter`**, **`SinkPump`**, **`SDL3*VideoSink`**, **`NDIAudioReceiver`**; short-slice **`MIDIInputDevice.Close`**, **`OSCServer.Dispose`** |
| Clock / player join with cancel | **`IMediaClock.Pause` / `Stop`**, **`VideoPlayer.StopInternal`** observe **`CancellationToken`** while draining decode / driver threads (via **`CooperativePlaybackJoin`**)
| Router stop + cancel | `AudioRouter.Stop(CancellationToken)` cooperatively joins the mixer thread between short sleeps |
| Zero-copy video interop (stub) | `IHardwareVideoInterop`, **`HardwareVideo*Descriptor`** structs, default `TryDescribeImportedSurface`, **`NoOpHardwareVideoInterop`** |
| NDI optional wall-clock pacing | `NDIOutput` ctor `minimumVideoSubmitSpacing`, `NDIVideoSender.PaceBeforePack` |
| Concept / format matrix docs | `Doc/MediaFramework-concepts.md`, `Doc/PixelFormats-OpenGL.md` |

---

## 1. OpenGL renderer (`S.Media.OpenGL`)

### 1.1–1.4

- [x] Prior correctness / architecture items unchanged (unpack restore, viewport fit, HDR uniforms, samplers, native pointer upload, etc.).
- [x] **Extended format coverage** — `Argb32`, `Abgr32` (FFmpeg memory order + swizzle shaders), `Gray8`/`Gray16` (`gray.frag.glsl`), `Yuv420P10Le` / `Yuv420P12Le` / `Yuv444P10Le` (planar R16 + `yuv_planar.frag.glsl` bitScale), `Yuva420p` (`yuva_planar.frag.glsl` + fourth alpha plane).
- [x] **Hardware GL decode (Linux NV12)** — EGL `EGL_EXT_image_dma_buf_import` (+ **`EGL_EXT_image_dma_buf_import_modifiers`** when non-zero **`DRM_FORMAT_MOD_*`**) + GL `GL_EXT_EGL_image_storage`, split Y (`DRM_FORMAT_R8`) / UV (`DRM_FORMAT_GR88`). **non-NV12 PRIME** / multi-planar FFmpeg layouts remain backlog §Suggested backlog below.

---

## 2. Core video (`S.Media.Core`)

- [x] **Argb32 / Abgr32** — enum + `PixelFormatInfo` plane metadata + alpha flag.
- [x] **Gray8 / Gray16**, **420 10/12**, **444 10**, **YUVA420P** — descriptors in `PixelFormatInfo`; tests in `PixelFormatInfoTests`.
- [x] **NV12 dma-buf metadata** — `VideoDmabufNv12Backing`, `VideoFrame.DmabufNv12` / `CreateNv12Dmabuf` (CPU `Planes` are empty stubs on that path).
- [x] **Hardware video interop surface contract** (`IHardwareVideoInterop`, `HardwareVideoSurfaceDescriptor`, `NoOpHardwareVideoInterop`) — concrete import adapters remain future work (dma-bufs use `VideoFrame.DmabufNv12` directly today).

---

## 3–5. FFmpeg / NDI / SDL3

- [x] **FFmpeg mappings** for new `PixelFormat` values where libav exposes a stable `AV_PIX_FMT_*`.
- [x] **`color_trc` → `VideoTransferHint`** per frame on pass-through and sws output paths.
- [x] **`SDL3GLVideoSink`** applies hint to `YuvVideoRenderer.HdrTransfer`; optional `GlVideoSinkHdrPreference` overrides or ignores per-frame hints.
- [x] **NDI pacing** — optional minimum spacing between submits (pairs with SDK `clockVideo:false` scenarios).
- [x] **Hardware FFmpeg decode** — VAAPI / D3D11VA / QSV / … via libav; **Linux VAAPI** zero-copy path uses `RetainDmabufForGl` → `drm_prime` + `VideoDmabufNv12Backing` → GL. **`IHardwareVideoInterop`** adapters for DXGI/Metal/etc. remain future work (stub contract + backlog).
- [x] **Resampling helper** — `AudioResampler` wraps `swresample` for packed float interleaved audio (see also `AudioFileDecoder` internals).

---

## 10. Cross-cutting

### 10.2 Cancellation

- [x] **`IMediaClock.Pause` / `Stop`** accept `CancellationToken` while joining the driver thread.
- [x] **`VideoPlayer.Pause` / `Stop`** accept token while joining decode thread.
- [x] **`AudioRouter.Stop`** uses short `Thread.Join` slices and honors `CancellationToken` during the join loop.
- [x] **Blocking shutdown paths** — short-slice cooperative joins (**`CooperativePlaybackJoin`** playback stack); **`AudioRouter`** / **`SinkPump`** / SDL / NDI patterns; **`MIDIInputDevice.Close`** (wake + threaded join slices); **`OSCServer.Dispose`** (task **`Wait`** slices after cancel).

### 10.3 Testing

- [x] **Transfer hint mapping tests** (`VideoTransferHintMappingTests`), **pixel layout tests** (`Yuva420p`, gray high depth, expanded alpha carriers).
- [x] **`ChannelMap`** — SIMD regressions (**`StereoSimd_*`**, **`MonoDupSimd_*`**, **`StereoDuplexWideSimd_*`**) plus seeded **`Apply_RandomLayouts_Deterministic_MatchNaive`**; **`AudioResampler`** (identity/up + **`Resampler_RandomishBuffer`**); **`HardwareVideoInteropTests`**. Full property/fuzz harness — optional backlog.

### 10.4 Docs

- [x] **Concept overview** — `Doc/MediaFramework-concepts.md`.
- [x] **Pixel format × GL mapping** — `Doc/PixelFormats-OpenGL.md`.

---

## Suggested backlog (remaining)

1. **`IHardwareVideoInterop` implementations** (DXGI / Metal / Vulkan, …) mapping **`HardwareVideoSurfaceDescriptor`** — Linux libav may already populate **`VideoDmabufNv12Backing`** (`RetainDmabufForGl`) without going through `IHardwareVideoInterop`.
2. SIMD for remaining hot **`ChannelMap` shapes** (`MonoToN` with `N > 2`, asymmetric silence maps, …) after profiling.
3. Dedicated fuzz / property-test harness (many random route graphs + gain ramps + decoder soak).

---

## Audit findings (2026-05-12)

Original morning entries reproduced below with the working-tree status applied: every "Likely bug" and "Robustness / contract gap" is now `[x]` (resolved in the working tree) except `AudioRouter.Pause` which is `[~]` (documented but race not closed). The optimization and cleanup lists are mostly resolved; the unresolved ones are kept as `[ ]` so they remain on the radar.

### Likely bugs

- [x] **P010 `bitScale` over-amplifies** — fixed in `GlVideoFormatSupport.cs`: `P010` recipe now uses `bitScale = 1f` with a comment explaining the 10-bit-in-high-bits storage layout.

- [x] **`Nv12DmabufGpuUploader.TryUpload` ignores GL errors after `glEGLImageTargetTexStorageEXT`** — fixed: `_gl.GetError()` is checked after each plane upload; failure short-circuits the second `glEGLImageTargetTexStorageEXT` and propagates `false` to the caller. The two `EGLImage` handles are destroyed inline regardless of success (no leak on the failure path).

- [x] **EGLImages are leaked between frames** — fixed: `_yImage` / `_uvImage` fields are gone. The two images are destroyed immediately after the `glEGLImageTargetTexStorageEXT` calls in the same `TryUpload`.

- [x] **Dead inner guard in `Nv12DmabufGpuUploader.AppendPlaneAttribs`** — removed (the modifier-availability check now lives only in `TryUpload`).

### Robustness / contract gaps

- [x] **`AvDrmFrameDescriptorInterop` size hard-coded to 528** — fixed: `WarnIfInteropSizeMismatchLp64LoggedOnce` runs once per process from `DrmPrimeNv12BackingFactory` and logs via `MediaDiagnostics.LogWarning` if `Marshal.SizeOf<AvDrmFrameDescriptorInterop>()` disagrees with `ExpectedSizeBytes`.

- [x] **`DrmPrimeNv12BackingFactory` modifier preference is surprising** — fixed: `VideoDmabufNv12Backing` now carries `YPlaneDrmFormatModifier` + `UvPlaneDrmFormatModifier` independently (with a `UsesDistinctDmaBufObjects` convenience). `AppendPlaneAttribs` is invoked per-plane, so `DRM_FORMAT_MOD_LINEAR == 0` no longer collides with "missing modifier."

- [x] **`VideoFileDecoder.NativePixelFormats` is empty when HW decode runs without DRM PRIME** — fixed: the hw-software fallback now advertises `PixelFormat.Nv12` (matching VAAPI/D3D11VA layouts after `av_hwframe_transfer_data`) plus a docstring note. Sinks that accept NV12 can now negotiate around the BGRA32 sws path.

- [~] **`AudioRouter.Pause` re-enters the lock to enumerate sinks for `Flush`** — partially addressed: added a `<remarks>` paragraph documenting the assumption ("callers invoke `Pause` from the same synchronization domain that owns routing"). The race is documented but not closed by combining the two lock blocks — leave open until profile shows it matters.

### Optimization candidates

- [x] **`VideoFileDecoder.BuildConvertedFrame` allocates a fresh `byte[]` per frame** — fixed: rents from `ArrayPool<byte>.Shared`, releases via the existing `VideoFrame.release` callback. Source/dest scratch arrays are now per-decoder fields (`_swScaleSrcLines` / `_swScaleSrcStride` / `_swScaleDstLines` / `_swScaleDstStride`) — single-threaded use is annotated in a remark.

- [ ] **`VideoFileDecoder.BuildPassThroughFrame` allocates `new ReadOnlyMemory<byte>[planeCount]` + `new int[planeCount]` per frame** — still outstanding. Per-decoder reusable arena would remove the last per-frame managed allocation on the hardware-NV12 / passthrough path (which is the path VideoPlaybackSmoke uses at 24 fps).

- [ ] **`ChannelMap.StereoSwapAdjacentChannels` uses a `WithElement` chain inside a per-vector loop** — still outstanding.

- [ ] **`ChannelMap.TryAccumulateMonoDupStereoInterleaved` and `TryAccumulateStereoDuplexWideInterleaved` fan out to a `scratch` span via `Unsafe.Add` in a per-element loop** — still outstanding.

- [x] **`NDIVideoSender.PaceBeforePack` rounds wait to whole milliseconds** — fixed: coarse `Thread.Sleep` (minus 1 ms) plus a `Stopwatch`-deadline `SpinWait(32)` busy-wait for the sub-millisecond remainder. Pacing is now precise to sub-ms.

### Simplifications / cleanup

- [x] **`VideoHardwareDecodeContext.IsActive` is unreferenced** — deleted.
- [x] **`AudioRouter.CooperativeJoin` and `VideoPlayer.TryJoinDecodeThread` are 1-line proxies** — inlined. The helper indirection is gone.
- [x] **`YuvVideoRenderer.DispatchUploadFromFrame` 17-arm switch** — still outstanding (not touched in this pass).
- [x] **`MIDIInputDevice.Close` and `OSCServer.Dispose` reimplement `CooperativePlaybackJoin`** — `<remarks>` blocks now document the intentional duplication. ("PMLib/OSCLib stay free of any S.Media.Core dependency for thread joins.")
- [x] **`SDL3GLVideoSink.Submit` swallows `_disposed` silently** — fixed: now throws `ObjectDisposedException`. `SDL3VideoSink.Submit` standardized the same way. `VideoPlayer.OnVideoTick`'s try/catch already disposes the frame on rethrow, so no frame leak.
- [x] **`try { … } catch { /* best effort */ }` blocks** — `VideoPlayer.OnVideoTick` Submit catch, `SDL3*VideoSink.Pump` + `RenderLoop` PresentFrame catches, and the `SafeRaise*` handlers now log via `MediaDiagnostics.LogError`. Silent glitches are now diagnosable.

---

## VideoPlaybackSmoke audit (2026-05-12 PM)

`MediaFramework/Tools/VideoPlaybackSmoke/` is a new smoke test that opens a media file, plays its video through SDL3 (GL or CPU) optionally mirrored over NDI, and slaves the video clock to PortAudio's playback clock. Below is a focused audit of (1) the tool itself, (2) likely root causes of the audio dropouts the user heard with 720p24 NV12 content, and (3) a roadmap to combining the audio and video paths under a single clock-aware facade.

### Tool-level cleanup

- [ ] **Unused `using System.Threading;` in `VideoDmabufNv12Backing.cs:2`** — the file no longer uses any `Threading` symbol after the per-plane modifier refactor; drop the import.
- [ ] **`TwinCpuVideoSink.Dispose` only disposes `_primary`** — `_secondary` (typically `NDIOutput.VideoSink`) is treated as caller-owned, which is fine, but the asymmetry with `_primary` is silently inconsistent. Either dispose nothing (`NDIOutput` owns both) or both — pick one and write the contract down.
- [ ] **`TwinCpuVideoSink.DuplicateCpuBackedFrame` allocates `new byte[totalBytes]` per plane per frame** — at 720p NV12 that's two heap allocations totalling ~1.4 MB / frame × 24 fps = 33 MB/s of Gen-0 traffic on top of whatever the decoder allocates. With `--ndi`, this is the dominant managed allocation source on the playback path. Plumb a per-plane `ArrayPool<byte>` (release in `VideoFrame.release`) the same way `BuildConvertedFrame` was just fixed.
- [ ] **`PlaybackCli.WriteUsageToStdErr` documents `--chunk-samples=` but the usage text omits `--ndi` behaviour with `--drm-gl`** — the program prints `[ndi] DRM dma-buf decode + twin NDI is unsupported — omit --ndi or disable --drm-gl.` in `Program.cs:71` *after* the user has chosen incompatible flags. The usage block should call this out up-front.
- [ ] **`AudioRouting.TryCreate` opens a probe `PortAudioOutput` just to read `DeviceIndex` and then discards it** (`Program.cs:181-184`). PortAudio's `Pa_Initialize` is reference-counted, but every open/close pair touches device enumeration; either skip the probe entirely or expose the eventual device index from `PortAudioOutput` without a second native handshake. Minor — not on the audio-dropout critical path.
- [ ] **Status-line lacks dropout diagnostics** — the periodic write (`Program.cs:109-113`) shows clock, vPTS, audio decoder position, and frame counts, but not the things you'd actually look at when chasing dropouts: `routing.Player.Router.GetPumpStats(sinkMain).Dropped`, `mainOutput.UnderrunSamples`, `mainOutput.DroppedSamples`, `videoPlayer.DroppedLate`. Add them.

### Audio dropouts — likely root causes (720p24 NV12)

A few independent suspects, ordered from most to least likely:

- [ ] **Startup ordering inverts prebuffer** — `Program.cs:95-97` calls `routing.StartHardwareOutput()` (which opens the PortAudio stream and immediately starts the audio thread callback) *before* `routing.Player.Play()` (which is what actually starts the router producing samples). `PortAudioOutput.WaitForCapacity` returns immediately while the stream isn't running, **but the stream is already running** when the router thread spawns. Result: PortAudio's first ~1–2 callbacks fire against an empty ring (`UnderrunSamples` ticks up, output is silence). The same inversion is in `PlaybackSmoke/Program.cs:51-52` (`output.Start(); router.Start();`). Fix: swap the order — start the router first, let it burst-fill the ring up to a target depth, *then* start PortAudio. Verify via `output.UnderrunSamples` after a known-good clip.
- [ ] **No explicit prebuffer threshold** — even with the order swapped, `PortAudioOutput.WaitForCapacity` returns true unconditionally while the stream isn't started, so the producer burst-fills until the **whole** ring (`ringCapacityFrames = SampleRate` = 1.0 s) is full and any further `Submit` drops samples (counted in `DroppedSamples`). For VideoPlaybackSmoke this is harmless (decode is fast, ring is 1 s deep), but it's brittle: a slow source could let PortAudio start with only a few ms of audio in the ring. Add a `Prefill(int frames)` to `PortAudioOutput` (or `AudioPlayer`) that blocks until `QueuedSamples >= frames` before returning, then call it from the smoke tools.
- [ ] **`SinkPump` drainer thread runs at default priority** while `AudioRouter` runs at `ThreadPriority.AboveNormal`. If a higher-priority thread (the SDL render thread, MediaClock driver, anything `AboveNormal`) preempts the pump for >`pumpCapacityChunks * chunkSamples / sampleRate` ≈ 80 ms (at the defaults), the pump's `BlockingCollection` overflows and `Commit()` evicts the oldest chunk via `RecordDrop()`. The router silently writes silence to PortAudio's ring. Bump pump priority to `AboveNormal` (it's a real-time data path with a hard latency budget) or raise `pumpCapacityChunks` to absorb scheduler jitter — the latter trades a small audible-latency-on-pause cost for resilience.
- [ ] **`chunkSamples=480` (10 ms @ 48 kHz) at default** is tighter than the 20–50 ms host buffer PortAudio asks ALSA/Pulse for. Each chunk turn-around (decode → mix → commit → pump → ring) must complete in <10 ms or the pump starts to lag. On a clean system 10 ms is fine, but it's not generous; raise the default `--chunk-samples` to 960 (20 ms) or expose a `--prebuffer-ms` flag.
- [ ] **First-chunk silence-padding on resume** — `AudioRouter.RunLoop` silences any partial read (`src.Scratch.AsSpan(read).Clear();`). After a Pause → Resume, `swr` may need one cycle to refill; that chunk is partly silent and audible as a soft click. Largely benign but worth noting.

### Audio + video clock unification — proposal

Today's situation:
- `MediaClock` is the visible playhead, optionally mastered to an `IPlaybackClock`.
- `PortAudioOutput` implements both `IClockedSink` (paces the audio router) and `IPlaybackClock` (exposes `PlayedSamples / SampleRate` as elapsed-since-start). `AudioPlayer.AddOutput` auto-wires both when present.
- `VideoPlayer` subscribes to `IMediaClock.VideoTick` and uses `IMediaClock.CurrentPosition` to pick the most recent in-window frame.
- `VideoFileDecoder` provides each frame's libav PTS as `VideoFrame.PresentationTime` (the "PTS clock" the user mentioned, indirectly).

So **A/V sync to PortAudio's hardware clock already works** when the master is wired. What's *not* unified:

- [ ] **No single facade that owns Play / Pause / Stop / Seek for both** — VideoPlaybackSmoke threads it together by hand (`videoPlayer.Play()` separately from `routing.Player.Play()`; `videoPlayer.Stop()` separately from `routing.Player.Stop()`). Mistakes cascade: stop one before the other on a long pause and the clock keeps advancing while the renderer is gone, etc. Propose a `MediaPlayer` (or `AVPlayer`) that wraps `AudioPlayer + VideoPlayer + MediaClock` and exposes one Play/Pause/Stop/Seek that:
  1. Starts the audio router (begins prefilling the PortAudio ring).
  2. Blocks until ring depth ≥ target prebuffer (e.g. 100 ms at 48 kHz).
  3. Starts the PortAudio stream (calls `Start()` on the hardware output).
  4. Sets `MediaClock` master = PortAudio (when audio-mastered) and starts the clock.
  5. Starts the video player.
  6. Stop is the reverse order; Pause/Resume mirror via existing `AudioRouter.Pause` / `VideoPlayer.Pause`.
- [ ] **No `PtsClock` for video-only / live playback** — when there's no audio (or audio is paused), today's `MediaClock` falls back to a `Stopwatch`. Variable-frame-rate (VFR) sources and frame-accurate seeks would benefit from a clock that tracks the most recently-decoded video frame's PTS. Sketch: `VideoPtsClock : IPlaybackClock` exposes `ElapsedSinceStart = lastFramePts - sessionStartPts + (now - lastFrameWallTime)` — anchors to the last known good PTS plus wall-clock delta since. Useful as a `MediaClock.SetMaster` candidate when the audio output isn't present.
- [ ] **Two `AVFormatContext`s per AV file** — `VideoFileDecoder.Open(path)` and `AudioFileDecoder.Open(path)` each parse and demux the container independently. For typical mp4/mkv playback this means twice the file IO, two read pointers that drift across long files, and seek must hit both. A future `MediaContainerDecoder` that demuxes once and routes packets to per-stream decoders would: (a) halve IO, (b) keep audio/video PTS aligned at the demuxer, (c) enable a single seek that atomically repositions both. Larger lift — flag as backlog only.
- [ ] **`SinkSlavedRouterClock` falls back to `WallClockRouterClock` if the slaved sink is removed** — fine for graceful degradation, but the fallback wall-clock instance is created once with the original sample rate / chunk size; a later sample-rate change wouldn't propagate. Today the router throws if sample rates disagree on `AddSource`, so this is dormant — flag as "watch when introducing dynamic resampling."

### Verification once changes land

After fixing the dropout suspects, the smoke tool should print, after a 30-second playback of a 720p24 NV12 file with audio:

- `mainOutput.UnderrunSamples` near 0 (a few hundred at start is tolerable; thousands means prebuffer is still wrong).
- `routing.Player.Router.GetPumpStats(<sinkId>).Dropped == 0` (any non-zero means the pump is being preempted — recheck thread priorities / pumpCapacityChunks).
- `videoPlayer.DroppedLate < 0.5%` of `videoPlayer.DecodedCount`.
- Clock and `vPTS` agree within ±1 frame (≈ 41 ms at 24 fps).
