# 08 · FFmpeg Decode & Encode

`S.Media.FFmpeg` is where actual media files turn into `AudioFrame`/`VideoFrame`s,
and `S.Media.FFmpeg.Encode` is the reverse (frames → encoded file). These are the
only assemblies that touch libav directly; everything above them speaks the Core
interfaces. FFmpeg is reached through FFmpeg.AutoGen bindings, mostly in `unsafe`
code.

## Boot: `FFmpegRuntime`

`FFmpegRuntime.EnsureInitialized()` does one-time native binding setup (safe to call
repeatedly) and installs the Core registry implementations: the CPU pixel converter
(`VideoCpuFrameConverterRegistry`), the yadif deinterlacer (`VideoDeinterlacerRegistry`),
and the resampler/decoder plugin slots. `MediaFrameworkRuntime.Init().UseFFmpeg()`
(`MediaFrameworkRuntimeFfmpegExtensions`) calls into this. `FFmpegException` wraps
libav error codes into readable .NET exceptions.

## The entry points

```csharp
using var media = MediaContainer.OpenFile("clip.mkv");   // combined A+V
// media.Audio : IAudioSource (default/AudioFormat(0,0) when no audio — guard HasAudio)
// media.Video : IVideoSource
```

* **`MediaContainer`** — the discoverable facade for combined audio+video decode. It
  forwards to `MediaContainerDecoder`. `MediaContainerOpenStreamOptions` /
  `MediaContainerSharedDemux` config covers stream-from-`Stream`, queue depths, stream
  selection, etc.
* **`MediaContainerDecoder`** — opens one file for *both* audio and video with a
  single host-facing object; `Seek` moves both streams to the same timeline position.
  Exposes `ContainerOwnedAudioSource` / `ContainerOwnedVideoSource` (each owns the
  decoder and exposes its track).
* **Single-stream decoders** for when you only want one track:
  `AudioFileDecoder` (best audio stream → packed float32 at native rate/channels) and
  `VideoFileDecoder` (best video stream → native pixel format, or sws_scale to a
  requested one). `VideoFileDecoder` implements both `IVideoSource` and
  `ISeekableSource`.
* **`MediaStreamProbe` / `MediaStreamInfo`** — read the container's stream table
  *without* building a decoder (codec, dimensions, channel count, the container stream
  index usable as an explicit `AudioStreamIndex`/`VideoStreamIndex`). HaPlay uses this
  to decide which cue-drawer tabs to show.

## `MediaContainerSharedDemux` — one file, two threads, no fighting

This is the heart of file playback and the single most intricate file in the
framework (~2,560 lines). Here's the problem it solves and how.

**The problem:** a media file interleaves audio and video packets in one byte stream.
You must *demux* (read packets and sort them by stream) once. But audio and video want
to decode on **different threads** at **different rates** (audio in tiny 10 ms chunks,
video in big frames), and neither should block the other. And libav's
`AVFormatContext` is not safe to bang on from multiple threads.

**The design — ELI5:**

> Think of one person (the **reader thread**) standing at a conveyor belt pulling
> mixed red and blue boxes off it (packets). They drop red boxes (audio) into a red
> bin and blue boxes (video) into a blue bin (the **bounded per-stream queues**:
> 192 audio / 384 video by default). Two other workers — one red, one blue — take
> boxes from their own bin and unpack them (**decode**), each with their **own
> workbench lock** so they never reach for the same tool. If a bin fills up, the
> reader waits (back-pressure) instead of overflowing memory. If a bin empties, that
> worker waits for the reader.

Concretely:

* **One `AVFormatContext`**, one background **reader/demuxer thread** that reads
  packets and routes them into `_audioPacketQ` / `_videoPacketQ`.
* **Separate decode locks** (`_audioDecodeLock`, `_videoDecodeLock`) so audio and
  video decode concurrently; only the shared *demux* and *seek* are serialized (via a
  `ReaderWriterLockSlim` read/seek gate).
* **Bounded queues** apply back-pressure so a paused-but-not-disposed consumer can't
  make the reader buffer the whole file into RAM.
