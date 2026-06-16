# HaPlay Composition Pump Performance Plan

Status: **IMPLEMENTED — primary §4 path landed 2026-06-16.** Created 2026-06-16 after the composition pump
was measured over budget driving a stacked multi-NDI wall.

## 1. Problem & measurement

From a real session (`logs/haplay-20260616-073707.log`): a 1920×2160 @ 60 fps composition fanned to **two
NDI outputs** ran the composition pump consistently **over budget** — `pump over budget (25–30ms > 16.67ms,
layers=1, slotOverflow=1)`. The source media was **~24 fps** (94944/3955). So the pump misses its 60 fps
deadline and drops/judders frames.

## 2. What we already tried, and why it was the wrong lever (dedup post-mortem)

We added opt-in **dedup** to `VideoCompositorSource`: skip the composite (and the whole pump tick) when the
inputs were unchanged since the last emit. It reduced GPU work, **but made the visible result worse** and was
reverted.

Why it backfired: the canvas pump ticks on the **60 fps grid** (16.67 ms). A 24 fps source changes every
~41.7 ms, which does not align to the 60 fps grid, so the surviving (non-skipped) emissions landed at
irregular wall-clock spacing (~50 / 33 / 50 / 33 ms). The NDI sender runs `clock_video = true` (paces to its
declared 60 fps), so it previously received a **steady 60 fps stream with duplicated frames** — smooth. Dedup
replaced that with an **uneven ~24 fps subset → judder**, which reads as more dropped frames even though less
GPU work was done.

**Lesson:** for a clock-paced real-time output (NDI), the fix must make the full target rate **cheaper**, not
reduce/irregularize the emission rate. Dedup is only safe for a consumer that displays at its own pace (e.g. a
local SDL window) — not for a genlocked/clocked egress.

## 3. Root cause of the cost: redundant GPU↔CPU roundtrips in the multi-output path

`GlVideoCompositor` already keeps **single-output** mapping fully on the GPU (the integrated warp pass —
`ReevaluateIntegratedWarp`): one composite + one `glReadPixels`. Good.

**Multi-output falls back to per-lease chained stages** (`ClipCompositionRuntime.OutputMappingStage`), and
each `SDL3GLVideoCompositor` owns its **own GL context** (`SDL.GLCreateContext`), so the canvas texture cannot
be shared between them. The per-frame pipeline for 2 NDI outputs is therefore:

1. **Canvas composite** (context A) → `glReadPixels` → CPU canvas frame (~16.6 MB at 2160p).
2. **Output 1 stage** (context B): upload CPU canvas → GPU, warp, `glReadPixels` → CPU frame (~8.3 MB at 1080).
3. **Output 2 stage** (context C): upload CPU canvas → GPU, warp, `glReadPixels` → CPU frame (~8.3 MB).

That's **3 readbacks + 2 redundant canvas re-uploads + 3 GL contexts** per frame. The canvas makes a full
GPU→CPU→GPU round trip purely to be re-uploaded into the output stages. `glReadPixels` is a **synchronous**
GPU→CPU stall, and the redundant canvas readback + re-uploads are ≈ 50 MB/frame of pure-waste PCIe traffic
(~3 GB/s at 60 fps).

## 4. Primary rework — multi-output integrated GPU warp (single context, GPU-resident canvas)

**Goal:** one compositor / one GL context composites the canvas into a **GPU-resident texture**, then runs
each output's warp pass sampling that texture, reading back **only the final per-output frames**. Removes the
canvas readback + N re-uploads and collapses N+1 contexts to 1.

### 4.1 API
Extend the warp-capable compositor (`IWarpPassVideoCompositor` in `GlVideoCompositor`) to emit multiple warp
outputs from one composited canvas, e.g.:

```
// Composite layers into the internal canvas texture (no readback), then for each requested output
// run its warp sections against that texture and read back one CPU frame. Returns N frames.
IReadOnlyList<VideoFrame> CompositeMulti(
    IReadOnlyList<CompositorLayer> layers,
    IReadOnlyList<WarpOutputRequest> outputs,   // (outputFormat, WarpSection[]) per output
    TimeSpan presentationTime);
```

`WarpOutputRequest` carries the per-output format + sections (and `null` sections = full-canvas passthrough at
that output size). Internally: composite once into the canvas FBO/texture; loop outputs, binding each output
FBO, running `RunWarpPass` from the **retained canvas texture**, then one `glReadPixels` per output.

### 4.2 Wiring (`ClipCompositionRuntime`)
- New path used when: backend is warp-capable (GL), there are at least two outputs, and at least one output is
  mapped. Single-output keeps the existing integrated warp; **multi-output mapping now uses `CompositeMulti`**
  on the **canvas compositor's** context instead of per-lease stages.
