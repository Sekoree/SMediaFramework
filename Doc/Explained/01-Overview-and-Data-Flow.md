# 01 · Overview & Data Flow

## What MFPlayer actually is

MFPlayer is a **media playback framework** (a library) plus a **demo/show-control
application** (HaPlay) built on it. The framework decodes audio and video with
FFmpeg and pushes it through a small set of *syncing and routing layers* to a
pluggable set of outputs: PortAudio for sound; SDL3 / OpenGL / Avalonia for local
video; and NDI for sending audio+video over the network. On top of that sit
"product" concepts — media players, cue stacks, soundboards, routing scenes — and
a scripting/control layer that glues MIDI and OSC controllers to all of it.

It grew out of wanting an FFmpeg decoder for an existing audio library, then
"could it play video too?", and finally a clean re-architecture. The result is
unusually disciplined for a hobby/work tool: clear assembly layering, no FFmpeg
leakage into the core, a real plugin-indirection system, NativeAOT support, and a
strong test/probe suite.

The core design philosophy is **four roles and a conductor**:

1. **Sources** produce data (`IAudioSource`, `IVideoSource`).
2. **Outputs** consume data (`IAudioOutput`, `IVideoOutput`).
3. **Routers** connect them (`AudioRouter`, `VideoRouter`).
4. **Clocks** decide *when* (`MediaClock` + `IPlaybackClock`).

If you understand those four interfaces, you understand the framework. Everything
else is an implementation of one of them, or a facade that wires several together.

## Assembly layering (where a type lives, and why)

The repository is split into native binding libraries, the framework proper, and
the UI. The framework enforces a strict dependency direction so the engine never
takes a hard dependency on FFmpeg or any host platform.

```
┌──────────────────────────────────────────────────────────────────────────┐
│  HaPlay (Avalonia desktop app)   +   Tools/* (smoke tests & probes)        │  hosts
└──────────────────────────────────────────────────────────────────────────┘
        │ references everything below
┌──────────────────────────────────────────────────────────────────────────┐
│  S.Media.Playback   — MediaPlayer, MediaSession, MediaGraph,               │  PRODUCT /
│                       MediaPlayerController, Soundboard, CueVoice,          │  show-control
│                       Clip standby/composition runtimes                     │  tier
└──────────────────────────────────────────────────────────────────────────┘
        │
┌─────────────────────────┬───────────────────┬────────────────────────────┐
│ S.Media.FFmpeg          │ S.Media.Effects   │ Output backends:            │
│  (decode/demux/encode,  │  (CPU & GL         │  S.Media.PortAudio          │  capability
│   resample, hw decode)  │   compositor,      │  S.Media.OpenGL / .SDL3     │  packages
│ S.Media.FFmpeg.Encode   │   transitions,     │  S.Media.NDI                │  (opt-in,
│ S.Media.SkiaSharp       │   warp mesh)       │  S.Media.Avalonia           │  reference
│  (images, text)         │                   │                             │  what you use)
└─────────────────────────┴───────────────────┴────────────────────────────┘
        │ all reference ↓
┌──────────────────────────────────────────────────────────────────────────┐
│  S.Media.Core  — the engine + primitives. NO FFmpeg, NO host deps.         │  ENGINE /
│  Interfaces (IAudioSource/Output, IVideoSource/Output, ISeekableSource,    │  primitive
│  IMediaClock, IPlaybackClock …), AudioRouter, VideoRouter, VideoPlayer,    │  layer
│  clips/voices/buses, clocks, TriggerBus, compositor *contracts*.           │
│  Only dependency: Microsoft.Extensions.Logging.Abstractions.               │
└──────────────────────────────────────────────────────────────────────────┘

  Native binding libs (separate, low-level, no framework deps):
  PALib (PortAudio) · NDILib · PMLib (PortMidi) · OSCLib · JackLib

  Control surface (its own stack): S.Control  — Mond scripting, OSC/MIDI bridges
```

### The rule for "which assembly?"

* **`S.Media.Core`** — a reusable engine or buffer/voice primitive. It must not
  reference FFmpeg or any platform. When Core needs FFmpeg-only behaviour (decode
  a file, resample, deinterlace, convert pixels) it reaches through an indirection
  table (`MediaFrameworkPlugins` / the various registries) that the FFmpeg package
  *populates at startup*. This is why you must call `MediaFrameworkRuntime.Init().UseFFmpeg()`
  before opening files — it installs those plugin slots.
