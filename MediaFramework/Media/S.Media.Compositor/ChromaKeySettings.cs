namespace S.Media.Compositor;

/// <summary>
/// Chroma-key ("green screen") settings for one compositor layer. Pixels whose chroma (CbCr,
/// BT.709) lies within <see cref="Similarity"/> of the key color's chroma become transparent;
/// <see cref="Smoothness"/> widens the alpha ramp at the boundary, and <see cref="SpillSuppression"/>
/// desaturates surviving near-key pixels toward their luma so key-colored light bleeding onto the
/// subject doesn't tint the composite. The parameter semantics (and defaults) mirror OBS's
/// chroma-key filter so values translate directly for operators who know that tool:
/// <c>mask = pow(clamp((dist - similarity) / smoothness, 0, 1), 1.5)</c>.
/// GPU (<see cref="OpenGL.GlVideoCompositor"/>) and CPU (<see cref="CpuVideoCompositor"/>) paths
/// implement the same math.
/// </summary>
/// <param name="KeyR">Key color red, [0, 1] (sRGB).</param>
/// <param name="KeyG">Key color green, [0, 1] (sRGB).</param>
/// <param name="KeyB">Key color blue, [0, 1] (sRGB).</param>
/// <param name="Similarity">Chroma-distance below which a pixel is fully keyed out, [0, 1].</param>
/// <param name="Smoothness">Width of the soft alpha ramp above <paramref name="Similarity"/>, [0, 1].</param>
/// <param name="SpillSuppression">Width of the desaturation ramp for near-key spill, [0, 1]; 0 disables.</param>
public readonly record struct ChromaKeySettings(
    float KeyR,
    float KeyG,
    float KeyB,
    float Similarity = ChromaKeySettings.DefaultSimilarity,
    float Smoothness = ChromaKeySettings.DefaultSmoothness,
    float SpillSuppression = ChromaKeySettings.DefaultSpillSuppression)
{
    public const float DefaultSimilarity = 0.4f;
    public const float DefaultSmoothness = 0.08f;
    public const float DefaultSpillSuppression = 0.1f;

    /// <summary>Standard green-screen key with the default tolerances.</summary>
    public static ChromaKeySettings GreenScreen { get; } = new(0f, 1f, 0f);

    /// <summary>Standard blue-screen key with the default tolerances.</summary>
    public static ChromaKeySettings BlueScreen { get; } = new(0f, 0f, 1f);

    /// <summary>Every field clamped to its documented range (smoothness kept off zero so the
    /// mask division stays finite). Compositors call this once per layer before use.</summary>
    public ChromaKeySettings Clamped() => new(
        Math.Clamp(KeyR, 0f, 1f),
        Math.Clamp(KeyG, 0f, 1f),
        Math.Clamp(KeyB, 0f, 1f),
        Math.Clamp(Similarity, 0f, 1f),
        Math.Clamp(Smoothness, 1e-4f, 1f),
        Math.Clamp(SpillSuppression, 0f, 1f));

    /// <summary>BT.709 Cb/Cr of the key color - the chroma plane the distance test runs in.</summary>
    public (float Cb, float Cr) KeyChroma() => RgbToChroma(KeyR, KeyG, KeyB);

    /// <summary>BT.709 RGB → (Cb, Cr), both roughly in [-0.44, 0.44]. Shared by the CPU
    /// compositor and tests; the GLSL side duplicates the same coefficients.</summary>
    public static (float Cb, float Cr) RgbToChroma(float r, float g, float b) => (
        -0.100644f * r - 0.338572f * g + 0.439216f * b,
        0.439216f * r - 0.398942f * g - 0.040274f * b);
}
