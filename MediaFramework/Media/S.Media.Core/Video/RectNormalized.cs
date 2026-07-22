namespace S.Media.Core.Video;

/// <summary>
/// A normalized sub-rectangle of a layer's source frame, in [0,1] UV coordinates (top-left
/// origin, Y down). Used to crop/trim a source before compositing. <see cref="Full"/> is the
/// whole frame.
/// </summary>
public readonly record struct RectNormalized(float X0, float Y0, float X1, float Y1)
{
    /// <summary>The entire source frame - no crop.</summary>
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
