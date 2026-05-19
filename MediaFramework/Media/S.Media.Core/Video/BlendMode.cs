namespace S.Media.Core.Video;

/// <summary>
/// Per-layer blend mode used by <see cref="IVideoCompositor"/> implementations.
/// </summary>
/// <remarks>
/// <para>
/// Math is expressed in straight-alpha terms below; implementations may operate on premultiplied
/// pixels and adjust accordingly. <c>src.a</c> is the layer alpha multiplied by the layer's
/// <see cref="CompositorLayer.Opacity"/>.
/// </para>
/// </remarks>
public enum BlendMode
{
    /// <summary>Replace destination with source (<c>dst = src</c>). Per-layer opacity still applies to the source alpha.</summary>
    Source = 0,

    /// <summary>Porter-Duff "source-over": <c>dst = src + dst*(1 - src.a)</c>. Standard alpha-blend.</summary>
    SourceOver = 1,

    /// <summary>Multiplicative blend: <c>dst.rgb = src.rgb * dst.rgb</c>, weighted by <c>src.a</c>. Useful for tinting overlays.</summary>
    Multiply = 2,
}
