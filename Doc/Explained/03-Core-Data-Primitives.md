# 03 · Core Data Primitives

These are the values that travel through every pipeline. They live in
`S.Media.Core` and have no dependencies. Understand these and the router/player
docs will read easily, because everything is "move one of these from a source to
an output."

## The two frames

### `AudioFrame` (readonly record struct)

```csharp
AudioFrame(TimeSpan PresentationTime, AudioFormat Format,
           int SamplesPerChannel, ReadOnlyMemory<float> Samples,
           IDisposable? Release = null)
```

One block of audio. The golden rule of the whole framework: **all audio is packed
(interleaved) 32-bit float.** No sample-format tag travels through the mixer —
sources convert at their boundary. So `Samples.Length == SamplesPerChannel ×
Format.Channels`, laid out `L R L R L R …`.

* `Release` is an optional disposable the producer attaches when the buffer comes
  from a pool (e.g. `AudioFileDecoder` rents from `ArrayPool<float>`). The consumer
  calls `Dispose()` when done; it's idempotent (only the producer's release runs,
  once). `WithActionRelease(...)` wraps an `Action` as the disposable.
* Because it's a `record struct`, `default(AudioFrame)` is a valid "empty" value.

> **ELI5:** an `AudioFrame` is a little tray of sound samples with a timestamp on
> it and, optionally, a note that says "give this tray back when you're done."

### `VideoFrame` (sealed partial class)

A class, not a struct — frames are big (1080p BGRA ≈ 8 MB), so they're heap objects
with explicit disposal rather than copied-by-value. It carries:

* `PresentationTime`, `Format`.
* `Planes` (`ReadOnlyMemory<byte>[]`) and `Strides` (`int[]`) — exposed as concrete
  arrays so hot loops index without interface dispatch. Plane order follows the
  pixel format: I420 = Y,U,V; NV12 = Y,UV; BGRA = one packed plane. *Stride* (pitch)
  can exceed the visible row width because of alignment padding — always honor it.
* `Metadata` (`VideoFrameMetadata`) with convenience forwarders (`ColorSpace`,
  `ColorRange`, `FieldOrder`, `ColorTransferHint`, `Timecode`, `AlphaMode`).
* `HardwareBacking` — `null` for CPU frames, or one of the GPU/zero-copy backings
  (see below). Typed accessors: `DmabufNv12`, `DmabufP010`, `DmabufP016`, `Win32Nv12`.
* `Dispose()` runs the release exactly once (`Interlocked.Exchange` guard).

**The clever part — zero-copy fan-out views.** When one decoded frame must go to
several outputs, copying the planes per output is wasteful. `TryCreateCpuFanOutViews`
(and the NV12-specialized `TryCreateNv12CpuFanOutViews`) produce N independent
`VideoFrame`s that *share the same plane memory* and a *shared countdown release*:
the underlying buffer is freed only after all N views are disposed. This is how the
`VideoRouter` hands a slow converting branch a raw view to repack on its own thread
without stealing the buffer from the fast branches. If it can't (hardware backing,
no release to share), it returns `false` and the caller deep-copies instead.

> **ELI5:** instead of photocopying a big picture for everyone, the frame hands out
> several "library cards" to the *same* picture and only shreds the picture once the
> last card is returned.

## Formats

### `AudioFormat(int SampleRate, int Channels)`

The canonical audio stream description. Helpers: `BytesPerSample` (always 4),
`BytesPerFrame`, `IsValid`. `default`/`AudioFormat(0,0)` is the "no audio" sentinel
(a container with no audio track). The ctor does **not** validate so the sentinel
stays legal; instead, every API that wires a format into a live pipeline calls
`Validate()` to fail fast at the boundary.

### `VideoFormat(int Width, int Height, PixelFormat PixelFormat, Rational FrameRate)`

Same idea for video, with the same `Validate()` discipline (positive dimensions and
frame rate). Pixel-format *suitability* is intentionally **not** validated here —
that's negotiated per output via `IVideoOutput.AcceptedPixelFormats`.

### `Rational(long Numerator, long Denominator)`

An exact integer ratio. Used for frame rates because `29.97` fps is really
`30000/1001` and a `double` would silently drift. Frame timing is computed from the
exact ratio, never a rounded float.

### `PixelFormat` (enum) + `PixelFormatInfo`

A deliberately curated enum of the layouts the framework can *talk about* (it's not
"all of FFmpeg"). Component order in names is **native byte order** — `Bgra32` is
B,G,R,A in memory. It spans:

* Packed RGB(A): `Bgra32`, `Rgba32`, `Bgr24`, `Rgb24`, `Argb32`, `Abgr32`, plus
  high-bit `Rgba16` (16-bit unorm) and `Rgba16F` (half-float).
* Planar YUV 4:2:0: `I420`, `Yv12`; 4:2:2: `Yuv422P`; 4:4:4: `Yuv444P`.
* Semi-planar YUV (Y + interleaved chroma): `Nv12`, `Nv21`, and 10/16-bit `P010`,
  `P016`, `P216`, plus alpha-carrying `Pa16`.
* Packed YUV 4:2:2: `Uyvy`, `Yuyv`.
* A large family of high-bit-depth planar variants (`Yuv420P10Le`, `Yuv422P10Le`,
  `Yuv444P12Le`, …) and their alpha versions (`Yuva420p`, `Yuva444P12Le`, …) for
  ProRes / HEVC / professional capture pipelines.
* Grayscale: `Gray8`, `Gray16`.

`PixelFormatInfo` provides static descriptors (plane count, per-plane vertical
subsampling) so decoders, outputs, and converters can reason about layout without a
giant switch in each.

## Per-frame metadata & color

