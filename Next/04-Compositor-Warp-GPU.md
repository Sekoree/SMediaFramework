# 04 — Compositor, Warp & GPU

Covers compositions/layers, the mesh-warp + splitting feature (at both composition and output level),
combining multiple outputs into one composition, the GPU layer, the plugin layer-surface seam
(the "3D object layer" idea), and subtitles. The current design here is genuinely good — the rewrite
keeps the model and fixes only the decoder coupling (P3).

## 1. The pipeline

```
   layer sources                compositor                         warp pass                outputs
  ┌──────────────┐   frames   ┌───────────────────────┐  canvas  ┌───────────────────┐   ┌──────────┐
  │ video layer  │──────────► │                       │ texture  │ section 0 (crop+   │──►│ output A │
  ├──────────────┤            │  blend back-to-front  │─────────►│   mesh/affine)     │   ├──────────┤
  │ image layer  │──────────► │  per-layer:           │          │ section 1 …        │──►│ output B │
  ├──────────────┤            │   transform/zoom      │          │ (Catmull-Rom mesh, │   ├──────────┤
  │ text/subtitle│──────────► │   opacity / blend     │          │  2×2 = corner-pin) │──►│ NDI out  │
  ├──────────────┤            │   transitions         │          └───────────────────┘   └──────────┘
  │ plugin surf. │──────────► │                       │     one composite → many output targets
  └──────────────┘            └───────────────────────┘
        (each a SourceTimeline, scheduled against the session master clock — see 03)
```

Two compositor implementations (kept): **`GlVideoCompositor`** (GPU, default — uploads each layer to
a texture, blends in a shader into an FBO) and **`CpuVideoCompositor`** (BGRA reference, runs
anywhere). The CPU one is a correctness reference and fallback only — it is **not** viable for mapped
1080p live (memory: 41–49 ms/frame); GL is required for real use.

**Working color space (D12):** the GL compositor blends in **8-bit BT.709 when every input and output
is SDR**, and switches to **linear-light RGBA16F when any HDR/wide-gamut** layer or output is present
(reconfiguring when the color-space set changes). The `S.Media.Gpu` color pieces
(`YuvColorSpace`/`RgbGamutMatrix`/HDR-transfer) feed both paths.

## 2. Layers

Salvage as-is from `S.Media.Effects`: `LayerHandle`, `LayerConfig`, `LayerTransform2D`,
`LayerPosition`/`LayerAnchor`, `LayerConfigResolver`, `LayerOpacityTween`, `BlendMode`, transitions
(`Cut`, `FadeFromBlack`, `Transition`), `VideoCompositorSource` (the slot model).

A layer is `(IVideoSource source, LayerConfig config)`. `LayerConfig` carries transform (translate/
scale/rotate via `LayerTransform2D`), anchor, opacity, blend mode, and scheduled transitions. The
`LayerHandle.AdvanceTo(masterTime, canvasFormat)` logic — bounded look-ahead, pick the frame whose
interval contains master time, re-resolve animated params while a frame is held — is good; keep it,
but have it pull its frame through its `SourceTimeline` (so live layers obey §03 rebase).

**Layer source types** (all are just `IVideoSource`s, so they compose uniformly):
- video file / live (NDI, capture) — `Decode.FFmpeg` / `NDI`
- still image — `Images.Skia`
- text — `Images.Skia` text renderer → frame source
- subtitle — `Subtitles` (§6)
- **plugin surface** — `IVideoCompositorLayerSurface` (§5)

**The P3 fix:** `LayerHandle` currently converts non-BGRA frames with the concrete
`S.Media.FFmpeg.Video.VideoCpuFrameConverter`. Replace with `IVideoCpuFrameConverter` from the
registry. The GL compositor prefers uploading YUV directly and converting in-shader (via
`S.Media.Gpu`), so the CPU converter is only a fallback — the compositor no longer needs FFmpeg at all.

## 3. Mesh warp & splitting (the core feature)

