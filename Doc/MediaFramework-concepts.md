# Media framework — playback concepts

This document summarizes how the MFPlayer-style media DLLs hang together: clocks, routers, negotiation, video presentation, and optional HDR hints.

## Clocking

- **`MediaClock`** emits **audio** and **video** tick events on a dedicated driver thread. Position can track an external **`IPlaybackClock`** (`SetMaster`) so video stays aligned with played audio samples. **`MediaClockExtensions.SetMasterChain`** builds a **`CompositePlaybackClock`** when you need priority-ordered fallbacks (e.g. PortAudio vs **`VideoPtsClock`** vs NDI ingest).
- **Pause / Stop** can take a **`CancellationToken`** so callers avoid wedging indefinitely while the timing driver shuts down (`MediaClock.JoinDriver`).
- **`VideoPlayer`** multiplexes **`IVideoSource.TryReadNextFrame`** into a PTS-sorted queue; on each **`VideoTick`** it selects the freshest frame whose PTS ≤ playhead ± tolerances.

## Negotiation & formats

- **`VideoFormatNegotiator.Connect`** aligns **`IVideoSource.NativePixelFormats`** with **`IVideoSink.AcceptedPixelFormats`**, optionally filtered by **`negotiatePixelFormats`** predicate (keeps negotiator ignorant of GPU details).
- **`PixelFormat`** and **`PixelFormatInfo`** encode plane counts, chroma rounding, strides, alpha, and bit depth helpers shared by FFmpeg, sinks, GL, and tests.
- Decoder frames optionally carry **`VideoTransferHint`** inferred from libav **`AVFrame.color_trc`**.

## OpenGL rendering

- **`GlVideoFormatSupport`** is the authoritative map from **`PixelFormat`** to shader filenames, sampler names, plane sizes, **`GL`** internal formats, default bit scales, YUV-matrix flags.
- **`YuvVideoRenderer`** owns textures, uploads (including **`Upload(ReadOnlySpan<nint>, ReadOnlySpan<int>)`** for unmanaged planes), mipmaps-on-Y where enabled, **`VideoHdrTransfer`** preview tonemap path, **`VideoViewportFit`**.

## Routing & SIMD

- **`AudioRouter`** mixes multiple **`IAudioSource`** instances into **`IAudioSink`** pumps; steady-state **`ApplyRoute`** detects stereo identity **`ChannelMap([0,1])`** + uniform gain and uses **`Vector<float>`** accumulation (**`TryAccumulateStereoIdentityInterleaved`**) before falling back to nested loops.

## NDI pacing

When NDI `"clockVideo"` is off or you need host-side throttling, construct **`NDIOutput(..., minimumVideoSubmitSpacing: TimeSpan.FromSeconds(1d/60))`** so **`NDIVideoSender`** sleeps between submits to cap frame rate crudely (wall-clock, not PTS-aware).
