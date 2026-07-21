namespace S.Media.Compositor.Effects;

/// <summary>
/// The built-in chroma-key ("green screen") layer effect - the first effect of the layer-effect
/// system and the reference for plugin authors. Parameter semantics live on
/// <see cref="ChromaKeySettings"/>; this class packs them into a <see cref="VideoLayerEffect"/>
/// whose GLSL body and CPU kernel implement identical math.
/// </summary>
public static class ChromaKeyVideoEffect
{
    public const string EffectId = "chroma-key.v1";

    // Body of vec4 apply(vec4 src). $P(key) = key color RGB; $P(ranges) = (similarity, smoothness,
    // spill width). Chroma coefficients must stay in sync with ChromaKeySettings.RgbToChroma.
    private const string GlslBody = """
        vec3 cbCoeff = vec3(-0.100644, -0.338572, 0.439216);
        vec3 crCoeff = vec3(0.439216, -0.398942, -0.040274);
        vec2 pixChroma = vec2(dot(src.rgb, cbCoeff), dot(src.rgb, crCoeff));
        vec2 keyChroma = vec2(dot($P(key), cbCoeff), dot($P(key), crCoeff));
        float baseMask = distance(pixChroma, keyChroma) - $P(ranges).x;
        float fullMask = pow(clamp(baseMask / max($P(ranges).y, 1e-4), 0.0, 1.0), 1.5);
        src.a *= fullMask;
        if ($P(ranges).z > 0.0)
        {
            float spillVal = pow(clamp(baseMask / $P(ranges).z, 0.0, 1.0), 1.5);
            float luma = clamp(dot(src.rgb, vec3(0.2126, 0.7152, 0.0722)), 0.0, 1.0);
            src.rgb = mix(vec3(luma), src.rgb, spillVal);
        }
        return src;
        """;

    public static VideoLayerEffectDescriptor Descriptor { get; } = new(
        EffectId,
        GlslBody,
        [new VideoLayerEffectParameter("key", 3), new VideoLayerEffectParameter("ranges", 3)],
        static values => new CpuKernel(values));

    /// <summary>Builds a configured effect instance from clamped <paramref name="settings"/>.</summary>
    public static VideoLayerEffect Create(ChromaKeySettings settings)
    {
        var s = settings.Clamped();
        return new VideoLayerEffect(
            Descriptor,
            [s.KeyR, s.KeyG, s.KeyB, s.Similarity, s.Smoothness, s.SpillSuppression]);
    }

    /// <summary>
    /// Registry factory (<c>IBusRegistry</c> opaque-config contract, mirroring
    /// <c>GainAudioEffect.FromJson</c>): builds an instance from an optional JSON blob
    /// (<c>{"keyR":0,"keyG":1,"keyB":0,"similarity":0.4,"smoothness":0.08,"spill":0.1}</c>,
    /// every property optional). Malformed JSON falls back to the green-screen defaults -
    /// a corrupt config must not fault the line.
    /// </summary>
    public static VideoLayerEffect FromJson(string? configJson)
    {
        var settings = ChromaKeySettings.GreenScreen;
        if (string.IsNullOrWhiteSpace(configJson))
            return Create(settings);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(configJson);
            var root = doc.RootElement;
            float Read(string name, float fallback) =>
                root.TryGetProperty(name, out var el) && el.TryGetDouble(out var v) ? (float)v : fallback;
            settings = new ChromaKeySettings(
                Read("keyR", settings.KeyR),
                Read("keyG", settings.KeyG),
                Read("keyB", settings.KeyB),
                Read("similarity", settings.Similarity),
                Read("smoothness", settings.Smoothness),
                Read("spill", settings.SpillSuppression));
        }
        catch (System.Text.Json.JsonException)
        {
            // opaque blob didn't parse - keep the defaults
        }

        return Create(settings);
    }

    private sealed class CpuKernel : IVideoLayerCpuEffect
    {
        private readonly float _keyCb;
        private readonly float _keyCr;
        private readonly float _similarity;
        private readonly float _smoothness;
        private readonly float _spill;

        public CpuKernel(float[] values)
        {
            (_keyCb, _keyCr) = ChromaKeySettings.RgbToChroma(values[0], values[1], values[2]);
            _similarity = values[3];
            _smoothness = MathF.Max(values[4], 1e-4f);
            _spill = values[5];
        }

        public void Apply(ref float r, ref float g, ref float b, ref float a)
        {
            var (cb, cr) = ChromaKeySettings.RgbToChroma(r, g, b);
            var dCb = cb - _keyCb;
            var dCr = cr - _keyCr;
            var baseMask = MathF.Sqrt(dCb * dCb + dCr * dCr) - _similarity;
            var fullMask = Pow15(Math.Clamp(baseMask / _smoothness, 0f, 1f));
            a *= fullMask;
            if (_spill > 0f)
            {
                var spillVal = Pow15(Math.Clamp(baseMask / _spill, 0f, 1f));
                var luma = Math.Clamp(0.2126f * r + 0.7152f * g + 0.0722f * b, 0f, 1f);
                r = luma + (r - luma) * spillVal;
                g = luma + (g - luma) * spillVal;
                b = luma + (b - luma) * spillVal;
            }
        }

        /// <summary>pow(x, 1.5) for x in [0, 1] as x·√x - same curve as the GLSL body's pow at a
        /// fraction of the per-pixel cost (this kernel runs once per pixel per frame).</summary>
        private static float Pow15(float x) => x * MathF.Sqrt(x);
    }
}
