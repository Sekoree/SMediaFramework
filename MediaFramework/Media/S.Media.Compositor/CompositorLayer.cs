namespace S.Media.Compositor;


/// <summary>
/// One layer in an <see cref="IVideoCompositor"/> composite - a frame plus how to crop, position
/// and blend it onto the destination canvas.
/// </summary>
/// <param name="Frame">Pixel data. The compositor does not take ownership - caller still disposes.</param>
/// <param name="Transform">Source-to-destination affine, in destination pixels (top-left origin, Y down).</param>
/// <param name="Opacity">Per-layer multiplier in <c>[0, 1]</c> applied on top of the frame's own alpha. Values outside the range are clamped by the compositor.</param>
/// <param name="BlendMode">How this layer combines with what's already on the canvas.</param>
public readonly record struct CompositorLayer(
    VideoFrame Frame,
    LayerTransform2D Transform,
    float Opacity,
    BlendMode BlendMode)
{
    /// <summary>
    /// Normalized sub-rectangle of <see cref="Frame"/> to sample; pixels outside it are never drawn.
    /// Defaults to the whole frame so existing callers are unaffected.
    /// </summary>
    public RectNormalized SourceCrop { get; init; } = RectNormalized.Full;

    /// <summary>
    /// Optional mesh geometry for this layer. When set, the affine transform is still the CPU fallback,
    /// but warp-capable GPU compositors draw the layer through these absolute destination-pixel points.
    /// </summary>
    public WarpMesh? Mesh { get; init; }

    /// <summary>
    /// Optional ordered per-layer effect chain (chroma key, plugin effects), applied to the
    /// sampled straight-alpha color before opacity/blending. Null/empty = no effects. GPU
    /// compositors compile one cached shader variant per distinct chain; the CPU fallback runs
    /// each effect's <see cref="IVideoLayerCpuEffect"/> kernel and silently skips
    /// GPU-only effects.
    /// </summary>
    public IReadOnlyList<VideoLayerEffect>? Effects { get; init; }

    /// <summary>Convenience: identity transform, full opacity, source-over blend, no crop.</summary>
    public static CompositorLayer Default(VideoFrame frame) =>
        new(frame, LayerTransform2D.Identity, 1f, BlendMode.SourceOver);
}
