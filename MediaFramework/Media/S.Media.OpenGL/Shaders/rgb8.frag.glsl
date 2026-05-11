#version 330 core

// Single GL_RGB8 texture; outputs opaque RGB.

in vec2 v_uv;
out vec4 fragColor;

uniform sampler2D image;

void main()
{
    fragColor = vec4(texture(image, v_uv).rgb, 1.0);
}
