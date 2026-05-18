# MediaFramework Review — 2026-05-18

Scope: every project under `MediaFramework/` plus `S.Media.Playback`. Explicitly **excludes** the end-user `UI/HaPlay` app. About 48 K LOC C# (`MediaFramework/**/*.cs`).

The review is written for the framework author rather than an outside consumer — it assumes the code is correct and focuses on what would surprise the next person who builds on it, what is needlessly hard to extend, and what magic to flag before professional work lands on top.

---

## 1. Public-surface ergonomics (what a library consumer sees)

### 1.1 The contracts that matter

Four interfaces define almost every extension point:

| Interface | File | Methods to implement |
|---|---|---|
| `IAudioSource` | `S.Media.Core/Audio/IAudioSource.cs:13` | `Format`, `IsExhausted`, `ReadInto(Span<float>)`; optional `TryReadNextFrame` (default returns false) |
| `IAudioSink` | `S.Media.Core/Audio/IAudioSink.cs:8` | `Format`, `Submit(ReadOnlySpan<float>)` |
| `IVideoSource` | `S.Media.Core/Video/IVideoSource.cs:23` | `Format`, `NativePixelFormats`, `IsExhausted`, `SelectOutputFormat`, `TryReadNextFrame` |
| `IVideoSink` | `S.Media.Core/Video/IVideoSink.cs:22` | `Format`, `AcceptedPixelFormats`, `Configure`, `Submit(VideoFrame)` |

These are genuinely minimal and well-documented. An author writing a new sink (e.g. WASAPI direct, JACK direct, BMD DeckLink output) needs to implement two methods on audio side and four on video side. There is **no base class** to fight against, no template-method indirection, no DI container. This is the framework's strongest asset for consumers — keep it that way.

Optional sub-interfaces add capability without bloating the core contract:

- `IClockedSink` (Audio/IClockedSink.cs) — `WaitForCapacity` for sample-accurate router pacing
- `IFlushableSink` (Audio/IFlushableSink.cs) — immediate silence on pause/seek
- `IPlaybackClock` (Clock/IPlaybackClock.cs) — sinks that can drive the master clock
- `ISeekableSource` (Audio/ISeekableSource.cs) — duration/position/seek
- `ICooperativeVideoReadInterrupt` (Video/ICooperativeVideoReadInterrupt.cs) — yield from blocking reads
- `IVideoSinkD3D11GlBorrowSetup` / `IHardwareD3D11GlInteropSource` — D3D11↔GL device negotiation
- `IHardwareVideoInterop` — generic hardware-decode descriptor

This à-la-carte design is excellent. `PortAudioOutput` (PortAudioOutput.cs:25) implements `IAudioSink, IClockedSink, IFlushableSink, IPlaybackClock` in one class and that's the canonical "fully clocked sink" example.

### 1.2 The entry point `MediaPlayer`

`S.Media.Playback/MediaPlayer.cs:28` is intended as the consumer-facing entry — open a file, get back wired-up players. The shape is good (factory-style `TryOpen`, error string out-param, optional video-sink injection at open time). But:

- 10 public properties (Decoder, VideoRouter, VideoRouterInputId, Video, Audio, AudioSourceId, PlayClock, FreerunClock, Bundle, Av) and three overlapping `TryOpen` overloads. For a *primary* entry point this is too many handles — a consumer needs to understand `MediaContainerDecoder`, `VideoRouter`, `VideoPlayer`, `AudioPlayer`, `AvRouter`, `MediaContainerMegaPlaybackHost`, `IMediaClock`, `MediaClock`, `MediaPlayerDecoderOwnership`, and `MediaPlayerOpenOptions` to use it productively.
- The PortAudio path is documented only in remarks ("add sinks from optional packages"). The basic "play file out of default speakers" flow is **two assemblies and several wiring steps**, not a one-liner — see `S.Media.PortAudio/MediaContainerPlaybackHost.cs:35`. For a soundboard / cue-player you will want a higher-level convenience like `MediaPlayer.OpenAndPlayDefault(path)`.

**Recommendation.** Add a thin `Quick` facade on top of `MediaPlayer` that returns one disposable object with `Play/Pause/Seek/Volume/Dispose` and hides routers/clocks until needed. Keep the current `MediaPlayer` for power users.

### 1.3 The `MediaContainer*` family

Six types share the prefix and they are not all peers:

| Type | File | Role |
|---|---|---|
| `MediaContainerSharedDemux` | `S.Media.FFmpeg/MediaContainerSharedDemux.cs:24` *(internal)* | The actual demuxer — one `AVFormatContext`, two queues, the meat |
| `MediaContainerDecoder` | `MediaContainerDecoder.cs:24` | Public facade exposing one `IAudioSource` + one `IVideoSource` over the shared demux |
| `MediaContainerAvRouter` | `MediaContainerAvRouter.cs:14` *(static factory)* | Builds `AvRouter` from a decoder |
| `MediaContainerPlaybackGraph` | `MediaContainerPlaybackGraph.cs:26` | Non-disposable holder grouping decoder + players + clock + router |
| `MediaContainerMegaPlaybackHost` | `MediaContainerMegaPlaybackHost.cs:67` | Single-`Dispose` owner over decoder + players + router + freerun clock, with bitflag ownership |
| `MediaContainerPlaybackHost` | `S.Media.PortAudio/MediaContainerPlaybackHost.cs:35` | PortAudio-specific "build a sink for me" helper |

The naming is confusing in two ways:

