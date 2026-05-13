# Media framework — playback concepts

This document summarizes how the MFPlayer-style media DLLs hang together: clocks, routers, negotiation, video presentation, and optional HDR hints.

## Clocking

- **`MediaClock`** emits **audio** and **video** tick events on a dedicated driver thread. Position can track an external **`IPlaybackClock`** (`SetMaster`) so video stays aligned with played audio samples. **`MediaClockExtensions.SetMasterChain`** builds a **`CompositePlaybackClock`** when you need priority-ordered fallbacks (e.g. PortAudio vs **`VideoPtsClock`** vs NDI ingest).
- **Pause / Stop** can take a **`CancellationToken`** so callers avoid wedging indefinitely while the timing driver shuts down (`MediaClock.JoinDriver`).
- **`VideoPlayer`** multiplexes **`IVideoSource.TryReadNextFrame`** into a PTS-sorted queue; on each **`VideoTick`** it selects the freshest frame whose PTS ≤ playhead ± tolerances.

## Negotiation & formats

- **`VideoFormatNegotiator.Connect`** aligns **`IVideoSource.NativePixelFormats`** with **`IVideoSink.AcceptedPixelFormats`**, optionally filtered by **`negotiatePixelFormats`** predicate (keeps negotiator ignorant of GPU details).
- On **Windows**, when **NV12** uses D3D11 shared handles, **`VideoFormatNegotiator`** can connect **`IVideoSinkD3D11GlBorrowSetup`** (e.g. **`SDL3GLVideoSink`**) to an **`IHardwareD3D11GlInteropSource`** so GL uses the same **`ID3D11Device`** as libav decode without the app manually calling **`TryGetHardwareD3D11DeviceForWin32Gl`**. **`D3D11InteropUtility`** validates COM device pointers (and **`ID3D11Texture2D`** when present); optional one-time DXGI adapter LUID diagnostics help catch multi-GPU mismatches between decode and GL. **`Nv12Win32SharedHandleGpuUploader`** uses **`OpenSharedResource`** on the NT handle, or wraps the libav **`ID3D11Texture2D`** COM pointer when **`VideoWin32Nv12Backing.LibavD3D11DeviceComPtr`** matches the uploader device (skips **`OpenSharedResource`**). For a **portable** libav-internal surface description (same **`ID3D11Texture2D`** + device COM pointers, no NT handle export), see **`WindowsNv12D3D11TextureInterop`** and **`HardwareVideoMemoryKind.Win32D3D11Nv12Texture`**.
- **`PixelFormat`** and **`PixelFormatInfo`** encode plane counts, chroma rounding, strides, alpha, and bit depth helpers shared by FFmpeg, sinks, GL, and tests.
- Decoder frames optionally carry **`VideoTransferHint`** inferred from libav **`AVFrame.color_trc`**.

## OpenGL rendering

- **`GlVideoFormatSupport`** is the authoritative map from **`PixelFormat`** to shader filenames, sampler names, plane sizes, **`GL`** internal formats, default bit scales, YUV-matrix flags.
- **`YuvVideoRenderer`** owns textures, uploads (including **`Upload(ReadOnlySpan<nint>, ReadOnlySpan<int>)`** for unmanaged planes), mipmaps-on-Y where enabled, **`VideoHdrTransfer`** preview tonemap path, **`VideoViewportFit`**.
- **Windows NV12 (D3D11 shared handles):** set **`MF_MEDIA_PROFILE_WIN32_NV12_UPLOAD=1`** to enable **`Nv12Win32SharedHandleGpuUploadProfiling`** counters on **`Nv12Win32SharedHandleGpuUploader.TryUpload`** (interop success, staging after interop miss, both paths failed). When decode and GL share the same device, the uploader may open the backing texture via libav COM pointers instead of **`OpenSharedResource`**.

## Routing & SIMD

- **`AudioRouter`** mixes multiple **`IAudioSource`** instances into **`IAudioSink`** pumps; steady-state **`ApplyRoute`** detects stereo identity **`ChannelMap([0,1])`** + uniform gain and uses **`Vector<float>`** accumulation (**`TryAccumulateStereoIdentityInterleaved`**) before falling back to nested loops.

## NDI pacing

When NDI `"clockVideo"` is off or you need host-side throttling, construct **`NDIOutput(..., minimumVideoSubmitSpacing: TimeSpan.FromSeconds(1d/60))`** so **`NDIVideoSender`** sleeps between submits to cap frame rate crudely (wall-clock, not PTS-aware).
