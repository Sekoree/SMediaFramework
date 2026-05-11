namespace S.Media.Core.Video;

/// <summary>
/// Subset of pixel layouts the framework knows how to talk about.
/// Sources/sinks declare which formats they accept; the mixer/scaler is
/// responsible for any conversion in between.
/// </summary>
/// <remarks>
/// Intentionally tiny — add formats here as concrete sources/sinks need them.
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
}