- The per-lease `OutputMappingStage` chained path is **retained as the CPU-compositor fallback** (and any case
  where a warp-capable single context can't serve every output).
- `UpdateOutputMapping` updates the relevant output's `WarpSection[]` (no context churn) — same live-edit
  contract as today.
- Frame fan-out/lifetime: `CompositeMulti` returns one owned CPU frame per output; the pump submits each to
  its lease and disposes. No shared-canvas fan-out views needed (each output is already its own frame).

### 4.3 Expected gain
Per frame (2 × 1080 outputs over a 2160 canvas): eliminates 1× 16.6 MB readback + 2× 16.6 MB re-uploads
(~50 MB/frame, ~3 GB/s at 60 fps) and 2 context switches. The two output readbacks remain (NDI needs CPU
pixels). This is the change most likely to bring the pump back under budget at the real frame rate.

## 5. Follow-on — asynchronous readback (PBO)

`glReadPixels` blocks the GL thread until the GPU finishes the frame. Switch the per-output readback to
**Pixel Buffer Objects**: issue the readback into a PBO and map it on the **next** frame (1-frame latency),
so the GPU→CPU copy overlaps the next composite instead of stalling. Do this only **after** §4 lands and only
if still over budget; it adds one frame of egress latency, which is usually acceptable for NDI.

Status: implemented for `GlVideoCompositor.CompositeMulti` only. The single-output `Composite` path remains
synchronous to avoid adding latency outside the multi-NDI/mapped-output path. The first call after a shape
change returns the current frame synchronously while priming PBOs; subsequent calls issue current readbacks
and return the previous completed PBO using the current tick's presentation time. PBO-specific failures disable
the async path for that compositor instance and re-render through the existing synchronous multi-output path.

## 6. Secondary / cheap items

1. **Carrier idle allocation** — `NDIOutputPreviewRuntime.OnVideoTick` allocates a fresh ~8 MB black BGRA
   frame every idle tick. Cache one reusable black frame (mirror `CloneLogoFrame`, which already shares the
   template's planes). **Idle-only — does not affect the playback frame drops**, but trims steady-state GC.
   Status: implemented. The carrier now caches one black template frame, clones timestamp-only views for
   idle ticks, and disposes the template on reconfigure/dispose.
2. **`clock_video` double-pacing** — the carrier sets `clock_video = true` *and* drives its own 30 fps timer,
   so the idle stream is paced twice. Evaluate pacing via exactly one mechanism. Low priority / behavioral.
   Status: evaluated/no change. The carrier does use `clockVideo:true`, but it passes
   `minimumVideoSubmitSpacing:null`, so `NDIVideoSender`'s host wall-clock throttle is not active there.
   Changing SDK clocking would be behavioral rather than a clear performance fix.
3. **Canvas-rate matching (the *correct* version of dedup)** — for a composition whose canvas rate exceeds its
   (single) source rate, drive the pump at a rate matched to the source so emissions are **steady** at the
   source rate, and declare the NDI output at that rate so `clock_video` paces correctly. This avoids the
   judder dedup caused, but is fiddly with multi-source compositions (must pick a common rate) and is a weaker
   general fix than §4. Defer.

## 7. Sequencing

1. **§4 multi-output integrated GPU warp** — the big win. Measure against the 2160p60 × 2-NDI repro.
2. **§5 PBO async readback** — implemented for `CompositeMulti`.
3. **§6.1 / §6.2 cleanups** — §6.1 implemented; §6.2 evaluated/no change.
4. **§6.3 canvas-rate matching** — optional, only if a use case needs it.

## 8. Validation

- Re-run the 1920×2160 @ 60 fps → 2× 1080 NDI repro with `--media-log-level debug`; confirm the
  `pump over budget` warnings disappear (or drop sharply) **and** the NDI monitor shows smooth motion (no
  judder, no drop counter climbing).
- Existing tests must stay green: framework `S.Media.Playback.Tests` (composition pixel-capture harness),
  `S.Media.Core.Tests` (`VideoCompositorSourceTests`), and the single-output + CPU-fallback paths.
- Verify mapping live-edit (`UpdateOutputMapping`) still applies without recreating the context.

## 9. Risks

- **GL context / threading** is the highest risk: texture/FBO lifetime, state hygiene, and the rule that all
  GL work + disposal run on the compositor's owner thread. The single-context multi-output path must preserve
  the discipline already in `GlVideoCompositor` (save/restore FBO + pixel-store state).
- **Driver variance** for PBO/readback. The implementation contains this by falling back to synchronous
  multi-output readback for the compositor instance if a PBO fence/map/unmap step fails.
- **Keep the CPU chained fallback** intact and correct for the CPU compositor and any non-warp-capable case.

## 10. Implementation notes (2026-06-16)

- The plan's main direction was sound, but the implementation needed one extra contract: `VideoCompositorSource`
  now has a multi-frame read path so `ClipCompositionRuntime` can snapshot/acquire layer frames once and call
  the compositor's multi-output path without first forcing a canvas `glReadPixels`.
- `IWarpPassVideoCompositor.CompositeMulti` and `WarpOutputRequest` are the new capability boundary. `null`
  sections mean full-canvas passthrough; an empty section list means a mapped output with no enabled sections.
- `GlVideoCompositor.CompositeMulti` composites the canvas once, then emits output frames in request order from
  the retained canvas texture. `SDL3GLVideoCompositor` forwards the capability through HaPlay's hidden SDL GL
  context.
- `ClipCompositionRuntime` uses the new path only when the canvas compositor is warp-capable, there are at least
  two outputs, and at least one output is mapped. Pure raw fan-out and CPU/non-warp backends keep the existing
  fallback path.
- Follow-up pass: `GlVideoCompositor.CompositeMulti` now uses double-buffered PBO readbacks after the first
  synchronous priming frame. The compositor saves/restores `GL_PIXEL_PACK_BUFFER` and binds pack buffer 0 for
  synchronous reads so host GL state does not leak across embedded render paths.
- Follow-up pass: `NDIOutputPreviewRuntime` now reuses a cached black idle frame template, matching the existing
  logo-template view pattern without per-tick BGRA allocation.
- Validation run: full solution build, `S.Media.Playback.Tests`, `VideoCompositorSourceTests`,
  `S.Media.OpenGL.Tests`, and targeted HaPlay cue/output-mapping tests. The full HaPlay test project reported
  all tests passed before the host aborted on ALSA device probing in this environment.
- Follow-up validation run: full solution build, `S.Media.Playback.Tests`, `VideoCompositorSourceTests`,
  `S.Media.OpenGL.Tests`, and targeted HaPlay composition/output-mapping tests all passed.
