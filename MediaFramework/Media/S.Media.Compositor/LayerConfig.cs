namespace S.Media.Compositor;

/// <summary>Declarative per-layer composite parameters resolved each output frame.</summary>
/// <remarks>Rotation/scale are composed around the source origin (see <see cref="LayerTransform2D"/>). A
/// previous <c>ScaleAnchor</c> field was never read by the resolver, so it was removed rather than left as
/// an API control that did nothing - anchor-aware composition can be reintroduced when actually wired.</remarks>
public readonly record struct LayerConfig(
    LayerPosition Position,
    float Scale = 1f,
    float Opacity = 1f,
    float Rotation = 0f,
    BlendMode Blend = BlendMode.SourceOver)
{
    /// <summary>Optional per-layer effect chain (e.g. chroma key); null = none. Not animated by
    /// transitions - swap the config to change effect parameters.</summary>
    public IReadOnlyList<VideoLayerEffect>? Effects { get; init; }

    /// <summary>Letterboxed full-frame background (scale-to-fit, centered).</summary>
    public static LayerConfig Background { get; } = new(LayerPosition.Center, 1f, 1f);

    /// <summary>Half-size centered overlay.</summary>
    public static LayerConfig CenteredHalf { get; } = new(LayerPosition.Center, 0.5f, 1f);

    /// <summary>Maps this config into a destination-canvas <see cref="LayerTransform2D"/> for <paramref name="source"/>.</summary>
    public LayerTransform2D ToTransform(VideoFormat source, VideoFormat canvas) =>
        LayerConfigResolver.ToTransform(this, source, canvas);
}