* `VideoFrameMetadata` (readonly record struct) bundles the optional hints so the
  `VideoFrame` ctor doesn't grow a dozen parameters: color space, range, field
  order, transfer hint, timecode, alpha mode.
* `VideoColorSpace` — YCbCr→RGB matrix hint (BT.709 / BT.601 / BT.2020). Lets each
  renderer pick the right matrix instead of re-deriving FFmpeg metadata.
* `VideoColorRange` — limited (TV, 16–235) vs full (PC, 0–255).
* `VideoTransferHint` — transfer/EOTF (for HDR preview tone-mapping).
* `VideoFieldOrder` — interlaced field order (top/bottom first), read by
  deinterlacers and NDI senders.
* `VideoAlphaMode` — how alpha is encoded when a format carries it.
* `VideoTimecode` + `VideoTimecodeMath` — SMPTE 12M timecode (HH:MM:SS:FF + drop-frame
  flag + source rate). The math helpers handle drop-frame arithmetic and frame-rate
  classification so a timecode can round-trip to NDI's timecode slot or a string.

## Channel mapping & layouts

### `ChannelMap` (readonly partial struct)

This is how the audio router decides which source channel feeds which output
channel. **Encoding: `map[outputChannel] = sourceChannel`** (output-indexed), with
`-1` meaning "silence this output channel." It's exhaustive on the output side
(every output channel is assigned) and source channels not referenced are *dropped*
— an explicit contract so there's never surprise leakage.

```csharp
new ChannelMap([0, 1])          // identity stereo
new ChannelMap([1, 0])          // swap L/R
new ChannelMap([0, 0])          // duplicate L to both outputs
new ChannelMap([-1, 0, 0, -1])  // source → center pair, sides silent
ChannelMap.Identity(n) / MonoToN(n) / StereoToN(n) / StereoToNSwapped(n)
```

`Apply` overwrites the destination; `ApplyAdditive` *sums* into it (what the mixer
uses to accumulate multiple sources into one output). The struct validates that the
source has enough channels (`RequiredInputChannels`).

**The SIMD story** (`ChannelMap.SimdAccumulate.cs`, ~1,830 lines): `ApplyAdditive`
runs a *cascade* of `TryAccumulate…` fast paths before falling back to the scalar
double loop. Each fast path recognizes a common shape — identity stereo, stereo→quad
paired duplicates, wide-source consecutive pair, packed permutation (N∈{3..8} via
SSE `SHUFPS` / AVX2 `PermuteVar8x32`), mono-dup-to-N, etc. — and vectorizes it. The
ordering in `ApplyAdditive` is the priority list. This is why mixing is allocation-
and branch-light at 4K60 (see the hot-path notes in [04](04-Core-Audio-Engine.md)).

> **ELI5:** a `ChannelMap` is a seating chart that says "output speaker #1 plays
> what came from microphone #2." The SIMD paths are pre-memorized seating charts the
> CPU can execute 8 seats at a time instead of one by one.

### `AudioChannelLayoutPresets` & `AudioMixPreset`

* `AudioChannelLayoutPresets` — standard gain matrices indexed `[srcCh, outCh]`
  (e.g. 5.1→stereo downmix folds). The router turns each non-zero cell into a route.
* `AudioMixPreset` (record) — a *named, file-persistable* gain matrix; the shareable
  form of a routing layout ("5.1 → stereo broadcast fold", "stems to monitors").

## Hardware video backings (zero-copy GPU memory)

A `VideoFrame` can carry **at most one** `VideoFrameHardwareBacking` describing GPU
memory so frames never touch the CPU on the fast path:

* `DmabufNv12Backing` / `DmabufP010Backing` / `DmabufP016Backing` — Linux DRM PRIME
  / dma-buf file descriptors + layout. Consumers import via EGL (the OpenGL backend);
  CPU plane access is stubbed out (use `VideoDmabufCpuReadback` for best-effort mmap).
* `Win32SharedNv12Backing` — Windows DXGI/D3D11 NT shared handles, optionally
  carrying non-owning libav `ID3D11Device`/`Texture2D` COM pointers so GL can import
  the *same* texture on the decode device without `OpenSharedResource`.
* `HardwareVideoSurfaceDescriptor` / `HardwareVideoPlaneDescriptor` / `…MemoryKind`
  — a portable bundle (up to 4 planes) for the generic interop contract.
* `VideoFrameHardwareBackingFactories` — helpers to build frames from backings.
* `HardwareVideoWin32Nv12` — builds a Win32 backing from a surface descriptor.

These are the data side of the hardware path; the *plumbing* contracts
(`IHardwareVideoInterop`, `IVideoOutputD3D11GlBorrowSetup`, etc.) live in the video
engine doc, and the actual upload code is in `S.Media.OpenGL` ([09](09-Output-Backends.md)).

## Small but pervasive helpers

* `ClipWindow` (readonly record struct) — a trim window over a source timeline:
  start offset, optional end, effective playable duration, and conversions between
  source-timeline and clip-relative positions. Pair it with `RetimingVideoOutput` /
  `OffsetPlayhead` to rebase a clip that starts mid-source to t=0. This single type
  replaced trim logic that used to be duplicated across cues, soundboards, and
  output wrappers — see the timebase-mismatch discussion in [06](06-Clocks-and-AV-Sync.md).
* `DisposableRelease` — the release-callback machinery: `Wrap(Action)`,
  `SharedCountdown(inner, n)` (the refcount behind fan-out views), and adjustment
  helpers.
* `SDebug` / `ChangeTrace` — ad-hoc profiling. `ChangeTrace` is a segment timer for
  playlist/transport traces (`+Δms (total Tms) label`), toggled by
  `MF_HAPLAY_CHANGE_TRACE`.

Next: [04 · Core Audio Engine](04-Core-Audio-Engine.md).
