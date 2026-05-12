#version 330 core

// 4-plane YUVA420: Y/U/V behave like planar 4:2:0 RGB path; plane A is full-size 8-bit alpha.

in vec2 v_uv;
out vec4 fragColor;

uniform sampler2D yPlane;
uniform sampler2D uPlane;
uniform sampler2D vPlane;
uniform sampler2D aPlane;
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
    float y = textureBicubicR(yPlane, v_uv, uTexBicubicDim0) * bitScale;
    float u = textureBicubicR(uPlane, v_uv, uTexBicubicDim1) * bitScale;
    float v = textureBicubicR(vPlane, v_uv, uTexBicubicDim2) * bitScale;
    vec3 yuv = vec3(y, u, v) - yuvOffset;
    vec3 rgbLin = yuvMatrix * yuv;
    vec3 rgb = hdrPreviewAfterMatrix(rgbLin);
    float a = textureBicubicR(aPlane, v_uv, uTexBicubicDim3);
    fragColor = vec4(rgb, a);
}
