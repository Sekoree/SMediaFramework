# HaPlay Output Mapping (Composition Slices / Warp Sections) — Planning Doc

Status: **Phases 1, 2 and 4 implemented** (2026-06-11) · Scope: HaPlay cue player + S.Media.Playback/S.Media.Effects
Author: planning session 2026-06-11

Implemented: model (`CueOutputMapping`/`CueOutputMappingSection` incl. per-section Brightness, on
`CueVideoOutputBinding`), framework spec + `OutputMappingResolver` (S.Media.Playback/ClipOutputMapping.cs),
per-lease chained mapping compositor in `ClipCompositionRuntime` pump with live
`UpdateOutputMapping`, HaPlay wiring (binding→lease, `CuePlaybackEngine.UpdateCompositionOutputMapping`,
calibration grid via `SetCompositionTestPattern` + `MappingTestPattern`), and the mapping editor
dialog (section list, numeric fields, drag-to-move dest preview, N×M splitter, grid toggle) opened
per binding from `CueOutputSetupDialog`. Open questions resolved: media-player path deferred;
size mismatch at the physical output letterboxes (`SDL3GLVideoOutput` `VideoViewportFit.Contain`,
bars painted black); per-section brightness shipped in Phase 1 (folded into layer opacity — exact
over the black background).

## 1. Goal

Let an operator cut a composition's output into N independently transformable **sections**
(slices) per physical output, so one rendered canvas can be geometrically adapted to imperfect
real-world surfaces.

Motivating example: rear projection onto a screen built from 3 individual panels. Each panel
warps slightly differently, and the panel frames create physical gaps. The operator wants to:

- cut the 1920×1080 canvas into three 640×1080 slices,
- nudge/scale/rotate each slice independently so it lands exactly on its panel,
- leave black gaps between the slices in projector space so content doesn't fall on the frames.

```
   Composition canvas (1920×1080)            Projector output (1920×1080)
   ┌─────────┬─────────┬─────────┐           ┌────────┐ ┌────────┐ ┌────────┐
   │ slice A │ slice B │ slice C │   ──►     │ A      │ │  B ↻1° │ │ C      │
   │ 0..640  │640..1280│1280..   │           │ x=12   │ │ x=660  │ │ x=1308 │
   └─────────┴─────────┴─────────┘           └────────┘ └────────┘ └────────┘
                                               ▲ gaps (panel frames) stay black
```

This is the slice/warp model of Resolume Advanced Output / MadMapper, scoped down: **Phase 1 is
affine sections** (move/scale/rotate/crop — covers the panel case), corner-pin keystone and mesh
warp are later phases.

## 2. Current pipeline (what exists today)

- `CueComposition` (HaPlay model): virtual canvas (W×H@fps). `CueVideoOutputBinding` pairs a
  composition with an output line; several bindings may share one composition (fan-out).
- `CueCompositionRuntime` (HaPlay) wraps `ClipCompositionRuntime` (S.Media.Playback): cue video
  routes land in compositor **slots** (latest-frame mailboxes), a pump thread composites at canvas
  rate via `IVideoCompositor.Composite(layersBackToFront) → VideoFrame`, and the composited CPU
  frame fans out to every **output lease** (local video window, NDI sender) via zero-copy views /
  CPU clones (`ClipCompositionRuntime` pump, ~line 340).
- Compositor backends: `SDL3GLVideoCompositor` (GPU; accepts all YUV natively) and
  `CpuVideoCompositor` (BGRA32, scalar). Layers already carry `SourceCrop` (normalized rect) and
  `LayerTransform2D` (**2×3 affine**, CPU inverse-sampling + GL vertex uniform) and `Opacity`.
- Live edits precedent: `CuePlaybackEngine.UpdateActiveCueVideoPlacementAsync` mutates running
  slots from the UI thread — the same pattern the mapping editor needs.

**Key insight:** a mapping section is exactly a `CompositorLayer` — source = the composited canvas
frame, `SourceCrop` = the slice rect, `Transform` = the slice's placement in output space. The
mapping stage can therefore be a *second compositor pass* reusing all existing machinery, on both
backends, with no new sampling code in Phase 1.

## 3. Data model

New records in `HaPlay.Models` (JSON-persisted with the cue list, like everything else in
`CueList.cs`). The mapping hangs off the **binding** (composition→output pair), because the warp
is a property of the physical surface behind that output — two projectors fed by the same
composition need different maps, and an NDI send may want none.

