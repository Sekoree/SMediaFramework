#version 330 core

// NV12: Y in plane 0 (R8), interleaved UV in plane 1 (RG8).
// Same offset/matrix model as yuv_planar; no bit-depth scale (8-bit only).

in vec2 v_uv;
out vec4 fragColor;

uniform sampler2D yPlane;
uniform sampler2D uvPlane;
uniform vec3 yuvOffset;
uniform mat3 yuvMatrix;

void main()
{
    float y  = texture(yPlane,  v_uv).r;
    vec2  uv = texture(uvPlane, v_uv).rg;
    vec3 yuv = vec3(y, uv.r, uv.g) - yuvOffset;
    vec3 rgb = yuvMatrix * yuv;
    fragColor = vec4(clamp(rgb, 0.0, 1.0), 1.0);
}
