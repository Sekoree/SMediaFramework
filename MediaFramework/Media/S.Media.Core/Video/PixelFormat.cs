namespace S.Media.Core.Video;

/// <summary>
/// Subset of pixel layouts the framework knows how to talk about.
/// Sources/outputs declare which formats they accept; the mixer/scaler is
/// responsible for any conversion in between.
/// </summary>
/// <remarks>
/// Intentionally tiny — add formats here as concrete sources/outputs need them.
/// Component order in names follows native byte order, e.g. <see cref="Bgra32"/>
/// is B,G,R,A in memory (Avalonia/SDL surfaces).
/// </remarks>
public enum PixelFormat
{
    Unknown = 0,

    // Packed RGB / RGBA
    Bgra32,
    Rgba32,
    Bgr24,
    Rgb24,

    // Planar YUV 4:2:0 (3 planes: Y, U, V)
    I420,
    Yv12,

    // Semi-planar YUV 4:2:0 (2 planes: Y, interleaved UV/VU)
    Nv12,
    Nv21,

    // Packed YUV 4:2:2
    Uyvy,
    Yuyv,

    // Planar YUV 4:2:2, 10-bit little-endian (e.g. ProRes 422). 3 planes:
    // Y, U, V. Each sample is stored in a 16-bit word (10 bits valid in
    // the lower bits). Chroma planes are half-width but full height.
    Yuv422P10Le,

    /// <summary>8-bit planar 4:2:2: Y full size, U and V at half horizontal resolution, full height.</summary>
    Yuv422P,

    /// <summary>8-bit planar 4:4:4: Y, U, and V full width and height.</summary>
    Yuv444P,

    /// <summary>Semi-planar 4:2:0, 10-bit MSBs in 16-bit little-endian words (NV12 layout, e.g. HEVC decode).</summary>
    P010,

    /// <summary>Semi-planar 4:2:0, 16-bit little-endian samples per component (NV12 layout).</summary>
    P016,

    /// <summary>
    /// Packed 32 bpp. Memory order follows FFmpeg <c>AV_PIX_FMT_ARGB</c>:
    /// per pixel bytes are A,R,G,B at increasing addresses (RGBA texture upload interprets channels raw;
    /// the fragment shader remaps to display RGBA).
    /// </summary>
    Argb32,

    /// <summary>Packed 32 bpp. Memory order follows FFmpeg <c>AV_PIX_FMT_ABGR</c> (bytes A,B,G,R).</summary>
    Abgr32,

    /// <summary>Single-plane luminance — 8 bits per pixel.</summary>
    Gray8,

    /// <summary>Single-plane luminance — stored as 16-bit little-endian samples (e.g. FFmpeg GRAY16LE).</summary>
    Gray16,

    /// <summary>Planar YUV 4:2:0 — 10 valid bits stored in lower bits of LE 16-bit words per sample (planes Y,U,V).</summary>
    Yuv420P10Le,

    /// <summary>Planar YUV 4:2:0 — 12 valid bits stored in LE 16-bit words per sample (planes Y,U,V).</summary>
    Yuv420P12Le,

    /// <summary>Planar YUV 4:4:4 — 10-bit LE 16-bit words per sample.</summary>
    Yuv444P10Le,

    /// <summary>Planar YUV 4:2:0 with full-resolution 8‑bit alpha plane (Y, U, V, A) — FFmpeg <c>YUVA420P</c>.</summary>
    Yuva420p,

    /// <summary>Planar YUV 4:2:2 + full-resolution 8-bit alpha plane (Y, U, V, A) — FFmpeg <c>YUVA422P</c>.</summary>
    Yuva422P,

    /// <summary>Planar YUV 4:4:4 + full-resolution 8-bit alpha plane (Y, U, V, A) — FFmpeg <c>YUVA444P</c>.</summary>
    Yuva444P,

    /// <summary>Planar YUV 4:2:0 + alpha — 10-bit LE samples in 16-bit words (4 planes) — FFmpeg <c>YUVA420P10LE</c>.</summary>
    Yuva420P10Le,

    /// <summary>Planar YUV 4:2:2 + alpha — 10-bit LE samples in 16-bit words (4 planes) — FFmpeg <c>YUVA422P10LE</c>.</summary>
    Yuva422P10Le,

    /// <summary>Planar YUV 4:4:4 + alpha — 10-bit LE samples in 16-bit words (4 planes) — FFmpeg <c>YUVA444P10LE</c>.</summary>
    Yuva444P10Le,

    /// <summary>Planar YUV 4:2:2 + alpha — 12-bit LE samples in 16-bit words (4 planes) — FFmpeg <c>YUVA422P12LE</c>.</summary>
    Yuva422P12Le,

    /// <summary>
    /// Planar YUV 4:4:4 + alpha — 12-bit LE samples in 16-bit words (4 planes) — FFmpeg <c>YUVA444P12LE</c>.
    /// Common in professional capture and HEVC 4:4:4 12-bit pipelines (e.g. <c>yuva444p12le(tv, bt709, progressive)</c>).
    /// </summary>
    Yuva444P12Le,

    /// <summary>Planar YUV 4:2:0 + alpha — 16-bit LE samples (4 planes) — FFmpeg <c>YUVA420P16LE</c>.</summary>
    Yuva420P16Le,

    /// <summary>Planar YUV 4:2:2 + alpha — 16-bit LE samples (4 planes) — FFmpeg <c>YUVA422P16LE</c>.</summary>
    Yuva422P16Le,

    /// <summary>Planar YUV 4:4:4 + alpha — 16-bit LE samples (4 planes) — FFmpeg <c>YUVA444P16LE</c>.</summary>
    Yuva444P16Le,

    /// <summary>Planar YUV 4:2:2 — 12-bit LE samples in 16-bit words (3 planes) — FFmpeg <c>YUV422P12LE</c>.</summary>
    Yuv422P12Le,

    /// <summary>Planar YUV 4:4:4 — 12-bit LE samples in 16-bit words (3 planes) — FFmpeg <c>YUV444P12LE</c>.</summary>
    Yuv444P12Le,
}