* **`S.Media.Playback`** — a high-level facade a host app *drives* (a player, a cue
  stack, a soundboard, a routing scene). It references Core + FFmpeg.
* **Backend packages** (`S.Media.PortAudio`, `.OpenGL`, `.SDL3`, `.NDI`, `.SkiaSharp`,
  `.Avalonia`) — concrete `IAudioOutput` / `IVideoOutput` / `IVideoSource`
  implementations. A host references only the ones it actually uses; the core graph
  never names them.

This layering is what lets the framework publish as a **46 MB NativeAOT binary**:
no reflection-heavy DI, plugin slots are plain delegates, and platform code is
isolated behind interfaces.

## The plugin indirection (why `UseFFmpeg()` exists)

Core defines *contracts* and a set of process-wide *registries*:

* `MediaFrameworkPlugins` — slots for file/stream decode, audio resampling, etc.
* `MediaFrameworkExtensionRegistry` — file-extension → source factory (images).
* `VideoCpuFrameConverterRegistry`, `VideoDeinterlacerRegistry` — pixel convert /
  deinterlace factories.

`MediaFrameworkRuntime.Init()` returns a fluent builder. Module hooks installed by
each package fill in the slots:

```csharp
MediaFrameworkRuntime.Init()
    .UseFFmpeg()          // installs decode, resample, swscale, yadif (S.Media.FFmpeg)
    .UsePortAudio()       // PortAudio device lifetime (S.Media.PortAudio)
    .UseSkiaSharpImages() // image + text sources (S.Media.SkiaSharp)
    .UseNDI();            // NDI runtime (S.Media.NDI)
```

After that, `MediaContainer.OpenFile(...)`, `VideoSource.OpenImage(...)`, etc. work
without the caller (or Core) ever naming FFmpeg or Skia. `MediaFrameworkRuntime.Shutdown()`
tears the slots back down for long-lived hosts.

## End-to-end data flow: a file to your ears and eyes

Here is the canonical single-file playback path (what `MediaPlayer` and the smoke
tools build). Follow the numbers.

```
 ┌────────────┐   1. open + probe          ┌──────────────────────────────────┐
 │  clip.mkv  │ ─────────────────────────► │ MediaContainerDecoder            │
 └────────────┘                            │   └ MediaContainerSharedDemux    │
                                           │       (one reader thread reads    │
                                           │        packets, fans them to per- │
                                           │        stream bounded queues)     │
                                           └───────────────┬──────────────────┘
                                          audio packets    │   video packets
                                  ┌────────────────────────┘  └───────────────────────┐
                          2a. decode (own lock)                          2b. decode (own lock)
                                  ▼                                                ▼
                       ┌───────────────────┐                          ┌────────────────────┐
                       │ AudioFileDecoder  │  IAudioSource            │ VideoFileDecoder   │ IVideoSource
                       │  → packed float32 │                          │  → native pixfmt   │
                       └─────────┬─────────┘                          └─────────┬──────────┘
                                 │ pull (ReadInto)                              │ pull (TryReadNextFrame)
                                 ▼                                              ▼
                       ┌───────────────────┐                          ┌────────────────────┐
                       │   AudioRouter     │ sums N sources           │   VideoRouter      │ fans 1 input
                       │  per-chunk mix    │ into M outputs           │  to M outputs,     │ to M outputs,
                       │  (ChannelMap +    │                          │  negotiates pixfmt │ per-branch convert
                       │   SIMD, gain)     │                          │  per output        │
                       └─────────┬─────────┘                          └─────────┬──────────┘
                       3. push   │                                    via VideoPlayer (picks the
                                 ▼                                    right frame each tick) + Pump
            ┌────────────────────┴───────────┐                                  ▼
            ▼                    ▼            ▼                  ┌──────────┬──────────┬──────────┐
    ┌─────────────┐     ┌──────────────┐ ┌─────────┐            ▼          ▼          ▼          ▼
    │ PortAudio   │     │ NDI audio    │ │ file mux│       ┌─────────┐┌─────────┐┌─────────┐┌─────────┐
    │ output      │◄─┐  │ sender       │ │ (encode)│       │ SDL3/GL ││ Avalonia││ NDI vid ││ file mux│
    └─────────────┘  │  └──────────────┘ └─────────┘       └─────────┘└─────────┘└─────────┘└─────────┘
     reports played  │
     sample count    │ 4. MediaClock.SetMaster(thisOutput)
                     │
              ┌──────┴───────┐
              │  MediaClock  │  position = audio output's real elapsed time.
              │ (the conductor)│ VideoPlayer asks the clock "what time is it?"
              └──────────────┘  each VideoTick and presents the matching frame.
```

