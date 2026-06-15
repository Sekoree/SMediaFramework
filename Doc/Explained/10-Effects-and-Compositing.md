# 10 · Effects & Compositing

`S.Media.Effects` is "when one piece of media at a time isn't enough": layering
multiple sources, picture-in-picture, lower-thirds, text overlays, transitions, and
the **output mapping / warp mesh** that bends the composited image onto physical
panels. It builds on the Core video pipeline ([05](05-Core-Video-Pipeline.md)) and is
where the cue player's visuals come from ([11](11-Playback-Product-Tier.md)).

## The compositor contract

```csharp
public interface IVideoCompositor   // combine N sources back-to-front → one frame
```

Two implementations, swappable via `VideoCompositorBackend`:

* **`CpuVideoCompositor`** — software reference: BGRA32 layers in, BGRA32 out. Always
  available, no GPU context. Used by tests, headless servers, and as the fallback.
* **`GlVideoCompositor`** (`OpenGL/`, ~855 lines) — GL 3.3: renders each layer to an
  off-screen FBO and reads back a BGRA32 frame. `GlCompositorOutputPrecision` sets the
  composite FBO precision.

> **ELI5:** a compositor is Photoshop layers for video. Each layer is a video frame
> plus "where to put it, how big, how to blend." The compositor stacks them back-to-
> front into one picture, 60 times a second.

## From "a compositor" to "a video source": `VideoCompositorSource`

`IVideoCompositor` composites a *single* set of frames. To put a compositor *in the
graph*, `VideoCompositorSource` wraps it: each input is a `Slot` (an `IVideoOutput`
the upstream `VideoRouter` targets) and the single output is an `IVideoSource` a
downstream `VideoPlayer`/router pulls from.

```
 decoder A ─► VideoRouter ─► Slot 0 ┐
 decoder B ─► VideoRouter ─► Slot 1 ┼─► VideoCompositorSource ─► VideoPlayer ─► output
 image C   ─────────────────► Slot 2 ┘   (composites on its own tick/clock)
```

* **`Slot`** — one input: an `IVideoOutput` target + mutable composite params
  (`LayerConfig`). The host updates a slot's position/opacity/blend live.
* **`SlotKeepPolicy`** — how a slot picks which submitted frame to expose at composite
  time (`Latest`, `MasterAligned`, …). `MasterAligned` degrades source/canvas
  frame-rate mismatches gracefully when a master clock is present (it does *not* merge
  two physical clocks — see the cross-cue caveat in [06](06-Clocks-and-AV-Sync.md)).
* `VideoCompositorOptions` configures canvas size/fps/backend; `LayerHandle` is a
  stable handle to one declared layer.

## Layers: how a layer is placed and blended

A layer is "a frame + how to draw it." The placement model is layered (declarative on
top, affine underneath):

* **`LayerConfig`** (resolved per output frame) and **`LayerPosition`** (the declarative
  "where") — position, size, anchor.
* **`LayerAnchor`** — the anchor point for placement and scale origin (top-left,
  center, …).
* **`LayerTransform2D`** — the low-level 2×3 affine mapping a layer's source pixels into
  the canvas (top-left origin, Y-down). Everything resolves down to one of these.
* **`RectNormalized`** — a [0,1] UV sub-rectangle to crop the source *before*
  compositing (split-screen, trims). `Full` = whole frame.
* **`CompositorLayer`** — the fully-resolved "one layer" struct the compositor consumes
  (frame + crop + placement + blend).
* **`BlendMode`** — per-layer blend (normal/add/…); **`CompositorSamplingMode`** — the
  sampling kernel (nearest/bilinear) for software scaling.
* **`PlacementResolver`** + **`PlacementFit`** / **`LayerConfigResolver`** — turn a
  declarative placement (destination rect + fit mode + per-edge crop insets) into the
  concrete `LayerTransform2D` + source crop. The crop is expanded to absorb fit overflow
  so the visible image maps *exactly* into its destination rect and never spills onto
  neighbouring layers — this is what makes clean split-screen possible.

## Transitions & tweens

* **`Transition`** — a timed change to a layer's `LayerConfig`, evaluated each composite
  (animated moves/resizes/fades).
