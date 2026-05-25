# MediaFramework — format support

Snapshot of which pixel formats and audio formats the framework supports directly at each stage of the pipeline. "Directly" means the type accepts the format without an intermediate conversion. Generated 2026-05-19; verify against the code if anything looks off (the source-of-truth is the enum in `S.Media.Core/Video/PixelFormat.cs` and each component's accepted-format table).

---

## Video pixel formats

### The format enum

`S.Media.Core.Video.PixelFormat` enumerates every layout the framework can talk about. Anything missing from this enum is unsupported end-to-end — add it here first.

| Group | Members | Notes |
|---|---|---|
| Packed RGB 24-bit | `Bgr24`, `Rgb24` | No alpha; native byte order. |
| Packed RGBA 32-bit | `Bgra32`, `Rgba32`, `Argb32`, `Abgr32` | Component order matches the name's byte order in memory. `Bgra32` is the framework's "neutral" RGBA. |
| Packed RGBA 64-bit | `Rgba16`, `Rgba16F` | 16-bit per channel LE (`Rgba16` integer, `Rgba16F` half-float where supported). Phase 10. |
| Planar 4:2:0 (8-bit) | `I420`, `Yv12`, `Nv12` (semi), `Nv21` (semi) | Y full size, U/V half-W half-H. Nv12 = interleaved UV; Nv21 = interleaved VU. |
| Planar 4:2:2 (8-bit) | `Yuv422P` | Y full, U/V half-W full-H. |
| Planar 4:4:4 (8-bit) | `Yuv444P` | Y full, U/V full-W full-H. |
| Packed 4:2:2 | `Uyvy`, `Yuyv` | 2 bytes per pixel, chroma shared per horizontal pair. |
| Planar 4:2:0 (10/12-bit LE) | `Yuv420P10Le`, `Yuv420P12Le` | Each sample stored in a 16-bit LE word; only the low 10 / 12 bits are valid. |
| Planar 4:2:2 (10/12-bit LE) | `Yuv422P10Le`, `Yuv422P12Le` | E.g. ProRes 422 (10-bit), ProRes 4444 XQ-style 12-bit. |
| Planar 4:4:4 (10/12-bit LE) | `Yuv444P10Le`, `Yuv444P12Le` | High-fidelity broadcast / HEVC 4:4:4. |
| Semi-planar 4:2:0 (10/16-bit LE) | `P010`, `P016` | NV12 layout, 16-bit words. P010 valid bits in the MSB-aligned high 10. |
| Semi-planar 4:2:2 (16-bit LE) | `P216` | 4:2:2 NV16-style; two 16-bit planes (Y + interleaved UV). Phase 10. |
| Semi-planar 4:2:2 + alpha (16-bit LE) | `Pa16` | `P216` + full-res 16-bit alpha plane. Phase 10. |
| Alpha-bearing planar (8-bit) | `Yuva420p`, `Yuva422P`, `Yuva444P` | Y/U/V at the given subsampling + full-resolution 8-bit alpha plane. |
| Alpha-bearing planar (10-bit LE) | `Yuva420P10Le`, `Yuva422P10Le`, `Yuva444P10Le` | 4 planes; chroma and alpha at the chroma subsampling, all stored in 16-bit LE words with 10 valid bits. |
| Alpha-bearing planar (12-bit LE) | `Yuva422P12Le`, `Yuva444P12Le` | Common in HEVC 4:4:4 12-bit + alpha pipelines (e.g. `yuva444p12le(tv, bt709, progressive)`). libav has no `YUVA420P12LE` so the 4:2:0 12-bit YUVA variant is intentionally absent. |
| Alpha-bearing planar (16-bit LE) | `Yuva420P16Le`, `Yuva422P16Le`, `Yuva444P16Le` | Full 16-bit-per-sample alpha + YUV; rare but maps directly to libav. |
| Single-plane luminance | `Gray8`, `Gray16` | `Gray16` is LE 16-bit per pixel. |

### Direct support matrix

Legend: ✓ direct; – not handled (no conversion path inside that component — caller must pre-convert via `IVideoCpuFrameConverter` / `swscale`).

| PixelFormat | FFmpeg decode | swscale convert | Yadif deint. | Bob deint. | GL display | CPU compositor (layer in) | NDI sender |
|---|:---:|:---:|:---:|:---:|:---:|:---:|:---:|
| `Bgra32`            | ✓ | ✓ | – | ✓ | ✓ | ✓ | ✓ |
| `Rgba32`            | ✓ | ✓ | – | ✓ | ✓ | – | ✓ |
| `Rgba16`            | ✓ | ✓ | – | – | ✓ | – | – |
| `Rgba16F`           | ✓ | ✓ | – | – | ✓ | – | – |
| `Bgr24`             | ✓ | ✓ | – | – | ✓ | – | – |
| `Rgb24`             | ✓ | ✓ | – | – | ✓ | – | – |
| `Argb32`            | ✓ | ✓ | – | – | ✓ | – | – |
| `Abgr32`            | ✓ | ✓ | – | – | ✓ | – | – |
| `I420`              | ✓ | ✓ | ✓ | ✓ | ✓ | – | ✓ |
| `Yv12`              | ✓ | ✓ | – | – | ✓ | – | – |
| `Nv12`              | ✓ | ✓ | ✓ | ✓ | ✓ | – | ✓ |
| `Nv21`              | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuv422P`           | ✓ | ✓ | ✓ (Phase 7) | – | ✓ | – | – |
| `Yuv444P`           | ✓ | ✓ | ✓ (Phase 7) | – | ✓ | – | – |
| `Yuv422P10Le`       | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuv422P12Le`       | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuv420P10Le`       | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuv420P12Le`       | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuv444P10Le`       | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuv444P12Le`       | ✓ | ✓ | – | – | ✓ | – | – |
| `Uyvy`              | ✓ | ✓ | – | – | ✓ | – | ✓ |
| `Yuyv`              | ✓ | ✓ | – | – | ✓ | – | – |
| `P010`              | ✓ | ✓ | – | – | ✓ | – | – |
| `P016`              | ✓ | ✓ | – | – | ✓ | – | – |
| `P216`              | ✓ | ✓ | – | – | ✓ | – | – |
| `Pa16`              | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuva420p`          | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuva422P`          | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuva444P`          | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuva420P10Le`      | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuva422P10Le`      | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuva444P10Le`      | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuva422P12Le`      | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuva444P12Le`      | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuva420P16Le`      | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuva422P16Le`      | ✓ | ✓ | – | – | ✓ | – | – |
| `Yuva444P16Le`      | ✓ | ✓ | – | – | ✓ | – | – |
| `Gray8`             | ✓ | ✓ | – | – | ✓ | – | – |
| `Gray16`            | ✓ | ✓ | – | – | ✓ | – | – |

Notes:

- **FFmpeg decode**: the FFmpeg `AVPixelFormat` for each is wired in `S.Media.FFmpeg/Video/Internal/FfmpegVideoPixelMaps.cs`. Decoders that produce a format outside this map will throw at frame-build time.
- **swscale convert**: `S.Media.FFmpeg.Video.VideoCpuFrameConverter` (and the demux / decoder swscale paths in `MediaContainerSharedDemux` / `VideoFileDecoder`) all use **`SWS_BICUBIC`** for resize and conversion. Format coverage is whatever swscale itself accepts in your FFmpeg build (typically every entry in the table above).
- **Yadif deinterlacer**: `S.Media.FFmpeg.Video.YadifDeinterlacer` accepts I420, Nv12, Yuv422P, Yuv444P. Other interlaced formats fall through to `BobDeinterlacer` via the `VideoDeinterlacerRegistry.Factory`.
- **Bob deinterlacer**: `S.Media.Core.Video.BobDeinterlacer` covers BGRA32, RGBA32, I420, NV12. Same registry forwards unsupported formats to the bob fallback; if neither yadif nor bob accepts the format, deinterlace is unavailable and the frame stays interlaced.
- **GL display**: `S.Media.OpenGL.GlVideoFormatSupport` declares a recipe per format covering shader selection + texture upload. Every enum member has a recipe; the renderer picks the matching fragment shader (`yuv_planar`, `yuv_nv12`, `yuv_nv21`, `yuva_planar`, `uyvy422`, `yuyv422`, `bgra`, `rgba`, `gray`, etc.). High-bit-depth YUV / YUVA share their per-family shader — texture-side storage uses `R16` with the matching `bitScale` uniform (`65535 / 1023` for 10-bit, `65535 / 4095` for 12-bit, `1.0` for 16-bit) so the shader sees normalized `[0, 1]` samples regardless of underlying bit depth. The YUVA shader applies the same scale to the alpha plane so high-bit YUVA renders correctly.
- **CPU compositor**: `S.Media.Core.Video.CpuVideoCompositor` accepts BGRA32 layers only. Other formats must be CPU-converted upstream — Phase 4 design note.
- **GL compositor**: `S.Media.OpenGL.GlVideoCompositor` accepts **every format `YuvVideoRenderer` supports** as a layer (Phase 7.5). BGRA32 layers run a direct RGBA8 upload; every other format runs a per-layer YUV → RGB pre-pass into a cached RGBA16F intermediate texture so 10 / 12 / 16-bit precision survives through the blend stage. The composite shader samples the intermediate identically to a BGRA32 layer. Caches are keyed: BGRA32 textures by `(W, H)`; YUV intermediate textures + FBOs by `(W, H)` (one per size, reused across formats); YUV renderers by `(PixelFormat, W, H)`. Output stays RGBA8 BGRA32 via `glReadPixels`. Typical use case: `yuva444p12le` foreground over `yuv422p10le` background with the alpha properly applied — both layers native, no upstream BGRA32 conversion required.

### Compositor known limitations

| Limitation | Where | Workaround |
|---|---|---|
| Output is always RGBA8 BGRA32 (single 8-bit step at `glReadPixels`). Chained `compositor -> compositor` truncates at each hop. | `GlVideoCompositor.Composite` final readback. | Single-stage composites are fine for any source bit depth; if you genuinely chain composites and need to preserve > 8-bit through, opt into RGBA16F output (not implemented — flagged as a future Phase-8 candidate). |
| `CpuVideoCompositor` is still BGRA32 layers only — Phase 7.5 only touched the GL path. | `S.Media.Core.Video.CpuVideoCompositor`. | Use the GL compositor for native YUV/YUVA layers. Software composite still requires a `VideoCpuFrameConverter` pre-pass to BGRA32 (lossy at 10/12/16-bit sources). |
| No headless GL test infrastructure — `GlVideoCompositor` is verified by static accepted-format tests + a runnable smoke (`Tools/CompositorSmoke`); the actual GPU pipeline isn't exercised in CI. | `MediaFramework/Test/S.Media.OpenGL.Tests/`. | Run `dotnet run --project MediaFramework/Tools/CompositorSmoke -- --background <bg> --foreground <fg> --out out.png [--seek <s>]` to verify end-to-end behaviour on real content. |
| Per-`(W, H)` intermediate texture cache thrashes if layer dimensions change every frame. | `_yuvIntermediates` in `GlVideoCompositor`. | Steady-state UI / typical playback is fine; if dynamic sizing becomes a workload, swap in an LRU eviction policy. |
| No automatic gamut mapping inside the composite pipeline — each layer's `YuvVideoRenderer.GamutMatrix` defaults to `Identity`. | `GlVideoCompositor.PrepareYuvLayerIntermediate`. | BT.2020 sources keep their primaries through the composite; if you need a BT.2020 -> BT.709 SDR display preview, apply `RgbGamutMatrix.Bt2020ToBt709` to whichever stage drives the final display (typically a `YuvVideoRenderer` wrapping the compositor's BGRA32 output is not the right path because the gamut info has already been baked — apply per-layer instead). |
| Shader source must be plain ASCII. Some GL drivers (notably Mesa) reject non-ASCII bytes in shader comments with confusing "unexpected end of file" errors. | All `*.glsl` files under `Shaders/`. | Use ASCII-only characters in shader source (the YUV / composite shaders ship clean today; a CI check is on the future-work list). When in doubt, run the runnable smoke against your driver — `CompileShader` writes the failing source to `/tmp/mfplayer-failed-<type>.glsl` so you can validate it with `glslangValidator`. |
- **NDI sender**: `S.Media.NDI.Video.NDIVideoSender` accepts BGRA32, RGBA32, UYVY, Nv12, I420. Other source formats need a `VideoCpuFrameConverter` branch upstream.

