namespace S.Media.Core.Video;

/// <summary>
/// YCbCr → RGB color-space hint attached to decoded frames (<see cref="VideoFrame.ColorSpace"/>).
/// Lets sinks pick the right conversion matrix (BT.709 vs BT.601 vs BT.2020) without each renderer
/// re-implementing FFmpeg metadata mapping.
/// </summary>
/// <remarks>
/// <see cref="Unspecified"/> means the producer did not surface a value; renderers should fall back
/// to their existing default (typically height-based: BT.709 for ≥720p, BT.601 otherwise).
/// </remarks>
public enum VideoColorSpace : byte
{
    Unspecified = 0,
    /// <summary>ITU-R BT.709 / Rec.709 (HD).</summary>
    Bt709 = 1,
    /// <summary>ITU-R BT.601 / Rec.601 (SD).</summary>
    Bt601 = 2,
    /// <summary>ITU-R BT.2020 / Rec.2020 non-constant-luminance (UHD / HDR).</summary>
    Bt2020 = 3,
    /// <summary>ITU-R BT.2020 constant-luminance variant (rare in practice).</summary>
    Bt2020Cl = 4,
}
