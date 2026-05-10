namespace S.Media.Core.Video;

/// <summary>
/// Static descriptors for the <see cref="PixelFormat"/> values: plane count and
/// per-plane vertical subsampling. Shared by decoders, sinks, and any conversion
/// node that needs to reason about plane layout.
/// </summary>
public static class PixelFormatInfo
{
    /// <summary>How many separate byte planes a frame in this format carries.</summary>
    public static int PlaneCount(PixelFormat format) => format switch
    {
        PixelFormat.I420 or PixelFormat.Yv12   => 3,
        PixelFormat.Nv12 or PixelFormat.Nv21   => 2,
        _                                      => 1,
    };

    /// <summary>
    /// Visible row count of plane <paramref name="planeIndex"/> for a frame of
    /// total height <paramref name="frameHeight"/>. Chroma planes in 4:2:0
    /// formats are half-height; everything else matches the frame height.
    /// </summary>
    public static int PlaneHeight(PixelFormat format, int frameHeight, int planeIndex) => format switch
    {
        PixelFormat.I420 or PixelFormat.Yv12 when planeIndex > 0  => frameHeight / 2,
        PixelFormat.Nv12 or PixelFormat.Nv21 when planeIndex == 1 => frameHeight / 2,
        _                                                         => frameHeight,
    };
}