```csharp
public sealed record CueOutputMapping
{
    /// <summary>Sections drawn back-to-front onto the output. Empty/null = identity (no mapping
    /// stage at all — zero cost, and the wire format stays backward compatible).</summary>
    public List<CueOutputMappingSection> Sections { get; init; } = new();

    /// <summary>Output canvas size; defaults to the composition size. Lets a 1920×1080 composition
    /// map onto e.g. a 2560×800 edge-blend canvas later.</summary>
    public int? OutputWidth { get; init; }
    public int? OutputHeight { get; init; }
}

public sealed record CueOutputMappingSection
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";          // "Panel left", …
    public bool Enabled { get; init; } = true;

    // Source slice, normalized canvas coordinates (matches SourceCrop semantics).
    public double SrcX { get; init; }                 // 0..1
    public double SrcY { get; init; }
    public double SrcWidth { get; init; } = 1.0;
    public double SrcHeight { get; init; } = 1.0;

    // Destination placement, output pixels. Phase 1: affine (position/scale/rotation).
    public double DestX { get; init; }
    public double DestY { get; init; }
    public double DestWidth { get; init; }            // 0 = natural slice size
    public double DestHeight { get; init; }
    public double RotationDegrees { get; init; }      // around the dest rect center

    public double Opacity { get; init; } = 1.0;       // per-section dimming (panel brightness matching)

    // Phase 3 (reserved, null in Phase 1): corner-pin quad in output pixels. When present it
    // overrides the affine fields. Order: TL, TR, BR, BL.
    public List<CuePoint>? Corners { get; init; }
}

public sealed record CuePoint(double X, double Y);
```

`CueVideoOutputBinding` gains `public CueOutputMapping? Mapping { get; init; }` — additive and
optional, so existing project files load unchanged (null → no mapping stage, identical behavior
and performance to today).

Invariants / notes:

- Sections may overlap in source (same slice shown twice) and in destination (later wins via
  back-to-front draw with opacity).
- Unmapped output area is black — that is the "gap" behavior the panel case needs, for free.
- Default new mapping = one full-canvas section, so the editor starts from identity.

## 4. Rendering design

### Phase 1 — chained mapping compositor (works on both backends today)

Add an optional **mapping stage** per acquired output inside `ClipCompositionRuntime`:

```
slots → Composite() → canvas frame ──► output A (unmapped: today's zero-copy fan-out path)
                                   └─► mapping stage B ──► mapped frame ──► output B
                                        (own IVideoCompositor: N section layers,
                                         source = canvas frame, SourceCrop+Transform per section)
```

- Framework: `ClipCompositionOutputLease` gains an optional `OutputMappingSpec` (neutral record
  mirroring §3, no HaPlay types). The pump, for leases with a mapping, builds
  `CompositorLayer[]` from the sections (the *same* canvas frame as every layer's source — the
  fan-out countdown-release machinery already supports multiple readers) and calls a per-lease
  mapping compositor's `Composite()`; the mapped frame is what gets submitted to that lease.
- The mapping compositor is created with the same backend factory as the canvas compositor
  (GL when available). Output format = mapping output size (§3), BGRA32 like today.
- Transform math: `LayerTransform2D.Compose(Translate(destCenter), Compose(Rotate(θ),
  Compose(Scale(sx, sy), Translate(-sliceCenter))))` — all existing factories; unit-testable pure
  math in one helper (`OutputMappingResolver`, mirroring `PlacementResolver`).
- HaPlay: `CueCompositionRuntime.BuildOutputLeases` already knows the binding per target line —
  it passes the binding's mapping through to the lease. `CuePlaybackEngine` needs no routing
  changes.
- Live edit: `CueCompositionRuntime.UpdateOutputMapping(outputLineId, mapping)` → inner runtime
  swaps the lease's section snapshot under the pump lock (same idempotent-reconfigure spirit as
  slot placement updates). The editor calls this on every drag tick; cheap (it's just replacing
  an array of transforms).

Cost: for mapped outputs the GL backend pays one extra texture upload + draw + readback per frame
(the canvas frame is CPU-backed after the first readback). At 1080p60 this is well inside budget;
**measure first** (CompositorSmoke tool, §8) before optimizing. CPU backend: one extra
inverse-mapped resample per mapped output — fine for 2–3 sections at 1080p30, document as "GL
recommended" for heavier setups.

### Phase 2 — integrated GL second pass (IMPLEMENTED 2026-06-11)

Measurements demanded it (1080p, Release, desktop GL): CPU compositor 41 ms canvas + 49 ms mapping
(non-viable for live use — GL backend required for mapped outputs); GL chained 2.6 ms canvas +
4.8 ms mapping vs **3.0 ms total integrated**. Implementation:

- `IWarpPassVideoCompositor` + `WarpSection` (S.Media.Effects/WarpPass.cs): optional capability —
  `SetWarpPass(outputFormat, sections)` is a thread-safe snapshot swap.
