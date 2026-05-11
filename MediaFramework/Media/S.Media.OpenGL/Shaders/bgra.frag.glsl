#version 330 core

// Pre-RGBA pass-through. Texture is uploaded with internal=GL_RGBA8 and
// data format=GL_BGRA — the driver swizzles on upload so the sampled
// texel is already in the correct R,G,B,A order.

in vec2 v_uv;
out vec4 fragColor;

uniform sampler2D image;

void main()
{
    fragColor = texture(image, v_uv);
}