### Hardware-backed frames

Beyond the CPU-plane formats above, the framework wires four zero-copy hardware backings used by the FFmpeg decoder when hardware acceleration is enabled:

| Backing | Platform | Format | Notes |
|---|---|---|---|
| `VideoDmabufNv12Backing` | Linux | NV12 (8-bit) | Imported via EGL; DMA-BUF FDs. |
| `VideoDmabufP010Backing` | Linux | P010 (10-bit) | Same EGL import; 16-bit words. |
| `VideoDmabufP016Backing` | Linux | P016 (16-bit) | Same EGL import; full 16-bit words. |
| `VideoWin32Nv12Backing` | Windows | NV12 (8-bit) | NT shared handles + optional borrowed COM pointers on the libav decode device. |

All four are ref-counted via the CAS-loop pattern shared across backings (`AddReference` / `Dispose`).

### Scaling kernels

Where the framework re-samples pixels:

| Stage | Kernel | Configurable |
|---|---|---|
| `VideoFileDecoder` resize (swscale) | bicubic | No — `SwsResizeFilter = SWS_BICUBIC` constant. |
| `VideoCpuFrameConverter` (swscale) | bicubic | No. |
| `MediaContainerSharedDemux` swscale paths | bicubic | No. |
| `YuvVideoRenderer` (OpenGL) | bicubic (default) / nearest (recipe override) | `GlVideoFormatSupport.GlFormatRecipe.NearestSampling`. |
| `CpuVideoCompositor` | nearest / bilinear / bicubic (Catmull-Rom) | `CompositorSamplingMode` enum on the compositor (default `Nearest`). |
| `BobDeinterlacer` line interpolation | linear (between rows) | No. |

