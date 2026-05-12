#version 330 core

// FFmpeg AV_PIX_FMT_ABGR: memory bytes A,B,G,R per RGBA UNSIGNED_BYTE texture upload -
// .r=A, .g=B, .b=G, .a=R for display RGB,R,G,B and alpha A.

in vec2 v_uv;
out vec4 fragColor;

uniform sampler2D image;

void main()
{
    vec4 t = textureBicubicVec4(image, v_uv, uTexBicubicDim0);
    fragColor = vec4(t.a, t.b, t.g, t.r);
}
