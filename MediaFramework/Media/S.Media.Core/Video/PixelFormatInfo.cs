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
        PixelFormat.I420 or PixelFormat.Yv12 or PixelFormat.Yuv422P10Le => 3,
        PixelFormat.Nv12 or PixelFormat.Nv21                            => 2,
        _                                                                => 1,
    };

    /// <summary>
    /// Visible row count of plane <paramref name="planeIndex"/> for a frame of
    /// total height <paramref name="frameHeight"/>. Chroma planes in 4:2:0
    /// formats are half-height; 4:2:2 chroma is full height (only horizontal
    /// subsampling).
    /// </summary>
    public static int PlaneHeight(PixelFormat format, int frameHeight, int planeIndex) => format switch
    {
        PixelFormat.I420 or PixelFormat.Yv12 when planeIndex > 0  => frameHeight / 2,
        PixelFormat.Nv12 or PixelFormat.Nv21 when planeIndex == 1 => frameHeight / 2,
        _                                                         => frameHeight,
    };

    /// <summary>
    /// Visible byte width of plane <paramref name="planeIndex"/> for a frame
    /// of total width <paramref name="frameWidth"/>. Accounts for both
    /// chroma horizontal subsampling and per-sample storage size (10-bit
    /// formats store each sample in 2 bytes).
    /// </summary>
    public static int PlaneByteWidth(PixelFormat format, int frameWidth, int planeIndex) => format switch
    {
        PixelFormat.I420 or PixelFormat.Yv12 when planeIndex > 0       => frameWidth / 2,
        PixelFormat.Nv12 or PixelFormat.Nv21 when planeIndex == 1      => frameWidth, // interleaved UV at half-width × 2 bytes
        PixelFormat.Yuv422P10Le when planeIndex == 0                   => frameWidth * 2,
        PixelFormat.Yuv422P10Le when planeIndex > 0                    => frameWidth, // half-width × 2 bytes
        PixelFormat.Bgra32 or PixelFormat.Rgba32                       => frameWidth * 4,
        PixelFormat.Bgr24 or PixelFormat.Rgb24                         => frameWidth * 3,
        PixelFormat.Uyvy or PixelFormat.Yuyv                           => frameWidth * 2,
        _                                                              => frameWidth,
    };

    /// <summary>True for 10-/12-/16-bit pixel formats whose samples don't fit in 8 bits.</summary>
    public static bool IsHighBitDepth(PixelFormat format) => format switch
    {
        PixelFormat.Yuv422P10Le => true,
        _                       => false,
    };
}
