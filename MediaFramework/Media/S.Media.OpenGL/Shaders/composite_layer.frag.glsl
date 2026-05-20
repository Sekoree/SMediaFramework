#version 330 core

// Compositor fragment shader. uBlendKind: 0 = Source / SourceOver, 1 = Multiply.
// The host configures glBlendFunc per layer; the shader only does its own work for Multiply
// (where the fragment output needs to be a "multiplier color" so glBlendFunc(DST_COLOR, ZERO)
// produces the desired result).

in vec2 vUV;
uniform sampler2D uLayer;
uniform float uOpacity;
uniform int uBlendKind;
out vec4 fragColor;

void main()
{
    vec4 src = texture(uLayer, vUV);
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
        // Source / SourceOver: pass through (premul) * opacity. glBlendFunc handles the dst combine.
        fragColor = src * uOpacity;
    }
}
