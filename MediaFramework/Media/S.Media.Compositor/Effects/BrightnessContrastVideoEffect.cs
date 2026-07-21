namespace S.Media.Compositor.Effects;

/// <summary>Serializable brightness/contrast parameters, mirroring <see cref="ChromaKeySettings"/>'
/// role for the chroma key: the value the session placement records carry.</summary>
/// <param name="Brightness">Additive offset in [-1, 1]; 0 = unchanged.</param>
/// <param name="Contrast">Multiplier in [0, 4] pivoting around mid-gray; 1 = unchanged.</param>
public readonly record struct BrightnessContrastSettings(float Brightness = 0f, float Contrast = 1f);

/// <summary>
/// Brightness/contrast color-stage layer effect - the second effect of the layer-effect system,
/// mostly here to prove the plugin story stays a ~60-line affair. Brightness is an additive offset
/// in [-1, 1]; contrast is a multiplier in [0, 4] pivoting around mid-gray (0.5) so contrast alone
/// never shifts overall lightness. Alpha is untouched.
/// </summary>
public static class BrightnessContrastVideoEffect
{
    public const string EffectId = "brightness-contrast.v1";

    private const string GlslBody = """
        src.rgb = clamp((src.rgb - 0.5) * $P(bc).y + 0.5 + $P(bc).x, 0.0, 1.0);
        return src;
        """;

    public static VideoLayerEffectDescriptor Descriptor { get; } = new(
        EffectId,
        GlslBody,
        [new VideoLayerEffectParameter("bc", 2)],
        static values => new CpuKernel(values));

    public static VideoLayerEffect Create(float brightness, float contrast) =>
        new(Descriptor, [Math.Clamp(brightness, -1f, 1f), Math.Clamp(contrast, 0f, 4f)]);

    public static VideoLayerEffect Create(BrightnessContrastSettings settings) =>
        Create(settings.Brightness, settings.Contrast);

    /// <summary>Registry factory: <c>{"brightness":0.0,"contrast":1.0}</c>, both optional.
    /// Malformed JSON falls back to the identity (0, 1) - a corrupt config must not fault the line.</summary>
    public static VideoLayerEffect FromJson(string? configJson)
    {
        var brightness = 0f;
        var contrast = 1f;
        if (!string.IsNullOrWhiteSpace(configJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(configJson);
                if (doc.RootElement.TryGetProperty("brightness", out var b) && b.TryGetDouble(out var bv))
                    brightness = (float)bv;
                if (doc.RootElement.TryGetProperty("contrast", out var c) && c.TryGetDouble(out var cv))
                    contrast = (float)cv;
            }
            catch (System.Text.Json.JsonException)
            {
                // opaque blob didn't parse - keep the identity
            }
        }

        return Create(brightness, contrast);
    }

    private sealed class CpuKernel(float[] values) : IVideoLayerCpuEffect
    {
        private readonly float _brightness = values[0];
        private readonly float _contrast = values[1];

        public void Apply(ref float r, ref float g, ref float b, ref float a)
        {
            r = Math.Clamp((r - 0.5f) * _contrast + 0.5f + _brightness, 0f, 1f);
            g = Math.Clamp((g - 0.5f) * _contrast + 0.5f + _brightness, 0f, 1f);
            b = Math.Clamp((b - 0.5f) * _contrast + 0.5f + _brightness, 0f, 1f);
        }
    }
}
