namespace S.Media.Core.Video;

/// <summary>
/// Optional per-frame metadata bundled into <see cref="VideoFrame.Metadata"/>. Lets producers
/// surface color-space / range / field-order / transfer / SMPTE timecode hints in one shot,
/// keeping the <see cref="VideoFrame"/> ctor signature manageable as the hint set grows.
/// </summary>
/// <param name="ColorTransferHint">EOTF / inverse-tone-mapping selector (PQ / HLG / sRGB / SDR / Unspecified).</param>
/// <param name="ColorSpace">YCbCr → RGB matrix hint (BT.709 / BT.601 / BT.2020 / Unspecified).</param>
/// <param name="ColorRange">YUV value range (Limited / Full / Unspecified).</param>
/// <param name="FieldOrder">Interlace field order; <see cref="VideoFieldOrder.Progressive"/> for the common case.</param>
/// <param name="Timecode">SMPTE 12M timecode when the source carries one; null otherwise.</param>
/// <remarks>
/// <para>
/// All fields default to "unspecified" so producers only have to fill in what they know. The
/// receiving renderer (or NDI sender, etc.) falls back to its own defaults when a field is
/// unspecified.
/// </para>
/// <para>
/// As a <see langword="readonly record struct"/> this is allocation-free at every layer — pass by
/// value, copy freely.
/// </para>
/// </remarks>
public readonly record struct VideoFrameMetadata(
    VideoTransferHint ColorTransferHint = default,
    VideoColorSpace ColorSpace = default,
    VideoColorRange ColorRange = default,
    VideoFieldOrder FieldOrder = default,
    VideoTimecode? Timecode = null);
