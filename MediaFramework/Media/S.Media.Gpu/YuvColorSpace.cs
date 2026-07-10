using S.Media.Core.Video;

namespace S.Media.Gpu;

/// <summary>
/// YUV → RGB colour-space matrices used by the planar / NV12 fragment
/// shaders. Written as <c>row-major</c> 3×3 matrices that satisfy
/// <c>RGB = M * (YUV - offset)</c> with values normalized to [0, 1].
/// Pass to GLSL as <c>uniformMatrix3fv</c> with <c>transpose = true</c> (or
/// transpose locally) - GLSL <c>mat3</c> is column-major.
/// </summary>
/// <remarks>
/// <para>
/// Two pieces per choice: the <see cref="Matrix"/> applied after
/// <see cref="Offset"/> subtraction. <see cref="Offset"/> handles black
/// level / chroma centering - <c>(0, 0.5, 0.5)</c> for full range and
/// <c>(16/255, 128/255, 128/255)</c> for limited / studio range.
/// </para>
/// <para>
/// BT.709 is the modern HD/UHD primary; BT.601 covers SD content (many
/// cameras still tag old colour metadata that resolves to 601). For ProRes
/// 4K, BT.709 limited-range is the typical default.
/// </para>
/// </remarks>
public readonly record struct YuvColorSpace(float[] Matrix, float[] Offset)
{
    public static readonly YuvColorSpace Bt709Limited = new(
        Matrix: new float[]
        {
            1.16438356f,  0.00000000f,  1.79274107f,
            1.16438356f, -0.21324861f, -0.53290932f,
            1.16438356f,  2.11240178f,  0.00000000f,
        },
        Offset: new[] { 16f / 255f, 128f / 255f, 128f / 255f });

    public static readonly YuvColorSpace Bt709Full = new(
        Matrix: new float[]
        {
            1.0f,  0.00000000f,  1.57480000f,
            1.0f, -0.18732427f, -0.46812427f,
            1.0f,  1.85560000f,  0.00000000f,
        },
        Offset: new[] { 0f, 128f / 255f, 128f / 255f });

    public static readonly YuvColorSpace Bt601Limited = new(
        Matrix: new float[]
        {
            1.16438356f,  0.00000000f,  1.59602679f,
            1.16438356f, -0.39176229f, -0.81296765f,
            1.16438356f,  2.01723214f,  0.00000000f,
        },
        Offset: new[] { 16f / 255f, 128f / 255f, 128f / 255f });

    /// <summary>
    /// ITU-R BT.2020 / Rec.2020 (UHD HDR) limited-range matrix. Coefficients <c>Kr=0.2627</c>,
    /// <c>Kb=0.0593</c>; same offset shape as BT.709 limited.
    /// </summary>
    public static readonly YuvColorSpace Bt2020Limited = new(
        Matrix: new float[]
        {
            1.16438356f,  0.00000000f,  1.67867410f,
            1.16438356f, -0.18732610f, -0.65042418f,
            1.16438356f,  2.14177232f,  0.00000000f,
        },
        Offset: new[] { 16f / 255f, 128f / 255f, 128f / 255f });

    /// <summary>BT.2020 full-range YCbCr → RGB matrix (no Y scaling, chroma centered at 0.5).</summary>
    public static readonly YuvColorSpace Bt2020Full = new(
        Matrix: new float[]
        {
            1.0f,  0.00000000f,  1.47460000f,
            1.0f, -0.16455313f, -0.57135313f,
            1.0f,  1.88140000f,  0.00000000f,
        },
        Offset: new[] { 0f, 128f / 255f, 128f / 255f });

    /// <summary>Pick a default by frame height - BT.709 for HD+ content, BT.601 for SD.</summary>
    public static YuvColorSpace DefaultForHeight(int height, bool fullRange = false)
    {
        if (height >= 720)
            return fullRange ? Bt709Full : Bt709Limited;
        return Bt601Limited;
    }

    /// <summary>
    /// Pick the right matrix from a per-frame <see cref="VideoColorSpace"/> +
    /// <see cref="VideoColorRange"/> hint. Falls back to <see cref="DefaultForHeight"/> when
    /// <paramref name="cs"/> is <see cref="VideoColorSpace.Unspecified"/>.
    /// </summary>
    public static YuvColorSpace FromHint(VideoColorSpace cs, VideoColorRange range, int height)
    {
        var full = range == VideoColorRange.Full;
        return cs switch
        {
            VideoColorSpace.Bt709 => full ? Bt709Full : Bt709Limited,
            VideoColorSpace.Bt601 => Bt601Limited, // SD path - full-range 601 is rare; map to limited.
            VideoColorSpace.Bt2020 or VideoColorSpace.Bt2020Cl => full ? Bt2020Full : Bt2020Limited,
            _ => DefaultForHeight(height, full),
        };
    }
}
