namespace S.Media.Core.Video;

/// <summary>
/// Static descriptors for the <see cref="PixelFormat"/> values: plane count and
/// per-plane vertical subsampling. Shared by decoders, sinks, and any conversion
/// node that needs to reason about plane layout.
/// </summary>
public static class PixelFormatInfo
{
    /// <summary>Rounds up when halving luma dimensions for 4:2:0 chroma (odd sizes).</summary>
    public static int ChromaWidth420(int frameWidth) => (frameWidth + 1) >> 1;

    /// <summary>Rounds up when halving luma dimensions for 4:2:0 chroma (odd sizes).</summary>
    public static int ChromaHeight420(int frameHeight) => (frameHeight + 1) >> 1;

    /// <summary>4:2:2 chroma columns (half horizontal resolution, rounded up).</summary>
    public static int ChromaWidth422(int frameWidth) => (frameWidth + 1) >> 1;

    /// <summary>How many separate byte planes a frame in this format carries.</summary>
    public static int PlaneCount(PixelFormat format) => format switch
    {
        PixelFormat.I420 or PixelFormat.Yv12 or PixelFormat.Yuv422P10Le or PixelFormat.Yuv422P or PixelFormat.Yuv444P
            or PixelFormat.Yuv420P10Le or PixelFormat.Yuv420P12Le or PixelFormat.Yuv444P10Le => 3,
        PixelFormat.Nv12 or PixelFormat.Nv21 or PixelFormat.P010 or PixelFormat.P016 => 2,
        PixelFormat.Yuva420p => 4,
        _ => 1,
    };

    /// <summary>
    /// Visible row count of plane <paramref name="planeIndex"/> for a frame of
    /// total height <paramref name="frameHeight"/>. Chroma planes in 4:2:0
    /// formats are half-height (rounded up); 4:2:2 chroma is full height (only horizontal
    /// subsampling).
    /// </summary>
    public static int PlaneHeight(PixelFormat format, int frameHeight, int planeIndex) => format switch
    {
        PixelFormat.I420 or PixelFormat.Yv12 or PixelFormat.Yuv420P10Le or PixelFormat.Yuv420P12Le
            when planeIndex is 1 or 2 => ChromaHeight420(frameHeight),
        PixelFormat.Nv12 or PixelFormat.Nv21 or PixelFormat.P010 or PixelFormat.P016 when planeIndex == 1 =>
            ChromaHeight420(frameHeight),
        PixelFormat.Yuva420p when planeIndex is 1 or 2 => ChromaHeight420(frameHeight),
        PixelFormat.Yuv422P or PixelFormat.Yuv422P10Le when planeIndex > 0 => frameHeight,
        _ => frameHeight,
    };

    /// <summary>
    /// Visible byte width of plane <paramref name="planeIndex"/> for a frame
    /// of total width <paramref name="frameWidth"/>. Accounts for both
    /// chroma horizontal subsampling and per-sample storage size (10-bit
    /// formats store each sample in 2 bytes).
    /// </summary>
    public static int PlaneByteWidth(PixelFormat format, int frameWidth, int planeIndex) => format switch
    {
        PixelFormat.I420 or PixelFormat.Yv12 when planeIndex > 0 => ChromaWidth420(frameWidth),
        PixelFormat.Nv12 or PixelFormat.Nv21 when planeIndex == 1 => ChromaWidth420(frameWidth) * 2,
        PixelFormat.P010 or PixelFormat.P016 when planeIndex == 1 => ChromaWidth420(frameWidth) * 4,
        PixelFormat.Yuv422P10Le when planeIndex == 0 => frameWidth * 2,
        PixelFormat.Yuv422P10Le when planeIndex > 0 => ChromaWidth422(frameWidth) * 2,
        PixelFormat.Yuv422P when planeIndex == 0 => frameWidth,
        PixelFormat.Yuv422P when planeIndex > 0 => ChromaWidth422(frameWidth),
        PixelFormat.Yuv444P => frameWidth * BytesPerSample(format),
        PixelFormat.Yuv444P10Le => frameWidth * 2,
        PixelFormat.Yuv420P10Le or PixelFormat.Yuv420P12Le when planeIndex == 0 => frameWidth * 2,
        PixelFormat.Yuv420P10Le or PixelFormat.Yuv420P12Le when planeIndex is 1 or 2 =>
            ChromaWidth420(frameWidth) * 2,
        PixelFormat.P010 or PixelFormat.P016 when planeIndex == 0 => frameWidth * 2,
        PixelFormat.Bgra32 or PixelFormat.Rgba32 or PixelFormat.Argb32 or PixelFormat.Abgr32 => frameWidth * 4,
        PixelFormat.Bgr24 or PixelFormat.Rgb24 => frameWidth * 3,
        PixelFormat.Uyvy or PixelFormat.Yuyv => frameWidth * 2,
        PixelFormat.Yuva420p when planeIndex is 0 or 3 => frameWidth,
        PixelFormat.Yuva420p when planeIndex is 1 or 2 => ChromaWidth420(frameWidth),
        PixelFormat.Gray8 => frameWidth,
        PixelFormat.Gray16 => frameWidth * 2,
        _ => frameWidth * BytesPerSample(format),
    };

    /// <summary>Bytes per packed/planar sample for high bit-depth planes (1 for 8-bit, 2 for 16-bit word storage).</summary>
    public static int BytesPerSample(PixelFormat format) => format switch
    {
        PixelFormat.Yuv422P10Le or PixelFormat.P010 or PixelFormat.P016 or PixelFormat.Yuv420P10Le
            or PixelFormat.Yuv420P12Le or PixelFormat.Yuv444P10Le or PixelFormat.Gray16 => 2,
        _ => 1,
    };

    /// <summary>True if the format may carry a meaningful alpha channel for compositing.</summary>
    public static bool IsAlphaCarrying(PixelFormat format) => format switch
    {
        PixelFormat.Bgra32 or PixelFormat.Rgba32 or PixelFormat.Argb32 or PixelFormat.Abgr32 => true,
        PixelFormat.Yuva420p => true,
        _ => false,
    };

    /// <summary>True for 10-/12-/16-bit pixel formats whose samples don't fit in 8 bits.</summary>
    public static bool IsHighBitDepth(PixelFormat format) => format switch
    {
        PixelFormat.Yuv422P10Le or PixelFormat.P010 or PixelFormat.P016 or PixelFormat.Yuv420P10Le
            or PixelFormat.Yuv420P12Le or PixelFormat.Yuv444P10Le or PixelFormat.Gray16 => true,
        _ => false,
    };

    /// <summary>
    /// Minimum contiguous byte span for <paramref name="planeIndex"/> when rows are padded to
    /// <paramref name="strideBytes"/> (must be ≥ <see cref="PlaneByteWidth"/> for that plane).
    /// </summary>
    public static int PlanePitchBufferLength(PixelFormat format, int frameWidth, int frameHeight, int planeIndex, int strideBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(planeIndex);
        if (strideBytes < PlaneByteWidth(format, frameWidth, planeIndex))
            throw new ArgumentOutOfRangeException(nameof(strideBytes), "stride is shorter than the visible row bytes for this plane.");
        checked
        {
            return strideBytes * PlaneHeight(format, frameHeight, planeIndex);
        }
    }
}
