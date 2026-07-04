# 02 — Project Structure

Every project in `MFPlayer.Next.sln` (framework scope). For each: its one-line job, key
public types, dependencies, and **what salvages into it** from today's tree. "Salvage" means the
logic is good and should be moved + cleaned, not rewritten blank.

Naming keeps your `S.Media.*` convention. Native wrappers keep their current names (you want them
kept; renaming is optional churn). The split mirrors the stubs you already created.

---

## Tier 0 — Native wrappers & protocol libs (KEEP)

These are stable P/Invoke and protocol code. Move them across unchanged except for namespace tidy.

| Project | Job | Notes |
|---|---|---|
| `PALib` | PortAudio P/Invoke | keep |
| `MALib` | miniaudio P/Invoke | keep (lazy native resolve already) |
| `PMLib` | PortMidi P/Invoke | keep |
| `NDILib` | NDI SDK P/Invoke | keep |
| `OSCLib` | OSC client/server library | keep (you explicitly want this kept) |
| `JackLib` | JACK P/Invoke | keep, optional module backend |
| `LibAssLib` | libass P/Invoke (ASS/SSA subtitles) | **new** (D14); same native-deploy as PALib/PMLib |

NuGet primitives (not vendored): `FFmpeg.AutoGen`, `SDL3-CS`, `Silk.NET.OpenGL`, `SkiaSharp`,
`Mond`, `CommunityToolkit.Mvvm`, `Vortice.*` (D3D11 interop on Windows).

---

## Tier 1 — `S.Media.Core`  *(contracts + primitives; no backend deps)*

**Job:** the vocabulary every other project speaks. Target ≈ 5–6k LOC (down from 15.9k).

**Keeps (salvage from today's `S.Media.Core`):**
- Frames & formats: `VideoFrame` (+ `.Validation`, `VideoFrameMetadata`), `AudioFrame`,
  `PixelFormat`/`PixelFormatInfo`, `VideoFormat`, `AudioFormat`, `Rational`, color/range/space enums.
- Hardware-frame backing descriptors: `DmabufNv12/P010/P016Backing`, `Win32SharedNv12Backing`,
  `HardwareVideoInterop*`, `IHardwareD3D11GlInteropSource`, `DrmPixelFormats`. (Descriptors only —
  the *upload* code moves to `S.Media.Gpu`.)
- I/O contracts: `IVideoSource`/`IVideoOutput`, `IAudioSource`/`IAudioOutput`, `IAudioBackend`,
  and capability interfaces (`IClockedOutput`, `IFlushableOutput`, `ISeekableSource`,
  `IVideoOutputQueueControl`, `IVideoOutputCooperativeAbort`, `ISyncPresentableVideoOutput`,
  `IVideoCpuFrameConverter`, `IDeinterlacer`, …).
- Negotiation: `VideoFormatNegotiator`, `VideoOutputFanoutFormats`.
- Channel math: `ChannelMap` + `ChannelMap.SimdAccumulate`, `AudioChannelLayoutPresets`,
  `AudioFormat`/`AudioBus` value types. (The *router* leaves; the *math* stays.)
- Diagnostics: `MediaDiagnostics` (logger factory + per-class loggers), `NativeResourceHealth`.
- `ClipWindow`, `DisposableRelease`, small utilities.

**New in Core:**
- The **media registry contracts**: `IMediaModule`, `IMediaRegistry`, `IMediaRegistryBuilder`, and
  media capability provider interfaces (decoder/backend/presenter/image/subtitle factories). Compositor
  layer-surface factories and control decoder/profile providers live in their owning projects as
  registry extensions, so Core never names compositor/control types. See [05](05-Plugin-Model.md).

**Leaves Core (this is the big change):**
- `Audio/AudioRouter*` → `S.Media.Routing`
- `Clock/*`, `OutputSyncGroup`, `VideoPresentSyncGroup`, `VideoPtsClock` → `S.Media.Time`
- `Video/VideoRouter`, `VideoOutputPump`, `RetimingVideoOutput`, `SyncPresentVideoOutput` → `S.Media.Routing`
- `Video/VideoPlayer` → `S.Media.Players`
- `Playback/AvPlaybackCoordinator`, `MediaPlaybackSession` → `S.Media.Players`
- `Diagnostics/MediaFrameworkPlugins`/`…Runtime`/`…ExtensionRegistry` → **deleted**, replaced by the registry.

---

## Tier 2 — engine primitives

### `S.Media.Time`  *(→ Core)*
**Job:** the single source of "what time is it" for sync. **Salvage:** `MediaClock`,
`CompositePlaybackClock(.Blend)`, `IMediaClock`/`IPlaybackClock`/`IPlayhead`, `VideoPtsClock`,
`OutputSyncGroup`, `VideoPresentSyncGroup`, `PlaybackTimelineClockExtensions`. **New:** a first-class
`SessionClock` (master), `SourceTimeline` (per-source offset + rebase policy), and `SourceSyncGroup`
(correlated A/V streams from one sender/device) — see [03](03-AV-Sync-Clocks-Routing.md).

### `S.Media.Routing`  *(→ Core, Time)*
**Job:** N→M audio mixing/routing and video fan-out — backend-neutral. **Salvage:** `AudioRouter`
(+ `.Matrix`/`.OutputPump`/`.Playback`), the router clocks (`OutputSlaved`/`PlaybackSlaved`/
`WallClockRouterClock`), `VideoRouter`, `VideoOutputPump`, `RetimingVideoOutput`,
`SyncPresentVideoOutput`, `RouteGainSlot`, `PumpPressurePlaybackHintMonitor`. These are solid and
heavily tested — move with minimal change.

### `S.Media.Gpu`  *(→ Core, Silk.NET.OpenGL)*
**Job:** the GL device and everything pixels-on-GPU. **Salvage from `S.Media.OpenGL`:**
`YuvVideoRenderer` (1592), `Nv12Win32SharedHandleGpuUploader` (953), `EglDmabufNv12Uploader`,
`GlVideoFormatSupport`, `D3D11GlInteropDeviceHost`, `D3D11InteropUtility`, `SharedGlProgramCache`,
`YuvColorSpace`/`RgbGamutMatrix`/`VideoHdrTransfer`, `VideoViewportFit`, the WGL/EGL interop bits.
**Principle:** this is the *only* project that knows GL. Presenters and the compositor consume it;
they don't re-implement uploads. The context is shared **per render thread** (the `SharedSdlGlContext`
pattern), not one global group; cross-context/API output (Avalonia, NDI, plugins) is fed via exported
external images + semaphores — the D7/D8 handle currency — not by sharing this context. Leave a thin
internal seam (`IGpuDevice`) so a future Vulkan/Metal backend is possible — but ship GL only.

