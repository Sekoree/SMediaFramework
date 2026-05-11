# Media Framework — Review & Improvement Checklist

**Last updated:** 2026-05-11 — Linux NV12 DRM PRIME dma-buf → EGL/GL (`RetainDmabufForGl`, `YuvDmabufEglInterop`, `Nv12DmabufGpuUploader`); FFmpeg VAAPI zero-copy MVP.

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
