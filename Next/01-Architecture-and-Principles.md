# 01 — Architecture & Principles

## 1. What's actually wrong today

This is not a "the code is bad" list — most of the engine is good. These are the **structural**
problems that make the codebase feel overengineered and hard to extend, and that the rewrite exists
to fix. Each is grounded in the current tree.

| # | Problem | Evidence | Cost |
|---|---|---|---|
| P1 | **Product logic lives in two places.** The cue/playback engine exists both in the framework *and* in the UI. | `S.Media.Playback/ClipCompositionRuntime.cs` (1297) + `UI/HaPlay/Playback/CuePlaybackEngine.cs` (2425), `HaPlayPlaybackSession.cs` (1939), `SoundboardEngine.cs`. | Every feature is implemented/maintained twice; the framework can't be used headless for shows; tests duplicated. |
| P2 | **Process-wide mutable plugin state.** Backends register by writing static fields. | `MediaFrameworkPlugins.AudioSourceFileFactory = …`, `…VideoCpuFrameConverterFactory`, `PreserveDefaults()` scope to stop leakage across tests. | Can't run two configurations in one process; tests fight global state; ordering bugs; not obvious what's installed. |
| P3 | **The compositor secretly depends on the decoder.** | `S.Media.Effects/LayerHandle.cs` → `using S.Media.FFmpeg.Video;` and the `.csproj` references `S.Media.FFmpeg`. | "Compositing" drags in all of FFmpeg; you can't ship a GL-only compositor; layering violation. |
| P4 | **`S.Media.Core` is a kitchen sink.** 15.9k LOC / 119 files: primitives **and** routers **and** clocks **and** a playback coordinator. | `Core/Audio/AudioRouter.cs` (1774), `Core/Video/VideoRouter.cs` (1129), `Core/Video/VideoPlayer.cs` (1085), `Core/Playback/AvPlaybackCoordinator.cs`. | "Core" can't be reasoned about or depended on cheaply; everything transitively pulls everything. |
| P5 | **God-objects in the UI.** | `MediaPlayerViewModel.cs` (3549), `ControlWorkspaceViewModel.cs` (2926), `CuePlayerViewModel` (~3.4k across partials). | Unmaintainable; the decomposition was started (memory: 4862→3418) but stalled. |
| P6 | **Device-specific protocol code baked into the control framework.** | `S.Control/X32Session.cs`, `X32MeterCacheDecoder.cs`, `ControlX32ProtocolMaintenanceManager.cs`, `XTouchMiniX32FaderMapping.cs`. | Every new console = new code in the shared lib; "compatibility with multiple device types" doesn't scale. |
| P7 | **Live vs. file A/V sync are different code paths.** | memory + `Core` review docs: NDI is "master-less" (ingest clock discarded), file path schedules against a master clock. | Live sync is fragile (the recurring NDI desync work); two mental models for one problem. |

## 2. Design principles for the rewrite

1. **Strict acyclic layering.** Dependencies point down only. A lower layer never names a higher one.
   `Core` has **zero** backend dependencies (no FFmpeg/GL/SDL/PortAudio/NDI).

2. **Core = contracts + primitives, nothing else.** Frames, formats, channel math, the I/O
   interfaces, negotiation, and the *media registry contracts*. Routers, clocks, players, and the
   session move out into their own projects (you already sketched these).

3. **Every backend is a peer behind an interface.** The current `IAudioBackend` doc already states
   the ethos — *"the only layer a new backend must implement … a new backend is a peer of PortAudio,
   not a translation of it."* Generalize that to **all** I/O: decode, encode, present, ingest,
   compositor layer-surfaces. The engine sees interfaces, never concrete backend types. Shared
   backend implementation details live in explicit common libraries (for example
   `S.Media.FFmpeg.Common`), not by making backend modules reference each other.

4. **No process-wide mutable plugin state.** Replace the static `MediaFrameworkPlugins` slots with
   **instance-scoped registries** built once and injected. `S.Media.Core` owns media capability
   contracts; compositor and control add their own capability registries/extensions so Core never
   names GL/compositor/control-specific types. Two sessions with different capabilities can coexist;
   tests need no global teardown. (See [05](05-Plugin-Model.md).)

5. **One dynamic-extension mechanism: the native C-ABI plugin.** The framework must stay
   NativeAOT-compatible (it already ships `s_media_player.so` via `S.Media.Interop`), so there is **no**
   managed reflection / `AssemblyLoadContext` plugin loading. Third-party plugins are compiled native
   libraries exposing a C plugin ABI; the general `S.Abi` host `dlopen`s them and adapts their vtables
   to managed media/compositor/control capability interfaces. Build-time .NET extensions use typed
   registration instead.

6. **Product logic belongs in the framework.** The cue engine, soundboard, output mapping, and show
   session live in `S.Media.Session`, usable headless (and from the C ABI). The UI only *drives* them.

7. **AOT-first, allocation-aware.** Everything compiles clean under `PublishAot`. Source-generated
   MVVM (CommunityToolkit) and source-generated marshalling; no reflection on hot or registration
   paths; pooled frame buffers stay pooled.

8. **One sync model.** File and live both schedule against a master clock (one per transport group) with
   per-source timelines (offset + rebase policy) and source sync groups for correlated streams from
   one sender/device. No second "master-less" path. (See [03](03-AV-Sync-Clocks-Routing.md).)