* **An AVIO interrupt callback** lets a blocking network/file read be interrupted for
  shutdown (cooperative — ties into the yield flags).

### Seeking: landing on the *exact* frame

Seeking in compressed video is hard: you can only seek to a *keyframe*, which may be a
whole GOP (group of pictures, often 1–2 s) *before* where you asked. The demux handles
this with `PrimeBothAfterSeekLocked`: after the container seeks to the keyframe, it
**decodes forward**, discarding frames until it reaches the exact requested position,
so audio and video resume *together* at the right spot. A `_seekGeneration` counter
invalidates in-flight packets/frames from before the seek. The straddling "keeper"
frame is pushed back (`_aHasBufferedFrame`) so the next read emits it instead of
skipping it.

> This is exactly the area of the two seek war-stories in [06](06-Clocks-and-AV-Sync.md):
> if a hardware frame loses its PTS (missing `av_frame_copy_props`), the prime logic
> can mislabel the keyframe as the target and audio ends up ahead. The
> `TransportSyncProbe --verify-content` tool ([14](14-Tools-and-Probes.md)) exists to
> catch label-blind seeks.

### Mid-file format changes

Audio streams can change format mid-file (HE-AAC SBR kicking in, DVB parameter
changes). `EnsureSwrMatchesDecodedAudioFrameLocked` compares each live frame's format
against what the resampler (`swr`) was built for and **rebuilds swr** rather than
feeding it misinterpreted samples.

## Hardware decode

* **`VideoHardwareDecodeContext`** — sets up libav hardware decode: device context, the
  `get_format` hook (negotiate a hardware pixel format), and CPU transfer scratch.
  `HardwareVideoDeviceType` mirrors the libav enum.
