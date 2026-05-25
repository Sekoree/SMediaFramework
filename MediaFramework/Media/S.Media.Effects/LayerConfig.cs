namespace S.Media.Effects;

/// <summary>Declarative per-layer composite parameters resolved each output frame.</summary>
public readonly record struct LayerConfig(
    LayerPosition Position,
    float Scale = 1f,
    float Opacity = 1f,
    float Rotation = 0f,
    BlendMode Blend = BlendMode.SourceOver,
    LayerAnchor ScaleAnchor = LayerAnchor.Center)
{
    /// <summary>Letterboxed full-frame background (scale-to-fit, centered).</summary>
    public static LayerConfig Background { get; } = new(LayerPosition.Center, 1f, 1f);

    /// <summary>Half-size centered overlay.</summary>
    public static LayerConfig CenteredHalf { get; } = new(LayerPosition.Center, 0.5f, 1f);

    /// <summary>Maps this config into a destination-canvas <see cref="LayerTransform2D"/> for <paramref name="source"/>.</summary>
    public LayerTransform2D ToTransform(VideoFormat source, VideoFormat canvas) =>
        LayerConfigResolver.ToTransform(this, source, canvas);
}
