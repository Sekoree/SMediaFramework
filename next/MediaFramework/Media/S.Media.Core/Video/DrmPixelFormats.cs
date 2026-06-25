namespace S.Media.Core.Video;

/// <summary>Linux DRM fourcc codes referenced by libav / EGL DMA-BUF import.</summary>
public static class DrmPixelFormats
{
    /// <summary>Semi-planar 8-bit Y + interleaved UV (matches <see cref="PixelFormat.Nv12"/>).</summary>
    public static uint Nv12 => FourCc('N', 'V', '1', '2');

    /// <summary>Semi-planar 10-bit-per-channel Y + interleaved UV in 16-bit words (matches <see cref="PixelFormat.P010"/>).</summary>
    public static uint P010 => FourCc('P', '0', '1', '0');

    /// <summary>Semi-planar 16-bit-per-channel Y + interleaved UV (matches <see cref="PixelFormat.P016"/>).</summary>
    public static uint P016 => FourCc('P', '0', '1', '6');

    /// <summary>Single-plane 8-bit luma-only (first plane of NV12 for EGL split import).</summary>
    public static uint R8 => FourCc('R', '8', ' ', ' ');

    /// <summary>16-bit single-channel luma (P010 Y plane for EGL split import).</summary>
    public static uint R16 => FourCc('R', '1', '6', ' ');

    /// <summary>8-bit interleaved RG (UV) — second plane split from NV12 for EGL.</summary>
    public static uint Gr88 => FourCc('G', 'R', '8', '8');

    /// <summary>16:16 interleaved G:R (P010 UV plane for EGL, matches Linux <c>DRM_FORMAT_GR1616</c>).</summary>
    public static uint Gr1616 => FourCc('G', 'R', '3', '2');

    public static uint FourCc(char a, char b, char c, char d) =>
        (byte)a | ((uint)(byte)b << 8) | ((uint)(byte)c << 16) | ((uint)(byte)d << 24);
}
