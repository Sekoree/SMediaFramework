#version 330 core

// 3-plane YUV (I420, YV12, YUV422P10LE, …). Samples three R-channel
// textures, scales for high-bit-depth storage, applies an offset + matrix
// to produce linear-ish RGB.
//
// Uniforms:
//   yPlane / uPlane / vPlane : R8 or R16 textures sampled as red.
//   bitScale                 : multiplies the sampled value to fix the
//                              storage normalization. 1.0 for 8-bit
//                              (R8 in [0,1]); for 10-bit packed in 16-bit
//                              (R16 storage, only the low 10 bits valid),
//                              pass 65535.0 / 1023.0 ≈ 64.0625.
//   yuvOffset                : per-channel subtraction before the matrix
//                              (limited-range Y subtracts 16/255, U/V
//                              subtract 128/255; full-range Y subtracts 0,
//                              U/V still 128/255).
//   yuvMatrix                : 3×3 transform from offset-YUV to RGB.

in vec2 v_uv;
out vec4 fragColor;

uniform sampler2D yPlane;
uniform sampler2D uPlane;
uniform sampler2D vPlane;
uniform float bitScale;
uniform vec3 yuvOffset;
uniform mat3 yuvMatrix;

void main()
{
    float y = texture(yPlane, v_uv).r * bitScale;
    float u = texture(uPlane, v_uv).r * bitScale;
    float v = texture(vPlane, v_uv).r * bitScale;
    vec3 yuv = vec3(y, u, v) - yuvOffset;
    vec3 rgb = yuvMatrix * yuv;
    fragColor = vec4(clamp(rgb, 0.0, 1.0), 1.0);
}