1. **"PlaybackHost" means two different things** depending on the assembly (FFmpeg ⇒ "Mega" lifecycle bundle; PortAudio ⇒ helper to wire up PA). Same word, different concept.
2. **"AvRouter" is not a router** in the sense `AudioRouter` and `VideoRouter` are routers (graph of sources × sinks). It is a *coordination façade* over `IAvPlaybackSession` that knows to call `FlushCodecPipelines` on pause/seek. The doc-comment says exactly this (AvRouter.cs:7–14) but the name still misleads.

The classes themselves are fine — they are documented, ownership flags are explicit, and DEBUG/Release teardown is consistent. But the **public taxonomy** is hard to internalize: `Decoder`, `PlaybackGraph`, `MegaPlaybackHost`, and `AvRouter` are all *almost* the same concept (a bag of related pieces), distinguished by whether they own disposal and whether they bundle the `AvRouter`. The "Tier F row 24 stepping stone" comment in MegaPlaybackHost.cs:40 admits these are evolutionary stages that have not been collapsed.

**Recommendation.**
- Rename `AvRouter` → `MediaSession` or `AvCoordinator`. It is not a router.
- Drop `MediaContainerPlaybackGraph` (non-disposable, no clear job that `MegaPlaybackHost` doesn't do better) or fold it into `MediaContainerMegaPlaybackHost` with an ownership flag set to `None`.
- Rename `MediaContainerMegaPlaybackHost` → `MediaContainerPlaybackBundle` (or similar). "Mega" reads as a placeholder that escaped review.
- Rename `S.Media.PortAudio.MediaContainerPlaybackHost` → `PortAudioPlaybackWiring` (or move its statics into `AudioPlayerPortAudioExtensions`) to break the name collision.

---

## 2. Adding a new audio or video backend

### 2.1 Audio backend

Implement `IAudioSink`. Add `IClockedSink` if the device imposes a sample-clock you want to pace the router with. Add `IPlaybackClock` if the device can report wall-time. Add `IFlushableSink` if you can drop in-flight buffers cheaply. Wire it in via `AudioRouter.AddSink` (router) or `AudioPlayer.AddOutput` (player façade).

For a **PortAudio replacement** (e.g. JACK direct, ALSA direct, ASIO direct on Windows), `S.Media.PortAudio/PortAudioOutput.cs` is the model. About 600 lines: ring buffer, callback marshalling via `GCHandle` userdata, calibration of `Pa_GetStreamTime` against `PlayedSamples` for clock smoothing. Half of that is PortAudio-specific. A WASAPI implementer would write ~300 lines of new code.

For a **capture source** (microphone, JACK input, line-in), implement `IAudioSource`. `S.Media.PortAudio/PortAudioInput.cs` and `S.Media.NDI/Audio/NDIAudioReceiver.cs` are working examples; the receiver path is the more general (it also handles format changes mid-stream via atomic snapshots).

**Friction points worth fixing now:**

- The router enforces "all sources and sinks share the nominal sample rate" (AudioRouter.cs:167). If a user wires a 44.1 kHz device alongside a 48 kHz decoder, the only way out is to wrap the sink in `AdaptiveRateAudioSink` (FFmpeg/Audio/AdaptiveRateAudioSink.cs) or pre-resample the source with `ResamplingAudioSink`. There is no automatic resampling at registration time. For a soundboard hosting clips of mixed rates this is a sharp edge — every clip must be pre-resampled to the router's rate before `AddSource`. Consider an `AutoResample` flag on `AddSource` that inserts the wrapper for you.
- `AudioPlayer(int sampleRate = 48000, ...)` (AudioPlayer.cs:75) silently bakes 48000 into the default. Document it in the XML doc or, better, require the caller to pass it explicitly (or to pass the source first and infer).
- There is no "soundboard" abstraction — i.e. a player that owns N short clips and triggers any of them on demand without re-opening the file. Today you would build it on top of `AudioRouter` directly (N `IAudioSource`s, add/remove routes on trigger). Worth turning into a documented sample under `Tools/`.

### 2.2 Video backend

Implement `IVideoSink`. Declare which `PixelFormat`s you accept zero-copy; the negotiator (Video/VideoFormatNegotiator.cs) will only insert a `VideoCpuFrameConverter` when nothing matches. For hardware-interop sinks (D3D11↔GL), add `IVideoSinkD3D11GlBorrowSetup`. Register via `VideoRouter.AddOutput` (in `S.Media.FFmpeg.Video.VideoRouter`).

**There IS a video router**, contrary to one of the working notes — `S.Media.FFmpeg/Video/VideoRouter.cs:57`. It does *one-to-many fan-out* (each output is owned by at most one input; one input can drive many outputs). NV12 dma-buf, P010, P016, and CPU NV12 are fanned out by refcount/view (VideoRouter.cs:32–44). This is exactly the foundation needed for multi-monitor, NDI mirror, and recording-while-displaying flows.

It is **not** the *N-to-1* compositor you mentioned wanting in the future (multiple inputs blended into one frame). That doesn't exist anywhere yet and is the largest gap for cue/qlab-style scenarios where you want layered video.

For a **DeckLink / Vulkan / DirectX present sink**, the work is straightforward — `SDL3GLVideoSink` (S.Media.SDL3/SDL3GLVideoSink.cs:47) and the Avalonia control (S.Media.Avalonia/VideoOpenGlControl.cs) are the references. Both delegate the heavy lifting (color conversion, HDR transfer, plane uploads) to `S.Media.OpenGL/YuvVideoRenderer.cs`, which means **a new GL-based sink reuses 90 % of the rendering pipeline** by just owning a window/context.

**Friction points:**

- `IVideoSink.Submit` runs on the clock driver thread (VideoPlayer.cs:24–29). Slow sinks therefore stall every other subscriber. The framework offers `VideoSinkPump` and a `VideoSinkPumpAttachOptions` parameter to `VideoRouter.AddOutput`, but the contract on `IVideoSink` does not advertise this. A new implementer might block by default and only learn from a stutter under load. Either document this hard-and-loud on `IVideoSink`, or — better — wrap every sink in a pump by default and offer an `synchronous: true` opt-out.

---

## 3. Multi-input → multi-output capability

### 3.1 Audio: excellent

`AudioRouter` (S.Media.Core/Audio/AudioRouter.cs) is the framework's strongest piece. N sources × M sinks, per-route `ChannelMap`, per-route `Gain` with click-free ramps (AudioRouter.cs:60–66), summation mixing, fully dynamic add/remove while running, optional per-sink pump for slow sinks (NDI), `SlaveTo` for sample-accurate pacing against a chosen clocked sink, plus a documented multi-output drift story (AudioRouter.cs:67–80) using `AdaptiveRateAudioSink` per leaf when the master can't speak for all hardware. The 2106-line `ChannelMap.cs` with SIMD fast paths (14 `TryAccumulate*` shortcut routines) is real production code.

The model already supports what a soundboard, multitrack mixer, or cue player needs: every source maps to every output independently, gain changes are smooth, dropping a source mid-playback rebuilds the immutable `RouterState` without stopping the loop, and pump pressure is observable per sink. The one missing layer is convenience: there is no DSL or builder for declaring routes — every caller writes `AddSource → AddSink → AddRoute(channelMap)` manually. Worth a small `AudioGraphBuilder` later.

### 3.2 Video: partially there

`VideoRouter` (S.Media.FFmpeg/Video/VideoRouter.cs:57) does 1→M fan-out, including dma-buf refcounted sharing across multiple sinks without copy. Configure-time format mismatches insert `VideoCpuFrameConverter` per branch.

What's missing:
- **N→1 compositor.** No multi-source blend, no PiP, no transition framework, no text overlay. The graphics pipeline (`YuvVideoRenderer`) already has shader infrastructure and supports HDR transfer functions — the right place to grow this is *inside* the GL project, not as a Core abstraction. For now, callers wanting "two videos side-by-side on NDI out" must do it themselves.
- **No `VideoRouter` in `S.Media.Core`.** It lives in `S.Media.FFmpeg.Video` because it depends on `VideoCpuFrameConverter` (FFmpeg `sws_scale`). That's a sensible split, but it means the abstraction can never be used by a consumer that doesn't pull in FFmpeg.AutoGen. Worth pushing the router skeleton + fan-out logic down into Core and leaving only the CPU converter implementation in FFmpeg.

**Recommendation roadmap for compositing.**
1. Define `IVideoCompositor` in Core: takes `(IReadOnlyList<VideoFrame> layers, Span<byte> output)`, runs in shader on the sink's GL context.
2. Implement `GlVideoCompositor` in `S.Media.OpenGL` using existing `YuvVideoRenderer` plane-upload code.
3. Add a `CompositorVideoSink` that exposes N input slots and a single downstream `IVideoSink` output. Each input slot is itself an `IVideoSink` so existing `VideoRouter` wiring works unchanged.
4. Add a separate `TextLayerSource : IVideoSource` (SDF font rendering on GPU, or pre-rasterized via SkiaSharp).

The framework will reach this naturally — just be sure new composite work goes through that interface and not into `VideoRouter` directly.

---

## 4. Image / static-frame support

Today: **no first-class story**. `VideoFormat` (S.Media.Core/Video/VideoFormat.cs:7) carries a `Rational FrameRate` but there is no special handling for `0/1`. `VideoFrame` (Video/VideoFrame.cs:17) carries a single `PresentationTime`; "hold this image until further notice" would require the caller to keep resubmitting it on each `VideoTick`.

What is **there** that helps:

- `DiscardingVideoSink` (Video/DiscardingVideoSink.cs) is the no-op terminator; useful for "video off" states.
- Album cover art / `attached_pic` is handled in `MediaContainerSharedDemux.cs:85, 271, 535, 721` — synthesized to ~30 fps so the decode loop drains. So *playing a JPEG inside an MP3* works. But "load a PNG from disk and display it" does not — there is no image decoder bound to `IVideoSource`.

This matters for the cue/qlab use case: "trigger this PNG with a 200 ms fade-in" is a baseline feature.

**Recommendation.**
1. Add `ImageFileSource : IVideoSource` in a small new project (probably `S.Media.SkiaSharp` or `S.Media.Image`) that loads PNG/JPEG/WebP into a `VideoFrame` once and replays it on every `TryReadNextFrame` until disposed.
2. Add a `StaticFrameSource` (Core-side, no dependencies) that takes an already-built `VideoFrame` and repeats it forever — cheap building block.
3. Extend `VideoPlayer` so that when the source `IsExhausted` and `LoopStaticFrame == true`, the last frame stays on screen instead of going blank. Today the `VideoPlayer` (Video/VideoPlayer.cs:81) just sets `CompletedNaturally` and stops.

Image *fades* / *blends* depend on the compositor described in §3.2 — but the still-frame primitives can land independently and would unlock 80 % of the soundboard/cue use case.

---

## 5. Magic numbers

I went looking specifically for hardcoded sample rates / framerates / buffer sizes that pretend to be configurable but really aren't. Results:

| Value | Location | Verdict |
|---|---|---|
| `48000` | `S.Media.Core/Audio/AudioPlayer.cs:75` — `AudioPlayer(int sampleRate = 48000, …)` | **Problematic default.** A consumer who doesn't pass a rate gets 48 kHz silently. If their decoder is 44.1 kHz the router will reject it (AudioRouter.cs:167). At minimum document this in XML; ideally drop the default and force the caller to be explicit. |
| `48000` | `S.Media.FFmpeg/MediaContainerSharedDemux.cs:230` | **Sentinel only.** Comment is explicit ("placeholder for clock-math fallback paths that never run when HasAudio is false"). Leave as-is. |
| `480` | `AudioPlayer.cs:75`, `AudioRouter.cs:136`, `MediaPlayerOpenOptions.cs:14` | 10 ms @ 48 kHz. Reasonable, configurable. Not a bug — but if `sampleRate ≠ 48000` the implied "10 ms" is wrong. Document or compute as `sampleRate / 100`. |
| `96000` | `S.Media.NDI/Audio/NDIAudioReceiver.cs:90` | Ring capacity in *frames*, not Hz. Comment is explicit ("~2 s @ 48 kHz"). Fine — but if a 96 kHz NDI source arrives the ring is 1 s, not 2. Switch the default to a duration (`TimeSpan.FromSeconds(2)`) and compute frames from `Format.SampleRate`. |
| `64` and `8` burst caps | `S.Media.Core/Clock/MediaClock.cs:284, 298, 312` | Catch-up ceilings after a stall. Fine; not exposed and shouldn't be. |
| `150 ms` late, `8 ms` early | `Video/VideoPlayer.cs:97, 103` | Exposed as settable properties. Good. |
| `8192 floats` resampler scratch | `ResamplingAudioSink.cs:43`, `AdaptiveRateAudioSink.cs:186` | Grows on demand. Fine. |
| `MaxAudioPacketsQueued = 192`, `MaxVideoPacketsQueued = 384` | `MediaContainerSharedDemux.cs:27–28` | Per-stream packet queue depth. Fine for files; might be tight for some HEVC 4K streams with many B-frames. Consider making this a `MediaPlayerOpenOptions` field. |
| `2 s` thread join | `AudioRouter.cs:725, 905` | Shutdown timeout. Fine, not user-visible. |
| `_ringBuffer` size, `_framesPerBuffer` | `S.Media.PortAudio/PortAudioOutput.cs:30–31, 163–166` | Power-of-two, computed from caller-supplied ring length. Not magic. |
| `~50 ppm` drift expectation | `AudioRouter.cs:71` (doc only) | Documented in remarks; never used numerically. Fine. |

**The single real magic number is `AudioPlayer`'s default sample rate.** Everything else is either configurable, documented as a sentinel, or properly scoped as an internal tuning constant.

---

## 6. Allocations on the hot path

The Explore audits confirm: **the per-sample audio loop and per-frame video loop are allocation-free** when no format changes happen. Specifically clean:

- `AudioRouter.RunLoop` (~AudioRouter.cs:800–874): `Span<float>`/`Array.Clear`/`ApplyRoute` — no LINQ, no boxing, no closures, no list growth in the per-chunk path.
- `ChannelMap.ApplyRoute` (ChannelMap.cs:933–1052): 14 SIMD fast-path branches, all zero-alloc.
- `MediaClock.DriverLoop` (Clock/MediaClock.cs:259–323): stack locals reused across iterations.
- `VideoPlayer.OnVideoTick` (Video/VideoPlayer.cs:293–359): `ConcurrentQueue` peek/dequeue (lock-free), event invoke. No allocations.
- `PortAudioOutput` ring callback (PortAudioOutput.cs:418–486): zero GC. `[UnmanagedCallersOnly]`, span over fixed ring memory.

Notable per-frame allocations *outside* the hottest loops:

- `VideoFileDecoder.TryReadNextFrame` software path: one `ArrayPool<byte>.Rent` + one `GCHandle.Alloc(pinned)` per plane per frame, all freed within the same frame (VideoFileDecoder.cs:800–840). This is the right shape — pinning is unavoidable for `sws_scale` — but on a 4K 60p stream that's 8–16 short pins per second per stream. Not a bottleneck; mention it if you ever profile high-throughput batch transcode.
- `AudioFileDecoder.TryReadNextFrame`: `new float[outCapacity * Format.Channels]` per returned frame (AudioFileDecoder.cs:373). For frame-based consumers (router uses `ReadInto`, not `TryReadNextFrame`) this is dormant. If a future component goes the frame-based route, switch to `ArrayPool<float>` here.
- `AudioRouter` Stop/Pause spreads the pump dictionary into an array (AudioRouter.cs:820, `[.. _state.Sinks.Values.Select(e => e.Pump)]`). Off the hot path, fine.

Worth flagging:

- **Boxed event args** in `AudioRouterSinkErrorEventArgs` / `AudioRouterPumpPressureEventArgs` / `VideoRouterPumpPressureEventArgs` — each event raise allocates one event-args object. Pump-pressure on a struggling NDI sink could fire dozens per second. If you ever want a fully steady-state allocation-free pipeline, change to `EventHandler<T>` with `T` as `readonly record struct`, or expose counters and let consumers poll. Not urgent.
- **`VideoFrame` is a class, not a struct.** Comment on VideoFrame.cs:9–13 explains why (8 MB BGRA frames). Keep it. The thing that matters is the `Action _release` closure pattern — that *is* a closure-per-frame allocation in some code paths (e.g. dma-buf creation, VideoFrame.cs:225). Consider replacing `Action? _release` with a `IVideoFrameReleaser` interface + struct pool for the few hot release patterns. Optional optimization.

---

## 7. Code cleanliness

### 7.1 What's good

- Documentation density is very high — every public type has substantive XML doc that explains *why*, not just *what*.
- Ownership and disposal are explicit and consistent: `XxxOwnedParts` flags, "best effort with DEBUG logging" via `MediaDiagnostics.LogError`.
- The split between Core (algorithmic, no third-party deps) and per-backend assemblies (FFmpeg, PortAudio, NDI, SDL3, OpenGL, Avalonia) is clean. Nothing leaks the wrong way.
- Tests are heavy (28 + tests in Core, ~20 each in FFmpeg/PortAudio/NDI dirs). The soak test (`MediaContainerDecoderSoakTests.cs`) and the property-style "live graph seeded" test (`AudioRouterLiveGraphSeededTests.cs`) are genuinely useful.

### 7.2 Where it shows the seams

1. **"Tier E / Tier F / row 24 / row 31" annotations**. These appear throughout the codebase (AudioRouter.cs:82, AvRouter.cs:34, MegaPlaybackHost.cs:40, VideoFileDecoder.cs:39). They reference an external checklist not present in the repo. To a new reader they look like cargo-cult version numbers. Either commit the checklist to `Doc/` and link to it, or strip the references when each item is closed.
2. **Tutorial-text in doc-comments.** Many XML docs read more like a `Doc/` page than an IntelliSense hover. Example: `AvRouter`'s 30-line `<remarks>` with cross-references and deadlock guidance. Consider moving the long-form prose into `Doc/` files and leaving 3–4 lines of XML doc.
3. **Some prefixes are evolving in place rather than redesigned.** "MegaPlaybackHost", "SharedDemux", "AggregatingSink", "MuxPlayheadClock", "EgressPresentationTimeline", "MonitorReceiverPumpFusion" (NDI). NDI especially is dense with broadcast jargon (Egress, Ingest, Mux, Fusion) — defensible, but a new contributor will need a glossary. Add one to `Doc/NDI-Terminology.md`.
4. **`OutputPreview` in HaPlay** has files mirroring framework concepts (`LocalVideoPreviewRuntime`, `NDIOutputPreviewRuntime`, `PortAudioOutputRuntime`). Out of scope here, but if those runtimes embody a useful "wire a sink to a player" recipe, that recipe deserves to come back down into the framework as a sample/helper.
5. **Large files.** `ChannelMap.cs` 2106 lines, `MediaContainerSharedDemux.cs` 1674, `AudioRouter.cs` 1275, `VideoFileDecoder.cs` 1027, `VideoRouter.cs` 739. All justified individually (SIMD permutations, the demuxer is complex), but `ChannelMap` could plausibly split into `ChannelMap.cs` (data + small ops) + `ChannelMap.SimdAccumulate.cs` (the 14 SIMD shortcuts) without losing locality.
6. **`AvRouter` is misnamed** (see §1.3). It's a session façade, not a router.
7. **Doc comments occasionally drift from code.** The memory note from 2026-05-10 already shipped stale — by the time of this review, `S.Media.Playback.MediaPlayer` exists, `VideoRouter` exists, `MediaContainerSharedDemux` exists, `NDI*` clock types exist. Memory is point-in-time; mention is just to flag that the framework moves quickly enough that the project memory needs refreshing.
8. **Untracked files in working tree:** `UI/HaPlay/OutputPreview/PortAudioOutputRuntime.cs`, `Doc/HaPlay-Review-2026-05.md` (does not actually exist on disk — `git status` lists it as untracked but it's been removed). Worth cleaning up.

### 7.3 Tools

`Tools/PlaybackSmoke`, `Tools/VideoPlaybackSmoke`, `Tools/NDIPlayer`, `Tools/NDIReceiver` all currently build and exercise current APIs. `NDIPlayer` (490 LoC) and `VideoPlaybackSmoke` (396 LoC) are non-trivial wall-pace + drift-correction demos and double as documentation. Keep them living, ideally smoke them in CI.

---

## 8. Professional-format readiness

| Capability | Status | Notes |
|---|---|---|
| 32-bit float audio, arbitrary rates | ✅ | `AudioFormat(int sampleRate, int channels)` is sample-format-agnostic at the boundary; everything internally is packed float32. |
| 24-bit / 32-bit integer source PCM | ✅ via FFmpeg | Source converts at decode; framework never sees the original bit-depth. |
| Non-48 kHz routing | ⚠️ | Works, but `AudioRouter` enforces a single nominal rate (AudioRouter.cs:167). Mixed-rate inputs require explicit `ResamplingAudioSink` wrapping. No automatic resample on `AddSource`. |
| Multi-device output at different hardware clocks | ✅ | Documented drift story; `AdaptiveRateAudioSink` per leaf + `PumpPressurePlaybackHintMonitor` is the supported pattern (AudioRouter.cs:67–80). |
| Channel routing / mapping (incl. mono → 5.1, swap L/R, etc.) | ✅ | `ChannelMap` is fully general. |
| Per-channel / per-route gain with click-free ramp | ✅ | `SetRouteGain` linearly interpolates within a chunk. |
| Multi-bus mixing (sub-mix groups) | ⚠️ | Not modeled. Routes go source-to-sink; there's no intermediate bus. Achievable by introducing a `BusSink` that is also an `IAudioSource`, but no such helper exists. |
| 10/12-bit video (P010, P016, Yuv420P10Le, Yuv422P10Le, Yuv444P10Le, Yuv420P12Le) | ✅ | PixelFormat.cs:38–75; GL renderer scales bit depth correctly. |
| HDR transfer (PQ, HLG, sRGB) | ✅ | `VideoHdrTransfer.cs`, inverse-EOTF shaders in `YuvVideoRenderer`. |
| BT.709 / BT.601 color matrices | ✅ | `YuvColorSpace.cs`. |
| BT.2020 / Rec.2020 primaries (wide-gamut) | ❌ | Not present. Matters for UHD HDR. |
| Interlaced video / field metadata | ❌ | `VideoFrame` is progressive only. No field flag, no deinterlace. Broadcast ingest will need this. |
| SMPTE LTC / drop-frame timecode embedding | ❌ | NDI carries 100-ns timecode (NDIVideoTimecodeMode.cs), but no SMPTE LTC, no drop-frame logic, no burn-in. |
| Embedded ancillary data (closed captions, AFD, etc.) | ❌ | Not surfaced anywhere. |
| Image still frames (PNG/JPEG) | ❌ | No image source. Workaround via FFmpeg's `attached_pic` only works inside containers. |
| Multi-input video compositing | ❌ | See §3.2. |
| Hardware decode (VAAPI, D3D11VA, QSV) | ✅ | VideoHardwareDecodeContext.cs, with CPU fallback. |
| Zero-copy GPU paths | ✅ | DMA-BUF on Linux, D3D11 shared handle on Windows, both refcount-shared across multiple sinks. |

For "playout / streaming / NDI demo" the framework is **production-ready**. For "broadcast ingest" or "color-critical grading host" it is not — interlace, BT.2020, and SMPTE timecode would all need work.

---

## 9. Specific recommendations, ranked

**Do soon (small, high payoff):**

1. **Drop the 48000 default on `AudioPlayer`.** Force callers to pass a rate or pass the source first.
2. **Rename `AvRouter` → `MediaSession` (or `AvCoordinator`).** Misnomer; trips every new reader.
3. **Document the "default video-sink Submit runs on the clock thread" contract on `IVideoSink` itself**, not just in `VideoPlayer` remarks. Add a one-line `// Sinks that block must wrap themselves with VideoSinkPump.` warning in XML doc.
4. **Add the missing `ImageFileSource` + `StaticFrameSource`** (see §4). Unlocks cue-player/soundboard use cases.
5. **Strip or commit the Tier E/F/row-N annotations.** Either link the checklist or remove them.

**Do soon-ish (medium):**

6. **Auto-resample at `AudioRouter.AddSource` when rates mismatch** (opt-in flag). Stops surprising rejections in soundboard scenarios.
7. **Wrap every `IVideoSink` in a `VideoSinkPump` by default**; opt-out only when the sink is provably fast (GL on dedicated GPU, etc.).
8. **Add a `Quick` facade** on `MediaPlayer` with one-line open + play, hiding routers and clocks behind defaults.
9. **Consolidate `MediaContainerPlaybackGraph` and `MediaContainerMegaPlaybackHost`.** Rename "Mega" out. Reconcile naming clash with PortAudio's `MediaContainerPlaybackHost`.
10. **Make `NDIAudioReceiver`'s ring capacity express a duration**, not a sample count.

**Plan for, do when needed:**

11. **N→1 compositor** (§3.2 roadmap). The interface, then a `GlVideoCompositor`, then text overlays.
12. **Sub-mix bus support in `AudioRouter`** for mixing-console-style routing.
13. **BT.2020 primaries** in `YuvColorSpace` and the GL renderer.
14. **Interlace metadata on `VideoFrame`** and a deinterlace converter (FFmpeg `yadif` is the obvious choice).
15. **SMPTE LTC + drop-frame timecode**: surface on `VideoFrame`, plumb through NDI timecode.
16. **Boxed event-args → struct events** if you ever want sustained zero-allocation steady-state under heavy pressure (low priority).

**Cleanup, no rush:**

17. Split `ChannelMap.cs` into a SIMD partial. Move long-form XML prose into `Doc/`.
18. Add a `Doc/NDI-Terminology.md` glossary (Egress/Ingest/Mux/Fusion/Aggregating).
19. Refresh `Doc/` with current state — the project memory is already 8 days stale and the codebase has grown since.

---

## 11. Correctness pass — addendum 2026-05-18

Pass 1 (above) explicitly assumed the code was correct and focused on shape/ergonomics. This addendum does the opposite: I went back and read the highest-risk files skeptically, then verified each candidate defect against the actual source before listing it here. I dispatched three Explore audits for breadth, then personally re-read the cited lines for each finding — the agent audits surfaced about a dozen candidates; roughly half were real and the other half were misreadings or overstatements. Both lists are included so the rejections are auditable.

### 11.1 Confirmed defects

**C1 — `AddReference` TOCTOU race on all four `VideoFrame` backings.**
Files: `S.Media.Core/Video/VideoDmabufNv12Backing.cs:70-75`, `VideoDmabufP010Backing.cs`, `VideoDmabufP016Backing.cs`, `VideoWin32Nv12Backing.cs` (identical pattern in each).

```
public void AddReference()
{
    if (Volatile.Read(ref _closed) != 0)
        throw new ObjectDisposedException(nameof(VideoDmabufNv12Backing));
    Interlocked.Increment(ref _refCount);
}
```

The read of `_closed` and the increment of `_refCount` are not atomic together. Thread A enters `AddReference`, reads `_closed == 0`, and is about to increment. Thread B calls `Dispose()`, decrements `_refCount` 1→0, sets `_closed = 1`, and `libc_close`s the fds. Thread A now increments `_refCount` from 0 to 1 and returns — successfully — handing the caller a "new reference" to a backing whose underlying fds are already closed. When the caller later disposes the frame holding that reference, `_refCount` goes back to 0 and `Interlocked.Exchange(_closed, 1)` returns nonzero (so we don't double-close), but the fds the caller thought were live were never live. On Linux this usually surfaces as `EBADF` on the next dma-buf import, or — worse, if the kernel has recycled the fd — as a silent read from an unrelated file.

Routes that exercise this: `VideoFrame.CreateNv12DmabufSharedReference` (VideoFrame.cs:249), `CreateP010DmabufSharedReference` (cs:320), `CreateP016DmabufSharedReference` (cs:341), `CreateNv12Win32SharedReference` (cs:391). `VideoRouter` fan-out uses these on every multi-output dma-buf frame (S.Media.FFmpeg/Video/VideoRouter.cs:32–44), so the race is reachable from normal operation, not just shutdown.

**Fix:** replace the two-step read+increment with a CAS loop:
```
while (true) {
    var n = Volatile.Read(ref _refCount);
    if (n <= 0) throw new ObjectDisposedException(...);
    if (Interlocked.CompareExchange(ref _refCount, n + 1, n) == n) return;
}
```
The `_closed` flag becomes redundant — refcount == 0 *is* "closed".

**C2 — `PortAudioOutput.Flush()` resets the calibration flag *after* `Pa_StartStream`.**
File: `S.Media.PortAudio/PortAudioOutput.cs:125-137`.

```
public void Flush()
{
    ...
    Native.Pa_AbortStream(_stream);
    Volatile.Write(ref _writeIndex, 0);
    Volatile.Write(ref _readIndex, 0);
    var err = Native.Pa_StartStream(_stream);              // ← callback may fire here
    if (err != PaError.paNoError) ...
    Volatile.Write(ref _streamSmoothCalibrated, 0);        // ← reset too late
}
```

After `Pa_StartStream` succeeds, the PortAudio audio thread can invoke the callback before line 136 runs. That callback reads `_streamSmoothCalibrated`, sees `1` (left over from the previous stream segment), and skips the calibration block at `PortAudioOutput.cs:460-471`. `_segmentStreamT0` and `_segmentPlayed0Samples` therefore retain values from the *previous* segment, while `Pa_GetStreamTime(...)` now returns the new segment's stream time (PortAudio's stream time resets per stream-start cycle). `ElapsedSinceStart` (cs:92-107) then computes `_segmentPlayed0Samples / sr + (st - _segmentStreamT0)` where the two halves of the sum are anchored to different segments. The result is monotonicity-broken `ElapsedSinceStart` until the next time a fresh callback happens to trip the calibration check — which, since `_streamSmoothCalibrated` will be reset by line 136 a few microseconds later, recovers quickly but not before the master clock has reported a glitched value. Downstream `IMediaClock.SetMaster` consumers will see a position jump on every `Flush()`.

`Stop()` at lines 244-264 does not have this problem — the flag is reset *after* `Pa_StopStream`/`Pa_CloseStream`, so no callback runs in the window.

**Fix:** move `Volatile.Write(ref _streamSmoothCalibrated, 0)` to *before* `Pa_StartStream`, between the index resets and the start call.

**C3 — `AudioFormat(48000, 0)` sentinel leaks a real-looking sample rate.**
File: `S.Media.FFmpeg/MediaContainerSharedDemux.cs:230`.

```
// Sentinel format for video-only files: 0 channels signals "no audio" to consumers.
// The 48000 Hz rate is a placeholder for clock-math fallback paths that never run when
// MediaContainerDecoder.HasAudio is false (MediaPlayer skips AudioPlayer creation).
AudioCodecName = "";
Audio.Format = new AudioFormat(48000, 0);
```

The comment is right about today's call sites, but the guarantee only holds because `MediaPlayer` checks `HasAudio` before constructing `AudioPlayer` (S.Media.Playback/MediaPlayer.cs:239). A third-party consumer that builds their own router and reads `decoder.Audio.Format.SampleRate` without first checking `Channels` (or `HasAudio`) will get `48000` from a stream that has no audio. The framework already enforces a "your source's `SampleRate` must equal the router's nominal rate" check (S.Media.Core/Audio/AudioRouter.cs:167) so this is the kind of value that *looks* correct and won't fail-fast. **Fix:** use `AudioFormat(0, 0)` and document that consumers must guard with `HasAudio`.

**C4 — `AudioRouter` defaults `pumpCapacityChunks` to 8 with no per-sink reasoning.**
File: `S.Media.Core/Audio/AudioRouter.cs:136-153`. Not a bug per se, but worth flagging because the choice is invisible to consumers: each sink defaults to 8 mixed chunks × `chunkSamples` samples of headroom. At the framework's default `chunkSamples = 480`, that's 80 ms per sink. For NDI senders that are typically pacing themselves and want a buffer, 8 chunks is fine. For a hardware PortAudio sink which itself has a ring buffer, the pump capacity adds *another* tier of latency. Document, or auto-shrink for sinks that implement `IClockedSink`.

### 11.2 Suspect, not confirmed

**S1 — Event raises that may run under `_gate`.**
`AudioRouter.RetargetSlaveClock` (cs:459-471) constructs a `SinkSlavedRouterClock` *inside* the `_gate` lock. Today the constructor just captures a `Func<IClockedSink?>` closure and does not call back into the router, so the pattern is safe. If anyone ever adds initialization that subscribes to a router event, you'll have a reentrancy/deadlock candidate. Either move the construction outside the lock or assert no-side-effect via comment.

**S2 — Per-event-args allocation under pressure.**
`AudioRouterPumpPressureEventArgs` and `VideoRouterPumpPressureEventArgs` are classes. A drowning NDI sink can raise pump-pressure many times per second. Not a correctness bug — just GC pressure that compromises the otherwise-zero-allocation steady state described in §6. Switch to `readonly record struct` event args when this is profiled.

**S3 — `AudioFileDecoder.TryReadNextFrame` allocates `new float[]` per call.**
File: `S.Media.FFmpeg/Audio/AudioFileDecoder.cs:373`. Dormant in the typical FFmpeg → `AudioRouter` path (which uses `ReadInto`), but live for any consumer that opens an `AudioFileDecoder` and pulls `AudioFrame`s. Switch to `ArrayPool<float>` with the lifetime tied to a release callback (mirroring the `VideoFrame.release` pattern).

**S4 — `VideoOpenGlControl` detach + render race.**
File: `S.Media.Avalonia/VideoOpenGlControl.cs`. The Explore agent flagged a race between `OnDetachedFromVisualTree` setting `_sinkDisposed = true` and a render callback firing concurrently. I did not re-read this file myself; flagging it as suspect — the pattern (volatile flag + lock around pending-frame disposal) is generally OK but worth a careful audit because the failure mode (disposing a frame that GL is still uploading) is hard to reproduce.

**S5 — `Nv12Win32SharedHandleGpuUploader` keyed-mutex pairing on failure paths.**
Same caveat: agent flagged early-return paths that may skip a keyed-mutex release. I did not verify line-by-line. The symptom would be a Windows-only D3D11 hang the next time the same texture is acquired. Worth a focused read.

**S6 — `swr_init` after `swr_close` on seek.**
Agent claimed `swr_close` doesn't exist in FFmpeg — that's wrong; `swr_close` is a real function. But the underlying question is still open: after a seek, does `MediaContainerSharedDemux` correctly drain the resampler's internal delay buffer (`swr_get_out_samples` / `swr_convert` with `null` input until 0) before the next decoded packet, or do residual samples from before the seek leak into the post-seek output? I did not verify either way — worth a unit test that seeks mid-stream and checks the first post-seek samples for discontinuity.

### 11.3 Agent claims I rejected after verifying

For honesty's sake, the items below were flagged by the audits but turned out to be misreadings. I'm listing them so you don't get them re-raised next time and waste a re-investigation.

- **"VideoRouter fan-out leaves primary in inconsistent state on branch exception"** (`S.Media.FFmpeg/Video/VideoRouter.cs:647-665`). The catch block correctly disposes only un-submitted branchFrames; `frame = null!` after the primary submit prevents double-dispose. The primary has accepted ownership and the code respects that. Not a bug.
- **"PortAudio calibration race between field writes and flag write"** (`PortAudioOutput.cs:460-469`). The `Thread.MemoryBarrier()` at line 468 is a full fence; the subsequent `Volatile.Write` provides release semantics. Reader's `Volatile.Read` is an acquire. Pattern is correct.
- **"`NDIEgressMuxPlayheadClock` allows backward jumps up to 1 second"** (`S.Media.NDI/Clock/NDIEgressMuxPlayheadClock.cs:67-68`). `_maxMuxTicks` only updates if `t > _maxMuxTicks`. Monotonic by construction. Agent confused this clock with the receiver-side ingest clock, which does have a re-anchor on backward seeks > 1 s (NDIIngestPlaybackClock.cs) — that one is intentional and correct.
- **"`NDIAudioAggregatingSink` discards partial tail at Dispose"** (`NDIAudioAggregatingSink.cs:69-91`). Dispose flushes `_filledFloats` to the inner sink as long as it's a multiple of `Channels`, which it always is (Submit validates at line 47). No tail loss.
- **"`AudioRouter` gain-ramp dictionary read-modify-write is racy"** (`AudioRouter.cs:843-848`). The run loop is single-threaded; `_currentGains` is written only by this thread. `_routeTargetGains` may change concurrently, but the worst case is that one chunk ramps to value X and the next chunk ramps from X to Y — which is exactly the documented click-free behavior. Not a bug.

### 11.4 Updated recommendation priority

Add to the §9 ranked list:
1. **C1 first.** The four-backing TOCTOU race is the most dangerous finding — silent fd corruption with no panic, and on the user-reachable fan-out path. Single CAS loop fix.
2. **C2 second.** One-line reorder in `PortAudioOutput.Flush`; trivially testable by repeatedly flushing and asserting `ElapsedSinceStart` monotonicity.
3. **C3 / S2 / S3** at convenience; none are user-facing crashes.
4. **S4 / S5 / S6** schedule a focused re-audit — they may be real and I didn't have time to confirm.

### 11.5 Methodology note

Three Explore subagents read the highest-risk files. They returned ~14 candidate findings. I personally re-read the cited lines for each and accepted 4 confirmed defects + 2 lower-priority issues, kept 6 as "suspect — worth verifying", and rejected 5 outright. The keep-rate is consistent with my prior experience: skeptical agent audits surface roughly 50 % real signal, the rest being doc-comment misreads or transcription errors. **For the suspects in §11.2 I have written down enough context that a follow-up pass can confirm or reject each in under 15 minutes per item.**

---

## 10. One-paragraph summary

The framework's core architecture is in a very good place. The interface surface for extension (`IAudioSource`/`IAudioSink`/`IVideoSource`/`IVideoSink`, with optional capability mixins) is genuinely minimal — adding a new audio or video backend is a few hundred lines and no fights with the framework. The audio routing layer is already what a soundboard or cue player needs: N×M with channel maps, click-free gain, dynamic mutation, slow-sink isolation. Hot paths are allocation-free. The big remaining gaps are higher-level convenience for end consumers (a one-line "play this file" facade), explicit support for still images and N-to-1 video compositing, and one missing default the framework should stop pretending to choose for the caller (`AudioPlayer`'s baked-in 48000 Hz). Naming around `AvRouter` / `MediaContainer*` should be sorted before more code anchors on the current shape. Once those land — plus an `ImageFileSource` and the compositor sketch in §3.2 — this is a credible foundation for media players, cue stacks, and soundboards alike, with broadcast-ingest features (interlace, BT.2020, SMPTE timecode) the obvious next frontier.