* **`LayerOpacityTween`** + **`LayerEasing`** — a stateless tween: feed it a timeline-
  relative elapsed time, get an opacity, apply it to the slot. Easing curves included.
* **`FadeFromBlackVideoSource`** — wraps a source so its first *N* seconds fade from
  black, then passes through untouched (gets out of the way completely after).
* **`CutVideoSource`** — plays source A until the first frame whose PTS ≥ a cut time,
  then hard-switches to source B (no blend).
* **`VideoCpuOpacity`** — CPU fade toward black (and neutral chroma for YUV) for cue/
  output opacity ramps on array-backed planes.

## Frame-source utilities

* **`StaticFrameSource`** — holds one pre-built frame and re-emits it every read
  (logo cards, image cues, stable test visuals).
* **`PixelFormatConvertingVideoSource`** — wraps a source and converts each frame to a
  fixed `PixelFormat` via the registered converter (e.g. force BGRA for the CPU
  compositor, which is BGRA-only).
* **`CompositorOutputScaler`** — a one-shot CPU compositor for scaling/letterboxing a
  *single* frame into a fixed output raster. Used by output-side wrappers (NDI format
  lock, logo template render) that receive frames via `Submit` rather than pulling.
* **`CompositorBgraHelper`** — shared BGRA blit/blend helpers used by the CPU paths.

## Output mapping & the warp mesh (the advanced bit)

"Output mapping" = taking the composited canvas and drawing pieces of it, possibly
*warped*, onto physical output(s). This is for multi-panel walls, rear projection with
panel gaps, and corner-pinning a projector onto an angled surface. The plan lives in
`Doc/HaPlay-Output-Mapping-Plan.md`; here's the engine side.

> In user terms (project memory), "tiling / tiles" = mapping **sections**. The mesh
> warp shipped 2026-06-11 (GL-only, Catmull-Rom).

* **`WarpSection`** — one section: a crop of the composited canvas + an affine placement
  into the warp-output (canvas px → output px) + opacity. With a `WarpMesh`, the affine
  is ignored for geometry — the mesh control points *are* the destination shape.
* **`WarpMesh`** — a Columns×Rows grid of control points (row-major, absolute output
  pixels) defining an **interpolating Catmull-Rom surface**. "Interpolating" means the
  surface passes through *every* control point — drag a point and the image under it
  lands exactly there. Borders use mirror extrapolation, which makes a 2×2 grid exactly
  bilinear (i.e. a corner-pin). `ClipMeshPoint` is the normalized-space persisted form.
* **`WarpMeshTessellator`** — pure math: evaluates the Catmull-Rom surface and
  tessellates it into an indexed triangle grid for the GL warp pass. CPU-side and
  allocates only on (re)build; per frame the GPU just redraws the uploaded buffers.
* **`WarpPass`** — the GL warp render pass.
* **`IWarpPassVideoCompositor`** — the optional fast-path capability: after compositing
  the layers, render the warp sections from the composited canvas straight into a
  (possibly differently sized) output, **entirely on the GPU with a single readback at
  the end**. Chaining two compositors instead would cost an extra readback + re-upload
  per frame — so a warp-capable GL backend integrates the warp into the same pass.

### How it fits together

```
 layers ──► IVideoCompositor ──► composited canvas ──► WarpSections (crop + mesh/affine)
                                       │                        │
                          (GL: same pass via IWarpPassVideoCompositor)
                                       └──────────► output raster (panels, projector)
                          CPU fallback: chained CompositorOutputScaler stage (affine only,
                          ignores the mesh — that's why warp is GL-only)
```

The product-tier `OutputMappingResolver` / `ClipOutputMappingSection` /
`ResolvedMappingSection` ([11](11-Playback-Product-Tier.md)) turn a saved mapping into
these `WarpSection`s; HaPlay's `MappingEditorViewModel` + `MappingTestPattern` are the
operator-facing editor and calibration grid ([13](13-HaPlay-UI.md)).

> **GL-only reminder:** the warp mesh needs the GL backend. The CPU chained stage
> falls back to the affine transform and ignores `ResolvedMappingSection.MeshPoints`.
> Verify GL composite/warp paths under `xvfb`, since headless tests run the CPU path.

Next: [11 · Playback (Product) Tier](11-Playback-Product-Tier.md).
