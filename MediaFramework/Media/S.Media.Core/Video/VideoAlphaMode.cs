namespace S.Media.Core.Video;

/// <summary>
/// Describes how alpha is encoded in CPU pixel data when a format carries an alpha channel.
/// </summary>
public enum VideoAlphaMode
{
    /// <summary>Producer did not state an alpha convention. Consumers should keep their legacy default.</summary>
    Unspecified = 0,

    /// <summary>RGB channels have already been multiplied by alpha.</summary>
    Premultiplied,

    /// <summary>RGB channels are straight color and must be multiplied by alpha before premultiplied blending.</summary>
    Straight,

    /// <summary>Frame is fully opaque or alpha should be treated as 1.0.</summary>
    Opaque,
}
