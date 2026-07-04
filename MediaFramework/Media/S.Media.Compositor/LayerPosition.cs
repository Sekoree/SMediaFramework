namespace S.Media.Compositor;

/// <summary>How a layer is positioned on the compositor canvas.</summary>
public abstract record LayerPosition
{
    /// <summary>Uniform scale-to-fill (may crop).</summary>
    public static LayerPosition Cover { get; } = new CoverPosition();

    /// <summary>Uniform scale-to-fit with letterboxing (centered).</summary>
    public static LayerPosition Center { get; } = new CenterPosition();

    /// <summary>Anchor to a corner/edge with optional margin in destination pixels.</summary>
    public static LayerPosition Anchored(LayerAnchor anchor, float marginX = 0f, float marginY = 0f) =>
        new AnchoredPosition(anchor, marginX, marginY);

    /// <summary>Top-left at absolute destination pixel coordinates.</summary>
    public static LayerPosition AbsolutePixels(float x, float y) => new AbsolutePixelsPosition(x, y);

    /// <summary>Top-left at normalized coordinates in <c>[0, 1]</c> of the canvas.</summary>
    public static LayerPosition NormalizedXY(float x01, float y01) => new NormalizedPosition(x01, y01);

    private sealed record CoverPosition : LayerPosition;
    private sealed record CenterPosition : LayerPosition;
    internal sealed record AnchoredPosition(LayerAnchor Anchor, float MarginX, float MarginY) : LayerPosition;
    internal sealed record AbsolutePixelsPosition(float X, float Y) : LayerPosition;
    internal sealed record NormalizedPosition(float X01, float Y01) : LayerPosition;
}