### `S.Media.FFmpeg.Common`  *(→ Core, FFmpeg.AutoGen)*
**Job:** shared FFmpeg runtime binding, error/status helpers, stream/format mapping, native-library
resolution, and small utilities used by decode, encode, and FFmpeg-backed subtitle/capture code.
It must not expose concrete decoders/encoders. This keeps `Decode.FFmpeg`, `Encode.FFmpeg`, and
subtitle FFmpeg support from referencing each other while avoiding duplicated FFmpeg glue.

---

## Tier 3 — `S.Media.Compositor`  *(→ Core, Gpu)*

**Job:** combine N layers into a canvas, transform/blend/animate them, and warp the canvas to one
or more outputs. **This must not reference any decoder.**

**Salvage from `S.Media.Effects`:** `IVideoCompositor`, `VideoCompositor`, `CpuVideoCompositor`,
`GlVideoCompositor` (1402), `VideoCompositorSource`, `LayerHandle`/`LayerConfig`/`LayerConfigResolver`/
`LayerTransform2D`/`LayerPosition`/`LayerAnchor`/`LayerOpacityTween`, `BlendMode`, transitions
(`Cut`/`FadeFromBlack`/`Transition`), `WarpPass`/`WarpMesh`/`WarpSection`/`WarpMeshTessellator`,
`IWarpPassVideoCompositor`, `PlacementResolver`, `StaticFrameSource`.

**Fix the coupling (P3):** today `LayerHandle.cs` uses the concrete `S.Media.FFmpeg.Video.
VideoCpuFrameConverter`. In the rewrite it uses `IVideoCpuFrameConverter` resolved from the registry
(`registry.VideoCpuConverterFactory`). With FFmpeg registered the behavior is identical; without it
the compositor still builds and runs (GPU path / BGRA-only).

**New:** `IVideoCompositorLayerSurface` — the plugin seam for custom layer types that render directly
into the GL canvas (the "3D object layer" idea). `S.Media.Compositor` owns the layer-surface registry
extension; Core only knows that registries can be extended by higher-layer packages. See
[04](04-Compositor-Warp-GPU.md) and [05](05-Plugin-Model.md).

---

## Tier 3b — backend modules (capabilities; each registers itself)

Each is an `IMediaModule`. None reference each other directly; shared implementation goes through
explicit common projects such as `S.Media.FFmpeg.Common`. Presence in the registry = capability
available.

