namespace S.Media.Core.Video;

/// <summary>
/// Transfer / EOTF hint attached to decoded frames (<see cref="VideoFrame.ColorTransferHint"/>).
/// Helps displays pick inverse tone-mapping for HDR preview modes without Core referencing GL types.
/// </summary>
/// <remarks>Unspecified means callers should leave their existing HDR / SDR pathway unchanged.</remarks>
public enum VideoTransferHint : byte
{
    Unspecified = 0,
    /// <summary>SDR / BT.709-ish — clamp to display range.</summary>
    Sdr = 1,
    /// <summary>IEC 61966-2-1 (similar to decoding sRGB-encoded RGB).</summary>
    FromSrgb = 2,
    /// <summary>Bt.2100 perceptual quantizer curve (typically ST 2084 / PQ).</summary>
    FromPq = 3,
    /// <summary>Bt.2100 hybrid log‑gamma curve.</summary>
    FromHlg = 4,
}
