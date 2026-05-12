#version 330 core

// FFmpeg AV_PIX_FMT_ARGB: memory bytes A,R,G,B per texel uploaded as GL_RGBA UNSIGNED_BYTE -
// sampled as .r=A, .g=R, .b=G, .a=B in GLSL -> map to RGBA fragment output (.rgb=R,G,B).

in vec2 v_uv;
out vec4 fragColor;

uniform sampler2D image;

void main()
{
    vec4 t = texture(image, v_uv);
    fragColor = vec4(t.g, t.b, t.a, t.r);
}
