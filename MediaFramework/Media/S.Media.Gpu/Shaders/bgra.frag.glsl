#version 330 core

// Pre-RGBA pass-through. Texture is uploaded with internal=GL_RGBA8 and
// data format=GL_BGRA - on little-endian hosts this matches B,G,R,A in
// memory (Avalonia/SDL surfaces). Big-endian ports must revisit packing.

in vec2 v_uv;
out vec4 fragColor;

uniform sampler2D image;

void main()
{
    fragColor = textureBicubicVec4(image, v_uv, uTexBicubicDim0);
}