| Project | Deps | Provides | Salvage from |
|---|---|---|---|
| `S.Media.FFmpeg.Common` | Core (+FFmpeg.AutoGen) | FFmpeg init, native loading, error helpers, stream/format mapping shared by FFmpeg modules | split from `S.Media.FFmpeg` / `S.Media.FFmpeg.Encode` |
| `S.Media.Decode.FFmpeg` | Core, FFmpeg.Common | file/stream `IVideoSource`+`IAudioSource`, hw decode (D3D11VA/VAAPI/…), `IVideoCpuFrameConverter` (swscale), `IDeinterlacer` (yadif), audio resampler, **capture** sources (v4l2/dshow), track enumeration, embedded subtitle packet/source providers | `S.Media.FFmpeg` (8.5k) |
| `S.Media.Encode.FFmpeg` | Core, FFmpeg.Common | muxers/encoders for record/stream out | `S.Media.FFmpeg.Encode` — **NOT BUILT: the empty Phase-0 shell was removed from the solution (NXT-13, 2026-07-01); salvage the old project when the YouTube remux gate (Gate 5) starts** |
| `S.Media.Audio.PortAudio` | Core, Time, Routing (+PALib) | `IAudioBackend` (output+input), clocked output | `S.Media.PortAudio` |
| `S.Media.Audio.MiniAudio` | Core, Time, Routing (+MALib) | `IAudioBackend` (output+input) | `S.Media.MiniAudio` |
| `S.Media.Present.SDL3` | Core, Gpu (+SDL3-CS) | `IVideoOutput` on its own render thread | `S.Media.SDL3` |
| `S.Media.Present.Avalonia` | Core, Gpu (+Avalonia) | `IVideoOutput` embeddable in an `OpenGlControlBase` | `S.Media.Avalonia` |
| `S.Media.NDI` | Core, Time, Routing (+NDILib) | NDI sender (video+audio `IVideoOutput`/`IAudioOutput`) **and** receiver (`IVideoSource`/`IAudioSource`) | `S.Media.NDI` (4.3k) |
| `S.Media.Images.Skia` | Core (+SkiaSharp) | still-image `IVideoSource`, text-layer rendering | `S.Media.SkiaSharp` — **NOT BUILT: the empty Phase-0 shell was removed from the solution (NXT-13, 2026-07-01); text/still rendering shipped HaPlay-private instead (`TextFrameRenderer` + the `text:` provider), images open via FFmpeg — a headless host cannot render text cues until a framework module exists** |
| `S.Media.Subtitles` | Core, LibAssLib (+ optional FFmpeg subtitle providers via registry) | timed subtitle layer source (SRT/VTT/ASS/PGS) | **new** — see [04](04-Compositor-Warp-GPU.md) §subtitles |

> JACK can ship later as `S.Media.Audio.Jack` (→ Core, Routing, JackLib) — same `IAudioBackend` seam.

---

## Tier 4 — `S.Media.Players`  *(→ Core, Time, Routing, media registry contracts)*

**Job:** play **one** media object (a file, a live source) — open → decode → sync → fan out to
outputs — with transport (play/pause/seek/rate). No compositing, no cues, no UI.

**Salvage:** `Core/Video/VideoPlayer` (1085), `Core/Playback/AvPlaybackCoordinator`,
`S.Media.Playback/MediaPlayer`(single-media parts)/`MediaPlayerController`/`MediaPlayerOpenBuilder`/
`MediaPlayerOpenOptions`/`PlaybackAudioStartup`/`OffsetPlayhead`. **New:** track-selection in the
open options (audio/subtitle: none/one/many).

**Registry boundary:** `MediaPlayer` receives `IMediaRegistry` (or a narrowed `IMediaSourceResolver`)
at construction/open time. It owns transport, sync, seek/rate, fan-out, and track selection; registry
providers own "how do I open this URI and expose tracks?" `Players` must never reference
`Decode.FFmpeg`, `NDI`, capture, or subtitle modules directly.

---

## Tier 5 — `S.Media.Session`  *(→ Core, Time, Routing, Players, Compositor, media/compositor registries)*

**Job:** the show. Multiple players + compositions + soundboard + cue sequencing + output mapping,
all sharing one master clock and a routing scene. **Headless-usable** (this is the API the UI and the
C ABI drive). This is where P1's duplication collapses into one home.

**Salvage & merge:**
- From `S.Media.Playback`: `ClipCompositionRuntime` (1297), `ClipStandbyEngine` (674),
  `ClipAudioOutputRuntime`, `ClipOutputMapping`, `CueGraph`/`CueVoice`, `MediaGraph`, `RoutingScene`,
  `Soundboard`/`SoundboardGrid`, `TriggerBindingSet`, `MediaSession`.
