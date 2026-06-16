# 05 · Core Video & Compositor Pipeline

Video is structurally similar to audio (sources, outputs, a router) but with two big
differences: **frames are big and one-at-a-time** (no summing — one input shows on an
output at a time), and **presentation is clock-driven** (a player picks *which*
decoded frame to show *right now*). This doc covers the Core video types; the actual
compositor implementations live in `S.Media.Effects` ([10](10-Effects-and-Compositing.md)).

## The two interfaces

### `IVideoSource` — pull-based producer

```csharp
VideoFormat Format { get; }
bool TryReadNextFrame([NotNullWhen(true)] out VideoFrame? frame);
bool IsExhausted { get; }
```

Mirrors `IAudioSource` but yields whole frames. Implementers: `VideoFileDecoder`,
`NDIVideoReceiver`, image/text sources, the compositor source, `StaticFrameSource`.
Optional `ICooperativeVideoReadInterrupt` lets a long decode bail out for shutdown.

### `IVideoOutput` — push-based consumer

```csharp
IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; }
VideoFormat Format { get; }
void Configure(VideoFormat format);   // negotiate before the first Submit
void Submit(VideoFrame frame);
```

The key extra over audio: an output advertises which `PixelFormat`s it can accept,
and `Configure` is called once (with the negotiated format) before frames flow.
Implementers: SDL3 / OpenGL / Avalonia displays, `NDIVideoSender`, file muxers,
`DiscardingVideoOutput`. Optional capabilities:

* `IVideoOutputQueueControl` — async outputs that buffer frames (`AbandonQueuedFrames`,
  `WaitForIdle`) so a seek can flush stale frames and a sync present can wait for idle.
* `IVideoOutputD3D11GlBorrowSetup` — accept the active `IVideoSource` before `Configure`
  so a Win32 NV12 GL path can borrow libav's `ID3D11Device`.

## `VideoFormatNegotiator`

Before frames flow, the negotiator picks the **cheapest pixel format both ends can
agree on** and wires both to it. Source says "I can deliver these"; output says "I
accept these"; the negotiator finds the lowest-cost overlap (ideally zero-copy
pass-through of the codec's native format). This is why you rarely think about pixel
formats — the framework converts only when it must, and picks the conversion that
costs least.

## `VideoPlayer` — the presentation engine

`VideoPlayer` glues an `IVideoSource`, an `IVideoOutput`, and an `IMediaClock`. This
is the type that makes video track the clock. Two threads:

1. **Decode thread** (`DecodeLoop`): keeps a small bounded presentation queue full
   by pulling from the source. Bounded by a semaphore (`_slotsAvailable`) so it
   doesn't run away ahead of presentation.
2. **Clock-driven present** (`OnVideoTick`): on every `VideoTick` from the clock, it
   picks the right frame and submits it.

### How a frame is chosen each tick — ELI5

> Imagine a conveyor belt of decoded frames, each stamped with a "show me at"
> timestamp (PTS). On each tick the player looks at the clock and asks "what time is
> it?" Then:
> * It throws away frames whose time has already passed *and* a newer on-time frame
>   exists (those are "skipped" — better to be current than complete).
> * It shows the newest frame whose time is at-or-before now (within a small early
>   tolerance).
> * **Anti-freeze:** if the clock has run past *every* queued frame (decode fell
>   behind after a seek, or a heavy scene), it does **not** show nothing — it shows
>   the *newest late* frame so the picture keeps moving and visually catches up as
>   decode bursts land. (Showing nothing would freeze forever, because the freerun
>   clock never waits for video.)

Two important details:

* **`PlayheadOffset`** — video is held back by the audio output's latency. Audio is
  buffered *ahead* in the output ring, so the raw clock time leads what you actually
  hear. The player presents at `clock − offset` so the picture lines up with *heard*
  audio, not with the clock's leading edge.
* **`HoldLastFrameAtEnd`** — for still images / cover-art / "leave the last frame up,"
  the player snapshots the final frame's planes (for CPU frames) before the output
  takes ownership, and keeps re-submitting it after the source is exhausted.

`VideoPresentationMode` selects the strategy: `LatestOnTick` (always show the newest
queued frame — used for live/held-frame sources where PTS pacing is a no-op) vs the
PTS-synced mode above. `TryPresentBufferedFrameForSync` is the seek path: it drains
the output, submits the single target frame, and waits for idle so a scrub lands on
exactly the right frame. `VideoPlayerFaultedEventArgs` surfaces a decode fault.

