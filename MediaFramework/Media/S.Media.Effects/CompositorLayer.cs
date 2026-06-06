namespace S.Media.Effects;

/// <summary>
/// A normalized sub-rectangle of a layer's source frame, in [0,1] UV coordinates (top-left
/// origin, Y down). Used to crop/trim a source before compositing. <see cref="Full"/> is the
/// whole frame.
/// </summary>
public readonly record struct RectNormalized(float X0, float Y0, float X1, float Y1)
{
    /// <summary>The entire source frame — no crop.</summary>
    public static RectNormalized Full { get; } = new(0f, 0f, 1f, 1f);

    public float Width => X1 - X0;

    public float Height => Y1 - Y0;

    /// <summary>True when this covers (at least) the whole [0,1] frame.</summary>
    public bool IsFull => X0 <= 0f && Y0 <= 0f && X1 >= 1f && Y1 >= 1f;

    /// <summary>Clamps every edge to [0,1], orders them, and guarantees a non-degenerate rect.</summary>
    public RectNormalized Clamped()
    {
        var x0 = Math.Clamp(MathF.Min(X0, X1), 0f, 1f);
        var x1 = Math.Clamp(MathF.Max(X0, X1), 0f, 1f);
        var y0 = Math.Clamp(MathF.Min(Y0, Y1), 0f, 1f);
        var y1 = Math.Clamp(MathF.Max(Y0, Y1), 0f, 1f);
        // A zero-area crop would draw nothing; nudge to a minimal sliver so layers never vanish silently.
        if (x1 <= x0) x1 = MathF.Min(1f, x0 + 1e-4f);
        if (y1 <= y0) y1 = MathF.Min(1f, y0 + 1e-4f);
        return new RectNormalized(x0, y0, x1, y1);
    }
}

/// <summary>
/// One layer in an <see cref="IVideoCompositor"/> composite — a frame plus how to crop, position
/// and blend it onto the destination canvas.
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
    /// <summary>
    /// Normalized sub-rectangle of <see cref="Frame"/> to sample; pixels outside it are never drawn.
    /// Defaults to the whole frame so existing callers are unaffected.
    /// </summary>
    public RectNormalized SourceCrop { get; init; } = RectNormalized.Full;

    /// <summary>Convenience: identity transform, full opacity, source-over blend, no crop.</summary>
    public static CompositorLayer Default(VideoFrame frame) =>
        new(frame, LayerTransform2D.Identity, 1f, BlendMode.SourceOver);
}