9. **Simplicity is a feature, measured.** Targets: no framework source file > ~800 LOC without a
   reason; `Core` < ~6k LOC; product logic implemented **once**; project count stays close to today's
   (the split is for dependency hygiene, not ceremony).

## 3. The layered dependency graph (framework)

Read top-to-bottom = high-to-low. An arrow `A → B` means "A references B".

```
                          ┌─────────────────────────────────────────────┐
  Tier 7  hosts/abi       │  S.Abi  (in/plugins)      S.Media.Interop (out)│
                          └─────────────────────────────────────────────┘
                                     │                    │
  Tier 6  control          S.Control │                    │
                                     ▼                    ▼
  Tier 5  show            ┌──────────────────────────────────────────────┐
                          │             S.Media.Session                   │
                          └──────────────────────────────────────────────┘
                                     │
  Tier 4  players                    ▼   S.Media.Players
                                     │
  Tier 3b backend modules ┌──────────────────────────────────────────────┐
   (plug-in capabilities) │ FFmpeg.Common  Decode.FFmpeg  Encode.FFmpeg    │
                          │ Audio.PortAudio  Audio.MiniAudio  Present.SDL3 │
                          │ Present.Avalonia  NDI  Images.Skia  Subtitles │
                          └──────────────────────────────────────────────┘
                                     │
  Tier 3  compositor                 ▼   S.Media.Compositor
                                     │
  Tier 2  engine prims      S.Media.Time     S.Media.Routing     S.Media.Gpu
                                     │              │                │
  Tier 1  core                       └──────► S.Media.Core ◄─────────┘
                                                   │
  Tier 0  native                      PALib · MALib · PMLib · NDILib · OSCLib · LibAssLib
```

Allowed reference rules (enforced by `.csproj` review and ideally an arch test):

| Project | May reference |
|---|---|
| `S.Media.Core` | native wrappers only if strictly needed (prefer none); NuGet primitives. **Not** FFmpeg/GL/SDL/PA/NDI. |
| `S.Media.Time` | Core |
| `S.Media.Routing` | Core, Time |
| `S.Media.Gpu` | Core, Silk.NET.OpenGL |
| `S.Media.Compositor` | Core, Gpu (**never** a decoder) |
| `S.Media.FFmpeg.Common` | Core (+ FFmpeg.AutoGen); shared FFmpeg runtime/mapping utilities only |
| `Decode.FFmpeg` / `Encode.FFmpeg` | Core, `S.Media.FFmpeg.Common` |
| `Audio.PortAudio` / `Audio.MiniAudio` | Core, Time, Routing (+ PALib / MALib) |
| `Present.SDL3` / `Present.Avalonia` | Core, Gpu (+ SDL3-CS / Avalonia) |
| `NDI` | Core, Time, Routing (+ NDILib) |
| `Images.Skia` | Core (+ SkiaSharp) |
| `Subtitles` | Core (+ libass; embedded/bitmap subtitle support through registry capabilities, not concrete decoder references) |
| `S.Media.Players` | Core, Time, Routing, **media registry contracts** |
| `S.Media.Session` | Core, Time, Routing, Players, Compositor, **media/compositor registry contracts** (not concrete backends) |
| `S.Control` | Core, Session (for actions), PMLib, OSCLib, Mond |
| `S.Abi` | Core plus media/compositor/control capability packages as needed; adapts native plugins into framework interfaces |
| `S.Media.Interop` | Core, Session, the backend modules it bundles (it's the host) |

The crucial inversion: **`S.Media.Session` depends on media/compositor registry *contracts*, not on
`Decode.FFmpeg`/`Present.SDL3`/etc.** The concrete backends are wired in at the composition root (the
host app, `S.Media.Interop`, or a test) by registering modules. That is what makes "if no audio module
is present, don't offer audio output" fall out naturally instead of needing special-casing.

## 4. How the must-have features map onto the layers

| Feature | Where it lives |
|---|---|
| A/V sync (file + live, multi-in/out) | `S.Media.Time` (`SessionClock`, `SourceTimeline`, `SourceSyncGroup`, output sync groups) + `S.Media.Routing` + `S.Media.Players`/`Session` scheduling |
| GPU decode, shader pixel formats | `Decode.FFmpeg` (hw decode) + `S.Media.Gpu` (upload + YUV shaders) + `Core` (format negotiation) |
| Compositions, layers, transforms, transitions | `S.Media.Compositor` |
| Mesh warp + splitting (composition & output) | `S.Media.Compositor` (`WarpMesh`/`WarpSection`/warp pass) — see [04](04-Compositor-Warp-GPU.md) |
| Multiple outputs in one composition | `S.Media.Compositor` `CompositeMulti` output targets + `S.Media.Time` sync group |
| Audio channel remap + multi-track | `Core` (`ChannelMap`) + `S.Media.Routing` (matrix) + `Session` (track selection) |
| Text / image / video layers | `Images.Skia` (image+text sources) → `Compositor` layers |
| MIDI/OSC/Mond scripting + automations | `S.Control` |
| Subtitles | `Subtitles` module → `Compositor` layer |
| Plugins (audio/video I/O, layer surfaces, control decoders) | typed modules + `S.Abi` native C-ABI — see [05](05-Plugin-Model.md) |
| SDL3 + Avalonia outputs | `Present.SDL3` + `Present.Avalonia` |
| PortAudio + miniaudio | `Audio.PortAudio` + `Audio.MiniAudio` |
| NDI in/out | `NDI` |

Continue to **[02 — Project Structure](02-Project-Structure.md)** for the per-project breakdown.
