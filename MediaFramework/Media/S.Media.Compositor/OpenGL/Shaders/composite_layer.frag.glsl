#version 330 core

// Compositor fragment shader. uBlendKind: 0 = Source / SourceOver, 1 = Multiply.
// The host configures glBlendFunc per layer; the shader only does its own work for Multiply
// (where the fragment output needs to be a "multiplier color" so glBlendFunc(DST_COLOR, ZERO)
// produces the desired result).

in vec2 vUV;
uniform sampler2D uLayer;
uniform float uOpacity;
uniform int uBlendKind;
// 1 = flip the sampled V coordinate. The YUV pre-pass path bakes a vertical flip (yUvFlip) into its
// intermediate texture, so a direct BGRA32 upload (no pre-pass) is vertically opposite; it sets this
// to 1 so both paths composite right-side-up. 0 = sample as-is (YUV pre-pass / already-flipped).
uniform float uLayerFlipV;
// Layer-effect injection point: for a layer with an effect chain (chroma key, plugin effects),
// VideoLayerEffectShaderComposer replaces the two __LAYER_FX__ markers with per-instance uniform
// declarations + ApplyFx{i} functions and the chained calls, and the host compiles one cached
// program variant per distinct chain. This base source (empty chain) compiles unchanged - the
// markers are plain comments. Effects see straight-alpha src (premultiply happens below).
//__LAYER_FX_DECLARATIONS__
out vec4 fragColor;

void main()
{
    vec2 st = vec2(vUV.x, mix(vUV.y, 1.0 - vUV.y, uLayerFlipV));
    vec4 src = texture(uLayer, st);
    //__LAYER_FX_APPLY__
    if (uBlendKind == 1)
    {
        // Multiply: emit a multiplier color. Layer alpha * opacity acts as the blend weight --
        // weight = 0 -> multiplier is white (no effect); weight = 1 -> multiplier is src.rgb.
        float w = src.a * uOpacity;
        vec3 c = mix(vec3(1.0), src.rgb, w);
        fragColor = vec4(c, 1.0);
    }
    else
    {
        // Source / SourceOver: convert straight-alpha renderer output to premultiplied alpha
        // before glBlendFunc(ONE, ONE_MINUS_SRC_ALPHA) combines it with the destination.
        float a = src.a * uOpacity;
        fragColor = vec4(src.rgb * a, a);
    }
}