- `GlVideoCompositor`: after the layer pass, draws the sections from the canvas FBO texture into a
  second warp FBO (same shader/draw core, Linear sampling, flipV like a direct BGRA32 upload) and
  reads back once at warp size. `SDL3GLVideoCompositor` forwards (buffering until its lazy inner
  init on the pump thread).
- `ClipCompositionRuntime.ReevaluateIntegratedWarp`: with exactly ONE output lease and a mapping,
  the warp routes through the canvas compositor and the chained per-lease stage is skipped (the
  mixer frame is already warped). Multi-output compositions keep the chained stages — each output
  may need a different (or no) warp from the same canvas. Pixel correctness verified by a
  slice-swap test against the real GL stack.

### Phase 3 — corner-pin / perspective (keystone)

- Extend the section model's `Corners` (already reserved in §3).
- Needs a homography (3×3) per section: GL — perspective-correct quad (pass q-coordinates or a
  3×3 matrix uniform; subdivision not needed); CPU — inverse-homography sampling (same structure
  as today's affine inverse sampling, ~15 lines of math).
- Introduce `LayerTransformPerspective` alongside `LayerTransform2D` rather than widening the
  affine struct (every existing call site stays untouched); `CompositorLayer` gets an optional
  perspective override the backends check.

### Phase 4 — Mesh warp per section (IMPLEMENTED 2026-06-11)

For projection onto non-flat surfaces (curved screens, draped cloth, uneven walls): the operator
bends a section by dragging control points until the projected image reads as flat.

Model (additive on `CueOutputMappingSection`, mirrored on `ClipOutputMappingSection`):

```csharp
public int MeshColumns { get; init; }            // 0 = no mesh (affine as today), else >= 2
public int MeshRows { get; init; }
public List<CuePoint>? MeshPoints { get; init; } // row-major, normalized DEST-RECT space
```

Two deliberate deviations from the original sketch:

- **Interpolating Catmull-Rom surface, not a Bézier patch** (`WarpMeshTessellator`): the surface
  passes *through* every control point — drag a point and the image under it lands exactly there,
  which is the calibration contract an operator expects. Borders use mirror-extrapolated virtual
  points, which cancels the cubic terms on single-segment axes: a 2×2 mesh is exactly bilinear
  corner pin. Visually as smooth as Bézier; UX strictly better.
- **Control points are stored normalized to the section's dest rect** (not output pixels), so
  moving/scaling/rotating the section carries its warp along; `OutputMappingResolver` bakes them
  to absolute output pixels (Catmull-Rom commutes with affine maps, so transforming control
  points equals transforming the surface). An identity grid resolves to no mesh — enabling the
  checkbox without dragging stays on the zero-cost affine path.

Rendering (GL, `GlVideoCompositor`): on warp-snapshot change (not per frame) the mesh is
tessellated CPU-side (8 sub-segments per cell, capped 256/axis) into a per-section VBO/EBO of
interleaved `(s, t, x, y)`; `composite_mesh.vert.glsl` reads `gl_Position` from the position
attribute (uXform reduces to pixels→NDC) and the fragment shader is shared with the layer pass.
Per-frame cost stays one (indexed) draw per section. The **chained multi-output stage** now also
routes through the warp pass on warp-capable compositors (one identity canvas layer + warp pass —
the same pixel path as the integrated single-output warp), so mesh works on every GL output;
the CPU backend renders mesh sections with their affine placement and logs a one-time warning
(GL was already mandatory for mapped outputs per the Phase 2 measurements).

Editor: per-section "Mesh warp" checkbox + Cols/Rows (2–16) + "Reset mesh"; the selected
section's warped grid is drawn as polylines (sampled from the same Catmull-Rom surface the GL
pass tessellates) with one drag handle per control point. Handle drags live-apply through
`UpdateOutputMapping` per tick; grid resize restarts from identity (no resampling in v1).
The mesh is kept in the VM while the checkbox is off (session-non-destructive) but only an
enabled mesh persists to the project file.

Verified on the real GL stack via `CompositorSmoke --pattern` (see §8): affine slice-swap,
2×2 corner pin (pinned corners exact, out-of-quad area transparent black), and a 4×4 wave
(smooth, watertight tessellation). Unit coverage: tessellator interpolation/bilinearity/caps,
resolver baking (rotation, identity-drop, malformed-grid fallback), runtime pass-through,
model JSON round-trip, editor VM behaviors.

Relationship to Phase 3: a 2×2 mesh equals corner pin with *bilinear* interpolation, not true
perspective — fine for calibration-by-eye, subtly wrong for content with straight lines under
strong keystone. Phase 3's homography stays the answer for exact keystone; in practice a 4×4
mesh covers most projector cases.