The existing types in `WarpPass.cs` are exactly right — keep them verbatim:

- **`WarpSection(SourceCrop, Transform, Opacity, Mesh?)`** — one piece of the canvas placed onto the
  warped output. `SourceCrop` is the region of the canvas this section takes; `Mesh` (when present)
  defines its destination shape.
- **`WarpMesh(columns, rows, points)`** — an interpolating **Catmull-Rom** control grid in output
  pixels. The surface passes through every control point (drag a point, the image lands there).
  A **2×2 grid is exactly bilinear = corner-pin** (homography-style keystone). Larger grids = full
  mesh warp for curved/projection surfaces.
- **"Splitting"** = multiple `WarpSection`s from one canvas → different destination regions/outputs
  (e.g. split a wide canvas across three projectors, each its own keystone).

### Two insertion points (both required by the prompt)

1. **Composition-level warp** — warp the composition's own canvas (e.g. a PiP composition pre-warped
   before it's routed). The composition owns a `WarpSection[]`.
2. **Output-level warp** — warp at the final output binding (per physical output: its own crop +
   keystone/mesh + opacity). The output map owns a `WarpSection[]` per binding.

Both use the same `WarpSection`/`WarpMesh` types and the same GPU pass — only *where the sections are
attached* differs. This matches today's HaPlay (per-binding warp sections in the output map, and warp
on compositions in the cue player).

## 4. Combining multiple outputs in one composition

`IWarpPassVideoCompositor.CompositeMulti(layers, outputs, presentationTime)` is target-domain aware:

- Composite the layers **once** into the internal canvas texture (retained on the GPU).
- For each `WarpOutputRequest(OutputFormat, Sections?, Target)`, run that output's warp pass against
  the retained canvas.
- **`GlCompositeTarget`** — the output shares the compositor's thread/context (an SDL3 window, a local
  GL surface). Render/blit straight into its FBO/texture. **Zero-copy, no readback.**
- **`ExternalImageCompositeTarget`** — the output lives in another context/API (Avalonia's render
  context, an NDI GPU encoder, a cross-API plugin). Export the warped result as an external image
  (dmabuf fd / D3D11-DXGI shared handle) + a sync semaphore; the consumer imports it — Avalonia via
  `IGlContextExternalObjectsFeature` / `ICompositionGpuInterop`. **Zero-copy across the boundary**; the
  handle type is backend-dependent (D7). Same handle currency as the D8 plugin frame ABI.
- **`CpuFrameCompositeTarget`** — the output needs CPU pixels (file dump, a CPU-only encoder/plugin, or
  the fallback when no compatible external-image type exists). Render into a readback PBO; **one async
  readback per such output**.
- `Sections == null` = full-canvas passthrough scaled to `OutputFormat`; empty list = transparent.

This is the **integrated fast path** (memory: output-mapping plan — ~0.4 ms vs ~5 ms chaining at
1080p): composite once, warp many, no readback/re-upload between composite and warp, zero readbacks
for GPU outputs, and one async readback only for each output that truly needs CPU frames. The N
outputs are placed in a `VideoPresentSyncGroup` (see [03](03-AV-Sync-Clocks-Routing.md) §5) so a
stitched wall never tears across seams.

Minimal API shape:

```csharp
public readonly record struct WarpOutputRequest(
    VideoFormat OutputFormat,
    IReadOnlyList<WarpSection>? Sections,
    ICompositeOutputTarget Target);

public interface ICompositeOutputTarget { }

public sealed class GlCompositeTarget : ICompositeOutputTarget
{
    public required GpuContext Context { get; init; }
    public required int Framebuffer { get; init; }
    public required Viewport Viewport { get; init; }
}

public sealed class ExternalImageCompositeTarget : ICompositeOutputTarget  // zero-copy cross-context/API (D7)
{
    public required string HandleType { get; init; }                       // dmabuf fd | D3D11 DXGI shared handle
    public required Action<ExternalImageHandle> OnImageReady { get; init; } // image handle + sync semaphore
}

public sealed class CpuFrameCompositeTarget : ICompositeOutputTarget
{
    public required IVideoFramePool Pool { get; init; }
    public Action<VideoFrame>? OnFrameReady { get; init; }
}
```

