# MediaFramework — Critical Review & Refactor Plan (2026‑05‑22)

> Scope: everything under `MediaFramework/**` (Core, FFmpeg, Playback, NDI,
> OpenGL, PortAudio, SDL3, Avalonia, Quick, SkiaSharp, Tools/Tests, plus the
> protocol helper libs PMLib/OSCLib/PALib/JackLib/NDILib).
> The HaPlay UI projects are out of scope as requested.
>
> Codebase snapshot used for this review:
>
> - 23 production projects, ~43 kLOC outside Tools/Tests.
> - Biggest individual files: `MediaContainerSharedDemux.cs` (1591), `AudioRouter.cs` (1479) +
>   `ChannelMap.SimdAccumulate.cs` (1831), `YuvVideoRenderer.cs` (1448), `VideoFileDecoder.cs` (1141),
>   `Nv12Win32SharedHandleGpuUploader.cs` (789), `VideoRouter.cs` (778).
> - 13 audio/video interfaces in `S.Media.Core` (sources/outputs/clocks/converters/deinterlacers/etc.).
>
> Breaking API changes are explicitly on the table. The goal is **a leaner, easier-to-consume
> framework for downstream library users**, while preserving the heavy lifting (hardware decode,
> NDI, multi-output routing, drift handling) that already works.
>
> **Direction update (post‑review discussion, 2026‑05‑22)**: see §10 for the consolidated
> follow-up — `Source` / `Output` naming everywhere, NDI collapsed to one source + one output
> type, Linux + Windows only (drop macOS scaffolding), first-class `Stream` IO, a six‑line
> minimum-viable playback API, and a config‑driven composition surface.

---

## 1. TL;DR

The framework has matured to the point where the *capabilities* are excellent
but the *surface area is overwhelming for new consumers*. Several themes:

1. **One job, several public APIs**. "Play a media file" is reachable through
   `QuickPlayer`, `MediaPlayer.TryOpen*` (6 overloads), `MediaContainerPlaybackBundle`,
   `MediaContainerSession`, `MediaPlaybackSession`, `IAvPlaybackSession`,
   `AvPlaybackCoordinator`, `MediaContainerDecoder` + manual `VideoPlayer` /
   `AudioPlayer`. Each one exists for a real reason, but together they form an
   identity tree that a first-time consumer cannot navigate.
2. **Legacy debris that compiles**. `VideoOutputRouter` is `[Obsolete]` and used
   only by itself. `VideoFileDecoder` is still public and ~1100 LOC but the
   only production path through `MediaContainerDecoder` always uses
   `MediaContainerSharedDemux` (the `LegacyVideo`/`LegacyAudio` properties hard‑return
   `null`). Five hardware‑interop "descriptor" types
   (`MetalIosurfaceNv12Interop`, `VulkanExternalNv12Interop`,
   `WindowsNv12D3D11TextureInterop`, `WindowsNv12SharedHandleInterop`,
   `LinuxDmabufNv12Interop`) are referenced from nowhere outside themselves.
3. **Wide value types and ctor zoos for one job**. `VideoFrame` has a single
   monolithic 10-parameter constructor that hand-validates 6 mutually exclusive
   HW backings; the legacy three-arg `AddRoute` and explicit-`routeId`
   `AddRoute` both ship; `MediaPlayer.TryOpen` has 5+ static overloads with
   nearly identical signatures.
4. **Optional state communicated through global statics**.
   `AudioRouterAutoResample.SourceWrapper`, `VideoCpuFrameConverterRegistry.Factory`
   /`CanConvertProbe`, `VideoDeinterlacerRegistry.Factory`, and
   `FFmpegRuntime.EnsureInitialized` form an implicit "you must call init somewhere
   first" contract that is invisible at the API site.
5. **Pump/clock language is internally consistent but externally dense**.
   `IRouterClock`, `OutputSlavedRouterClock`, `IClockedOutput`, `IPlaybackClock`,
   `IMediaClock`, `IPlaybackTimeline`, `IPlaybackPlayhead`, `CompositePlaybackClock`,
   `VideoPtsClock`, `NDIIngestPlaybackClock`, `NDIEgressMuxPlayheadClock`,
   `NDIAlignedRouterClock`, `IngestSlavedRouterClock`, `WallClockRouterClock`
   - that's 13 named time concepts before a consumer can wire a simple "play
   audio + video, drive a video clock from PTS" graph.
6. **Diagnostics ceremony is repeated everywhere.** Every `Dispose()` in the
   framework writes the same `#if DEBUG / MediaDiagnostics.LogError / #else
   /* best effort */` block 3–10 times. It's correct, but it's ~5% of the
   total LOC at this point.

Below is per-area detail plus a prioritized refactor plan.

---

## 2. Findings by area

### 2.1 `S.Media.Core` — what works

- `IAudioSource`/`IAudioOutput` and `IVideoSource`/`IVideoOutput` are tight and
  reasonable. The two videos sides differ from audio in pulling **frames** vs.
  filling **chunks**, which is the right shape for the two domains.
- The `AudioRouter` "explicit per‑route ChannelMap + per‑route gain" model is
  rare in OSS frameworks and a real differentiator. Click-free fades and
  SIMD-fast-path channel maps are well executed.
- `MediaClock` driving `AudioTick`/`VideoTick`/`PositionChanged` from one
  driver thread with burst-tolerance is solid. The master swap that preserves
  position is exactly the behaviour a host needs and is hard to do right.
- The `IVideoOutputD3D11GlBorrowSetup` cooperative-borrow protocol for Win32 NV12
  is non-trivial to design and the comment trail is good.

### 2.2 `S.Media.Core` — what's heavy

#### 2.2.1 `VideoFrame` — the constructor is fighting itself

`VideoFrame` (575 LOC) ships a single 10-parameter ctor with **four** mutually
exclusive HW backings (`dmaBufNv12 / dmaBufP010 / dmaBufP016 / win32Nv12`) and
hand-checks every binary combo in 6 explicit `throw`s before doing format
validation. There are then 7 named static factories (`CreateNv12Dmabuf`,
`CreateNv12DmabufSharedReference`, `CreateP010Dmabuf`, `CreateP010DmabufSharedReference`,
`CreateP016Dmabuf`, `CreateP016DmabufSharedReference`, `CreateNv12Win32Shared`,
`CreateNv12Win32SharedReference`) plus `TryCreateNv12CpuFanOutViews`. Each
follows the same pattern: ref-count, build planes/strides stub, call ctor.

Proposed change (breaking):

- Introduce a `VideoFrameHardwareBacking` discriminated record (or a small abstract
  `HardwareBacking` with concrete `Dmabuf` / `Win32SharedHandle` subclasses) so
  the ctor takes **one** backing parameter and the mutual-exclusion checks
  collapse to a type check.
- Move the 7 named factories into a single `VideoFrame.Hardware.From(...)` API
  on the backing type, keeping `VideoFrame.CreateCpu(...)` and
  `VideoFrame.Wrap(planes, strides, release)` as the two end-user verbs.
- This is also a chance to fold `IDisposable disposableRelease`
  vs. `Action? release` (currently both can be passed and both fire) into one
  release strategy.

#### 2.2.2 `VideoRouter` vs `AudioRouter` — asymmetric semantics for similar names

`AudioRouter`: multi-source → multi-output, outputs **sum**; routes have ChannelMap +
gain; nominal sample rate fixed; dynamic mutation; immutable state snapshots.
`VideoRouter`: **one** input per output (exclusive ownership), multiple outputs
allowed per input; pixel-format negotiation lead is the "primary"; branch
converters per output. Different conceptual model, same name suffix.

Two-source: this is the **right** model for video (compositing happens via
`VideoCompositorSource`/`IVideoCompositor`, not router summing). But the shared
name causes consumer confusion and the routers don't actually share code or an
interface despite the obvious overlap (id management, pump pressure events,
dispose order).

Proposed change: extract a tiny `IMediaRouter<TFrame, TSink>` shape (id alloc,
PumpPressure event, AddOutput/RemoveOutput, Dispose contract) so the two share
the boilerplate; rename `VideoRouter` to `VideoFanOut` (or keep names, but
document the asymmetry on the type docs explicitly).

#### 2.2.3 Clocks: 13 named things for one concept

`IMediaClock` (UI-facing), `IPlaybackTimeline`/`IPlaybackPlayhead` (read-only
views), `IPlaybackClock` (master input), `MediaClock` (driver),
`WallClockRouterClock`/`OutputSlavedRouterClock`/`IngestSlavedRouterClock`
(producer pacing), `VideoPtsClock` (PTS-derived playback clock),
`CompositePlaybackClock` + `CompositePlaybackClockBlend` (priority merge),
`NDIIngestPlaybackClock`/`NDIEgressMuxPlayheadClock`/`NDIAlignedRouterClock`
(NDI specifics), `IRouterClock` (router pacing), plus `MediaClockExtensions`/
`PlaybackTimelineClockExtensions`/`MediaContainerSession.SeekCoordinated`.

What consumers actually want:
- A **playhead** they can read and seek.
- An **audio-mastered** mode (output reports samples played, clock follows).
- A **video-PTS** mode (clock follows last presented frame PTS).
- A **free-running** mode (wall clock).
- The router pacing is an implementation detail of the framework, not the
  consumer's API surface.

Proposed change (breaking but big win):

- Collapse `IPlaybackClock`, `IPlaybackTimeline`, `IPlaybackPlayhead` into one
  `IPlayhead` (read) + `IPlaybackController` (Start/Pause/Seek/SetMaster).
- Hide `IRouterClock`/`WallClockRouterClock`/`OutputSlavedRouterClock` as
  `internal` — consumers never construct them; the router decides.
- Keep `CompositePlaybackClock`, `VideoPtsClock`, `NDIIngestPlaybackClock` as
  the three named time sources end users construct, document them as
  "pluggable masters for `IPlaybackController.SetMaster`".
- The two NDI helpers `NDIEgressMuxPlayheadClock` and `NDIAlignedRouterClock`
  are referenced only by themselves — see §2.5.

#### 2.2.4 `AudioRouter`/`AudioPlayer`/`AudioGraphBuilder` — three ways to wire the same graph

A consumer can:
1. Construct an `AudioRouter`, call `AddSource`/`AddOutput`/`AddRoute` manually.
2. Use `AudioGraphBuilder` for `.AddSource().AddOutput().Connect()` fluent.
3. Use `AudioPlayer.AddOwnedSource/AddOutput/Connect` (which forwards to router).

All three exist, all do the same thing, none of them is clearly "the" entry
point. `AudioGraphBuilder.ConnectLast` is the most ergonomic for the
"one clip, one device" case but isn't surfaced anywhere.

Proposed change:
- Pick **one** front door (`AudioPlayer`), document `AudioRouter` as power-user.
- Either delete `AudioGraphBuilder` or fold its `ConnectLast` semantics into
  `AudioPlayer.AddOutputAndConnectLast()`.
