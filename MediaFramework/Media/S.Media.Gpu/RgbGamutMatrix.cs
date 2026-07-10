namespace S.Media.Gpu;

/// <summary>
/// 3×3 RGB → RGB matrix applied after the per-frame YUV → RGB conversion and HDR preview pass.
/// Used to map between RGB working spaces - primarily BT.2020 → BT.709 for SDR display preview
/// of UHD HDR content.
/// </summary>
/// <remarks>
/// <para>
/// Stored row-major like <see cref="YuvColorSpace.Matrix"/>; pass to GLSL <c>uniformMatrix3fv</c>
/// with <c>transpose = true</c> (or transpose locally) - GLSL <c>mat3</c> is column-major.
/// </para>
/// <para>
/// The matrix is applied to display-space RGB, not linear-light RGB, which is technically incorrect
/// for strict color management. For a preview display the visual result is close enough; for an
/// authoring tool a future revision should re-order against an explicit linearisation pass. See
/// <c>Doc/MediaFramework-Architecture.md</c> for the deeper discussion.
/// </para>
/// </remarks>
public readonly record struct RgbGamutMatrix(float[] Matrix)
{
    /// <summary>Identity - no remap. Default for every renderer; cheap GPU branch.</summary>
    public static readonly RgbGamutMatrix Identity = new(
        Matrix: new float[]
        {
            1f, 0f, 0f,
            0f, 1f, 0f,
            0f, 0f, 1f,
        });

    /// <summary>
    /// BT.2020 → BT.709 RGB matrix (ITU-R BT.2087). Maps unit BT.2020 white (1, 1, 1) onto unit
    /// BT.709 white. Saturated BT.2020 primaries can fall outside the BT.709 gamut after the
    /// remap - caller is expected to clamp post-multiply if a strict 0…1 output is required.
    /// </summary>
    public static readonly RgbGamutMatrix Bt2020ToBt709 = new(
        Matrix: new float[]
        {
             1.6605f, -0.5876f, -0.0728f,
            -0.1246f,  1.1329f, -0.0083f,
            -0.0182f, -0.1006f,  1.1187f,
        });

    /// <summary>Choose the right gamut remap from a per-frame <see cref="VideoColorSpace"/> source +
    /// caller-set display color space. Returns <see cref="Identity"/> when no remap is needed.</summary>
    public static RgbGamutMatrix FromHint(S.Media.Core.Video.VideoColorSpace source, S.Media.Core.Video.VideoColorSpace display)
    {
        if (display != S.Media.Core.Video.VideoColorSpace.Bt709)
            return Identity; // only BT.709 SDR display preview is wired today.
        return source switch
        {
            S.Media.Core.Video.VideoColorSpace.Bt2020 or S.Media.Core.Video.VideoColorSpace.Bt2020Cl => Bt2020ToBt709,
            _ => Identity,
        };
    }
}