### Out of scope / future ideas (named so they don't creep in)

Edge blending (overlap + gamma feathering), per-section masks, black-level compensation,
per-output color correction. The section model is deliberately shaped so these attach later
without remodeling (they're all per-section or per-mapping additions).

## 5. UI / UX

### Where it lives

- `CueOutputSetupDialog` (composition/output bindings) gains a **"Mapping…"** button per binding
  row → opens the new mapping editor dialog for that binding.

### Mapping editor dialog (Phase 1)

- Left: section list (add / duplicate / remove / reorder / enable / name), numeric fields for the
  selected section (src rect, dest pos/size, rotation, opacity). Duplicate-then-nudge is the
  fast calibration workflow.
- Right: canvas preview — the composition area with each section's source rect drawn, and an
  output preview with each section's dest rect; drag to move, corner handles to resize, modifier
  +drag to rotate. (Avalonia canvas with thumbs; no live video needed in the preview — colored
  outlines + section names are enough and far simpler.)
- **Test pattern**: a "Show calibration grid" toggle that pushes a generated labeled grid frame
  through the composition (`HeldFrameVideoSource` + `TextFrameRenderer` precedent exists) so the
  operator aligns sections against the physical panels with live output. Editing while a cue
  runs also works (live-update path, §4) — the grid is for the no-content case.
- Splitter helper: "Split into N columns / M rows" button that replaces sections with an evenly
  cut grid — the 3-panel case becomes two clicks plus nudging.

### Phase 2+ UX

Edit-on-output overlay (drag handles drawn on the actual projector window), keyboard nudge with
configurable step, snap-to-edges. Persisted per-binding so recalibration survives restarts by
definition (it's in the project file).

## 6. Persistence & migration

- Additive optional `Mapping` on `CueVideoOutputBinding` — old files load (null), new files with
  mappings are ignored-but-preserved by older builds only if they round-trip unknown fields
  (System.Text.Json does **not** by default → note: bumping a `CueList.Version` field or simply
  accepting that downgrade drops mappings; decide at implementation — recommendation: accept the
  drop, document it).
- Export/import sections (`ExportProjectSections`) should include mappings with their cue lists
  automatically since they live inside the binding records.

## 7. Phasing & rough effort

| Phase | Contents | Effort (rough) | Status |
|---|---|---|---|
| 1a | Model records + `OutputMappingResolver` math + unit tests | S | ✔ 2026-06-11 |
| 1b | Framework: lease mapping spec + chained mapping compositor in pump + live-update API | M | ✔ 2026-06-11 |
| 1c | HaPlay wiring: binding → lease, `UpdateOutputMapping`, preview-session parity | S | ✔ 2026-06-11 |
| 1d | Mapping editor dialog (numeric + drag preview) + calibration grid + splitter helper | M–L | ✔ 2026-06-11 |
| 2 | Integrated GL second pass (only if measurements demand) | M | ✔ 2026-06-11 |
| 3 | Corner-pin homography (model already reserved) | M | open |
| 4 | Mesh warp: model + GL tessellated VBO + control-point editor (§4 Phase 4) | M–L | ✔ 2026-06-11 |

Phase 1 alone fully covers the motivating 3-panel use case.

## 8. Testing

- Unit (S.Media.Core.Tests / S.Media.Playback.Tests / HaPlay.Tests): resolver math (src slice →
  transform, rotation about center, identity round-trip), mesh tessellator (interpolation,
  bilinear 2×2, caps), mesh resolver baking, model serialization round-trip, null-mapping
  behavior unchanged, editor VM mesh behaviors.
- `CompositorSmoke` mapping mode (landed 2026-06-11): `--pattern WxH` renders a synthetic
  quadrant pattern through `--mapping <json>` / `--mapping-json '<json>'` on the real GL stack;
  `--probe x,y` prints result pixels for scripted assertions. Run under `xvfb-run -a` headless.
  Verified: affine slice-swap, 2×2 corner pin, 4×4 wave (all pixel-exact / visually smooth).
- Manual: 3-section calibration against a real projector; live-edit while a cue plays; NDI output
  receives mapped (baked) pixels.

## 9. Open questions (decide at implementation)

1. Should the **media-player path** (HaPlayPlaybackSession video outputs, outside compositions)
   get mapping too? The lease-level design would allow attaching the same stage to any
   `IVideoOutput`, but v1 scopes to composition outputs only (the user-facing ask).
2. Mapping output size vs output line's native size — warn or letterbox on mismatch?
3. Per-section opacity is in the model; is per-section *brightness/gamma* (panel matching) worth
   adding in Phase 1 while the editor is being built anyway?
