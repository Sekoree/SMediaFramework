#version 330 core

// Single GL_RGB8 texture; outputs opaque RGB.

in vec2 v_uv;
out vec4 fragColor;

uniform sampler2D image;

void main()
{
    vec4 t = textureBicubicVec4(image, v_uv, uTexBicubicDim0);
    fragColor = vec4(t.rgb, 1.0);
}
