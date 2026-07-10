namespace S.Media.Core.Video;

/// <summary>
/// YUV value-range hint - limited (TV / 16–235) vs full (PC / 0–255). Lets renderers pair the
/// right offset + scale with their <see cref="VideoColorSpace"/> matrix.
/// </summary>
public enum VideoColorRange : byte
{
    Unspecified = 0,
    /// <summary>Studio / TV range: Y in 16–235, Cb/Cr in 16–240. Default for most decoded video.</summary>
    Limited = 1,
    /// <summary>PC / JPEG range: every component spans 0–255.</summary>
    Full = 2,
}
