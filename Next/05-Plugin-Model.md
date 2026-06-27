# 05 — Plugin Model

The hard constraint: the framework must stay **NativeAOT-compatible** (it ships `s_media_player.so`
via `S.Media.Interop`). That rules out managed reflection / `Assembly.LoadFrom` / `AssemblyLoadContext`
plugin discovery — none of it survives AOT. So the model is:

- **Tier A — Build-time modules (typed registration).** AOT-pure. First-party + trusted .NET
  extensions, referenced at build time, registered through typed scoped registries. Replaces today's
  static `MediaFrameworkPlugins` global.
- **Tier B — Native C-ABI plugins (dynamic).** Third-party, language-agnostic. A plugin is any shared
  library (NativeAOT C#, C, C++, Rust, Zig…) that exports a C plugin ABI. The general `S.Abi` host
  `dlopen`s it and adapts its vtables to the same managed media/compositor/control capability
  interfaces a Tier-A module would register. The host stays AOT.

Both tiers end at scoped registries. Media capabilities end at **`IMediaRegistry`**; compositor
layer-surface capabilities end at a compositor registry extension; control profiles/decoders end at a
control registry. Once registered, a native plugin's audio backend is indistinguishable from PortAudio
to the rest of the engine.

---

## Tier A — the registry (replaces global static slots)

### Contracts (in `S.Media.Core`)

```csharp
public interface IMediaModule
{
    string Name { get; }            // "FFmpeg", "PortAudio", …
    void Register(IMediaRegistryBuilder b);
}

public interface IMediaRegistryBuilder
{
    // capability registration (append-only over time)
    IMediaRegistryBuilder AddDecoder(IMediaDecoderProvider p);        // file/stream/capture → sources, track enum
    IMediaRegistryBuilder AddAudioBackend(IAudioBackend backend);     // PortAudio, miniaudio, JACK…
    IMediaRegistryBuilder AddPresenter(IVideoPresenterFactory p);     // SDL3, Avalonia, NDI-out…
    IMediaRegistryBuilder AddImageSource(string ext, Func<string, IVideoSource> f);
    IMediaRegistryBuilder AddSubtitleProvider(ISubtitleProvider p);
    IMediaRegistryBuilder SetCpuConverterFactory(Func<IVideoCpuFrameConverter> f);   // swscale, etc.
    IMediaRegistryBuilder SetResamplerFactory(Func<IAudioSource,int,IAudioSource> f);
    IMediaRegistryBuilder SetDeinterlacerFactory(Func<VideoFormat,IDeinterlacer> f);
    // … one method per capability the old MediaFrameworkPlugins exposed as a static field
}

public interface IMediaRegistry   // immutable, queryable, injected
{
    IReadOnlyList<IAudioBackend> AudioBackends { get; }
    bool CanOpen(string uri);                                       // scheme-based dispatch (D2)
    bool TryOpenVideo(string uri, VideoSourceOpenOptions? o, out IVideoSource s);
    bool TryOpenAudio(string uri, AudioSourceOpenOptions? o, out IAudioSource s);
    IVideoCpuFrameConverter? CreateCpuConverter();
    // … typed queries; null/false when no module provides the capability
}
```

Higher layers extend registration without making Core depend upward:

```csharp
// S.Media.Compositor
public interface ICompositorRegistryBuilder
{
    ICompositorRegistryBuilder AddLayerSurface(
        string kind,
        Func<IVideoCompositorLayerSurface> factory);
}

// S.Control
public interface IControlRegistryBuilder
{
    IControlRegistryBuilder AddDeviceProfile(ControlDeviceProfile profile);
    IControlRegistryBuilder AddDecoder(string id, IControlFeedbackDecoder decoder);
}
```

### Composition root (host wires it once; nothing global)

```csharp
var plugins = AbiPluginCatalog.Load("./plugins");   // one dlopen pass; exposes typed registrations

var registry = MediaRegistry.Build(b => b
    .Use(new FFmpegModule())        // Decode.FFmpeg + Encode.FFmpeg
    .Use(new PortAudioModule())
    .Use(new MiniAudioModule())
    .Use(new Sdl3PresenterModule())
    .Use(new AvaloniaPresenterModule())
    .Use(new NdiModule())
    .Use(new SkiaImagesModule())
    .Use(new SubtitlesModule())
    .Use(plugins.Media));

var compositorRegistry = CompositorRegistry.Build(b => b
    .UseMedia(registry)
    .Use(plugins.Compositor));

var controlRegistry = ControlRegistry.Build(b => b
    .UseBuiltInProfiles()
    .Use(plugins.Control));

await using var session = MediaSession.Create(
    registry,
    compositorRegistry,
    sessionOptions);
```

Wins over the current static model (P2):
- **No process-wide mutable state.** Two sessions with different registries coexist; tests build a
  registry per case — the `MediaFrameworkPlugins.PreserveDefaults()` hack disappears.
- **Capability-by-presence falls out:** "no audio module registered ⇒ `registry.AudioBackends` is
  empty ⇒ the session offers no audio output." Exactly the behavior you asked for, with zero special
  casing. PortAudio-only ⇒ only PortAudio devices appear.
- **Discoverable & ordered:** the registry is the one place that says what's installed; registration
  order is explicit, not load-order-dependent.
- **AOT-clean:** all wiring is direct calls; the linker keeps only referenced modules.

A .NET author writing a Tier-A backend implements one interface (e.g. `IAudioBackend`) and ships an
`IMediaModule` that registers it — same effort as today, minus the globals.

---

## Tier B — native C-ABI plugins (`S.Abi`)

Symmetric to the outbound `s_media_player.h`: where `S.Media.Interop` lets other languages *drive*
the framework, `S.Abi` lets other languages *extend* media, compositor, and control capabilities. One
mechanism covers every dynamic plugin — including a .NET plugin that wants runtime loading: it
compiles to a NativeAOT shared lib exposing this same C ABI, so there's no separate managed-plugin
path to maintain.

### The contract (`include/mfp_plugin.h`)

```c
#define MFP_PLUGIN_ABI_VERSION MFP_MAKE_ABI_VERSION(1, 0)

typedef struct MfpPluginInfo {
    uint32_t abi_version;
    uint32_t struct_size;     // host validates required fields and ignores unknown trailing fields
    const char* id;           // "com.acme.webcam"
    const char* display_name;
    uint32_t capabilities;    // bitset: AUDIO_BACKEND | VIDEO_SOURCE | VIDEO_OUTPUT | LAYER_SURFACE | CONTROL_DECODER | SUBTITLE …
} MfpPluginInfo;

// Host services handed to the plugin (logging, diagnostics, time base, frame/sync capabilities).
typedef struct MfpHostApi {
    uint32_t abi_version;
    uint32_t struct_size;
    void (*log)(int level, const char* msg);
    void (*set_last_error)(const char* msg);
    int64_t (*now_ticks)(void);
    uint32_t supported_frame_kinds;
    uint32_t supported_sync_kinds;
    /* … append-only … */
} MfpHostApi;

// THE entry point every plugin exports. Fills info + registers capability vtables via callbacks.
int mfp_plugin_register(const MfpHostApi* host, MfpPluginInfo* out_info, MfpRegistrar* reg);
```

### Capability vtables (structs of function pointers)

Each maps 1:1 to a managed interface. Example — an audio backend (mirrors `IAudioBackend`):

```c
typedef struct MfpAudioBackendVTable {
    uint32_t abi_version;
    uint32_t struct_size;
    int  (*enumerate_outputs)(void* self, MfpAudioDeviceInfo* out, int cap, int* count);
    int  (*enumerate_inputs )(void* self, MfpAudioDeviceInfo* out, int cap, int* count);
    void*(*create_output)(void* self, const char* device_id, const MfpAudioFormat*, const MfpAudioOpts*);
    void*(*create_input )(void* self, const char* device_id, const MfpAudioFormat*, const MfpAudioOpts*);
    int  (*output_submit)(void* out, const float* interleaved, int float_count);   // push
    int  (*source_read_into)(void* in, float* dst, int float_count);               // pull
    void (*close_handle)(void* handle);
    void (*destroy)(void* self);
} MfpAudioBackendVTable;
```

Other vtables, each shadowing the framework interface it adapts to:
- **`MfpVideoSourceVTable`** ↔ `IVideoSource`: `native_pixel_formats`, `select_output_format`,
  `try_read_frame(out MfpVideoFrame)`, `is_exhausted`, `seek`. Frames cross as a **tagged union, one
  struct per kind** (OQ1 — one `uint64 gpu_handle` is not enough), mirroring the existing `S.Media.Core`
  HW-backings so the managed adapter is a near-direct map:
  - `MfpCpuFrame { void* planes[4]; int strides[4]; }`
  - `MfpDmaBufFrame { int n; int fds[4]; int offsets[4]; int strides[4]; uint64 modifiers[4]; uint32 fourcc; }` (← `DmabufNv12/P010/P016Backing`)
  - `MfpD3D11Frame { uint64 luma/chroma_nt_shared_handle; uint32 dxgi_format; uint32 array_slice; int y/uv_stride; }` (← `Win32SharedNv12Backing`)
  - `MfpGlTextureFrame { uint32 id; uint32 target; uint64 context_id; }` — **same-context only**, so for
    layer-surface plugins, never cross-process source/output frames.

  Common header `{ MfpFrameKind kind; uint32 w, h; MfpPixelFormat; int64 pts_ticks; MfpSync sync; void* opaque }`.
  GPU kinds are zero-copy via the same interop paths as `S.Media.Gpu` (D7); CPU-only plugins ignore the
  GPU kinds. **This struct set is the forever-surface — review hard before tagging ABI v1.**
- **`MfpVideoOutputVTable`** ↔ `IVideoOutput`: `accepted_pixel_formats`, `configure(format)`,
  `submit(frame)`.
- **`MfpLayerSurfaceVTable`** ↔ `IVideoCompositorLayerSurface`: `configure_gl(MfpGlContext, canvas)`,
  `render(MfpGlContext, int fbo, int64 master_ticks, const MfpTransform2D*, float opacity)`,
  `destroy`. **This carries the GL context + target FBO across the boundary** so a native plugin (the
  "3D object layer") renders straight into the canvas. `MfpGlContext` exposes the proc-address getter
  + current-thread guarantee, matching `S.Media.Gpu`'s device.
- **`MfpSubtitleVTable`** ↔ `ISubtitleSource`.
- **`MfpControlDecoderVTable`** ↔ `IControlFeedbackDecoder`: named binary/OSC/MIDI feedback decoders
  for device-profile entries such as `"decoder": "x32.meters"`.

### Managed adapters (`S.Abi`)

For each loaded vtable, `S.Abi` instantiates a small managed shim implementing the matching framework
interface and forwarding calls through the function pointers. It then registers the shim through the
appropriate scoped builder (`IMediaRegistryBuilder`, `ICompositorRegistryBuilder`,
`IControlRegistryBuilder`). After that, the engine cannot tell a native plugin from a built-in one.

```csharp
// inside AbiPluginCatalog.Load("./plugins"):
foreach (var lib in Directory.EnumerateFiles(dir, NativeLibPattern))
{
    var h = NativeLibrary.Load(lib);
    var register = (delegate* unmanaged<MfpHostApi*, MfpPluginInfo*, MfpRegistrar*, int>)
                   NativeLibrary.GetExport(h, "mfp_plugin_register");
    // call register, read info, build NativeAudioBackendAdapter(vtable) etc., add to builder
}
```

(`NativeLibrary.Load` + `GetExport` + `delegate* unmanaged` are fully AOT-safe — no reflection.)

### Cross-boundary rules (ABI hygiene)

- **Versioning:** `abi_version` + `struct_size` lead every public struct/vtable. The host validates the
  required prefix, copies the known prefix into a zero-filled normalized table, and ignores unknown trailing
  fields (append-only evolution). Reject + report on mismatch; never dereference an undersized table.
- **Errors:** functions return `int` status (0 ok, negative error) like `s_media_player`; never throw
  across the boundary; a thread-local last-error string is fetchable.
- **Ownership:** a source/subtitle producer keeps every returned CPU/GPU frame valid until the host calls
  that capability's `release_frame`. Output submit is synchronous; a queuing output copies or retains what it
  needs before returning. The host duplicates imported dma-buf/NT handles into Core-owned backings. GPU frames
  carry a **negotiated sync primitive** (`MfpSync`); unsupported frame/sync kinds are rejected explicitly.
- **Library lifetime:** disposing a plugin requests unload. Capability adapters and opened native instances hold
  leases; capability `destroy` callbacks and optional `mfp_plugin_unregister` run before `NativeLibrary.Free`
  only after the final lease is released.
- **Threading:** the contract states which thread each call runs on (e.g. `submit`/`render` on the
  clock/compositor thread, must return promptly; slow work goes to the plugin's own thread) — same
  rule as `IVideoOutput.Submit` today.
- **Time:** 100-ns ticks everywhere (matches `s_media_player`).
- **Trust:** native plugins run in-process with full rights (like any `.so`). Loading is **opt-in**
  via an explicit directory/allowlist; document it as "only load plugins you trust," same as VST/OBS.

### Why this is the right call

- Keeps the host **100% AOT** — no JIT-only code path, no `#if !AOT` divergence.
- **Language-agnostic** — the webcam/Decklink/3D-layer plugin can be Rust or C++; .NET authors get the
  same door via NativeAOT.
- **Symmetric & familiar** — it's the inbound twin of the export ABI you already maintain; one ABI
  style, one set of conventions (`s_media_player.h` ↔ `mfp_plugin.h`).
- **No managed-plugin lifetime hazards** — no ALC unload leaks, no assembly version hell.

### Cost / honest trade-offs

- Writing a plugin in C# means compiling it as a NativeAOT shared lib and marshalling by hand at the
  edge — more friction than "implement an interface in a referenced project." **Mitigation:** ship a
  `S.Abi.Plugin.Sdk` NuGet with the `[UnmanagedCallersOnly]` entry-point boilerplate and managed→ABI
  helper wrappers, so a C# plugin author mostly writes normal C# behind a generated shim.
- The ABI is a compatibility surface you must keep stable — and D8/D9 make v1 **large and
  platform-specific** (GPU handles + the full vtable set up front). **Mitigation:** append-only structs
  + a strict version gate + a per-platform conformance plugin (exercising every vtable) in CI from day
  one; isolate the GPU-handle fields behind the `MfpFrameKind` tag so CPU-only plugins are unaffected.

---

## Configurable layer surfaces — the cue integration (the MMD / 3D-object case)

A layer surface that draws a fixed effect needs no config; a *content* layer — "render these PMX models
playing that VMD motion" — needs per-instance config. The managed registry and the ABI
(`MfpLayerSurfaceFactoryVTable.create(config_json)`) both model it as a **factory + opaque config blob**.
Today's `AddLayerSurface(kind, Func<IVideoCompositorLayerSurface>)` gains the config parameter:

```csharp
// S.Media.Compositor — config-aware factory (the kind picks the plugin; the blob configures the instance)
ICompositorRegistryBuilder AddLayerSurface(
    string kind, Func<string /*configJson*/, IVideoCompositorLayerSurface> factory);
// CompositorRegistry.TryCreateLayerSurface(kind, configJson, out surface)
```

A `ShowDocument` composition layer carries the kind + the blob; the runtime creates the surface and
attaches it as a normal layer — the same path `ClipCompositionRuntime` already uses for the subtitle overlay:

```jsonc
{ "layer": 3, "surface": "mmd",
  "surfaceConfig": { "models": ["YYB-Miku.pmx", "stage.pmx"],
                     "motion": "rolling-girl.vmd", "camera": "cam.vmd" } }
```

Flow: `ShowSession` opens the composition → for a `surface` layer it calls
`TryCreateLayerSurface("mmd", surfaceConfig)` → the surface loads its PMX/VMD in `ConfigureGl` →
`Render(gl, fbo, masterTime, transform, opacity)` poses the skeleton at `masterTime` and draws into the
canvas FBO. `masterTime` is the cue's audio-paced playhead, so the dance stays locked to the music; the
layer composites with z-order/opacity beside the video/image layers. **A cue = "a motion + N models" is
just one configured `mmd` layer** (plus the song as the cue's audio).

Everything MMD-specific — PMX/VMD parsing, skinning, toon/sphere shading, bullet physics — is the
plugin's own GL code; the host only hands over the GL context, the master time, the placement, and the
config blob. The session and the cue model stay format-agnostic.

---

## Decision summary

| | Tier A (registration) | Tier B (native C-ABI) |
|---|---|---|
| Who | first-party + trusted .NET extensions | third parties, any language |
| Loaded | at build time (project/NuGet ref) | at runtime (`dlopen` from a trusted dir) |
| AOT | pure | host stays pure; plugin is its own native lib |
| Effort | implement an interface + tiny module | implement a C vtable (SDK eases C#) |
| Examples | FFmpeg, PortAudio, SDL3, NDI, Skia, Subtitles, X32 profile/decoder | webcam/capture-card source, exotic output, 3D layer surface, bespoke console protocol |