### Color space and HDR transfer

`S.Media.Core.Video.VideoColorSpace` carries the per-frame YUV → RGB matrix hint:

- `Bt709` (HD), `Bt601` (SD), `Bt2020` (UHD non-constant-luminance), `Bt2020Cl` (constant-luminance variant), `Unspecified` (renderer picks by height — BT.709 if ≥ 720p, BT.601 otherwise).
- Range: `VideoColorRange.Limited` (16/235 Y, 16/240 C) or `Full` (0/255).
- Matrices in `S.Media.OpenGL.YuvColorSpace`: `Bt709Limited`, `Bt709Full`, `Bt601Limited`, `Bt2020Limited`, `Bt2020Full`, plus `FromHint(...)` picker.

HDR transfer functions (for SDR preview tone-mapping):

- `S.Media.Core.Video.VideoHdrTransfer`: `None`, `Srgb`, `Pq` (BT.2100 PQ — Dolby Vision / HDR10), `Hlg` (BT.2100 HLG — broadcast HDR).
- Applied in every YUV shader's `hdrPreviewAfterMatrix` step. Caller controls strength via `YuvVideoRenderer.HdrPreviewExposure`.

RGB → RGB gamut mapping (BT.2020 → BT.709 SDR display preview):

- `S.Media.OpenGL.RgbGamutMatrix.Bt2020ToBt709` (ITU-R BT.2087 matrix).
- Settable via `YuvVideoRenderer.GamutMatrix`; defaults to `Identity`.
- See [Phase 7 — P7.3 in the checklist](MediaFramework-Checklist-2026-05.md#phase-7--beyond-the-checklist-polish--complete-2026-05-19-33).

### SMPTE timecode and field order

Optional `VideoFrameMetadata` carries `Timecode` (SMPTE LTC / S12M with drop-frame math) and `FieldOrder` (`Progressive`, `TopFieldFirst`, `BottomFieldFirst`). FFmpeg extracts these from `AV_FRAME_DATA_S12M_TIMECODE` side data + `AV_FRAME_FLAG_INTERLACED` / `AV_FRAME_FLAG_TOP_FIELD_FIRST`. `NDIVideoSender.SmpteFromFrame` mode re-encodes the timecode into the NDI sender slot.

---

## Audio formats

### Internal representation

The framework's mixer rail is **packed (interleaved) 32-bit float** (`float` / `System.Single`, IEEE 754). Every source / output declares its `AudioFormat` and the router fans samples between them. There is no internal multi-sample-format pipeline — sources convert at their boundary; outputs consume float directly.

| Aspect | Type / value |
|---|---|
| Sample type | `float` (IEEE 754 single-precision, 32-bit) |
| Layout | Packed / interleaved (`L R L R L R …` for stereo) |
| Carried by | `S.Media.Core.Audio.AudioFormat(int SampleRate, int Channels)` |
| Sentinel | `AudioFormat(0, 0)` ≡ `default(AudioFormat)` — "no audio". Guard reads with `MediaContainerDecoder.HasAudio`; `AudioFormat.Validate` rejects the sentinel at any live-pipeline entry point. |

### Sample rates

Any positive `int` is accepted. The router and players validate `> 0` at every public surface (`AudioFormat.Validate`). Typical:

| Rate | Common use |
|---|---|
| 44 100 Hz | CD audio, legacy consumer |
| 48 000 Hz | Broadcast, DAW, NDI default |
| 88 200 Hz | Studio masters |
| 96 000 Hz | Studio masters, high-rate captures |
| 176 400 Hz / 192 000 Hz | Archival / measurement |

Cross-rate routing is handled via `S.Media.FFmpeg.Audio.ResamplingAudioSource` (wraps the inner source in a libswresample resampler) and `ResamplingAudioOutput` (output side). The router supports `AddSource(..., autoResample: true)` to auto-wrap a mismatched-rate source via the FFmpeg factory installed by `FFmpegRuntime.EnsureInitialized`.

`AudioRouter.ReconfigureSampleRate` / `ReconfigureSampleRateWhileRunning` change the router's nominal Hz after every registered source and output already reports the new rate.

### Channel counts and layouts

Any positive `int` is accepted. The framework does not enforce a fixed set of layouts — `ChannelMap` is the explicit mapping table from source channels to output channels (route-specific). Common per-format channel counts:

| Channels | Typical layout |
|---|---|
| 1 | Mono |
| 2 | Stereo (L R) |
| 3 | LCR / 2.1 |
| 4 | Quad / LCRS |
| 6 | 5.1 surround (L R C LFE LS RS) |
| 8 | 7.1 surround (L R C LFE LS RS LB RB) |

Higher-order ambisonics or unusual layouts work mechanically through `ChannelMap` — define the coefficient matrix; the router applies it per chunk.

`S.Media.Core.Audio.ChannelMap` supports:

- Static factories: `Identity(int channels)`, `MonoToStereo`, etc.
- Custom coefficient matrix: per-output channel a list of `(srcChannel, gain)` contributions.
- SIMD fast paths: ~19 vectorized accumulators in `ChannelMap.SimdAccumulate.cs` for common channel counts (1, 2, 4, 6, 8). Scalar fallback for anything outside.

### Sources

| Source | Project | Notes |
|---|---|---|
| `S.Media.FFmpeg.Audio.AudioFileDecoder` | FFmpeg | File / URL via libavformat + libavcodec → packed float at the container's native rate. `TryReadNextFrame` returns pooled buffers (idempotent `Dispose`). |
| `S.Media.FFmpeg.Audio.ResamplingAudioSource` | FFmpeg | Wraps an inner `IAudioSource` in a libswresample rate converter. Used by the router's `autoResample: true` path. |
| `S.Media.PortAudio.PortAudioInput` | PortAudio | Capture device (lock-free SPSC ring + native callback). |
| `S.Media.NDI.Audio.NDIAudioReceiver` | NDI | NDI receiver ring (time-based capacity, see Phase 2 ergonomics). |
| `S.Media.Core.Audio.AudioBus` | Core | Dual-role sub-mix bus — implements both `IAudioOutput` and `IAudioSource`. |

### Outputs

| Output | Project | Notes |
|---|---|---|
| `S.Media.PortAudio.PortAudioOutput` | PortAudio | Implements `IClockedOutput` + `IPlaybackClock`. Defaults to `defaultHighOutputLatency` + prebuffer before `Pa_StartStream` (see project memory). |
| `S.Media.NDI.Audio.NDIAudioOutput` | NDI | Sender side; synchronous span read, no buffer retention. |
| `S.Media.FFmpeg.Audio.ResamplingAudioOutput` | FFmpeg | Per-output rate conversion wrapper. |
| `S.Media.FFmpeg.Audio.AdaptiveRateAudioOutput` | FFmpeg | Per-output ±ppm tweak driven by `PumpPressurePlaybackHintMonitor` for drift correction without retuning the master clock. |
| `S.Media.Core.Audio.AudioBus` | Core | Sub-mix bus (see above). |
| `S.Media.Core.Audio.DiscardingAudioSink` | Core | Test / null output. |

### Routing and clocking

- `S.Media.Core.Audio.AudioRouter` orchestrates: explicit `Route(sourceId, outputId, channelMap, gain)` entries; outputs sum contributions from every route targeting them.
- Pacing via `IRouterClock`. Default `WallClockRouterClock`; `SlaveTo(outputId)` binds production to a specific `IClockedOutput` (typically PortAudio output) for sample-accurate sync.
- Click-free volume changes: `SetRouteGain` linearly interpolates from the previously-applied gain to the new target across the chunk's samples.
- Per-output pump queue depth tunable via `AddOutput(..., pumpCapacityChunks: ...)` — defaults sized for ~80 ms at framework defaults; hardware outputs typically want 2–4 chunks, network outputs the default 8. See `AudioRouter.AddOutput` XML remarks for the latency budget formula.

See `Doc/MediaFramework-Architecture.md` for the in-depth discussion of multi-output drift, the optional profiling envvar, and the worked-example bundle wiring.

---

## Quick "what should I use" cheat sheet

- **Decoding a media file** → `MediaContainerDecoder.Open` (FFmpeg). Pixel format selection happens via `SelectOutputFormat`; the framework picks the closest supported native format and falls back to swscale → BGRA32 for anything unrecognised. Sample rate is whatever the container reports.
- **Decoding a static image** → `S.Media.SkiaSharp.ImageFileSource` (PNG / JPEG / WebP / BMP / GIF). Outputs BGRA32 premul.
- **Playing audio out to speakers** → `S.Media.PortAudio.PortAudioOutput`. Default to `defaultHighOutputLatency` + ≥ 250 ms prebuffer.
- **Sending video over NDI** → `S.Media.NDI.Video.NDIVideoSender`. Stick to BGRA32 / RGBA32 / UYVY / NV12 / I420; convert upstream if your source is anything else.
- **Compositing layers on the CPU** → `S.Media.Core.Video.CpuVideoCompositor` with BGRA32 layers; pick `CompositorSamplingMode.Bicubic` for photographic content, `Bilinear` for smooth scale, `Nearest` for byte-exact 1:1.
- **Compositing on the GPU** → `S.Media.OpenGL.GlVideoCompositor` accepts every `YuvVideoRenderer`-supported format as a layer (BGRA32 / Yuv422P10Le / Yuva444P12Le / etc.); bring your own GL context. RGBA16F intermediate preserves 10 / 12 / 16-bit precision through blends; final output is RGBA8 BGRA32.
- **Mixing audio routes** → `S.Media.Core.Audio.AudioRouter` (low-level) or `S.Media.Core.Audio.AudioGraphBuilder` (fluent). Sample-rate mismatch? Pass `autoResample: true` on `AddSource`.
- **One-call file playback** → `S.Media.Quick.QuickPlayer.Open(path)`. Auto-detects image vs media by extension and wires the rest.
