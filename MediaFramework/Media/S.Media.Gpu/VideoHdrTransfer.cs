namespace S.Media.Gpu;

/// <summary>
/// Inverse display transform after YUV→RGB matrix multiply. PQ/HLG are preview-style approximations
/// mapped into SDR on screen - not a calibrated broadcast chain.
/// </summary>
public enum VideoHdrTransfer
{
    /// <summary>Existing path: clamp to [0,1] - suitable for BT.709/601 SDR-ish content.</summary>
    None = 0,
    /// <summary>sRGB IEC 61966-2-1 EOTF (piecewise).</summary>
    Srgb = 1,
    /// <summary>
    /// ITU-R BT.2100 PQ (non-linear PQ domain in) → luminance-ish signal; reinhard-tonemapped preview.
    /// </summary>
    Pq = 2,
    /// <summary>ITU-R BT.2100 HLG OOTF^-1-ish preview (approximate).</summary>
    Hlg = 3,
}
