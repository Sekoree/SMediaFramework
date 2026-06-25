# 05 — Plugin Model

The hard constraint: the framework must stay **NativeAOT-compatible** (it ships `s_media_player.so`
via `S.Media.Interop`). That rules out managed reflection / `Assembly.LoadFrom` / `AssemblyLoadContext`
plugin discovery — none of it survives AOT. So the model is:

- **Tier A — Build-time modules (typed registration).** AOT-pure. First-party + trusted .NET
  extensions, referenced at build time, registered through a typed builder. Replaces today's static
  `MediaFrameworkPlugins` global.
- **Tier B — Native C-ABI plugins (dynamic).** Third-party, language-agnostic. A plugin is any shared
  library (NativeAOT C#, C, C++, Rust, Zig…) that exports a C plugin ABI. The host `dlopen`s it and
  adapts its vtables to the same managed interfaces a Tier-A module would register. The host stays AOT.

Both tiers end at the **same `IMediaRegistry`** — once registered, a native plugin's audio backend is
indistinguishable from PortAudio to the rest of the engine.

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
    IMediaRegistryBuilder AddLayerSurface(string kind, Func<IVideoCompositorLayerSurface> f);
    IMediaRegistryBuilder SetCpuConverterFactory(Func<IVideoCpuFrameConverter> f);   // swscale, etc.
    IMediaRegistryBuilder SetResamplerFactory(Func<IAudioSource,int,IAudioSource> f);
    IMediaRegistryBuilder SetDeinterlacerFactory(Func<VideoFormat,IDeinterlacer> f);
    // … one method per capability the old MediaFrameworkPlugins exposed as a static field
}

public interface IMediaRegistry   // immutable, queryable, injected
{
    IReadOnlyList<IAudioBackend> AudioBackends { get; }
    bool CanDecode(string pathOrExt);
    bool TryOpenVideo(string path, VideoSourceOpenOptions? o, out IVideoSource s);
    bool TryOpenAudio(string path, AudioSourceOpenOptions? o, out IAudioSource s);
    IVideoCpuFrameConverter? CreateCpuConverter();
    // … typed queries; null/false when no module provides the capability
}
```

### Composition root (host wires it once; nothing global)

```csharp
var registry = MediaRegistry.Build(b => b
    .Use(new FFmpegModule())        // Decode.FFmpeg + Encode.FFmpeg
    .Use(new PortAudioModule())
    .Use(new MiniAudioModule())
    .Use(new Sdl3PresenterModule())
    .Use(new AvaloniaPresenterModule())
    .Use(new NdiModule())
    .Use(new SkiaImagesModule())
    .Use(new SubtitlesModule())
    .LoadNativePlugins("./plugins"));   // Tier B (below)

await using var session = MediaSession.Create(registry, sessionOptions);
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

## Tier B — native C-ABI plugins (`S.Media.Abi`)

Symmetric to the outbound `s_media_player.h`: where `S.Media.Interop` lets other languages *drive*
the framework, `S.Media.Abi` lets other languages *extend* it. One mechanism covers every dynamic
plugin — including a .NET plugin that wants runtime loading: it compiles to a NativeAOT shared lib
exposing this same C ABI, so there's no separate managed-plugin path to maintain.

### The contract (`include/mfp_plugin.h`)

```c
#define MFP_PLUGIN_ABI_VERSION 1

typedef struct MfpPluginInfo {
    uint32_t abi_version;     // plugin sets = MFP_PLUGIN_ABI_VERSION; host rejects mismatched major
    const char* id;           // "com.acme.webcam"
    const char* display_name;
    uint32_t capabilities;    // bitset: AUDIO_BACKEND | VIDEO_SOURCE | VIDEO_OUTPUT | LAYER_SURFACE | SUBTITLE …
} MfpPluginInfo;

// Host services handed to the plugin (logging, frame-buffer pool, GL helpers, time base).
typedef struct MfpHostApi {
    uint32_t abi_version;
    void (*log)(int level, const char* msg);
    void* (*alloc_frame)(const MfpVideoFormat* fmt);   // pooled, host-owned
    void  (*release_frame)(void* frame);
    /* … append-only … */
} MfpHostApi;

// THE entry point every plugin exports. Fills info + registers capability vtables via callbacks.
int mfp_plugin_register(const MfpHostApi* host, MfpPluginInfo* out_info, MfpRegistrar* reg);
```

### Capability vtables (structs of function pointers)

Each maps 1:1 to a managed interface. Example — an audio backend (mirrors `IAudioBackend`):

```c
typedef struct MfpAudioBackendVTable {
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
  `try_read_frame(out MfpVideoFrame)`, `is_exhausted`, `seek`. Frames cross as descriptors
  (`{ void* planes[4]; int strides[4]; w; h; MfpPixelFormat; int64 pts_ticks; void* opaque }`),
  zero-copy where the buffer is host-pooled.
- **`MfpVideoOutputVTable`** ↔ `IVideoOutput`: `accepted_pixel_formats`, `configure(format)`,
  `submit(frame)`.
- **`MfpLayerSurfaceVTable`** ↔ `IVideoCompositorLayerSurface`: `configure_gl(MfpGlContext, canvas)`,
  `render(MfpGlContext, int fbo, int64 master_ticks, const MfpTransform2D*, float opacity)`,
  `destroy`. **This carries the GL context + target FBO across the boundary** so a native plugin (the
  "3D object layer") renders straight into the canvas. `MfpGlContext` exposes the proc-address getter
  + current-thread guarantee, matching `S.Media.Gpu`'s device.
- **`MfpSubtitleVTable`** ↔ `ISubtitleSource`.

### Managed adapters (`S.Media.Abi`)

For each loaded vtable, `S.Media.Abi` instantiates a small managed shim implementing the matching
framework interface and forwarding calls through the function pointers. It then registers the shim
through the **same `IMediaRegistryBuilder`** as a Tier-A module. After that, the engine cannot tell a
native plugin from a built-in one.

```csharp
// inside LoadNativePlugins("./plugins"):
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

- **Versioning:** `abi_version` on every struct; host checks major, ignores unknown trailing fields
  (append-only evolution). Reject + log on mismatch; never crash.
- **Errors:** functions return `int` status (0 ok, negative error) like `s_media_player`; never throw
  across the boundary; a thread-local last-error string is fetchable.
- **Ownership:** frames are host-pooled (`alloc_frame`/`release_frame`); the side that last holds a
  frame releases it, mirroring the managed `VideoFrame` release-callback contract.
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
  `S.Media.Plugin.Sdk` NuGet with the `[UnmanagedCallersOnly]` entry-point boilerplate and managed→ABI
  helper wrappers, so a C# plugin author mostly writes normal C# behind a generated shim.
- The ABI is a compatibility surface you must keep stable. **Mitigation:** append-only structs +
  version gate + a conformance test plugin in the tools harness.

---

## Decision summary

| | Tier A (registration) | Tier B (native C-ABI) |
|---|---|---|
| Who | first-party + trusted .NET extensions | third parties, any language |
| Loaded | at build time (project/NuGet ref) | at runtime (`dlopen` from a trusted dir) |
| AOT | pure | host stays pure; plugin is its own native lib |
| Effort | implement an interface + tiny module | implement a C vtable (SDK eases C#) |
| Examples | FFmpeg, PortAudio, SDL3, NDI, Skia, Subtitles | webcam/capture-card source, exotic output, 3D layer surface, bespoke console protocol |