- **Moved out of the UI** (P1): `UI/HaPlay/Playback/CuePlaybackEngine` (2425),
  `HaPlayPlaybackSession`(+`.OutputWiring`) (2.4k), `SoundboardEngine`, the group-seek barrier, the
  output-mapping/warp wiring, output-health probes. These become framework services with no Avalonia
  dependency.

**Suggested internal shape (files, not projects):** `ShowSession`, `CueEngine`, `Soundboard`,
`CompositionRuntime`, `OutputMap` (binding → warp sections), `RoutingScene`, `TransportGroup`
(group seek/pause/fire). If `Session` grows past ~6k LOC, split `Cues` into `S.Media.Cues`; start
unified.

**Threading + persistence (D5/D10):** the public `ShowSession` API marshals commands onto an internal
session dispatcher (queries return immutable snapshots; it never assumes the UI thread; `Post`/`InvokeAsync`
only — **no blocking `Invoke` from within a dispatcher callback**, OQ8). `Session` owns
a serializable `ShowDocument` (System.Text.Json source-generated, AOT-safe) so shows load headless and
via the C ABI; the UI persists only view-state on top.

---

## Tier 6 — control

### `S.Control.Abstractions`  *(→ OSCLib)*

**Job:** Session-free control-decoder contracts and the scoped decoder registry shared by `S.Control`
and `S.Abi`. This is the resolved OQ10 boundary.

### `S.Control`  *(→ Core, Session, Control.Abstractions, PMLib, OSCLib, Mond)*

**Job:** MIDI/OSC ingest + Mond scripting + automations that drive the session and external devices.
See [06](06-Control-Surface.md) for the device-profile refactor (P6).

**Salvage:** `ControlScriptRuntime`(1053)/`…Session`, `ControlScriptApiLibrary`(1034),
`ControlEventQueue`/`ControlEvents`, `ControlMidiDeviceManager`/`…Resolver`/`…Sessions`,
`ControlOscListenerManager`/`ControlPeriodicOscSendManager`/`UdpControlOscSender`,
`ControlValueCache`, `MidiHighResolution14BitCombiner`, `ControlSystemConfig`/`…IO`.

**Refactor out:** `X32*`, `XTouchMiniX32FaderMapping`, device-specific `BuiltInControlDeviceProfile*`
→ **data-driven profiles** (and, for protocol oddities like X32 meter decode, an optional native
plugin). `S.Control` keeps the *engine*; devices become *content*.

---

## Tier 7 — interop / plugin host

### `S.Abi`  *(→ Core, Time, Compositor, Control.Abstractions)*
**Job:** the **inbound** general native plugin host. Defines the C plugin ABI (`include/mfp_plugin.h`),
`dlopen`s plugins, and adapts their vtables into media, compositor, and control capability providers
(`IAudioBackend`, `IVideoSource`, `IVideoOutput`, `IVideoCompositorLayerSurface`, control decoders,
etc.), registering them through the appropriate scoped registry. See [05](05-Plugin-Model.md).

### `S.Media.Interop`  *(→ Core, Session, bundled modules)*  **KEEP**
**Job:** the **outbound** C ABI — `s_media_player.so`/`.dll`/`.dylib` — so other languages drive the
framework. **Salvage** the existing `NativeApi.*`/`PlayerInstance`. Update its init to build a
registry (`UseFFmpeg().UsePortAudio().UseMiniAudio()…`) instead of poking static slots, and to expose
the new session API. Keep the ABI conventions (opaque handles, status codes, 100-ns ticks).

---

## Tier 8 — tools & tests (parity harness — port FIRST)

The current `Tools/*` smoke apps and `Test/*` suites are the executable spec for parity. Port them
early so each phase has a green/red gate:
`PlaybackSmoke`, `VideoPlaybackSmoke`, `CompositorSmoke`, `EncoderSmoke`, `SoundboardSmoke`,
`TransportSyncProbe`, `FormatSwitchProbe`, `FrameDump`, `GlProbe`, `NDIPlayer`/`NDIReceiver`, plus the
xUnit projects (`S.Media.Core.Tests`, `…FFmpeg.Tests`, `…Playback.Tests`, `…NDI.Tests`, `…OpenGL.Tests`,
`…PortAudio.Tests`, `…MiniAudio.Tests`, `…SkiaSharp.Tests`, `OSCLib.Tests`, `PMLib.Tests`).

---

## Project count check (simplicity)

Framework projects: **5** core/engine (Core, Time, Routing, Gpu, Compositor) + **2** player/session
+ **10** backend/common modules + **2** interop/abi + **1** control = **~20**, plus 7 native wrappers and the
tools/tests. That's in the same ballpark as today's 25 — the difference is clean boundaries, not more
ceremony. The headline shrink is **`Core` 15.9k → ~6k** and **product logic implemented once** instead
of twice.
