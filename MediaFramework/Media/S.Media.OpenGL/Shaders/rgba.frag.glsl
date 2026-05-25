#version 330 core

// Packed RGBA8 pass-through (native byte order RGBA in memory - little-endian
// machines match OpenGL RGBA upload order).

in vec2 v_uv;
out vec4 fragColor;

uniform sampler2D image;

void main()
{
    fragColor = textureBicubicVec4(image, v_uv, uTexBicubicDim0);
}