- Keep the `AudioRouter` low-level surface intact but mark `AudioGraphBuilder`
  as the only fluent wrapper users see for the rare case they touch the router
  directly.

#### 2.2.5 Legacy `AddRoute(sourceId, outputId, ...)` vs. explicit `routeId`

Two overloads exist; the legacy one synthesizes a `routeId` from the (src,output)
pair using `` as a separator and supports replace-by-pair semantics. The
explicit `routeId` overload was added in "Phase C" for per-cell matrix routes
(HaPlay's audio matrix wants multiple routes per pair).

Proposed change: keep the explicit `routeId` overload, deprecate the
key‑synthesizing one. It's a small, contained breaking change.

#### 2.2.6 Hardware backing types & interop scaffolding

These exist in `S.Media.Core/Video/`:

- `HardwareVideoInteropDescriptors.cs`, `IHardwareVideoInterop.cs`,
  `HardwareVideoWin32Nv12.cs` — used by OpenGL.
- `VideoDmabufNv12Backing` / `P010Backing` / `P016Backing`,
  `VideoWin32Nv12Backing` — used by FFmpeg + OpenGL.
- `VideoDmabufCpuReadback` — used by `VideoRouter` for mmap fallback.
- **Unused everywhere except themselves**:
  - `MetalIosurfaceNv12Interop` (109 LOC)
  - `VulkanExternalNv12Interop` (138 LOC)
  - `WindowsNv12D3D11TextureInterop` (122 LOC)
  - `WindowsNv12SharedHandleInterop` (129 LOC)
  - `LinuxDmabufNv12Interop` (83 LOC)

That's ~580 LOC of dead scaffolding shipped in `S.Media.Core`. Either drive a
real consumer (e.g. a Vulkan or Metal output) or delete them; they're not pulling
their weight as "future‑proofing."

#### 2.2.7 Video CPU helpers, fade/cut sources

- `FadeFromBlackVideoSource` and `CutVideoSource` are used **only** by
  `UI/HaPlay/Playback/PlaybackVideoPipeline.cs`.
- `PixelFormatConvertingVideoSource` is used only by HaPlay too.
- `StaticFrameSource` is used only inside Core.
- `VideoCompositorSource` (323 LOC) + `CpuVideoCompositor` (359 LOC) + `BlendMode`
  + `LayerOpacityTween` + `LayerTransform2D` — referenced from CompositorSmoke
  and HaPlay's LogoFallbackVideoSink (uses `VideoCpuOpacity`). The CPU
  compositor is the **only** consumer of `BlendMode` outside the GL one.

These are useful primitives but they're a "video effects" sub-module
masquerading as core abstractions. Suggest moving them to an
`S.Media.Effects` (or `S.Media.Video.Effects`) project so `S.Media.Core` stays
focused on transport.

### 2.3 `S.Media.FFmpeg`

#### 2.3.1 `VideoFileDecoder` is a duplicate of `MediaContainerSharedDemux.VideoTrack`

`VideoFileDecoder.cs` (1141 LOC) implements the full FFmpeg video decode path
standalone. `MediaContainerSharedDemux` (1591 LOC) reimplements the same thing
inside a shared-mux wrapper. The `MediaContainerDecoder.LegacyVideo` property
that used to expose `VideoFileDecoder` now always returns `null`:

```csharp
/// <summary>Reserved for API stability; always <c>null</c>.</summary>
public VideoFileDecoder? LegacyVideo => null;
```

`VideoFileDecoder` is still public, still referenced by `VideoHardwareDecodeContext`
docs, but no production consumer touches it. The hardware acceleration code,
PassThroughArena, descriptor caching, swscale management — all of it has been
re‑implemented inside `MediaContainerSharedDemux` because that path needs the
shared format context.

Proposed change (breaking):
- Either **delete** `VideoFileDecoder` outright (preferred — it's pure
  duplication maintained at high cost) or extract its decode-stage helpers
  into an internal partial class shared by both. The 1141 LOC saving is
  immediate; on top of that you stop having to keep two HW-decode codepaths
  in sync.
- Same review for `AudioFileDecoder` (420 LOC). Currently used by `PlaybackSmoke`
  tool and tests; the production path is `MediaContainerSharedDemux.AudioTrack`.
  At minimum, `LegacyAudio` should be removed.

#### 2.3.2 `VideoOutputRouter`

Already `[Obsolete]`; only referenced by itself. Delete.

#### 2.3.3 `MediaContainerSession` vs `MediaPlaybackSession` vs `IAvPlaybackSession` vs `MediaContainerPlaybackBundle` vs `MediaPlayer`

That's 5 "play this thing" wrappers, each owning a slightly different set of
parts:

| Type | Owns decoder? | Owns players? | Has flush helper? | Disposable? |
|---|---|---|---|---|
| `IAvPlaybackSession` | no | no | no | no |
| `MediaPlaybackSession` | no | no | no | no |
| `MediaContainerSession` | no | no | yes (FFmpeg-specific) | no |
| `MediaContainerPlaybackBundle` | configurable via flags | configurable | inherited | yes |
| `MediaPlayer` (S.Media.Playback) | configurable | yes | inherited | yes |

The flags‑based ownership on `MediaContainerPlaybackBundle` (`Decoder | VideoPlayer | AudioPlayer | VideoRouter | FreerunMediaClock`) is a smart pattern; the rest of the layered façade is too much.

Proposed change:
- Treat `MediaPlayer` as **the** consumer entry point (it already is, kind of).
- Make `MediaContainerSession` an internal detail of `MediaPlayer`. The
  `Pause/SeekCoordinated/SeekCoordinatedSkippingSharedMuxFlush` etc. methods
  become methods on `MediaPlayer` directly.
- Keep `MediaContainerPlaybackBundle` as the low-level building block (it's
  doing the actual ownership/dispose work cleanly).
- Remove `IAvPlaybackSession`/`MediaPlaybackSession` as part of the public
  surface — they were added as façades for the bundle/Session split. The
  consumer never needs to type those names.

#### 2.3.4 The `flushSharedMuxAfterPause` parameter

This parameter rides through `AvPlaybackCoordinator.Pause`,
`MediaContainerSession.Pause/SeekCoordinated`, `MediaPlaybackSession.Pause`,
`IAvPlaybackSession.Pause`, plus dedicated "skipping" variants.
The actual decision is two-state ("flush" vs "don't flush"), with a small
escape hatch when the user wants a custom delegate. Architecture doc has a
section explaining when to use which method.

Proposed change: replace the `Action?` argument with an enum
`PauseFlushPolicy { FlushCodecPipelines, Skip, Custom(Action) }` (or just two
methods: `Pause()` defaults to flush, `PauseSkipFlush()` skips). The current
shape — `(CancellationToken ct = default, Action? flushSharedMuxAfterPause = null)` —
has a default behaviour that the param documentation reverses ("when omitted,
defaults to `MediaContainerDecoder.FlushCodecPipelines`"); the call site cannot
tell from the signature.

### 2.4 `S.Media.Playback`

`MediaPlayer` is the right name; the static `TryOpen` family is the right shape.
The problem is the **count**:

- `TryOpen(path, options, lead, dispose, out player, out err)`
- `TryOpen(path, options, lead, dispose, decoderOwnership, out player, out err)`
- `TryOpenFile(path, …)`
- `TryOpenUri(uri, …)`
- `TryOpenStream(stream, …)` × 2 (with/without inputName)
- `TryOpenLive(audio?, video?, …)` × 2 (with/without `disposeSourcesOnDispose`)
- `TryOpen(decoder, options, lead, dispose, decoderOwnership, out player, out err, videoSourceOverride: null)`

That's nine static factories. Most differ by one parameter. Many overloads
just forward to the next:

```csharp
public static bool TryOpenFile(...) =>
    TryOpen(mediaPath, options, videoNegotiationLead, ...);
```

Proposed change: replace with a builder, since the param matrix is unbounded:

```csharp
MediaPlayer.Open(path)
    .WithOptions(new MediaPlayerOpenOptions(...))
    .WithVideoLead(sdlSink, dispose: true)
    .TryBuild(out var player, out var err);
```

This collapses to one static (`MediaPlayer.Open(path)` / `MediaPlayer.OpenUri`
/ `MediaPlayer.OpenStream` / `MediaPlayer.OpenLive`) returning an
`OpenBuilder`. Even keeping the `Try` failure semantics is fine — `TryBuild`
returns false + a message.

Also: the dual "bundle path vs live path" stored as nullable fields
(`_bundle is not null` / `_liveSession is not null`) inside `MediaPlayer` is a
code-smell tag union — a `MediaPlayerInternals` discriminated record (or two
subclasses behind a base) would make the if-bundle/else-live noise vanish.

### 2.5 `S.Media.NDI`

Lots of value here, but the namespace has a *lot* of close-but-distinct types:

- Receivers: `NDIVideoReceiver`, `NDIAudioReceiver`, `NDILiveReceiver`,
  `NdiFrameSyncSession`, `NdiFrameSyncVideoSource`, `NdiFrameSyncAudioSource`.
  `NDILiveReceiver` is the "production" path; `NdiFrameSync*` is pull-mode
  alternative; `NDIVideoReceiver`/`NDIAudioReceiver` are the per-stream
  building blocks used by `NDILiveReceiver`. The doc comment says "remain useful
  for tools and focused tests"; in practice that's a maintenance burden — three
  receive paths that all need bug fixes when NDI SDK ergonomics shift.
- Clocks: `NDIIngestPlaybackClock`, `NDIEgressMuxPlayheadClock`,
  `NDIAlignedRouterClock`, `IngestSlavedRouterClock`. Only
  `NDIIngestPlaybackClock` and `IngestSlavedRouterClock` are referenced
  outside their own file; the other two look like staging code.
- `NDIMonitorReceiverPumpFusion`, `NDIFusionPlaybackHints`,
  `NDIEgressPresentationTimeline`, `NDIFrameTiming`, `NDIOutputExtensions` —
  five tiny helper types all riding on `NDIOutput`. Most could be inner types
  / extension methods on `NDIOutput` to deflate the namespace.

Proposed change:
- Keep `NDIOutput` (sender side) + `NDILiveReceiver` (receiver side) as the
  two consumer surfaces.
- Mark the standalone `NDIVideoReceiver`/`NDIAudioReceiver` as `internal` to
  the assembly; expose them only as `NDILiveReceiver.Audio` / `.Video`.
- Delete `NdiFrameSync*` (or move under a `NDI.Pull` namespace if you want to
  keep the alternative ingest path documented).
- Delete `NDIEgressMuxPlayheadClock` and `NDIAlignedRouterClock` if they
  remain unreferenced after the ingest tidy.

### 2.6 `S.Media.OpenGL`

This is the densest project for what it does (4631 LOC, 20 files). The big
file is `YuvVideoRenderer.cs` (1448 LOC) which carries:
- Format-recipe table.
- Shader compile + program cache.
- HDR transfer + gamut.
- BGRA/RGB/YUV planar/semi-planar uploads.
- Linux dma-buf via EGL.
- Windows NV12 shared-handle via WGL_NV_DX_interop.

Splitting it (one file per upload family + a small dispatcher) would make this
the most-readable code in the framework. Same goes for
`Nv12Win32SharedHandleGpuUploader.cs` (789 LOC) — it's doing one specific job
that happens to need many WGL/D3D11 syscalls.

`GlVideoFormatSupport` (409 LOC) is the single source of truth for what every
pixel format becomes at the GL layer — that's a fine pattern, keep it.

The four "device resolver / interop device / D3D11 utility" types
(`D3D11GlInteropDeviceHost`, `D3D11InteropUtility`, `Win32Nv12GlUploadDeviceResolver`,
`Nv12Win32SharedHandleGpuUploader`) are tangled — they all collaborate, but
which one starts which is non-obvious. Recommend a single `Win32Nv12GlPipeline`
class that owns the device, the uploader, and the resolver behind a small
public surface (one `Upload(VideoWin32Nv12Backing, ...)` method).

### 2.7 `S.Media.PortAudio`

- `PortAudioOutput` (563 LOC) — well-structured. The ring + callback +
  `IClockedOutput` + `IFlushableOutput` + `IPlaybackClock` combo is a lot of
  interfaces, but each is reasonable and tested.
- `PortAudioPlaybackHost` (221 LOC) — two near-identical factories
  (`TryCreatePortAudioMain` and `TryWirePortAudioMainForPlayer`) that compute
  the same target-queue-samples heuristic twice. Pull the heuristic into a
  private helper.
- `AudioPlayerPortAudioExtensions` is one method (`TryPrefillPrimaryPortAudio`)
  — fine.
- `PortAudioPlaybackHost` already has `PortAudioPlaybackHostPlayerOwnership`
  (enum, 2 values). Same pattern as bundle ownership; consider unifying the
  ownership-flag style across the framework.

### 2.8 `S.Media.SDL3`, `S.Media.Avalonia`, `S.Media.SkiaSharp`, `S.Media.Quick`

- `S.Media.SDL3` has both `SDL3VideoOutput` and `SDL3GLVideoOutput` — the latter
  is used by `QuickPlayer`. Check if the non-GL one is dead too (a quick grep
  shows it is referenced from tests and `S.Media.SDL3` itself only).
- `S.Media.Avalonia` is one 383-LOC file — a single `OpenGlControlBase` that
  delegates to the same `YuvVideoRenderer`. Clean.
- `S.Media.SkiaSharp` is `ImageFileSource` + `TextLayerSource`. Both small.
- `S.Media.Quick` is the right idea (one-call open-and-play). Currently
  hard-codes SDL3 GL window + PortAudio output. Worth either renaming to
  `S.Media.SoundboardQuick` so the assumption is explicit, or splitting the
  "image vs media" decision out so consumers can plug in their own output
  factories.

### 2.9 Auxiliary libs (PMLib / OSCLib / PALib / JackLib / NDILib)

These five projects all have `Native.cs` files, lib resolvers, structs, etc.
They are protocol bindings. Their fit with MediaFramework is:

- **NDILib** — used by `S.Media.NDI`. Tight fit, keep.
- **PALib** — used by `S.Media.PortAudio`. Tight fit, keep.
- **PMLib** (MIDI) — used by **nobody** under `MediaFramework/`. Only referenced
  by HaPlay. Either move it under `UI/` (it's really a HaPlay companion), or
  keep it but document that it's an "extras" lib not part of the playback
  graph.
- **OSCLib** — same situation. Used only by HaPlay.
- **JackLib** — Used by **nobody**. The `S.Media.PortAudio.csproj` does not
  reference `JackLib.csproj`. The `JackLib.csproj` declares
  `InternalsVisibleTo S.Media.PortAudio` and a comment saying PortAudio "uses
  JackLib for JACK port/connection management", but `grep "using JackLib"` in
  `S.Media.PortAudio` returns nothing. Either wire it up (if intended) or
  delete the `InternalsVisibleTo` line and put `JackLib` on the same
  "extras / used by HaPlay maybe" footing.

These five sit at the same level as the media stack but only NDILib and PALib
are actually consumed by media projects. Suggest a `MediaFramework/Extras/`
sub-folder for PMLib/OSCLib/JackLib so the dependency story is honest at the
solution level.

### 2.10 Cross-cutting: diagnostics ceremony

A grep for `#if DEBUG.*MediaDiagnostics.LogError` finds ~70 sites. They all
share this exact shape:

```csharp
try { thing.Dispose(); }
#if DEBUG
catch (Exception ex) { MediaDiagnostics.LogError(ex, "Owner.Dispose: thing"); }
#else
catch { /* best effort */ }
#endif
```

It's correct, but it's a lot of code for one decision. Two ways out:

1. A `MediaDiagnostics.SwallowDisposeErrors(Action, string label)` helper —
   collapses the block to one line at every call site. ~70 sites × 5 lines = 350
   LOC trimmed.
2. Or a `using` extension method `TryDispose(this IDisposable, string label)`
   — same effect.

This is the single highest LOC-saving change in the framework.

### 2.11 Cross-cutting: implicit static-init contract

Today a consumer must remember:

- `FFmpegRuntime.EnsureInitialized()` before any `MediaContainerDecoder.Open(...)`.
- That init also wires `AudioRouterAutoResample.SourceWrapper`,
  `VideoCpuFrameConverterRegistry.Factory`/`CanConvertProbe`,
  `VideoDeinterlacerRegistry.Factory`.
- `PortAudioRuntime.Acquire/Release` (refcount; PortAudioOutput's ctor pulls).
- `NDIRuntime.Create` (refcount; NDIOutput's ctor pulls).

That's three native runtime initializations and four optional static factory
slots, all installed lazily.

A nicer surface — and very little plumbing change — is a single
`MediaFrameworkRuntime.UseFFmpeg()/UsePortAudio()/UseNDI()` builder that
consumers call once. The lazy paths can keep working under the hood, but the
explicit "I'm enabling these backends" call site documents the runtime
dependencies a project has taken on.

### 2.12 Cross-cutting: `S.Media.Core` internals visible to host packages

`S.Media.Core.csproj`:

```xml
<InternalsVisibleTo Include="S.Media.Core.Tests" />
<InternalsVisibleTo Include="S.Media.PortAudio" />
<InternalsVisibleTo Include="S.Media.SDL3" />
<InternalsVisibleTo Include="S.Media.Avalonia" />
<InternalsVisibleTo Include="S.Media.Core.Benchmarks" />
```

Tests and Benchmarks are fine. The other three suggest that Core had to
expose details to specific outputs. Audit: if `S.Media.PortAudio`/`SDL3`/`Avalonia`
only need a handful of internals, promote those few members to `public` (or
`internal` + `[Friend]` style if you really want them scoped). Letting
backend packages reach into Core internals freely makes it impossible to
refactor Core without touching the backends.

### 2.13 Cross-cutting: `IAudioOutputChannelCapabilities` is the only "advanced output trait" that ships

Audio outputs today implement potentially:
- `IAudioOutput` (required)
- `IClockedOutput` (optional pacing)
- `IFlushableOutput` (optional flush)
- `IPlaybackClock` (optional master clock)
- `IAudioOutputChannelCapabilities` (optional channel reconfig)

That's 5 optional capability interfaces. The pattern works but the surface
is wide. Consider replacing them with a single
`AudioSinkCapabilities Capabilities { get; }` flags struct on `IAudioOutput`:

```csharp
[Flags] enum AudioSinkCapability {
    None = 0,
    Clocked = 1, Flushable = 2, ProvidesPlaybackClock = 4, ChannelReconfigure = 8,
}
```

…with the actual `WaitForCapacity`, `Flush`, etc. methods promoted to virtual
on a small abstract base or kept as `if (output is IClockedOutput c)` patterns
behind the flag. (Trade-off: flags interrogation is less idiomatic .NET than
`is IXxx`. Hold this one for discussion, but the cap-by-cap interface
proliferation is worth noting.)

Video has the same shape forming: `IVideoOutput` + `IVideoOutputD3D11GlBorrowSetup`
+ inner-disposable + future `IHardwareVideoInterop`. Watch that it doesn't
follow the audio side's growth.

---

## 3. Concrete extensibility wins (what to *add*)

### 3.1 First-class plugin registries

You already have `VideoCpuFrameConverterRegistry`, `VideoDeinterlacerRegistry`,
`AudioRouterAutoResample.SourceWrapper`. Make the pattern uniform and
discoverable:

```csharp
public static class MediaFrameworkPlugins
{
    public static IAudioSourceWrapper? AutoResample { get; set; }
    public static IVideoCpuFrameConverterFactory? CpuFrameConverter { get; set; }
    public static IVideoDeinterlacerFactory? Deinterlacer { get; set; }
    // new: IImageFileSourceFactory, IHardwareDecoderFactory, IAudioSinkFactory(deviceName) …
}
```

Then "I want to plug in libplacebo for color conversion" is a one-line
registration, not a hunt through several namespaces.

### 3.2 Pluggable image / video source factories by extension

`QuickPlayer.ImageExtensions` is currently a hardcoded set. A small
`MediaFrameworkExtensionRegistry` (extension → factory) would let
`QuickPlayer.Open(".raw" / ".dpx")` work without touching the core code.

### 3.3 Encoder / writer outputs

Today the framework decodes. There is no `IFileVideoSink` / `IFileAudioSink`
that wraps libavformat encoding. Many "play" applications eventually want
"record". The `IVideoOutput` / `IAudioOutput` shape already supports it — there's
just no implementation. Adding one (or a `S.Media.FFmpeg.Encode` project that
implements them) is straightforward and unlocks recording, transcoding,
"save what I just received".

### 3.4 Source types beyond decoder / capture

Consider first-class `ITimedAudioSource` / `ITimedVideoSource` with a
`Position` getter usable for ProRes/animation cases (frame-step API). Right
now everything is `IAudioSource.ReadInto` or `IVideoSource.TryReadNextFrame`;
that's fine for streaming, but a "give me the frame at PTS T" interface would
help frame-accurate editors, animation tools, and offline rendering on top of
this framework.

### 3.5 Async-friendly graph builder

`MediaPlayer.TryOpen` is sync. A consumer running in an ASP.NET Core or
WPF/Avalonia app context wants `await MediaPlayer.OpenAsync(...)`. The native
init paths are blocking by nature, but wrapping the public entry points in
`Task<MediaPlayer> OpenAsync(string path, …, CancellationToken)` (delegating
to a worker thread) is a small change for big usability gain.

### 3.6 First-class metrics surface

Right now metrics are spread across `AudioRouter.GetPumpStats` /
`GetAggregatePumpStats`, `VideoRouter.TryGetVideoOutputPumpMetrics`, `VideoPlayer.DroppedLate`,
`PortAudioOutput.PlayedSamples` / `DroppedSamples` / `UnderrunSamples` /
`CallbackCount`, `NDILiveReceiver.AudioOverflowFloats` etc. Each is well-named
but they're scattered.

A `MediaPlayer.GetMetrics()` returning one structured snapshot (audio router
stats + per-output + video router + per-output + decoder + clock) gives
dashboards and tools one place to read from.

### 3.7 Tests at the public API

The framework has thorough internal tests (S.Media.FFmpeg.Tests etc.) — but
relatively few "smoke" tests against `MediaPlayer.TryOpen`. A
`MediaPlayer.Tests` project that exercises the consumer API on a few canonical
samples (file with audio+video, image, audio-only, NDI source-not-found,
seek round-trip, mid-play output swap) would catch most of the surface
regressions a refactor like the one below could cause.

---

## 4. Recommended refactor plan (sequenced)

Each step is independently mergeable so you can stop at any layer.

### Phase R0 — easy wins, no API breakage

1. **Delete `VideoOutputRouter`** (obsolete, self-referencing only).
2. **Delete the unused HW interop placeholders** (`MetalIosurfaceNv12Interop`,
   `VulkanExternalNv12Interop`, `WindowsNv12D3D11TextureInterop`,
   `WindowsNv12SharedHandleInterop`, `LinuxDmabufNv12Interop`).
3. **Delete `NDIEgressMuxPlayheadClock`, `NDIAlignedRouterClock`** if a final
   check confirms zero references.
4. **Audit `S.Media.Core` `InternalsVisibleTo`** for PortAudio/SDL3/Avalonia.
   Promote what's actually needed to `internal` → `public`; drop the IVT
   entries we no longer need.
5. **Add `MediaDiagnostics.SwallowDisposeErrors(...)`** helper, mechanically
   replace the 70 `try/#if DEBUG/...catch` blocks. (Pure code-shrink.)
6. **Audit `JackLib.csproj`** — either wire it into `S.Media.PortAudio` or
   delete the `InternalsVisibleTo S.Media.PortAudio` line.

LOC saved: ~2000–2500 (mostly dead files + diagnostics boilerplate).
Risk: ~0.

### Phase R1 — Reorganise without breaking

1. **Move video effects out of Core**: `VideoCompositorSource`, `CpuVideoCompositor`,
   `BlendMode`, `LayerOpacityTween`, `LayerTransform2D`, `VideoCpuOpacity`,
   `FadeFromBlackVideoSource`, `CutVideoSource`, `StaticFrameSource`,
   `PixelFormatConvertingVideoSource` → `S.Media.Effects` project.
   `S.Media.Core` shrinks to "transport primitives".
2. **Move PMLib + OSCLib + JackLib** under `MediaFramework/Extras/` (or rename
   `Audio/` `MIDI/` `OSC/` → `Extras/Audio/...`) so the dependency story is
   clear: media stack vs. ancillary protocol libs.
3. **`MediaFrameworkPlugins` static** consolidating the per-feature factories
   in §3.1 (keeps the old static slots forwarding to the new home for
   backward compat).
4. **Split `YuvVideoRenderer.cs`** by upload family (planar / semi-planar /
   packed / dma-buf / win32) — pure file split, no API change.
5. **Pull the `target-queue-samples` heuristic** out of
   `PortAudioPlaybackHost.TryCreatePortAudioMain` / `TryWirePortAudio…` into
   one helper.

LOC saved: ~500–1000. Risk: low; sln + csproj churn.

### Phase R2 — Tidy session / playback layers

1. **`MediaContainerDecoder`** loses `LegacyAudio`/`LegacyVideo` (always
   null). `VideoFileDecoder` deleted; `AudioFileDecoder` either kept as
   "single-file audio decode helper" (clearly documented) or also deleted.
2. **`MediaContainerSession`** becomes `internal`; its public methods become
   `MediaPlayer.PauseSkippingFlush()` etc.
3. **`IAvPlaybackSession` / `MediaPlaybackSession`** removed from the public
   surface. `AvPlaybackCoordinator` stays as the internal sequencer.
4. **`MediaPlayer` builder API**:

   ```csharp
   var open = MediaPlayer.Open("foo.mkv")
       .WithOptions(o => o with { TryHardwareAcceleration = true })
       .WithVideoLead(sdlSink, dispose: true);
   if (!open.TryBuild(out var player, out var err)) ...
   ```

   …replaces the 9 `TryOpen*` overloads with 4 entry verbs
   (`Open` / `OpenUri` / `OpenStream` / `OpenLive`) each returning the same
   builder.
5. **`flushSharedMuxAfterPause` action → enum** on the Pause API.

LOC saved: ~1000 (mostly overload forwarding). Breaking: yes.

### Phase R3 — Clock consolidation

1. Collapse `IPlaybackTimeline` / `IPlaybackPlayhead` into `IPlayhead`.
2. Make `IRouterClock` and its impls (`WallClockRouterClock`,
   `OutputSlavedRouterClock`, `IngestSlavedRouterClock`) `internal`.
3. Keep `MediaClock`, `CompositePlaybackClock`, `VideoPtsClock`,
   `NDIIngestPlaybackClock` as the four public time concepts.
4. Document them on one page: "pick `MediaClock` if you don't know what to do.
   Set master via `IPlaybackClock`. The framework picks `IRouterClock`."

Breaking: yes (surface narrows). Compensating extension methods/aliases keep
day-to-day code working.

### Phase R4 — VideoFrame redesign

1. `VideoFrameHardwareBacking` discriminated union: `None | Dmabuf(Nv12|P010|P016) | Win32SharedNv12`.
2. `VideoFrame` ctor takes `(pts, format, planes, strides, release?, backing?, metadata?)`.
   The mutual-exclusion check becomes a type check.
3. Static `From*` factories collapse onto the backing type.
4. Unify `release: Action?` and `disposableRelease: IDisposable?` into one
   release strategy (probably keep `IDisposable?` only — `Action.Wrap` if you
   really need a closure).

Breaking: yes. Risk: contained to `S.Media.Core.Video` + every frame
constructor in `S.Media.FFmpeg`, `S.Media.NDI`, `S.Media.SkiaSharp`.

### Phase R5 — NDI surface diet

1. `NDIVideoReceiver` / `NDIAudioReceiver` → `internal`; access via
   `NDILiveReceiver`.
2. Delete `NdiFrameSync*` (or move under `NDI.Pull` namespace).
3. Fold `NDIMonitorReceiverPumpFusion` etc. into `NDIOutput` as nested types /
   extension methods.

Breaking: yes for power users; ergonomic for everyone else.

### Phase R6 — Async + metrics + extensibility (additive)

1. `MediaPlayer.OpenAsync`, `MediaPlayer.OpenUriAsync`, etc.
2. `MediaPlayer.GetMetrics()` aggregate.
3. `MediaFrameworkExtensionRegistry` for image-extension → factory.
4. `S.Media.FFmpeg.Encode` project (or `S.Media.Encode`) implementing
   `IAudioOutput` / `IVideoOutput` against libavformat encoders.

No breaking changes; pure additions.

---

## 5. Suggested API "happy path" for a brand-new consumer

> Below already adopts the §10 follow-up naming (`Output` instead of `Output`,
> `NDISource` + `NDIOutput` for the AV-coupled NDI surface).

After R0–R3 the canonical wiring for the four common cases would look like:

```csharp
// 1. Play a media file with audio + video on the default outputs.
using var player = await MediaPlayer.Open("clip.mkv")
    .WithVideoLead(new SDL3GLVideoOutput("clip"), dispose: true)
    .WithPortAudio()                              // optional sugar
    .OpenAsync();
player.Play();

// 2. Show a still image, hold the last frame.
using var player = await MediaPlayer.Open("logo.png")
    .WithVideoLead(new SDL3GLVideoOutput("logo"))
    .OpenAsync();
player.Play();

// 3. Play an audio-only stream.
using var player = await MediaPlayer.Open("audio.flac")
    .WithPortAudio()
    .OpenAsync();
player.Play();

// 4. Live NDI in to GL window + PortAudio out (one source, both A+V).
using var ndi = NDISource.Open(discoveredSource);
using var player = await MediaPlayer.OpenLive(ndi)
    .WithVideoLead(new SDL3GLVideoOutput("ndi-in"))
    .WithPortAudio()
    .OpenAsync();
player.Play();

// 5. Play a media stream from System.IO.Stream (first‑class).
await using var http = await httpClient.GetStreamAsync("https://…/clip.mkv", ct);
using var player = await MediaPlayer.OpenStream(http)
    .WithVideoLead(new SDL3GLVideoOutput("net"))
    .WithPortAudio()
    .OpenAsync(ct);
player.Play();
```

…and the existing power-user paths (manual router, custom clocks, multi-output
fan-out, NDI sender) remain available as `player.AudioRouter` / `player.VideoRouter`
/ etc.

---

## 6. What I'd *not* change

- The `AudioRouter` core algorithm — channel-map mixing, click-free fades,
  SIMD fast paths, per-output pump with drop-oldest, slave-clock model. This is
  the framework's strongest asset; resist temptation to "modernise" the
  internals.
- `VideoRouter`'s one-input-per-output exclusivity. This is the right
  semantics for video; compositing belongs elsewhere.
- The HW decode path (`MediaContainerSharedDemux`, `VideoHardwareDecodeContext`)
  — once `VideoFileDecoder` is gone, this is the single decode codepath and
  doesn't need touching.
- `PortAudioOutput`'s ring + callback model. It's tested and works.
- `YuvVideoRenderer`'s shader & recipe model — the surface (per-format
  recipe + shader picker) is genuinely good; only the file size is awkward.
- The `MediaContainerPlaybackBundle` flag-driven ownership pattern — that's
  exactly the right idea, lift it to the framework convention.

---

## 7. Risk register for the breaking changes

| Change | Blast radius | Mitigation |
|---|---|---|
| Delete `VideoFileDecoder` | `Tools/PlaybackSmoke`, tests | Update those, retain `AudioFileDecoder` short-term for the smoke tool |
| `VideoFrame` ctor reshape | Every frame producer + tests | Land alongside helper `VideoFrame.FromCpu(...)` etc; coordinate one PR |
| Collapse clock interfaces | Public surface | Provide alias `using IPlaybackTimeline = IPlayhead;` for one release |
| `MediaPlayer.Open(...)` builder | Tools, HaPlay, tests | Old `TryOpen` overloads can forward to the builder for one release before deletion |
| NDI receiver `internal`-isation | Tools, tests, HaPlay | Audit refs once; HaPlay is the heaviest user — coordinate the move |
| Effects sub-package | HaPlay (uses `FadeFromBlackVideoSource`, etc.) | Drop a `using` redirect in HaPlay; tests follow |

---

## 8. Quick file-level disposition (compact reference)

| File / Type | Action | Notes |
|---|---|---|
| `S.Media.FFmpeg/Video/VideoOutputRouter.cs` | **Delete** | Obsolete + unreferenced |
| `S.Media.FFmpeg/Video/VideoFileDecoder.cs` | **Delete** in R2 | Duplicate of `MediaContainerSharedDemux.VideoTrack` |
| `S.Media.FFmpeg/Audio/AudioFileDecoder.cs` | Re-evaluate | Used by `PlaybackSmoke`; either keep doc'd or delete |
| `MediaContainerDecoder.LegacyAudio/LegacyVideo` | Delete | Always null |
| `S.Media.Core/Video/MetalIosurfaceNv12Interop.cs` | **Delete** | Unreferenced |
| `S.Media.Core/Video/VulkanExternalNv12Interop.cs` | **Delete** | Unreferenced |
| `S.Media.Core/Video/WindowsNv12D3D11TextureInterop.cs` | **Delete** | Unreferenced |
| `S.Media.Core/Video/WindowsNv12SharedHandleInterop.cs` | **Delete** | Unreferenced |
| `S.Media.Core/Video/LinuxDmabufNv12Interop.cs` | **Delete** | Unreferenced |
| `S.Media.Core/Video/Compositor*.cs`, `*VideoSource.cs` (Fade/Cut/Static/PixelFormatConverting) | Move to `S.Media.Effects` | Effects, not transport |
| `S.Media.Core/Audio/AudioGraphBuilder.cs` | Fold into `AudioPlayer` or keep but document | Three ways do the same job today |
| `S.Media.Core/Clock/IPlaybackTimeline.cs`, `IPlaybackPlayhead.cs` | Merge into `IPlayhead` | Phase R3 |
| `S.Media.Core/Audio/IRouterClock.cs` + impls | Make `internal` | Phase R3 |
| `S.Media.NDI/NDIVideoReceiver.cs`, `NDIAudioReceiver.cs` | `internal`-ise | Expose via `NDILiveReceiver` |
| `S.Media.NDI/Input/NdiFrameSync*.cs` | Delete or move | Pull-mode alternative; choose |
| `S.Media.NDI/Clock/NDIEgressMuxPlayheadClock.cs` | **Delete** if unref | Self-only |
| `S.Media.NDI/Clock/NDIAlignedRouterClock.cs` | **Delete** if unref | Self-only |
| `MediaContainerSession.cs`, `MediaPlaybackSession.cs`, `IAvPlaybackSession.cs` | `internal`-ise in R2 | Public surface becomes `MediaPlayer` |
| `MediaPlayer.TryOpen*` 9 overloads | Replace with builder in R2 | Keep `Try` semantics on `TryBuild()` |
| `VideoFrame.cs` constructor + factories | Redesign in R4 | Discriminated backing |
| `MediaDiagnostics` debug-error blocks (×70) | Helper method, mechanical replace | Phase R0 |
| `MediaFramework/MIDI/PMLib`, `OSC/OSCLib`, `Audio/JackLib` | Move under `Extras/` | Dependency clarity |
| `S.Media.Core.csproj` `InternalsVisibleTo PortAudio/SDL3/Avalonia` | Audit + drop | Keep Core's contract intact |

---

## 9. Out-of-scope follow-ups worth noting

- **GL output bit depth**. Final `glReadPixels` on `GlVideoCompositor` is always
  RGBA8 — chaining composites truncates at every step. The Format-Support doc
  already flags this. Worth a sequenced fix when a real workload needs it.
- **CPU compositor limited to BGRA32 layers**. Same doc flags this. Bring it
  to parity with the GL compositor (which now does YUV/YUVA layers natively).
- **No headless GL test harness**. `S.Media.OpenGL.Tests` is static-only.
  Either drop in a swiftshader/Mesa LLVMpipe pipeline or accept that GL is
  validated by the smoke tool. Don't grow the test gap further.
- **No automatic per-leaf rate adaptation policy**. `AdaptiveRateAudioOutput`
  exists as a primitive but the framework doesn't auto-wire it. After R1's
  plugin consolidation, a one-line "register me on every non-master output" is
  a nice ergonomic touch.

---

## 10. Follow-up (2026‑05‑22): naming, NDI unification, platform focus, Stream IO, minimal API, composition

This section captures the post-review direction from the user. It supersedes
the earlier sections wherever they conflict.

### 10.1 Vocabulary: `Source` and `Output` everywhere

Today the framework mixes terminology: audio uses `IAudioOutput` for the device
side, video uses `IVideoOutput` for displays/encoders but the router method is
`AddOutput`. The internal classes pile on:`OutputPump`, `VideoOutputPump`,
`VideoOutputPumpAttachOptions`, `VideoOutputPumpMetrics`, `DiscardingVideoOutput`,
`DiscardingAudioSink`, `IClockedOutput`, `IFlushableOutput`,
`IAudioOutputChannelCapabilities`, `AudioBus`, `VideoCompositorSource`, etc. — 30+
type names ending in `Output`.

**Rule**: keep `Source` for everything that produces frames/samples; use
`Output` for everything that consumes them. Drop `Output` entirely.

Rename pass (the heavy ones):

| Today | Becomes |
|---|---|
| `IAudioOutput` | `IAudioOutput` |
| `IVideoOutput` | `IVideoOutput` |
| `IClockedOutput` | `IClockedOutput` |
| `IFlushableOutput` | `IFlushableOutput` |
| `IAudioOutputChannelCapabilities` | `IAudioOutputChannelCapabilities` |
| `AudioRouter.AddOutput / RemoveOutput` | `AddOutput / RemoveOutput` (already in `VideoRouter`) |
| `AudioRouter.OutputPump` (internal) | `OutputPump` |
| `AudioRouter.OutputPumpStats` | `OutputPumpStats` |
| `AudioRouterOutputErrorEventArgs` | `AudioRouterOutputErrorEventArgs` |
| `VideoOutputPump` / `VideoOutputPumpAttachOptions` / `VideoOutputPumpMetrics` | `VideoOutputPump` / `VideoOutputPumpAttachOptions` / `VideoOutputPumpMetrics` |
| `IVideoOutputD3D11GlBorrowSetup` | `IVideoOutputD3D11GlBorrowSetup` |
| `DiscardingAudioSink` / `DiscardingVideoOutput` | `DiscardingAudioOutput` / `DiscardingVideoOutput` |
| `AudioBus` | `AudioBus` (it's also a source — `Output` understates its dual role) |
| `VideoCompositorSource` | `VideoCompositorSource` (it's an `IVideoSource` whose inputs are slots — see §10.6) |
| `AudioPlayer.AddOutput` | already correctly named |
| `MediaContainerSession` "primary output" | "primary output" |
| `IAudioSource` / `IVideoSource` | unchanged — already the right name |

Symbol replacement is mechanical; the only place where care matters is
`VideoCompositorSource` (it's currently named "output" because each layer slot
implements `IVideoOutput` even though the type itself is a source). Renaming to
`VideoCompositorSource` and exposing `compositor.AddLayer(...)` returning an
`IVideoOutput` slot makes the "slot is a target, the whole thing is a source"
shape obvious.

Side benefit: it kills the "what kind of output does this mean" cognitive cost
when reading code that hops between audio and video.

### 10.2 NDI: one source, one output (A+V coupled)

The NDI SDK treats audio and video as separate streams but always belonging to
the same `NDIlib_send_instance_t` / `NDIlib_recv_instance_t`. The framework
currently splits them into ~6 types (`NDIVideoReceiver`, `NDIAudioReceiver`,
`NDILiveReceiver`, `NDIVideoSender`, `NDIAudioOutput`, `NDIOutput`) plus the
parallel `NdiFrameSync*` pull-mode path. Two practical problems:

- **Drift on independent capture/render threads**. Even when you wire both
  the `NDIVideoReceiver` and `NDIAudioReceiver` against the same discovered
  source they receive on different `_captureThread`s and report PTS
  independently. The mux side (`NDILiveReceiver`) already exists precisely
  because of this.
- **Audio-only NDI** is technically supported by the spec, but in practice
  every consumer either has both or just turns off whichever it doesn't
  consume. Two types are not buying anything for that case.

**Target shape**:

```csharp
public sealed class NDISource : IDisposable
{
    public static IReadOnlyList<NDIDiscoveredSource> Find(TimeSpan timeout, NDIFindOptions? options = null);
    public static NDISource Open(NDIDiscoveredSource source, NDISourceOptions? options = null);

    public IAudioSource Audio { get; }   // always present (silent when sender has no audio)
    public IVideoSource Video { get; }   // always present (black/exhausted when sender has no video)

    public IPlaybackClock IngestClock { get; }   // single combined ingest timeline
    public NDIConnectionState State { get; }

    public bool ReceiveAudio { get; set; }   // soft-disable while staying connected
    public bool ReceiveVideo { get; set; }
}

public sealed class NDIOutput : IDisposable
{
    public NDIOutput(string sourceName, NDIOutputOptions? options = null);

    public IAudioOutput Audio { get; }   // implements IClockedOutput when clockAudio: true
    public IVideoOutput Video { get; }

    public int ConnectionCount { get; }
    public NDITally Tally { get; }       // simple property; the polling API moves behind it
}
```

`NDIVideoReceiver` / `NDIAudioReceiver` go internal (or get deleted in favor
of one combined receiver). `NDIVideoSender` / `NDIAudioOutput` go internal as
the backing for `NDIOutput.Audio` / `NDIOutput.Video`. The three "monitor /
pump fusion / tally" auxiliary types become properties / methods on
`NDIOutput`. `NdiFrameSync*` deleted (the few legitimate "pull mode" callers
can be served by `NDISource` after a `.WithPullMode()` option, but in
practice live NDI playback wants the same capture-and-publish behaviour as
`NDILiveReceiver` today).

Result: NDI surface drops from ~24 files (4031 LOC) to **maybe 8 files**
(estimated 2400 LOC) without losing functionality.

### 10.3 Platform focus: Linux + Windows only

Drop macOS scaffolding outright. Specifically:

- Delete `MediaFramework/Audio/PALib/CoreAudio/` (Native.cs +
  PaMacCoreStructs.cs).
- Delete `S.Media.Core/Video/MetalIosurfaceNv12Interop.cs` (already on the R0
  cut list).
- Delete every `OperatingSystem.IsMacOS()` branch (`FFmpegRuntime.EnsureInitialized`
  is the main one; a handful of others scatter through PortAudio + NDI lib
  resolvers).
- Strip `osx-*` RIDs from `Directory.Packages.props` and any
  `PackageReference Condition=` entries that target macOS.
- Drop the `os.IsMacOS()` check on `VideoFrame.Create*Win32Shared*` (it never
  fires on macOS anyway — currently throws `PlatformNotSupportedException`,
  but the if-statement still ships in IL).

This buys:

- A simpler `FFmpegRuntime` (one platform decision: Linux vs Windows).
- No more "do we have a Metal device" hand-wringing in `S.Media.Core/Video/`.
- Smaller test matrix.
- Smaller binary surface area (the runtime PALib subdirectory alone is
  several hundred LOC of `[DllImport]` declarations no one runs).

If a macOS port becomes interesting later, reinstating these is mechanical;
keeping them today is dead code.

### 10.4 First-class `System.IO.Stream` input

Today `MediaContainerDecoder.OpenStream` spools the entire stream to a
temp file (`Path.GetTempPath()/mf_stream_<guid>.media`) and then opens that.
This works for finite streams (HTTP responses, embedded resources) but:

- Doubles the disk footprint while playback is running.
- Can't play unbounded streams (a piped capture, an `IAsyncEnumerable` of
  bytes, etc.) without buffering the whole thing first.
- The temp-file dance leaks if the host process crashes mid-open.

**Target**: a real libavformat `AVIOContext`-backed reader. The libav binding
(`FFmpeg.AutoGen`) already exposes `avio_alloc_context` with read/seek
callbacks. A `StreamAvioBridge` (in `S.Media.FFmpeg/Internal/`) of ~80 LOC
gives:

```csharp
public static MediaContainerDecoder OpenStream(
    Stream stream,
    bool isSeekable = false,
    string? probeHintName = null,
    VideoDecoderOpenOptions? options = null);
```

`isSeekable: true` plumbs a `seek` callback that maps to `Stream.Position`;
`false` declares the stream forward-only and disables container-level seek.
`probeHintName` (`"clip.mkv"`) lets libav narrow demuxer probing.

Keep the temp-file fallback as `MediaContainerDecoder.OpenStreamSpooled(...)`
for the rare case a caller has a stream that libav genuinely can't probe
incrementally (some formats need full-file index for accurate duration).

This also lets `MediaPlayer.OpenStream(stream)` be a first-class verb in the
builder API (§4 / R2), not a "try not to use this" path.

### 10.5 Minimum-viable playback API

The user's target shape for the simplest case:

```csharp
// 1. Init engines once at process start.
MediaFrameworkRuntime
    .Init()
    .UseFFmpeg()
    .UsePortAudio()
    .UseNDI();   // optional

// 2. Create an output.
using var speakers = new PortAudioOutput(AudioFormat.Default48k2);

// 3. Create a router (the format-and-pacing authority).
using var router = new AudioRouter(speakers.Format.SampleRate);

// 4. Create a source.
using var clip = AudioSource.OpenFile("song.mp3");   // or OpenStream(...)

// 5. Wire source + output into the router and route them.
var srcId = router.AddSource(clip, autoResample: true);
var outId = router.AddOutput(speakers);
router.Route(srcId, outId);   // identity channel map, gain 1.0

// 6. Play.
router.Play();
```

That's six end-user statements after `Init`. The framework already supports
this shape today, but several details get in the way:

- The simplest entry verb is currently `AudioPlayer`, which auto-wires master
  clock + primary output — fine for the soundboard case but it conceals what
  is happening.
- `AudioRouter.AddSource` doesn't have an `autoResample` overload that knows
  the router's rate without poking at it (caller currently passes the rate
  twice).
- `AudioRouter` has no `Route(srcId, outId)` shorthand — callers must build
  a `ChannelMap.Identity(channels)` explicitly. That's exactly what
  `AudioGraphBuilder.ConnectLast` was meant to be, but the builder is its
  own type.
- There is no `AudioSource.OpenFile(path)` — the file decoder lives at
  `S.Media.FFmpeg.AudioFileDecoder.Open(...)`. Hoisting it as
  `AudioSource.OpenFile / OpenStream` (and the same for `VideoSource`) under
  `S.Media.Core.Audio.AudioSource` static class collapses the discoverability
  problem.
- `MediaFrameworkRuntime.Init()` doesn't exist yet — see §3.1, §2.11. The
  fluent shape proposed there is exactly this.
- `router.Play()` doesn't exist — today it's `router.Start()`. Either rename
  or alias. (`Play` reads better at the call site.)

**Proposed Core additions** to support the six-line shape (all R0/R1, no
breaking changes if we alias):

```csharp
// S.Media.Core.Audio:
public static class AudioSource
{
    public static IAudioSource OpenFile(string path, AudioSourceOpenOptions? options = null);
    public static IAudioSource OpenStream(Stream stream, AudioSourceOpenOptions? options = null);
}

public partial class AudioRouter
{
    public string Route(string sourceId, string outputId, float gain = 1.0f); // identity map
    public string Route(string sourceId, string outputId, ChannelMap map, float gain = 1.0f);
    public void Play();   // alias for Start()
}

public partial class IAudioSource
{
    // already has Format; no change
}

// S.Media.Core.Video:
public static class VideoSource
{
    public static IVideoSource OpenFile(string path, VideoSourceOpenOptions? options = null);
    public static IVideoSource OpenStream(Stream stream, VideoSourceOpenOptions? options = null);
    public static IVideoSource OpenImage(string path);          // forwards to SkiaSharp
    public static IVideoSource OpenImage(Stream stream);
}
```

For the **A+V minimum** case the same shape extends:

```csharp
MediaFrameworkRuntime.Init().UseFFmpeg().UsePortAudio();

using var window = new SDL3GLVideoOutput("clip");
using var speakers = new PortAudioOutput(AudioFormat.Default48k2);

using var media = MediaContainer.OpenFile("clip.mkv");
// media.Audio is IAudioSource, media.Video is IVideoSource

using var audio = new AudioRouter(media.Audio.Format.SampleRate);
using var video = new VideoRouter();

var aSrc = audio.AddSource(media.Audio, autoResample: true);
var aOut = audio.AddOutput(speakers);
audio.Route(aSrc, aOut);

var vOut = video.AddOutput(window);
var vIn = video.AddInput(vOut);   // primary output drives negotiation
media.Video.ConnectTo(vIn.Output);  // small helper that calls Configure + drives a decode loop

audio.Play();
video.Start();    // separate Play() for video router is fine
```

That's still ten end-user lines for "play this file in this window with
audio." A consumer who wants less ceremony uses `MediaPlayer.Open(...)`
builder; a consumer who wants control uses the lines above. **Both shapes
should be first-class and documented next to each other**, so consumers
self-select.

(Today's `AudioPlayer` is a useful middle ground but it's not pulling its
weight as a separate type — it's a thin facade over `AudioRouter +
MediaClock`. After the rename pass, fold its surface into `AudioRouter`
itself and delete the `AudioPlayer` type. Same for `VideoPlayer`'s
single-source role — keep `VideoPlayer` as the "one source + clock-paced
display" helper but make `VideoRouter` adequate without it.)

### 10.6 Video composition: hard work behind a config surface

Composition today requires:

- Construct a `GlVideoCompositor` (GL context required) or `CpuVideoCompositor`.
- Wrap it in a `VideoCompositorSource` (which exposes per-slot `Opacity`,
  `Transform`, `BlendMode` mutable state on each `Slot`).
- For animations, manually update the slot fields from a `LayerOpacityTween`
  or hand-tick from the clock.

Asks: position, scale, opacity, layer order, blend mode, **transitions over
time** (fade in/out, move, scale-in, cut at PTS). These are presentation
concerns, not framework concerns — they should be declared, not
hand-driven each frame.

**Target shape**: a `VideoCompositor` configured at build time, exposed as a
single `IVideoSource` consumers route into a `VideoRouter` like any decoder:

```csharp
using var bgClip   = VideoSource.OpenFile("bg.mp4");
using var fgClip   = VideoSource.OpenFile("overlay.mov");
using var logo     = VideoSource.OpenImage("logo.png");

using var comp = VideoCompositor.Create(
    output: VideoFormat.For(1920, 1080, FrameRate: new Rational(60, 1)),
    backend: VideoCompositorBackend.Gl);   // Cpu fallback when no GL

// Layers are positional — order = back‑to‑front.
var bg = comp.AddLayer(bgClip,  LayerConfig.Background);   // covers viewport
var fg = comp.AddLayer(fgClip,  new LayerConfig
{
    Position = LayerPosition.Center,
    Scale    = 0.5f,
    Opacity  = 0,                       // start invisible
    Blend    = BlendMode.SourceOver,
});
var brand = comp.AddLayer(logo, new LayerConfig
{
    Position = LayerPosition.Anchored(LayerAnchor.TopRight, marginX: 32, marginY: 32),
    Scale    = 0.15f,
    Opacity  = 1,
});

// Transitions are timeline-driven, anchored to the host clock.
fg.AddTransition(at: TimeSpan.FromSeconds(1),
                 Transition.FadeTo(opacity: 1f, duration: TimeSpan.FromMilliseconds(500)));
fg.AddTransition(at: TimeSpan.FromSeconds(5),
                 Transition.MoveTo(LayerPosition.Anchored(LayerAnchor.BottomLeft, 64, 64),
                                    duration: TimeSpan.FromSeconds(1)));
fg.AddTransition(at: TimeSpan.FromSeconds(10),
                 Transition.FadeTo(opacity: 0f, duration: TimeSpan.FromMilliseconds(500)));

// Compositor is itself a source — wire it into the video router.
var vOut = video.AddOutput(window);
var vIn  = video.AddInput(vOut);
comp.ConnectTo(vIn.Output);

video.Start();
```

Where the new types live:

```csharp
// S.Media.Effects (the new project from R1):
public sealed class VideoCompositor : IVideoSource, IDisposable
{
    public static VideoCompositor Create(VideoFormat output,
                                         VideoCompositorBackend backend = VideoCompositorBackend.Auto,
                                         VideoCompositorOptions? options = null);

    public LayerHandle AddLayer(IVideoSource source, LayerConfig config);
    public bool RemoveLayer(LayerHandle handle);

    public IReadOnlyList<LayerHandle> Layers { get; }
    public IPlaybackClock? Clock { get; set; }   // attach to drive transition timing
}

public sealed class LayerHandle
{
    public IVideoSource Source { get; }
    public LayerConfig CurrentConfig { get; }    // resolves transitions against the clock

    public void SetConfig(LayerConfig config);   // jump (no transition)
    public void AddTransition(TimeSpan at, Transition transition);
    public void ClearTransitions();
}

public readonly record struct LayerConfig(
    LayerPosition Position,
    float         Scale       = 1f,
    float         Opacity     = 1f,
    BlendMode     Blend       = BlendMode.SourceOver,
    float         Rotation    = 0f,
    LayerAnchor   ScaleAnchor = LayerAnchor.Center)
{
    public static LayerConfig Background => new(LayerPosition.Cover, Scale: 1f);
    public static LayerConfig CenteredHalf => new(LayerPosition.Center, Scale: 0.5f);
}

public abstract record LayerPosition
{
    public static LayerPosition Cover                                       => new CoverPosition();
    public static LayerPosition Center                                      => new CenteredPosition();
    public static LayerPosition Anchored(LayerAnchor anchor, int marginX, int marginY) => …;
    public static LayerPosition AbsolutePixels(int x, int y)                => …;
    public static LayerPosition NormalizedXY(float x01, float y01)          => …;
}

public abstract record Transition
{
    public static Transition FadeTo(float opacity, TimeSpan duration, Easing? easing = null);
    public static Transition MoveTo(LayerPosition position, TimeSpan duration, Easing? easing = null);
    public static Transition ScaleTo(float scale, TimeSpan duration, Easing? easing = null);
    public static Transition Cut(LayerConfig snap);    // instant
    public static Transition Combo(params Transition[] parallel);
    public static Transition Sequence(params Transition[] serial);
}

public enum LayerAnchor { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }
public enum VideoCompositorBackend { Auto, Cpu, Gl }
```

Implementation notes:

- `VideoCompositor` swallows the existing `VideoCompositorSource` +
  `CpuVideoCompositor` / `GlVideoCompositor` pair (per §2.2.7 they move to the
  new effects project). The user never touches the internal `IVideoCompositor`.
- Transitions are evaluated **once per output frame** on `TryReadNextFrame`,
  resolved against the attached `IPlaybackClock`. No background tick thread,
  no per-handler subscription churn.
- Layer ordering follows insertion order; expose
  `Move(handle, toIndex)` for the rare reorder case.
- Backend selection: `Auto` picks GL when a GL context is available on the
  output thread, CPU otherwise. The framework owns the context discovery so
  consumers don't write context-probing code.
- Resize: composite size is fixed at construction. Add an
  `OutputResizeMode` (`Stretch`/`LetterboxInsideOutput`) when consumer-side
  display resizes need to be handled by the compositor itself rather than the
  final renderer.

Cross-fade between **two whole clips** is then expressible by composing two
layers + opacity transitions:

```csharp
var a = comp.AddLayer(clipA, LayerConfig.Background with { Opacity = 1f });
var b = comp.AddLayer(clipB, LayerConfig.Background with { Opacity = 0f });
a.AddTransition(at: cutAt, Transition.FadeTo(0f, TimeSpan.FromMilliseconds(500)));
b.AddTransition(at: cutAt, Transition.FadeTo(1f, TimeSpan.FromMilliseconds(500)));
```

…which is the bulk of what `FadeFromBlackVideoSource` and `CutVideoSource`
exist for. Those two types collapse into a `Transition.FadeFromBlack(...)`
preset on the new compositor — fewer building blocks, more expressive.

### 10.7 Updated refactor plan deltas

Insert into Phase R0:

- **Drop macOS scaffolding** (PALib/CoreAudio/*, Metal interop, RID entries,
  `OperatingSystem.IsMacOS()` branches). Zero‑risk delete.

Insert into Phase R1:

- **Source/Output rename pass** before extracting effects out of Core — saves
  redoing every reference in the new project. Single PR, mostly mechanical
  (Rider can drive 95% of it).
- **NDI surface collapse** (one source, one output; receivers and senders go
  internal). Land in R1 because it cuts ~1500 LOC from `S.Media.NDI` and the
  rename is small once the receiver/sender consolidation is done.
- **`MediaFrameworkRuntime.Init()` builder** (§3.1) plus
  `AudioSource.OpenFile/OpenStream`, `VideoSource.OpenFile/OpenStream`,
  `AudioRouter.Route(...)`, `AudioRouter.Play()`. These are additive aliases
  — no breakage, but they make §10.5 actually achievable.
- **Stream-backed `OpenStream`** (§10.4) — `StreamAvioBridge` lands in
  `S.Media.FFmpeg/Internal/` and lights up `MediaContainerDecoder.OpenStream`,
  `MediaPlayer.OpenStream`, and the new `AudioSource.OpenStream` /
  `VideoSource.OpenStream` helpers all at once.

Promote into Phase R2:

- **Drop `AudioPlayer` / fold into `AudioRouter`** (§10.5). After
  `Route(...)` and `Play()` aliases land, the only thing `AudioPlayer` adds
  is master-clock auto-wire and the seek glue. Both belong on `AudioRouter`
  itself (and on `MediaPlayer` for the bundled case). One fewer "is this the
  player or the router?" question for consumers.

Insert into Phase R1 (or R2 — separate scope):

- **`S.Media.Effects` houses the composition API** in §10.6. The shipping
  `VideoCompositorSource` / `CpuVideoCompositor` / `GlVideoCompositor` are the
  internal implementation; the public surface is `VideoCompositor` +
  `LayerHandle` + `LayerConfig` + `Transition`.

### 10.8 Updated file disposition

Additions to the table in §8:

| File / Type | Action | Notes |
|---|---|---|
| `MediaFramework/Audio/PALib/CoreAudio/` | **Delete** | macOS scaffolding |
| Every `OperatingSystem.IsMacOS()` branch | **Delete** | Linux+Windows only |
| `osx-*` RIDs in `Directory.Packages.props` | **Remove** | Same |
| All `*Output` interfaces/classes/events | **Rename** to `*Output` | §10.1 |
| `VideoCompositorSource` | **Rename** to `VideoCompositorSource` | Reflects role |
| `AudioBus` | **Rename** to `AudioBus` | Dual-role |
| `NDIVideoReceiver`, `NDIAudioReceiver` | **Internal** + delete from public surface; expose only via `NDISource` | §10.2 |
| `NDIVideoSender`, `NDIAudioOutput` | **Internal**; expose only via `NDIOutput.Audio` / `.Video` | §10.2 |
| `NDILiveReceiver` | **Rename** to `NDISource` (or fold its capture logic into a fresh `NDISource`) | §10.2 |
| `NdiFrameSyncSession`, `NdiFrameSyncAudioSource`, `NdiFrameSyncVideoSource`, `NdiAudioFrameConverter` | **Delete** | Redundant pull-mode |
| `S.Media.Quick.QuickPlayer` | Replace with §10.5 minimal-API doc + `MediaPlayer.Open(...).OpenAsync()` builder | One concept |
| `AudioPlayer` (`S.Media.Core.Audio.AudioPlayer`) | **Fold into** `AudioRouter` (Play/Pause/Seek alias) + `MediaPlayer` (bundled case) | §10.5 |
| `AudioGraphBuilder` | **Delete** (subsumed by `AudioRouter.Route(...)`) | §10.5 |
| `MediaContainerDecoder.OpenStream` temp-file path | Replace with `StreamAvioBridge` (libavformat AVIOContext) | §10.4 |

### 10.9 LOC budget after R0–R2 + §10

Rough back-of-envelope after the rename + NDI collapse + macOS strip +
`AudioPlayer`/`AudioGraphBuilder` fold + dead-code delete:

| Project | Today | After §10 |
|---|---:|---:|
| `S.Media.Core`         | 13 323 | ~11 000 (effects out, AudioBus/AudioPlayer/AudioGraphBuilder absorbed, no macOS) |
| `S.Media.FFmpeg`       | 6 749 | ~5 400 (VideoFileDecoder/Audio… deleted, StreamAvioBridge added) |
| `S.Media.NDI`          | 4 031 | ~2 400 (collapsed to NDISource + NDIOutput) |
| `S.Media.OpenGL`       | 4 631 | ~4 500 (mostly file-split, ~0 deletion) |
| `S.Media.PortAudio`    | 1 481 | ~1 400 |
| `S.Media.Playback`     | 835   | ~700 (builder consolidation) |
| `S.Media.Quick`        | 264   | 0 (deleted in favor of MediaPlayer builder) |
| `S.Media.SkiaSharp/SDL3/Avalonia` | ~2 300 | ~2 300 |
| `MediaFramework/Audio/PALib` | (4 600 native bindings) | ~3 800 (no CoreAudio) |
| **New `S.Media.Effects`** | — | ~1 200 (extracted + composition API) |

Net ~4 000 LOC shrink in the media stack, plus a substantially smaller
**public** surface (the cognitive payload, not just file size). The Effects
project is the only addition.

### 10.10 Migration cookbook for §10

Order of operations once consumers approve:

1. **Rename pass (mechanical).** One PR, no behaviour change. `Output` → `Output`
   everywhere except `IAudioSource`/`IVideoSource`. Rider's "Rename" handles
   nearly all of it; manual fixups in XML doc comments.
2. **Drop macOS.** Delete `PALib/CoreAudio/`, `MetalIosurfaceNv12Interop.cs`,
   `OperatingSystem.IsMacOS()` branches, `osx-*` RIDs. One PR.
3. **`MediaFrameworkRuntime.Init()` + Source factories + Router.Route/Play
   aliases + AudioSource/VideoSource statics**. Additive PR; doesn't break
   anything but unlocks §10.5.
4. **`StreamAvioBridge`** swap in `MediaContainerDecoder.OpenStream`. Add
   `OpenStreamSpooled` as the explicit "spool to temp file" path for the
   format-probe edge case. One PR.
5. **NDI surface collapse**. New `NDISource`/`NDIOutput`; internal-ise the
   per-stream types; delete the pull-mode session. One PR; clearly breaking
   for current NDI consumers (only HaPlay + smoke tools).
6. **`S.Media.Effects` extraction** + `VideoCompositor` + `LayerHandle` +
   `Transition`. One PR; clearly breaking for the few existing compositor
   consumers (CompositorSmoke + HaPlay's logo fallback).
7. **`AudioPlayer` deletion** — final cleanup PR, after consumers have moved
   to the §10.5 shape. Smallest change of the lot.
8. **Bit-depth completeness (§10.11)** — additive R7. 10-bit swapchain in
   the GL outputs; `PixelFormat.Rgba16` / `Rgba16F` + compositor opt-in;
   NDI `P216` / `PA16` sender support.

Each step is reviewable independently; (5), (6) and (8) are the only ones
that require coordinated test/UI updates.

### 10.11 GL bit depth: where 10/12-bit actually survives, and how to preserve it end‑to‑end

This is worth its own subsection because the "GL is RGBA8" line from §9 is
**only true at one specific spot** — the compositor's final `glReadPixels`
into a CPU `Bgra32` frame. Everywhere else, high bit depth is preserved
already. Here is what the pipeline really does today, and what would need to
change to keep 10/12/16-bit precision all the way to the consumer.

#### 10.11.1 Where bit depth lives today

| Stage | Today's precision | Code |
|---|---|---|
| Sample storage (Y/U/V/A planes for 10/12/16-bit YUV/YUVA) | **R16 / RG16** GPU textures | `YuvVideoRenderer.cs` upload, `bitScale` uniform = 65535/1023 (10b), 65535/4095 (12b), 1.0 (16b) |
| YUV → RGB matrix + range expand + HDR transfer + gamut | **FP32 in the fragment shader** | YUV shaders + `yuvColorSpace` / `hdrPreviewAfterMatrix` / `gamutMatrix` uniforms |
| Per-layer intermediate (when the compositor's source isn't BGRA32) | **RGBA16F** FBO | `GlVideoCompositor.CreateRgba16fIntermediate` (`GlInternalFormat.Rgba16f`) |
| Composite blend (`SourceOver` / `Multiply` / `Source` against the intermediate) | **FP32 math in the composite shader**, blended into the FBO at the FBO's precision | `composite_layer` shader |
| Compositor final FBO | **RGBA8** (`GlInternalFormat.Rgba8` in `RecreateFbo`) | `GlVideoCompositor.RecreateFbo` line 415 |
| `glReadPixels` back to CPU `VideoFrame` | **`GL_UNSIGNED_BYTE` → Bgra32** | `GlVideoCompositor.Composite` final readback |
| Direct-display GL surface (SDL3 / Avalonia OpenGL window) | **Whatever swapchain the GL context was created with** — defaults to 8-bit per channel on essentially every platform unless explicitly asked for 10-bit | `SDL3GLVideoOutput`, `VideoOpenGlControl` |

So 10-bit content on a 10-bit display through `SDL3GLVideoOutput` today loses
precision **at the swapchain**, not in the framework — every step inside the
shader sees full source precision, and the GPU does float math. On an 8-bit
display, dithering aside, you couldn't see the difference anyway.

The compositor's RGBA8 final-FBO + readback is the only place the framework
*itself* unconditionally narrows to 8-bit per channel. That's a one-line
change to flip (and the cost is mostly downstream: who consumes the result).

#### 10.11.2 Three opportunities, in increasing scope

##### A. 10-bit display swapchain (low cost, high visible win on HDR/wide-gamut displays)

Pure host-side change in `SDL3GLVideoOutput` and `VideoOpenGlControl`:

- **Windows / DXGI**: request `DXGI_FORMAT_R10G10B10A2_UNORM` for the swapchain
  (works on Windows 10+ with a 10-bit display + supported GPU driver).
  SDL3 honors `SDL_HINT_VIDEO_X11_NET_WM_BYPASS_COMPOSITOR` etc., and for
  GL you'd set `SDL_GL_RED_SIZE=10 / GREEN_SIZE=10 / BLUE_SIZE=10 / ALPHA_SIZE=2`
  before `SDL_GL_CreateContext`.
- **Linux / EGL**: pass `EGL_RED_SIZE=10`, etc. into the framebuffer config.
  Wayland compositors that advertise 10-bit (KWin since 5.27, Mutter since
  GNOME 45 on KMS-capable hardware) accept this directly; X11 needs a 10-bit
  DDX (Intel and AMD ship one; NVIDIA proprietary supports it).
- **Adaptive fallback**: if the requested 10-bit config is unavailable, fall
  back to 8-bit and log a single warning.

API shape:

```csharp
new SDL3GLVideoOutput(
    title: "preview",
    initialWidth: 1920, initialHeight: 1080,
    swapchainBitDepth: GlOutputBitDepth.Auto);  // Auto = try 10, accept 8

public enum GlOutputBitDepth { Eight, Ten, Auto }
```

This is the **biggest perceived-quality win** for a pro consumer on a real
display, with the **smallest framework change** (no new pixel format, no
shader changes, no readback path changes). The GPU is already producing
float per-channel data; the only question is whether the swapchain can hold
that precision.

##### B. RGBA16 (or RGBA16F) compositor output (medium cost, real win for chained processing)

Make `GlVideoCompositor.RecreateFbo` parameterizable:

```csharp
public sealed class GlVideoCompositor : IVideoCompositor, IDisposable
{
    public GlVideoCompositor(VideoFormat output,
                             GlCompositorOutputPrecision precision = GlCompositorOutputPrecision.Rgba8);
}

public enum GlCompositorOutputPrecision { Rgba8, Rgba16, Rgba16F }
```

Implementation:

- `Rgba8` (default, what ships today): `GlInternalFormat.Rgba8` FBO,
  `glReadPixels(GL_UNSIGNED_BYTE) → VideoFrame(Bgra32)`. Compatible with NDI
  `BGRA`, with `Tools/CompositorSmoke`, etc.
- `Rgba16`: `GlInternalFormat.Rgba16` (16-bit unsigned-normalized) FBO,
  `glReadPixels(GL_UNSIGNED_SHORT) → VideoFrame(Rgba16)`. The blend stage
  becomes 16-bit-per-channel; readback is one half-word per channel. **NDI
  sender accepts the 16-bit alpha-bearing layout `PA16` and the 16-bit
  4:2:2 `P216` already** — see §10.11.4 below. This format is broadcast-real,
  not a research thing.
- `Rgba16F`: `GlInternalFormat.Rgba16f` FBO, `glReadPixels(GL_HALF_FLOAT)
  → VideoFrame(Rgba16F)`. Best for chained compositing and for tone-mapping
  HDR before display. The shader output (which is already float) survives
  unchanged into the FBO.

New `PixelFormat` enum members:

```csharp
// S.Media.Core.Video.PixelFormat additions:
Rgba16,    // 16-bit unsigned-normalized packed RGBA (4 × uint16, little-endian)
Rgba16F,   // 16-bit IEEE half-float packed RGBA (4 × Half, little-endian)
```

…and one `IVideoCpuFrameConverter` recipe each (swscale already supports
both via `AV_PIX_FMT_RGBA64LE` and a 16-bit float path).

##### C. YUV pass-through (lowest precision loss, highest implementation cost)

For an output that's going to NDI (`P216` / `PA16`), an HEVC encoder
(`P010` / `Yuv420P10Le`), or a file (`ProRes 4444 XQ`), the cheapest thing is
**don't go through RGB at all**. Render the compositor result back to a
high-bit-depth YUV format directly via an additional GL pass, or — simpler —
do the composite in `RGBA16F` (option B) and convert RGBA16F → P010 / P216
on CPU once at readback via swscale.

The "shader-side YUV output" path is the lowest precision loss but it
requires:

- A second shader program that samples the FP16 intermediate and writes Y, U,
  V into three R16 textures (or NV12 into R16 + RG16).
- Multiple readback calls (one per plane) or attached MRT.
- A new pixel format on the output side.

I'd land **(A) and (B) first**, measure, and only do (C) if the FP16 →
swscale CPU conversion at typical 1080p60 / 4K30 turns out to be the
bottleneck. Modern swscale at FP16 → P010 on a desktop CPU is ~3 ms/4K
frame; this is usually fine.

#### 10.11.3 What about `YuvVideoRenderer` drawing straight to a window?

For `SDL3GLVideoOutput` / `VideoOpenGlControl` the compositor isn't in the
path. The renderer draws straight to the swapchain. Today the swapchain is
8-bit and the shader's float output is dithered/truncated to 8 bits at
present.

Implementing §10.11.2(A) is enough to fix this case — once the swapchain is
10-bit, the float shader output goes through at full precision, and you can
literally see the difference on a 10-bit display with smooth gradients (no
banding on dark scenes, smoother sky gradients, etc.).

This means **for the direct-display case, you don't need RGBA16F anywhere in
the framework**. You only need to expose 10-bit swapchain creation.

#### 10.11.4 NDI: it does support high bit depth

The NDI SDK FourCCs include:

| FourCC | Layout | Bits/component | Framework today |
|---|---|---|---|
| `BGRA` / `BGRX` | Packed RGBA | 8 | Supported on the sender |
| `RGBA` / `RGBX` | Packed RGBA | 8 | Supported on the sender |
| `UYVY` | Packed 4:2:2 | 8 | Supported |
| `UYVA` | Packed 4:2:2 + 8-bit alpha plane | 8 | Not exposed |
| `Nv12` / `I420` | Semi/planar 4:2:0 | 8 | Supported |
| `P216` | Planar 4:2:2 (Y + interleaved UV, 16-bit per component) | 16 (10 valid for P010-like) | **Not exposed** |
| `PA16` | `P216` + 16-bit alpha plane | 16 | **Not exposed** |

So an `NDIOutput.Video` configured with `PixelFormat.Rgba16` (from §10.11.2(B))
plus a small `Rgba16 → P216` swscale step gives a real 10/12/16-bit-clean NDI
sender. No CPU narrowing to RGBA8 along the way.

Adding `P216` / `PA16` to `NDIVideoSender.AcceptedPixelFormats` is small (the
`NDIlib_video_frame_v2_t.FourCC` and per-plane pointer/pitch setup) and
unlocks a real broadcast workflow.

#### 10.11.5 Encoder output (looking ahead)

When the §3.3 encoder outputs land (`S.Media.FFmpeg.Encode`), the natural
inputs are `Yuv420P10Le` / `P010` / `Yuv444P12Le` / `Yuva444P12Le`. The
RGBA16F path described in §10.11.2(B) makes that conversion *lossless* up to
the encoder's chroma subsampling — which is the right precision contract for
a media framework.

#### 10.11.6 Concrete change list

Drop into the refactor plan as **Phase R7 — bit depth completeness**
(non-breaking, all additive):

1. `GlOutputBitDepth` enum + 10-bit swapchain support in `SDL3GLVideoOutput`
   and `VideoOpenGlControl`. Graceful fallback to 8-bit with a single
   warning log.
2. New `PixelFormat.Rgba16` (16-bit unorm) + `PixelFormat.Rgba16F` (half-float)
   enum members. `PixelFormatInfo` recipe (bytes per pixel = 8). `swscale`
   binding for the converter.
3. `GlVideoCompositor` gains `GlCompositorOutputPrecision` parameter; FBO
   creation + readback `glReadPixels` type switch on it; output VideoFrame
   format follows.
4. `NDIVideoSender.AcceptedPixelFormats` gains `P216` (no alpha) and `PA16`
   (with alpha). `NDIVideoSender.Submit` plumbs the right FourCC + 16-bit
   plane pointers.
5. Optional: `S.Media.Effects.VideoCompositor` (§10.6) gains a `Precision`
   property on the builder so consumers don't need to know the GL backend
   internals.

LOC impact: ~400 lines added (mostly the new pixel format recipes + swscale
maps + the FBO/swapchain branches). No deletions; nothing else changes.

What 10/12-bit content looks like after R7 (best case, per stage):

| Stage | Today | After R7 |
|---|---|---|
| Source decode | 10/12/16-bit (Yuv422P10Le etc.) | unchanged |
| GPU texture storage | R16 / RG16 | unchanged |
| Fragment shader math | FP32 | unchanged |
| Per-layer intermediate (compositor) | RGBA16F | unchanged |
| Composite final FBO | RGBA8 | **RGBA16 / RGBA16F (opt-in)** |
| Compositor → CPU frame | `Bgra32` only | **`Bgra32` / `Rgba16` / `Rgba16F`** |
| Direct GL display swapchain | 8-bit | **10-bit on supported HW (opt-in)** |
| NDI sender output | 8-bit (BGRA/UYVY/I420) | **16-bit (P216/PA16) opt-in** |
| File / encoder output | n/a | 10/12/16-bit native via the §3.3 encoder project |

---

## 11. End notes
