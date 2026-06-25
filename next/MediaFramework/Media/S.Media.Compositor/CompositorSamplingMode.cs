namespace S.Media.Compositor;

/// <summary>
/// Per-layer source-sampling kernel for <see cref="CpuVideoCompositor"/> (and any other
/// software compositor that opts in to the same enum).
/// </summary>
public enum CompositorSamplingMode
{
    /// <summary>
    /// Pick the single source pixel whose center is closest to the inverse-mapped destination
    /// pixel center. Cheapest; preserves hard edges and pixel art; visible stair-stepping under
    /// scale or rotation. Default to preserve back-compat for callers that previously relied on
    /// the implicit nearest-neighbor behaviour.
    /// </summary>
    Nearest = 0,

    /// <summary>
    /// Sample the four nearest source pixels and blend them by fractional sub-pixel weight.
    /// Smooths edges under scale / rotation at ~4× the per-pixel cost of nearest. Out-of-bounds
    /// neighbors are clamped to the source edge so the layer's footprint stays sharp.
    /// </summary>
    Bilinear = 1,

    /// <summary>
    /// 4×4 Catmull-Rom bicubic kernel. Sharper edges than <see cref="Bilinear"/> for image / photo
    /// upscaling at ~4× the per-pixel cost of bilinear; can mildly overshoot at very high contrast
    /// transitions (clamped to <c>[0, 255]</c> per channel before write). Out-of-bounds samples
    /// in the 4×4 footprint clamp to the source edge.
    /// </summary>
    Bicubic = 2,
}
