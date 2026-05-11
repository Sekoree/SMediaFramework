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
uniform int uHdrTransfer;
uniform float uHdrExposure;

vec3 pqInverseEotf(vec3 Np)
{
    const float m1 = 0.1593017578125;
    const float m2 = 78.84375;
    const float c1 = 0.8359375;
    const float c2 = 18.8515625;
    const float c3 = 18.6875;
    vec3 N = clamp(Np, vec3(0.0), vec3(1.0));
    vec3 Lm = pow(N, vec3(1.0 / m2));
    vec3 num = max(Lm - vec3(c1), vec3(0.0));
    vec3 den = max(vec3(c2) - vec3(c3) * Lm, vec3(1e-6));
    return clamp(pow(num / den, vec3(1.0 / m1)), vec3(0.0), vec3(1.0));
}

vec3 srgbDecode(vec3 rgb)
{
    rgb = clamp(rgb, vec3(0.0), vec3(1.0));
    bvec3 low = lessThanEqual(rgb, vec3(0.04045));
    vec3 loLin = rgb / vec3(12.92);
    vec3 hiLin = pow((rgb + vec3(0.055)) / vec3(1.055), vec3(2.4));
    return mix(hiLin, loLin, vec3(low));
}

float inverseHlgOetf(float Eg)
{
    Eg = clamp(Eg, 0.0, 1.0);
    if (Eg <= 0.5)
        return Eg * Eg / 3.0;
    const float a = 0.17883277;
    const float b = 0.28466892;
    const float c = 0.55991073;
    return (exp((Eg - c) / a) + b) / 12.0;
}

vec3 hdrPreviewAfterMatrix(vec3 rgb)
{
    if (uHdrTransfer <= 0)
        return clamp(rgb, vec3(0.0), vec3(1.0));
    if (uHdrTransfer == 1)
        return clamp(srgbDecode(rgb), vec3(0.0), vec3(1.0));
    float expAmt = max(uHdrExposure, 1e-6);
    if (uHdrTransfer == 2)
    {
        vec3 L = pqInverseEotf(rgb);
        vec3 n = L * vec3(expAmt / 10000.0);
        return n / (n + vec3(1.0));
    }
    if (uHdrTransfer == 3)
    {
        vec3 L = vec3(inverseHlgOetf(rgb.x), inverseHlgOetf(rgb.y), inverseHlgOetf(rgb.z));
        vec3 n = L * vec3(expAmt / 12.0);
        return n / (n + vec3(1.0));
    }
    return clamp(rgb, vec3(0.0), vec3(1.0));
}

void main()
{
    float y = texture(yPlane, v_uv).r * bitScale;
    float u = texture(uPlane, v_uv).r * bitScale;
    float v = texture(vPlane, v_uv).r * bitScale;
    vec3 yuv = vec3(y, u, v) - yuvOffset;
    vec3 rgb = yuvMatrix * yuv;
    fragColor = vec4(hdrPreviewAfterMatrix(rgb), 1.0);
}
