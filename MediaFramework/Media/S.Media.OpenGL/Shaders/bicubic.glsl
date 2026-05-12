
// Catmull-Rom tensor bicubic via texelFetch (LOD 0). Requires plane dimensions in texels.
// Injected after #version in fragment shaders when the format does not use NearestSampling.

uniform vec2 uTexBicubicDim0;
uniform vec2 uTexBicubicDim1;
uniform vec2 uTexBicubicDim2;
uniform vec2 uTexBicubicDim3;

ivec2 bicubicTexDim(vec2 d)
{
    return max(ivec2(int(d.x + 0.5), int(d.y + 0.5)), ivec2(1));
}

vec4 bicubicFetch(sampler2D s, ivec2 c, ivec2 dim)
{
    c = clamp(c, ivec2(0), dim - ivec2(1));
    return texelFetch(s, c, 0);
}

vec4 catmullRomCoeffs(float u)
{
    float u2 = u * u;
    float u3 = u2 * u;
    return 0.5 * vec4(
        -u3 + 2.0 * u2 - u,
        3.0 * u3 - 5.0 * u2 + 2.0,
        -3.0 * u3 + 4.0 * u2 + u,
        u3 - u2);
}

vec4 textureBicubicVec4(sampler2D s, vec2 uv, vec2 texDim)
{
    uv = clamp(uv, 0.0, 1.0);
    ivec2 dim = bicubicTexDim(texDim);
    vec2 t = uv * vec2(dim) - vec2(0.5);
    ivec2 ip = ivec2(floor(t));
    vec2 f = t - vec2(ip);
    vec4 wx = catmullRomCoeffs(f.x);
    vec4 wy = catmullRomCoeffs(f.y);
    float wx0 = wx.x;
    float wx1 = wx.y;
    float wx2 = wx.z;
    float wx3 = wx.w;
    float wy0 = wy.x;
    float wy1 = wy.y;
    float wy2 = wy.z;
    float wy3 = wy.w;

    vec4 acc = vec4(0.0);
    float wsum = 0.0;

    acc += bicubicFetch(s, ip + ivec2(-1, -1), dim) * (wx0 * wy0);
    wsum += wx0 * wy0;
    acc += bicubicFetch(s, ip + ivec2(0, -1), dim) * (wx1 * wy0);
    wsum += wx1 * wy0;
    acc += bicubicFetch(s, ip + ivec2(1, -1), dim) * (wx2 * wy0);
    wsum += wx2 * wy0;
    acc += bicubicFetch(s, ip + ivec2(2, -1), dim) * (wx3 * wy0);
    wsum += wx3 * wy0;

    acc += bicubicFetch(s, ip + ivec2(-1, 0), dim) * (wx0 * wy1);
    wsum += wx0 * wy1;
    acc += bicubicFetch(s, ip + ivec2(0, 0), dim) * (wx1 * wy1);
    wsum += wx1 * wy1;
    acc += bicubicFetch(s, ip + ivec2(1, 0), dim) * (wx2 * wy1);
    wsum += wx2 * wy1;
    acc += bicubicFetch(s, ip + ivec2(2, 0), dim) * (wx3 * wy1);
    wsum += wx3 * wy1;

    acc += bicubicFetch(s, ip + ivec2(-1, 1), dim) * (wx0 * wy2);
    wsum += wx0 * wy2;
    acc += bicubicFetch(s, ip + ivec2(0, 1), dim) * (wx1 * wy2);
    wsum += wx1 * wy2;
    acc += bicubicFetch(s, ip + ivec2(1, 1), dim) * (wx2 * wy2);
    wsum += wx2 * wy2;
    acc += bicubicFetch(s, ip + ivec2(2, 1), dim) * (wx3 * wy2);
    wsum += wx3 * wy2;

    acc += bicubicFetch(s, ip + ivec2(-1, 2), dim) * (wx0 * wy3);
    wsum += wx0 * wy3;
    acc += bicubicFetch(s, ip + ivec2(0, 2), dim) * (wx1 * wy3);
    wsum += wx1 * wy3;
    acc += bicubicFetch(s, ip + ivec2(1, 2), dim) * (wx2 * wy3);
    wsum += wx2 * wy3;
    acc += bicubicFetch(s, ip + ivec2(2, 2), dim) * (wx3 * wy3);
    wsum += wx3 * wy3;

    return acc / max(wsum, 1e-5);
}

float textureBicubicR(sampler2D s, vec2 uv, vec2 texDim)
{
    return textureBicubicVec4(s, uv, texDim).r;
}
