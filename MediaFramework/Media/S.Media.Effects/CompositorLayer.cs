namespace S.Media.Effects;

/// <summary>
/// One layer in an <see cref="IVideoCompositor"/> composite — a frame plus how to position and
/// blend it onto the destination canvas.
/// </summary>
/// <param name="Frame">Pixel data. The compositor does not take ownership — caller still disposes.</param>
/// <param name="Transform">Source-to-destination affine, in destination pixels (top-left origin, Y down).</param>
/// <param name="Opacity">Per-layer multiplier in <c>[0, 1]</c> applied on top of the frame's own alpha. Values outside the range are clamped by the compositor.</param>
/// <param name="BlendMode">How this layer combines with what's already on the canvas.</param>
public readonly record struct CompositorLayer(
    VideoFrame Frame,
    LayerTransform2D Transform,
    float Opacity,
    BlendMode BlendMode)
{
    /// <summary>Convenience: identity transform, full opacity, source-over blend.</summary>
    public static CompositorLayer Default(VideoFrame frame) =>
        new(frame, LayerTransform2D.Identity, 1f, BlendMode.SourceOver);
}