## `VideoRouter` — one input, many outputs (fan-out)

Where `AudioRouter` sums many→one, `VideoRouter` fans **one input out to many
outputs**. Each output receives from at most one input at a time; one input can drive
several displays/encoders.

```csharp
var reg = router.AddInput(primaryOutputId);   // → InputRegistration { InputId, Output }
decoder.Video connects to reg.Output;
router.TryAddRoute(reg.InputId, secondOutputId, out var err);  // fan out
```

* `AddOutput` registers an `IVideoOutput`, optionally wrapping it in a `VideoOutputPump`
  (async) via `VideoOutputPumpAttachOptions`, or `synchronous: true` for inline.
* `AddInput(primaryOutputId)` returns a `VideoRouterInputRegistration` (stable id +
  the `IVideoOutput` the decoder connects to).
* `TryAddRoute` adds a fan-out branch. **It is transactional:** if branch negotiation
  throws (an output can't accept the negotiated format), the route is rolled back
  completely and the input is restored to its prior configured state — a rejected
  route never poisons the graph. (`out errorMessage` tells you why.)

### Per-output fan-out & branch conversion

Different outputs may want different pixel formats. The router negotiates a stream
format, then per branch:

* `VideoOutputFanoutFormats.PickBranchPixelFormat` chooses the concrete format that
  branch will receive.
* If a branch needs a different format, a per-branch `IVideoCpuFrameConverter` is
  attached — and (clever) run on **that pump's drain thread**, not the player's
  submit thread, so a heavy swscale (e.g. NDI's `yuv422p10le → UYVY` at 4K60) doesn't
  eat the per-frame budget (`VideoOutputPump.SetBranchConverter`).
* **Zero-copy fan-out:** for DRM dma-buf NV12/P010/P016 (and CPU NV12), if *every*
  output can take the format, all branches share one backing via the `VideoFrame`
  fan-out views (no per-output copy). Mixed-capability fan-outs fall back: dma-buf
  branches needing CPU conversion attempt an mmap readback (`VideoDmabufCpuReadback`);
  the router logs a warning when a CPU converter is in the fan-out path.
* `TryGetInputFanOutPixelFormats` reports what each output ends up receiving (HUD/diag).

`PumpPressure` (`VideoRouterPumpPressureEventArgs`) fires when an async branch drops a
frame; `TryGetVideoOutputPumpMetrics` exposes per-output counters.

## `VideoOutputPump` — bounded async delivery

The video analogue of audio's `OutputPump`. `Submit` enqueues and returns fast; a
drainer thread does the real `Submit`. **Drop policy is drop-*oldest*** (drop-late):
when the bounded queue (`MaxQueuedFrames`, default 3) is full, the oldest queued
frame is disposed to make room. Designed so a slow network encoder (NDI) can't stall
upstream decode.

Notable mechanics (from the source):

* **Branch conversion** runs here, between frames, with a staged converter swap so a
  reconfigure never disposes a converter mid-`Convert`.
* **Format change** drops queued old-format frames on `Configure` so a reconfigured
  inner output never receives a stale-format frame.
* **Disposal is paranoid:** it joins the drainer with a 2 s cap; if the drainer is
  still stuck inside a slow inner `Submit`, it *deliberately leaks* the pump state
  rather than pull state out from under the running thread (which would crash). The
  background thread exits on its own once the inner `Submit` returns. (A documented
  trade-off — see [15](15-Issues-and-Improvements.md).)

## Multi-output present sync / genlock (`VideoPresentSyncGroup`)

`VideoOutputPump` presents each output on *its own* cadence — perfect for independent
feeds, wrong for a **stitched canvas** split across several physical outputs (an
object crossing a panel seam would be one frame ahead on one side). The fix is to
present the grouped outputs in **lock-step**. Three Core types (`S.Media.Core.Video`)
do this — the video half of the multi-output sync work (audio half = `OutputSyncGroup`
+ `AdaptiveRateAudioOutput`, see [06](06-Clocks-and-AV-Sync.md) and
`Doc/HaPlay-MultiOutput-Sync.md`):

* **`ISyncPresentableVideoOutput`** — a video output that *buffers* frames and presents
  them only when told to. `TryPeekReadyPts(target)` reports the newest *unpresented*
  frame due at/before `target`; `PresentUpTo(target)` presents it and drops older ones.
* **`SyncPresentVideoOutput`** — the concrete member: wrap a directly-presenting device
  output in it, and it defers the present to the scheduler (the video analogue of
  `AdaptiveRateAudioOutput`). Owns/drops buffered frames; guards against presenting
  backwards.
* **`VideoPresentSyncGroup`** — the scheduler. Referenced to an `IReadOnlyPlayhead`
  (the show's present timeline — typically the same master `MediaClock` the audio sync
  group uses). Each `Tick()`:
  * **all members advance-ready →** present them at the *oldest* of their newest-due PTS
    (no member gets ahead) — lock-step.
  * **some ready, some behind →** *hold* (present nothing new) up to `MaxStarveHoldTicks`
    so the ready members don't tear ahead of the laggard; then *degrade* to presenting
    the ready ones so a wedged output can't freeze the wall. The laggard rejoins
    automatically.
  * **none due →** quiet between-frames hold.

  This is the "synchronized drop/repeat across outputs" the architecture doc lists as
  not-implemented. It's a built + unit-tested framework primitive; HaPlay host wiring
  (declaring which outputs form a group) is deferred pending real multi-output hardware
  validation. For a single output, prefer output mapping / warp ([10](10-Effects-and-Compositing.md))
  over a sync group — one output carved into panels needs no cross-output sync at all.

## Deinterlacing

* `IDeinterlacer` — converts an interlaced `VideoFrame` into one or more progressive
  frames. Same pluggable-registry shape as the converters: Core defines the contract
  and ships a fallback; FFmpeg ships the production one.
* `BobDeinterlacer` (Core) — simple line-doubling "bob": emits two progressive frames
  per interlaced input at double rate (top field + interpolated, then bottom). The
  always-available headless/test fallback.
* `YadifDeinterlacer` (FFmpeg, doc 08) — the production libavfilter yadif, installed
  via `VideoDeinterlacerRegistry` at FFmpeg init.

## Hardware video interop (the zero-copy contracts)

The *data* backings are in [03](03-Core-Data-Primitives.md); these are the *plumbing*
contracts Core defines so the GL backend can import GPU memory without Core
referencing any GPU API:

* `IHardwareVideoInterop` + `NoOpHardwareVideoInterop` — optional platform plumbing
  (Vulkan external memory, DRM PRIME FDs, DXGI shared handles, Metal IOSurface). No-op
  until a backend populates surface descriptors.
* `IHardwareD3D11GlInteropSource` — a source exposing libav's Windows D3D11VA
  `ID3D11Device` so GL can import decoded NV12 textures on the same adapter.
* `IVideoCpuFrameConverter` + `VideoCpuFrameConverterRegistry` — the swscale-style CPU
  converter contract; FFmpeg installs the implementation. `VideoFrameCpuClone` does
  pure-managed plane duplication (no FFmpeg) for the simple "give each branch its own
  copy" case.
* `DrmPixelFormats` — the Linux DRM FOURCC codes referenced by libav/EGL import.
* `HardwareVideoWin32Nv12` / `Win32SharedNv12Backing` / `Dmabuf*Backing` — covered in
  doc 03.

> Why all the indirection? Core stays GPU-agnostic and NativeAOT-friendly. The OpenGL
> backend (doc 09) implements these to do EGL dma-buf import on Linux and WGL/DXGI
> interop on Windows, but Core itself never links a GL or D3D symbol.

## Pacing & fan-out format helpers

* `VideoFormatPacing` — wall-clock pacing helpers for outputs that must self-throttle
  (NDI sending at the negotiated rate).
* `RetimingVideoOutput` — rewrites each frame's PTS by a fixed offset before
  forwarding. The video half of clip/trim rebasing (pair with `OffsetPlayhead` — see
  the timebase-mismatch trap in [06](06-Clocks-and-AV-Sync.md)).
* `DiscardingVideoOutput` — negotiates like a permissive display and drops every
  frame (hidden primary so playback can run before a real output attaches).
* `VideoFrame.Validation` (partial) — hardware-backing/plane/stride consistency checks
  in the ctor.

## The compositor contract (implementation in Effects)

Core does **not** contain a compositor — `IVideoCompositor` and `VideoCompositorSource`
live in `S.Media.Effects`. But conceptually they're "a video source that combines N
inputs into one," so they slot into this same pipeline: the compositor source exposes
input slots the `VideoRouter` targets and one output a `VideoPlayer` pulls from. See
[10 · Effects & Compositing](10-Effects-and-Compositing.md) for layers, transforms,
transitions, and the warp-mesh output mapping.

Next: [06 · Clocks & A/V Sync](06-Clocks-and-AV-Sync.md) — the keystone.
