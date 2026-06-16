# 09 · Output Backends

These packages turn frames into something real: sound from a card, pixels on a
screen, or A/V on the network. Each implements the Core interfaces
([04](04-Core-Audio-Engine.md)/[05](05-Core-Video-Pipeline.md)) and is referenced
*only* by hosts that use it.

## S.Media.PortAudio — sound out (and the master clock)

PortAudio is the audio device layer (over PALib, [02](02-Native-Bindings.md)).

### `PortAudioOutput` — the most important output in the framework

It's `unsafe` and implements a *lot* of capabilities — note the interface list,
because it's the type that makes A/V sync work:

```csharp
public sealed unsafe class PortAudioOutput :
    IAudioOutput, IAudioOutputChannelCapabilities, IClockedOutput,
    IFlushableOutput, IPlaybackClock, IAudioOutputPlaybackStats, IDisposable
```

How it works:

* **SPSC ring buffer.** Producers (the router's pump drainer) call `Submit` with
  packed float32; PortAudio's audio-thread **callback** drains the ring and fills
  **silence on underrun**. Submit and callback never share a lock — it's a lock-free
  single-producer/single-consumer ring.
* **It's the `IPlaybackClock` master.** `ElapsedSinceStart` is the monotonic playback
  time the whole graph slaves to ([06](06-Clocks-and-AV-Sync.md)). Cleverly, it
  *blends* `Pa_GetStreamTime` with the played-sample count so the clock advances
  *within* a buffer instead of jumping once per ~10–25 ms callback — smoother video
  pacing. It falls back to pure sample counts before the first callback or when
  inactive.
* **It honors the freeze contract.** `Flush` re-anchors the playback epoch
  (`_playbackEpochSamples = _playedSamples`) so `ElapsedSinceStart` resets to zero for
  the new segment, and crucially *underrun silence during pause cannot advance
  elapsed* — exactly what the pause-fold in `MediaClock` relies on.
* **`IClockedOutput.WaitForCapacity`** paces the router's mix loop to real playback
  (this is what `AudioRouter.SlaveTo` uses), and triggers the prebuffer-before-start
  behaviour.
* Diagnostics: `PlayedSamples`, underrun samples, `CallbackCount`,
  `CallbackFaulted`/`CallbackFaultException` (the callback never throws across the
  native boundary — it records and continues).

> **Latency default (project memory):** open with `defaultHighOutputLatency` and
> prebuffer the ring before `Pa_StartStream`. Starting an empty stream underruns
> immediately; the prebuffer fills it so the first audio you hear is clean.

### The rest of S.Media.PortAudio

* **`PortAudioInput`** — capture as an `IAudioSource`: PortAudio's callback writes into
  an SPSC ring; consumers pull via `ReadInto`. Live input for the playlist/cue system.
* **`PortAudioRuntime`** — ref-counted library lifetime: first holder calls
  `Pa_Initialize`, last holder calls `Pa_Terminate`. Every output/input/catalog holds
  one. (The shared lifetime pattern from [02](02-Native-Bindings.md).)
* **`PortAudioDeviceCatalog`** — enumerate host APIs and devices
  (`PortAudioHostApiEntry`, `PortAudioOutputDeviceEntry`, `PortAudioInputDeviceEntry`)
  using the same ref-count. This is what populates the device pickers in HaPlay.
* **`PortAudioPlaybackHost`** — wires shared-mux audio into an `AudioRouter` + clock
  with a primary `PortAudioOutput`. `PortAudioPlaybackHostPlayerOwnership` chooses
  whether it disposes the player or just tears down the device (so an outer bundle can
  own the player).
* **`AudioPrefill`** (internal) — pulls PCM from a source into a delivery delegate
  until a predicate stops — the hardware-ring prebuffer helper.
* Extensions (`AudioRouterPortAudioExtensions`, `MediaPlayerOpenBuilderPortAudioExtensions`,
  `MediaFrameworkRuntimePortAudioExtensions`) add the fluent `.UsePortAudio()` and
  router/builder sugar. `PortAudioException` wraps `PaError`.

## S.Media.OpenGL — GPU video rendering

The shader pipeline shared by SDL3 and Avalonia. It does **not** own a window — it
renders into "the currently bound framebuffer," so the same code drives an SDL window,
an Avalonia control, or an off-screen FBO.

* **`YuvVideoRenderer`** (~1,525 lines) — the renderer. Uploads each `VideoFrame`'s
  planes to textures and runs a fragment shader that does YUV→RGB (and HDR preview).
  Format-aware via `GlVideoFormatSupport` (the single source of truth for plane counts,
  texture sizing, shader selection, default bit scaling).
* **Color math:**
  * `YuvColorSpace` — the 3×3 YUV→RGB matrices (BT.601/709/2020), written row-major to
    satisfy `RGB = M·(YUV − offset)` normalized to [0,1].
  * `RgbGamutMatrix` — post-conversion RGB→RGB (primarily BT.2020→BT.709 for SDR preview
    of UHD HDR).
  * `VideoHdrTransfer` / `GlVideoOutputHdr` / `GlVideoOutputHdrPreference` — inverse
    EOTF (PQ/HLG) as a *preview-style* approximation into SDR — not a calibrated
    broadcast chain.
* **`VideoViewportFit` / `VideoViewportLayout`** — letterbox/fill/stretch math
  computing the GL viewport rect.
* **`SharedGlProgramCache`** — ref-counted linked programs keyed by shader-pair id, so
  many renderers in one context share compiled programs.
* **Hardware upload (zero-copy import of decoded GPU frames):**
  * Linux: `Nv12DmabufGpuUploader` / `EglDmabufNv12Uploader` — EGL/GL import of DRM
    PRIME dma-bufs (NV12/P010/P016 today; `LinuxDmabufGlHardwareFormats` reports which).
  * Windows: `Nv12Win32SharedHandleGpuUploader` (~775 lines) — imports a D3D11 NV12
    texture via WGL_NV_DX_interop (GPU path) or a staging-Map fallback, using the
    keyed-mutex hand-off (`D3d11TextureKeyedMutexScope`) that FFmpeg's d3d11va path
    expects. `Win32Nv12GlUploadDeviceResolver` / `D3D11GlInteropDeviceHost` /
    `D3D11InteropUtility` / `WglNvDxInterop` resolve and validate the device interop.
  * `OpenGlUnpackRowLength` computes `GL_UNPACK_ROW_LENGTH` for padded (strided) CPU
    uploads. `GlOutputBitDepth` requests the swapchain color depth.

> **Shader gotcha (project memory):** Mesa rejects non-ASCII bytes in `*.glsl` files
> with confusing "unexpected EOF" errors. Keep shader comments ASCII-only.

## S.Media.SDL3 — windowed video host

SDL3 gives a window + renderer/GL context that is *not* tied to the Avalonia
dispatcher, so video output runs on its own thread.

* **`SDL3VideoOutput`** — `IVideoOutput` over an SDL window + renderer.
* **`SDL3GLVideoOutput`** (~1,155 lines) — same but with an OpenGL 3.3 Core context,
  routing rendering through `YuvVideoRenderer` so it shares the shader pipeline with
  Avalonia.
* **Two dispatch modes** (both classes): **Auto-thread** (default) — the output owns
  its render thread; `Submit` is wait-free (latest-wins frame slot), callable from any
  thread. **Manual** — no internal thread; the host calls submit/render/present from
  one thread of its choice (required on macOS, which pins SDL window/event handling to
  the main thread).
* **`SDL3GLVideoCompositor`** — a framework-level GL compositor host backed by a
  *hidden* SDL window/context (so the GL compositor can run without a visible window).
* **`SDL3Runtime`** — ref-counted SDL video subsystem lifetime.

## S.Media.NDI — A/V over the network

Adapts NDILib ([02](02-Native-Bindings.md)) to the Core interfaces. The defining idea:
**one NDI source on the wire carries both audio and video**, so send and receive are
built around a shared `NDIOutput`/`NDISource` that owns the underlying NDI sender/
receiver and hands out child A/V outputs.

### Sending

* **`NDIOutput`** — one NDI source on the network: owns a single sender + the ref-count
  and exposes child audio/video outputs. Receivers see one combined source.
* **`NDIVideoSender`** — `IVideoOutput` backed by the sender (constructed only via
  `NDIOutput` so A/V share one source). `NDIVideoTimecodeMode` /
  `NDIVideoFrameUnpack` handle timecode and copying CPU payloads into owned buffers.
* **`NDIAudioOutput`** — `IAudioOutput` backed by the shared sender.
* **`NDIAudioAggregatingOutput`** — buffers packed float and forwards in fixed multiples
  so NDI packets align to a stable cadence (≈1 video frame of audio at 48 kHz).
* `NDIEgressPresentationTimeline` — one session anchor for NDI timecodes (100 ns ticks).
* `LockedFormatVideoOutput` (in HaPlay) wraps a sender to pin the pixel format /
  resolution receivers see regardless of source — covered in [13](13-HaPlay-UI.md).

### Receiving

* **`NDISource`** (~783 lines) — combined receive source: find on the network, connect,
  then wire `NDIVideoReceiver` / `NDIAudioReceiver` into the graph. Implements
  `INdiOverflowReporter`. `NDIConnectionState` reports link state; `NDIReceiveBandwidthPolicy`
  picks the bandwidth mode from enabled stream types; `NDISourceOptions`/`NDIFindOptions`
  configure it.
* **`NDIVideoReceiver`** — `IVideoSource`: captures video on a background thread, copies
  into pool-backed `VideoFrame`s, queues them.
* **`NDIAudioReceiver`** — `IAudioSource`: captures audio, converts native planar FLTP
  → packed float32 via `NDIAudioUtils`, queues into a lock-free SPSC ring.
* **`NDIIngestPlaybackClock`** — the receive-side master clock ([06](06-Clocks-and-AV-Sync.md)),
  driven by receiver timecode + wall extrapolation. `AudioRouterNdiExtensions.SlaveToIngest`
  wires it.

### Receive timing & diagnostics

* `NDIFrameTiming` maps NDI timecode/timestamp fields to presentation timelines.
* `NDIFusionPlaybackHints` / `NDIMonitorReceiverPumpFusion` correlate sender↔receiver
  feedback (from NDI Monitor and other receivers) with host pump counters for HUDs.
  These are *hints* — NDI pacing policy stays host-owned.
* `NDICaptureThreadLifecycle`, `NDIException`, `NDIOutputExtensions` round it out.

> See `Doc/NDI-Terminology.md` for the egress/ingest/fusion vocabulary.

## S.Media.SkiaSharp — images & text as video sources

Turns still content into `IVideoSource`s so an image or a title card flows through the
exact same pipeline as decoded video (compositing, output mapping, NDI send — all work
unchanged). Registered via `.UseSkiaSharpImages()`.

* **`ImageFileSource`** — loads PNG/JPEG/WebP/BMP/GIF (anything Skia's codecs read) as
  one decoded BGRA32 frame, re-emitted on every read. `VideoSource.OpenImage(...)`
  resolves to this via the extension registry.
* **`TextLayerSource`** — rasterizes a text label to a BGRA32 frame via Skia. Mutable
  text/font/color/size — setters invalidate the cache and the next read re-rasterizes.
  `TextAlignment` controls layout.

> This is the "static images are complementary on the same pipeline" point from project
> memory: a soundboard tile, a logo slate, or a text cue is just a one-frame video
> source.

## S.Media.Avalonia — video inside the Avalonia UI

* **`VideoOpenGlControl`** — an Avalonia `OpenGlControlBase` that implements `IVideoOutput`
  using the **same** `YuvVideoRenderer` / shader pipeline as `SDL3GLVideoOutput`. So an
  in-app preview pane and a detached SDL output render identically. (Only 1 file — the
  heavy lifting is shared in S.Media.OpenGL.)

> **Composition orientation caveat (project memory):** GL paths run on hardware; the
> CPU compositor runs headless. GL-only flips can pass all headless tests yet look
> wrong on screen — verify GL paths under `xvfb`. (Relevant to compositor output here.)

Next: [10 · Effects & Compositing](10-Effects-and-Compositing.md).