* The decoded frames carry **zero-copy backings** (doc 03) built by internal factories
  that parse libav's hardware frame metadata:
  * `DrmPrimeNv12BackingFactory` / `…P010` / `…P016` — parse DRM PRIME descriptors
    (`AvDrmFrameDescriptorInterop` mirrors libav's struct on LP64) into dma-bufs.
  * `D3D11VaNv12BackingFactory` — maps D3D11VA frames into Win32 shared NT handles,
    optionally recording the libav `ID3D11Device`/`Texture2D` for same-device GL upload.
* `FFmpegLinuxDup` (dup file descriptors), `FfmpegVideoPixelMaps` (PixelFormat ↔ libav
  AVPixelFormat) round it out. The GL upload of these backings is in
  [09](09-Output-Backends.md).

## Resampling & rate adaptation

* **`AudioResampler`** — a reusable swresample wrapper for packed float32.
* **`ResamplingAudioSource`** / **`SeekableResamplingAudioSource`** — present an inner
  `IAudioSource` at a *different* rate (the source-side resample the `AudioRouter`
  uses with `autoResample:true`, e.g. a 44.1 kHz clip into a 48 kHz router). The
  seekable variant flushes the resampler on seek.
* **`ResamplingAudioOutput`** — the output-side mirror: present at the router rate
  while forwarding to an output that needs a fixed different rate (48 kHz NDI from
  44.1 kHz mux).
* **`AdaptiveRateAudioOutput`** — the drift mitigation from [04](04-Core-Audio-Engine.md)/
  [06](06-Clocks-and-AV-Sync.md): wraps a slow output and applies a *few-ppm*
  swresample tweak driven by `PumpPressurePlaybackHintMonitor` filtered to that output
  id, so a non-master output that keeps dropping receives slightly fewer samples
  without retuning the global clock. `IAdaptiveRateWrappedOutput` marks it.
* **`AudioDecoderLibavThreadTypePreference`** / `AudioFileDecoderOpenOptions` — codec
  threading knobs.

## Pixel conversion & deinterlace (the shipping impls)

* **`VideoCpuFrameConverter`** — the swscale CPU converter Core's registry uses
  (e.g. high-bit YUV → BGRA for NDI). `DuplicateCpuBacking` deep-copies planes.
* **`YadifDeinterlacer`** — libavfilter yadif (mode 0, frame-rate-preserving), built
  as a buffer→yadif→buffersink graph per frame. The production deinterlacer installed
  at FFmpeg init (Core's `BobDeinterlacer` is the fallback).

## Memory & metadata plumbing (internal)

* `StreamAvioBridge` — bridges a managed `Stream` to libavformat via `AVIOContext`
  (no temp-file spool). The caller owns the stream.
* `UnmanagedMemoryManager<T>` — exposes an unmanaged pointer+length as `Memory<T>`
  (the producer keeps the buffer alive — e.g. a refcounted `AVFrame`).
* `PassThroughDescriptorArena` + `PassThroughRentHandle` — pooled paired pointer/stride
  arrays for libav pass-through metadata, using a lock-free Treiber stack (with the
  opt-in profiling/serialization from [07](07-Triggers-Diagnostics-Runtime.md)).

## Lifecycle helpers: session & bundle

These coordinate teardown order so libav never closes out from under a still-running
decode thread.

* **`MediaContainerSession`** — pairs a `MediaContainerDecoder` with playback controls
  and *shared-mux flush coordination*. Its `Pause`/`SeekCoordinated` take an optional
  `flushSharedMuxAfterPause` action (defaulting to `FlushCodecPipelines`). The default
  is fine for single-decode-thread cases but can **deadlock** if the video decode
  thread is still inside libav when pause fires and the flush wants the same demux
  locks. For that, use `PauseSkippingSharedMuxFlush` /
  `SeekCoordinatedSkippingSharedMuxFlush` (or pass `static () => {}`). This is the
  subtlety written up in `Doc/MediaFramework-Architecture.md`.
* **`MediaContainerPlaybackBundle`** — one process-local owner of a shared decoder +
  dependent video/audio players + optional router + clock, disposed in a **fixed order**
  (players → router → clock → decoder) so mux-backed decode stops before the container
  closes. `MediaContainerPlaybackBundleOwnedParts` flags pick which children it owns;
  presets `SmokeToolDefaultOwnership` and `DefaultBundledHostOwnership` match the smoke
  tool and `MediaPlayer` wirings respectively.

---

## The encode side: `S.Media.FFmpeg.Encode`

The mirror image — `IVideoOutput`/`IAudioOutput` implementations that *encode* frames
into a file instead of displaying them. Added in "Phase 12."

```csharp
using var mux = new FFmpegMuxFileOutput("out.mp4", videoOpts, audioOpts);
videoRouter.AddOutput(mux.Video);
audioRouter.AddOutput(mux.Audio);
```

* **`FFmpegMuxFileOutput`** — combined A+V file muxer. Exposes a `.Video`
  (`IVideoOutput`) and `.Audio` (`IAudioOutput`) leg that the routers target; both
  write into one shared output container. `FFmpegMuxFileOutputOptions` configures it.
* **`FFmpegVideoFileOutput`** / **`FFmpegAudioFileOutput`** — standalone single-stream
  encoders (also usable inside the mux). Video accepts negotiated `VideoFrame`s and
  encodes a stream; audio accepts packed float PCM. Options records carry codec/bitrate/
  quality.
* **Codec selection:** `FFmpegVideoCodec` (H.264/HEVC/ProRes), `FFmpegAudioCodec`
  (AAC/Opus/FLAC), `FFmpegEncodeContainer` (output container hint).
* **Internals:** `FfmpegMuxContext` (the shared `AVFormatContext` for one output file),
  `FfmpegVideoEncoder` / `FfmpegAudioEncoder` (the per-stream encode loops),
  `FfmpegAvFrameFill` (pack a `VideoFrame`/PCM into an `AVFrame`), `FfmpegEncodeMaps`
  (PixelFormat/codec ↔ libav mapping).

The `EncoderSmoke` tool ([14](14-Tools-and-Probes.md)) is the canonical encode wiring
example.

Next: [09 · Output Backends](09-Output-Backends.md).