**Step by step:**

1. **Open & probe.** `MediaContainer.OpenFile` builds a `MediaContainerDecoder`,
   which owns a `MediaContainerSharedDemux`: a *single* libav demuxer with one
   background reader thread that pulls packets and routes them into bounded
   per-stream queues. Audio and video then decode on *separate threads* with
   *separate decode locks*, so neither blocks the other.
2. **Decode.** `AudioFileDecoder` converts to packed (interleaved) 32-bit float at
   the source's native rate/channels. `VideoFileDecoder` decodes to the codec's
   native pixel format (zero-copy pass-through) unless a specific format is
   requested, in which case it inserts an sws_scale.
3. **Route & mix.** `AudioRouter` pulls a fixed chunk from each routed source once
   per cycle, mixes every route's contribution (channel map + gain) into each
   output's buffer, and hands the buffer to that output's *pump*. `VideoRouter`
   sends one input's frames to many outputs, negotiating a pixel format with each.
4. **Pace to the clock.** The audio output (PortAudio) reports how many samples the
   hardware has actually played; `MediaClock` slaves to that. `VideoPlayer` reads
   the clock on each video tick and presents the frame whose PTS matches — so the
   picture follows the *heard* audio, immune to wall-clock drift.

That's the whole engine. The product tier wraps this so you don't hand-wire it;
the control tier sends triggers into it; the UI visualizes and operates it.

## Repository map

| Path | Role | Doc |
|------|------|-----|
| `MediaFramework/Media/S.Media.Core` | Engine + primitives (154 production types) | 03–07 |
| `MediaFramework/Media/S.Media.FFmpeg(.Encode)` | Decode/demux/encode (70 production types) | 08 |
| `MediaFramework/Media/S.Media.Effects` | Compositor, transitions, warp (53 production types) | 10 |
| `MediaFramework/Media/S.Media.{PortAudio,OpenGL,SDL3,NDI,SkiaSharp,Avalonia}` | Output/source backends (82 production types) | 09 |
| `MediaFramework/Media/S.Media.Playback` | Product facades (115 production types) | 11 |
| `MediaFramework/Control/S.Control` | Show-control + Mond scripting (180 production types) | 12 |
| `MediaFramework/Audio/PALib`, `NDI/NDILib`, `Extras/{MIDI/PMLib,OSC/OSCLib,JackLib}` | Native bindings (199 production types) | 02 |
| `MediaFramework/Tools/*` | Smoke tools & probes (17 declared helper types plus top-level tools) | 14 |
| `UI/HaPlay`, `UI/HaPlay.Desktop` | Avalonia app and desktop entry point (344 production types) | 13 |

For a file-by-file inventory of the 1,214 production source types, see
[16 · Type Coverage Appendix](16-Type-Coverage-Appendix.md).

## Conventions you'll see everywhere

* **`Try*` + `out error`** patterns for fallible construction (`TryBuild`,
  `TryAddRoute`) alongside async `OpenAsync()` builders.
* **Immutable snapshot state** on the hot paths: routers keep an immutable
  `RouterState`/graph and swap the whole thing atomically, so the run loop reads a
  consistent view per chunk with no locks.
* **Pumps** (`OutputPump`, `VideoOutputPump`): bounded async hand-off to slow
  outputs with a drop policy, so one slow consumer can't stall the graph.
* **Cooperative shutdown** (`ICooperative*ReadInterrupt`, `CooperativePlaybackJoin`):
  long blocking reads observe a yield flag so Pause/Dispose stay responsive.
* **Ref-counted native lifetimes** (`PortAudioRuntime`, `SDL3Runtime`, `NDIRuntime`,
  `ControlMidiLibraryLease`): first holder inits the native lib, last holder frees it.
* **Opt-in profiling** behind env vars (`MF_MEDIA_PROFILE_*`) that cost a single
  volatile read when off.

Read [06 · Clocks & A/V Sync](06-Clocks-and-AV-Sync.md) next — the clock model is
the thing that makes the rest make sense.
