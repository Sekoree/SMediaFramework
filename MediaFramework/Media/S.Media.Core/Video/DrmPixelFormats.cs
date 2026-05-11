namespace S.Media.Core.Video;

/// <summary>Linux DRM fourcc codes referenced by libav / EGL DMA-BUF import.</summary>
public static class DrmPixelFormats
{
    /// <summary>Semi-planar 8-bit Y + interleaved UV (matches <see cref="PixelFormat.Nv12"/>).</summary>
    public static uint Nv12 => FourCc('N', 'V', '1', '2');

    /// <summary>Single-plane 8-bit luma-only (first plane of NV12 for EGL split import).</summary>
    public static uint R8 => FourCc('R', '8', ' ', ' ');

    /// <summary>8-bit interleaved RG (UV) — second plane split from NV12 for EGL.</summary>
    public static uint Gr88 => FourCc('G', 'R', '8', '8');

    public static uint FourCc(char a, char b, char c, char d) =>
        (byte)a | ((uint)(byte)b << 8) | ((uint)(byte)c << 16) | ((uint)(byte)d << 24);
}