So: *"one composition layout driving several physical outputs, each warped/keystoned to its surface,
all phase-locked"* = `CompositeMulti` output targets + sync group. *"Several inputs combined on one
canvas"* = layers (§2). The two compose.

## 5. GPU (`S.Media.Gpu`) and the plugin layer-surface seam

`S.Media.Gpu` is the only project that touches GL (salvaged from `S.Media.OpenGL`): the device host,
shared program cache, YUV→RGB shaders, hardware uploads (dmabuf NV12/P010/P016 on Linux, D3D11
keyed-mutex/NV-DX interop on Windows), HDR transfer, viewport fit. The compositor and both presenters use this code, each in whatever GL context it's given
(SDL3's shared context; Avalonia's own via `OpenGlControlBase`). Cross-context/API handoff uses exported
external images + semaphores (D7), not a shared context.

**`IVideoCompositorLayerSurface`** — the seam for custom, GPU-native layer types. A layer surface is
given the **shared GL context** and the canvas's target (FBO + viewport + the layer's resolved
transform) and renders directly into it each `AdvanceTo`. This is how a third party adds, e.g., a
**3D object layer that plays animations** (your stated future plugin) without the compositor knowing
anything about 3D:

```csharp
public interface IVideoCompositorLayerSurface : IDisposable
{
    // Called on the GL/compositor thread with the context current.
    void ConfigureGl(GpuContext ctx, VideoFormat canvas);
    // Render this layer for masterTime into the bound FBO under `transform`. Return false to skip.
    bool Render(GpuContext ctx, TimeSpan masterTime, in LayerTransform2D transform, float opacity);
}
```

A native (C-ABI) plugin provides this same capability across the boundary through `S.Abi`: the host
passes the GL context handle + FBO id + a transform struct; the plugin renders with its own
GL/Vulkan-on-GL code. See [05](05-Plugin-Model.md) §"layer-surface vtable". (CPU-only plugin layers
just implement `IVideoSource` instead and need no GL.)

## 6. Subtitles (new — `S.Media.Subtitles`)

Subtitles are "just another layer" produced by a timed source aligned to the session master clock,
and selectable like audio tracks (none / one / many, including external sidecar files).

| Format | Path |
|---|---|
| SRT / WebVTT / mov_text (timed text) | parse → render styled text via `Images.Skia` → BGRA layer frames |
| ASS / SSA (full styling, positioning, karaoke) | **libass** (native) → BGRA bitmaps → layer frames |
| PGS / DVB / VobSub (bitmap subs) | FFmpeg subtitle capability registered by `Decode.FFmpeg` → bitmaps → image layer |

Design:
- `ISubtitleSource` produces `(TimeSpan start, TimeSpan end, VideoFrame bitmap)` events; a thin
  `SubtitleLayerSource : IVideoSource` turns the active event into a layer frame at master time
  (transparent when no active cue).
- Track discovery comes from registry subtitle capabilities (`Decode.FFmpeg` for embedded subs,
  sidecar providers for external files); selection lives in the player/session open options next to
  audio-track selection ([03](03-AV-Sync-Clocks-Routing.md) §6).
- Positioning/styling honored: libass for ASS; for text formats, a style config (font, size, outline,
  safe-area margins) on the subtitle layer.
- Because it's a normal layer, subtitles inherit transform/warp — they end up correctly placed even on
  a warped/keystoned output.

## 7. What stays out of the compositor

- No decoding (P3 fixed): the compositor takes `IVideoSource`s and a registry-provided CPU converter;
  it never references FFmpeg.
- No presentation: it emits `VideoFrame`s; the router/presenters display them.
- No clock ownership: it advances layers to a master time handed in by the session.
