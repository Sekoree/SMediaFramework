#version 330 core

// Single-channel luminance — R8 or R16 texture (scaled for high bit depth).

in vec2 v_uv;
out vec4 fragColor;

uniform sampler2D grayPlane;
uniform float bitScale;

void main()
{
    float L = clamp(texture(grayPlane, v_uv).r * bitScale, 0.0, 1.0);
    fragColor = vec4(L, L, L, 1.0);
}
