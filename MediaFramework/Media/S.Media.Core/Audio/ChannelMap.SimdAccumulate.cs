using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace S.Media.Core.Audio;

/// <summary>
/// SIMD-accelerated fast paths for <see cref="ChannelMap.ApplyAdditive"/>. Split out into a partial
/// to keep the public API surface of <c>ChannelMap.cs</c> scannable; everything in this file is
/// internal/private and only invoked from the dispatcher in <c>ChannelMap.cs</c>.
/// </summary>
public readonly partial struct ChannelMap
{
    /// <summary>
    /// Stereo → stereo where both outputs are <see cref="Silence"/> (<c>[-1,-1]</c>). Additive no-op.
    /// </summary>
    internal static bool TryAccumulateStereoFullSilenceStereoInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        _ = src;
        _ = dst;
        _ = samplesPerChannel;
        _ = uniformGain;

        var routing = map.AsSpan();
        if (routing.Length != 2 || dstChannels != 2 || srcChannels < 1)
            return false;
        if (routing[0] != Silence || routing[1] != Silence)
            return false;

        return map.RequiredInputChannels == 0;
    }

    /// <summary>
    /// Wider interleaved source → stereo: take one consecutive L/R pair per frame (<c>[p,p+1]</c> with <c>p ≥ 0</c>),
    /// <see cref="RequiredInputChannels"/> == <c>p + 2</c>, and <c>srcChannels</c> strictly wider than stereo when <c>p == 0</c>
    /// (so <see cref="TryAccumulateStereoIdentityInterleaved"/> still owns true 2×2 identity). Uses an AVX2 permute when
    /// <c>srcChannels == 4</c>, <c>p + 1 &lt; 4</c> (both samples of the pair lie in one quad frame), and at least two output samples remain;
    /// otherwise a compact per-frame scalar loop (still avoids the generic map loop).
    /// </summary>
    internal static unsafe bool TryAccumulateWideSourceStereoConsecutivePairInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        var routing = map.AsSpan();
        if (routing.Length != 2 || dstChannels != 2 || srcChannels < 1)
            return false;

        var p = routing[0];
        if (routing[1] != p + 1)
            return false;

        if (p < 0)
            return false;

        if (map.RequiredInputChannels != p + 2)
            return false;

        if (srcChannels < map.RequiredInputChannels)
            return false;

        // True stereo identity [0,1] @ 2ch is handled earlier by TryAccumulateStereoIdentityInterleaved.
        if (p == 0 && srcChannels == 2)
            return false;

        if (uniformGain == 0f)
            return true;

        if (Avx2.IsSupported && srcChannels == 4 && p + 1 < 4 && samplesPerChannel >= 2)
        {
            var idx = Vector256.Create(p, p + 1, p + 4, p + 5, p, p + 1, p + 4, p + 5);
            var g128 = Vector128.Create(uniformGain);
            var s = 0;
            var limit = samplesPerChannel - samplesPerChannel % 2;
            for (; s < limit; s += 2)
            {
                fixed (float* pSrc = src.Slice(s * 4))
                fixed (float* pDst = dst.Slice(s * 2))
                {
                    var vec = Avx.LoadVector256(pSrc);
                    var perm = Avx2.PermuteVar8x32(vec, idx);
                    var stereo4 = perm.GetLower();
                    var scaled = Sse.Multiply(stereo4, g128);
                    var acc = Sse.LoadVector128(pDst);
                    Sse.Store(pDst, Sse.Add(acc, scaled));
                }
            }

            for (; s < samplesPerChannel; s++)
            {
                var sb = s * srcChannels;
                var db = s * 2;
                dst[db] += src[sb + p] * uniformGain;
                dst[db + 1] += src[sb + p + 1] * uniformGain;
            }

            return true;
        }

        for (var s = 0; s < samplesPerChannel; s++)
        {
            var sb = s * srcChannels;
            var db = s * 2;
            dst[db] += src[sb + p] * uniformGain;
            dst[db + 1] += src[sb + p + 1] * uniformGain;
        }

        return true;
    }

    /// <summary>
    /// Wider interleaved source → stereo duplicate of one channel (<c>[p,p]</c>), <see cref="RequiredInputChannels"/> == <c>p + 1</c>.
    /// Excludes <c>srcChannels == 2</c> with <c>p ∈ {0,1}</c> so <see cref="TryAccumulateStereoDupSingleChannelInterleaved"/> keeps stereo SIMD dup.
    /// Uses an AVX2 permute when <c>srcChannels == 4</c>, <c>p &lt; 4</c>, and at least two samples remain; otherwise a compact per-frame scalar loop.
    /// </summary>
    internal static unsafe bool TryAccumulateWideSourceSingleChannelDupStereoInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        var routing = map.AsSpan();
        if (routing.Length != 2 || dstChannels != 2 || srcChannels < 1)
            return false;

        var p = routing[0];
        if (routing[1] != p)
            return false;

        if (p < 0)
            return false;

        if (map.RequiredInputChannels != p + 1)
            return false;

        if (srcChannels < map.RequiredInputChannels)
            return false;

        if (srcChannels == 2 && p <= 1)
            return false;

        if (uniformGain == 0f)
            return true;

        if (Avx2.IsSupported && srcChannels == 4 && p < 4 && samplesPerChannel >= 2)
        {
            var idx = Vector256.Create(p, p, p + 4, p + 4, p, p, p + 4, p + 4);
            var g128 = Vector128.Create(uniformGain);
            var s = 0;
            var limit = samplesPerChannel - samplesPerChannel % 2;
            for (; s < limit; s += 2)
            {
                fixed (float* pSrc = src.Slice(s * 4))
                fixed (float* pDst = dst.Slice(s * 2))
                {
                    var vec = Avx.LoadVector256(pSrc);
                    var perm = Avx2.PermuteVar8x32(vec, idx);
                    var stereo4 = perm.GetLower();
                    var scaled = Sse.Multiply(stereo4, g128);
                    var acc = Sse.LoadVector128(pDst);
                    Sse.Store(pDst, Sse.Add(acc, scaled));
                }
            }

            for (; s < samplesPerChannel; s++)
            {
                var sb = s * srcChannels;
                var db = s * 2;
                var v = src[sb + p] * uniformGain;
                dst[db] += v;
                dst[db + 1] += v;
            }

            return true;
        }

        for (var s = 0; s < samplesPerChannel; s++)
        {
            var sb = s * srcChannels;
            var db = s * 2;
            var v = src[sb + p] * uniformGain;
            dst[db] += v;
            dst[db + 1] += v;
        }

        return true;
    }

    /// <summary>
    /// SIMD fast path for stereo → stereo duplicate of one channel: <c>[0,0]</c> (L to both) or <c>[1,1]</c> (R to both).
    /// </summary>
    internal static bool TryAccumulateStereoDupSingleChannelInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated)
            return false;

        var routing = map.AsSpan();
        if (routing.Length != 2 || srcChannels != 2 || dstChannels != 2)
            return false;

        var dupLeft = false;
        if (routing[0] == 0 && routing[1] == 0)
            dupLeft = true;
        else if (routing[0] == 1 && routing[1] == 1)
            dupLeft = false;
        else
            return false;

        var required = dupLeft ? 1 : 2;
        if (map.RequiredInputChannels != required)
            return false;

        if (uniformGain == 0f)
            return true;

        var vn = Vector<float>.Count;
        if ((vn % 2) != 0)
            return false;

        var floats = samplesPerChannel * 2;
        if (floats < vn)
            return false;

        var limit = floats - floats % vn;
        var g = new Vector<float>(uniformGain);
        var i = 0;
        for (; i < limit; i += vn)
        {
            var sin = MemoryMarshal.Cast<float, Vector<float>>(src.Slice(i, vn))[0];
            var duped = dupLeft ? StereoDupLeftAdjacent(sin) : StereoDupRightAdjacent(sin);
            var acc = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(i, vn))[0];
            MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(i, vn))[0] = acc + duped * g;
        }

        for (; i < floats; i += 2)
        {
            var v = (dupLeft ? src[i] : src[i + 1]) * uniformGain;
            dst[i] += v;
            dst[i + 1] += v;
        }

        return true;
    }

    /// <summary>
    /// Stereo → one output channel: <c>[0]</c> (left only) or <c>[1]</c> (right only). Narrows the scalar fallback for
    /// mismatched <c>srcChannels</c>/<c>dstChannels</c> when hardware supports AVX2 permutes; otherwise uses a compact scalar loop.
    /// </summary>
    internal static unsafe bool TryAccumulateStereoToMonoSingleOutputInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        var routing = map.AsSpan();
        if (routing.Length != 1 || srcChannels != 2 || dstChannels != 1)
            return false;

        var pick = routing[0];
        if (pick is not (0 or 1))
            return false;

        if (map.RequiredInputChannels > 2)
            return false;

        if (uniformGain == 0f)
            return true;

        if (Avx2.IsSupported && samplesPerChannel >= 8)
        {
            var idx = pick == 0
                ? Vector256.Create(0, 2, 4, 6, 0, 0, 0, 0)
                : Vector256.Create(1, 3, 5, 7, 1, 1, 1, 1);
            var g128 = Vector128.Create(uniformGain);
            var s = 0;
            var limit = samplesPerChannel - samplesPerChannel % 8;
            for (; s < limit; s += 8)
            {
                fixed (float* pSrc = src.Slice(s * 2))
                fixed (float* pDst = dst.Slice(s))
                {
                    for (var b = 0; b < 2; b++)
                    {
                        var vec = Avx.LoadVector256(pSrc + b * 8);
                        var perm = Avx2.PermuteVar8x32(vec, idx);
                        var lane = perm.GetLower();
                        var scaled = Sse.Multiply(lane, g128);
                        var acc = Sse.LoadVector128(pDst + b * 4);
                        Sse.Store(pDst + b * 4, Sse.Add(acc, scaled));
                    }
                }
            }

            for (; s < samplesPerChannel; s++)
                dst[s] += src[s * 2 + pick] * uniformGain;

            return true;
        }

        for (var s = 0; s < samplesPerChannel; s++)
            dst[s] += src[s * 2 + pick] * uniformGain;

        return true;
    }

    /// <summary>
    /// Wider interleaved source → mono: single packed channel <c>[p]</c>, <see cref="RequiredInputChannels"/> == <c>p + 1</c>.
    /// Excludes <c>srcChannels == 2</c> with <c>p ∈ {0,1}</c> so <see cref="TryAccumulateStereoToMonoSingleOutputInterleaved"/> keeps stereo SIMD downmix.
    /// </summary>
    internal static bool TryAccumulateWideSourceMonoSingleOutputInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        var routing = map.AsSpan();
        if (routing.Length != 1 || dstChannels != 1 || srcChannels < 1)
            return false;

        var p = routing[0];
        if (p < 0)
            return false;

        if (map.RequiredInputChannels != p + 1)
            return false;

        if (srcChannels < map.RequiredInputChannels)
            return false;

        if (srcChannels == 2 && p <= 1)
            return false;

        if (uniformGain == 0f)
            return true;

        for (var s = 0; s < samplesPerChannel; s++)
            dst[s] += src[s * srcChannels + p] * uniformGain;

        return true;
    }

    /// <summary>
    /// SIMD fast path for 2‑channel interleaved stereo (identity or swapped L/R) with uniform gain.
    /// </summary>
    internal static bool TryAccumulateStereoIdentityInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var routing = map.AsSpan();
        if (routing.Length != 2 || map.RequiredInputChannels != 2 || srcChannels != 2 || dstChannels != 2)
            return false;

        var swapStereo = false;
        if (routing[0] == 0 && routing[1] == 1)
            swapStereo = false;
        else if (routing[0] == 1 && routing[1] == 0)
            swapStereo = true;
        else
            return false;

        var vn = Vector<float>.Count;
        if ((vn % 2) != 0)
            return false;

        var floats = samplesPerChannel * 2;
        if (floats < vn)
            return false;

        var limit = floats - floats % vn;
        var g = new Vector<float>(uniformGain);
        var i = 0;
        for (; i < limit; i += vn)
        {
            var sin = MemoryMarshal.Cast<float, Vector<float>>(src.Slice(i, vn))[0];
            if (swapStereo)
                sin = StereoSwapAdjacentChannels(sin);
            var acc = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(i, vn))[0];
            MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(i, vn))[0] = acc + sin * g;
        }

        for (; i < floats; i += 2)
        {
            dst[i] += (swapStereo ? src[i + 1] : src[i]) * uniformGain;
            dst[i + 1] += (swapStereo ? src[i] : src[i + 1]) * uniformGain;
        }

        return true;
    }

    /// <summary>
    /// Stereo → N where every route is <see cref="Silence"/>, L (<c>0</c>), or R (<c>1</c>), with at least one silence
    /// (e.g. <c>[-1,0,1,0]</c>). Additive: silent outputs are left unchanged.
    /// </summary>
    internal static bool TryAccumulateStereoSilenceOrZeroDupInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var routing = map.AsSpan();
        var nOut = dstChannels;
        if (srcChannels != 2 || nOut < 2)
            return false;
        if (routing.Length != nOut)
            return false;
        if (map.RequiredInputChannels > 2)
            return false;

        var hasSilence = false;
        for (var i = 0; i < nOut; i++)
        {
            var c = routing[i];
            if (c == Silence)
            {
                hasSilence = true;
                continue;
            }

            if (c is not (0 or 1))
                return false;
        }

        if (!hasSilence)
            return false;

        return RunStereoSilenceOrZeroDupNPooled(src, dst, routing, samplesPerChannel, uniformGain);
    }

    /// <summary>
    /// Mono → N where every route is <see cref="Silence"/> or source channel <c>0</c>, with at least one silence
    /// (e.g. <c>[-1,0,0,-1]</c>). Additive: silent outputs are left unchanged.
    /// </summary>
    internal static bool TryAccumulateMonoSilenceOrZeroDupInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var routing = map.AsSpan();
        var nOut = dstChannels;
        if (srcChannels != 1 || nOut < 2)
            return false;
        if (routing.Length != nOut)
            return false;
        if (map.RequiredInputChannels != 1)
            return false;

        var hasSilence = false;
        for (var i = 0; i < nOut; i++)
        {
            var c = routing[i];
            if (c == Silence)
            {
                hasSilence = true;
                continue;
            }

            if (c != 0)
                return false;
        }

        if (!hasSilence)
            return false;

        return RunMonoDupNPooled(src, dst, routing, samplesPerChannel, uniformGain);
    }

    private static void RunMonoSilenceOrZeroDupVector(
        ReadOnlySpan<float> src, Span<float> dst, ReadOnlySpan<int> routing,
        int samplesPerChannel, Vector<float> gVec, float uniformGain, Span<float> scratch)
    {
        var nOut = routing.Length;
        var vn = Vector<float>.Count;
        var need = vn * nOut;
        var s = 0;
        var limit = samplesPerChannel - samplesPerChannel % vn;
        for (; s < limit; s += vn)
        {
            var m = MemoryMarshal.Cast<float, Vector<float>>(src.Slice(s, vn))[0] * gVec;
            ref var sc = ref MemoryMarshal.GetReference(scratch);
            for (var j = 0; j < vn; j++)
            {
                var v = m[j];
                var baseIdx = j * nOut;
                for (var k = 0; k < nOut; k++)
                    Unsafe.Add(ref sc, baseIdx + k) = routing[k] < 0 ? 0f : v;
            }

            var dstSlice = dst.Slice(s * nOut, need);
            for (var off = 0; off < need; off += vn)
            {
                var dVec = MemoryMarshal.Cast<float, Vector<float>>(dstSlice.Slice(off, vn))[0];
                var sVec = MemoryMarshal.Cast<float, Vector<float>>(scratch.Slice(off, vn))[0];
                MemoryMarshal.Cast<float, Vector<float>>(dstSlice.Slice(off, vn))[0] = dVec + sVec;
            }
        }

        for (; s < samplesPerChannel; s++)
        {
            var v = src[s] * uniformGain;
            var b = s * nOut;
            for (var k = 0; k < nOut; k++)
            {
                if (routing[k] >= 0)
                    dst[b + k] += v;
            }
        }
    }

    private static bool RunMonoDupNPooled(
        ReadOnlySpan<float> src, Span<float> dst, ReadOnlySpan<int> routing,
        int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var nOut = routing.Length;
        var vn = Vector<float>.Count;
        var need = checked(vn * nOut);
        if (need > PooledMonoDupScratchMaxFloats)
            return false;

        var g = new Vector<float>(uniformGain);
        if (need <= AccumulateInterleaveScratchCap)
        {
            Span<float> scratch = stackalloc float[need];
            RunMonoSilenceOrZeroDupVector(src, dst, routing, samplesPerChannel, g, uniformGain, scratch);
            return true;
        }

        var rented = ArrayPool<float>.Shared.Rent(need);
        try
        {
            RunMonoSilenceOrZeroDupVector(src, dst, routing, samplesPerChannel, g, uniformGain, rented.AsSpan(0, need));
            return true;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    private static void RunStereoToNVector(
        ReadOnlySpan<float> src, Span<float> dst, int nOut, int samplesPerChannel, float uniformGain,
        Span<float> scratch)
    {
        var vn = Vector<float>.Count;
        var need = vn * nOut;
        var s = 0;
        var limit = samplesPerChannel - samplesPerChannel % vn;
        for (; s < limit; s += vn)
        {
            for (var j = 0; j < vn; j++)
            {
                var L = src[(s + j) * 2] * uniformGain;
                var R = src[(s + j) * 2 + 1] * uniformGain;
                var vo = j * nOut;
                for (var k = 0; k < nOut; k++)
                    scratch[vo + k] = (k & 1) == 0 ? L : R;
            }

            var dstSlice = dst.Slice(s * nOut, need);
            for (var off = 0; off < need; off += vn)
            {
                var dVec = MemoryMarshal.Cast<float, Vector<float>>(dstSlice.Slice(off, vn))[0];
                var sVec = MemoryMarshal.Cast<float, Vector<float>>(scratch.Slice(off, vn))[0];
                MemoryMarshal.Cast<float, Vector<float>>(dstSlice.Slice(off, vn))[0] = dVec + sVec;
            }
        }

        for (; s < samplesPerChannel; s++)
        {
            var L = src[s * 2] * uniformGain;
            var R = src[s * 2 + 1] * uniformGain;
            var b = s * nOut;
            for (var k = 0; k < nOut; k++)
                dst[b + k] += (k & 1) == 0 ? L : R;
        }
    }

    private static void RunStereoToNVectorSwapped(
        ReadOnlySpan<float> src, Span<float> dst, int nOut, int samplesPerChannel, float uniformGain,
        Span<float> scratch)
    {
        var vn = Vector<float>.Count;
        var need = vn * nOut;
        var s = 0;
        var limit = samplesPerChannel - samplesPerChannel % vn;
        for (; s < limit; s += vn)
        {
            for (var j = 0; j < vn; j++)
            {
                var L = src[(s + j) * 2] * uniformGain;
                var R = src[(s + j) * 2 + 1] * uniformGain;
                var vo = j * nOut;
                for (var k = 0; k < nOut; k++)
                    scratch[vo + k] = (k & 1) == 0 ? R : L;
            }

            var dstSlice = dst.Slice(s * nOut, need);
            for (var off = 0; off < need; off += vn)
            {
                var dVec = MemoryMarshal.Cast<float, Vector<float>>(dstSlice.Slice(off, vn))[0];
                var sVec = MemoryMarshal.Cast<float, Vector<float>>(scratch.Slice(off, vn))[0];
                MemoryMarshal.Cast<float, Vector<float>>(dstSlice.Slice(off, vn))[0] = dVec + sVec;
            }
        }

        for (; s < samplesPerChannel; s++)
        {
            var L = src[s * 2] * uniformGain;
            var R = src[s * 2 + 1] * uniformGain;
            var b = s * nOut;
            for (var k = 0; k < nOut; k++)
                dst[b + k] += (k & 1) == 0 ? R : L;
        }
    }

    private static bool RunStereoToNPooled(
        ReadOnlySpan<float> src, Span<float> dst, int nOut, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var vn = Vector<float>.Count;
        if ((vn % 2) != 0)
            return false;

        var need = checked(vn * nOut);
        if (need > PooledMonoDupScratchMaxFloats)
            return false;

        if (need <= AccumulateInterleaveScratchCap)
        {
            Span<float> scratch = stackalloc float[need];
            RunStereoToNVector(src, dst, nOut, samplesPerChannel, uniformGain, scratch);
            return true;
        }

        var rented = ArrayPool<float>.Shared.Rent(need);
        try
        {
            RunStereoToNVector(src, dst, nOut, samplesPerChannel, uniformGain, rented.AsSpan(0, need));
            return true;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    private static bool RunStereoToNPooledSwapped(
        ReadOnlySpan<float> src, Span<float> dst, int nOut, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var vn = Vector<float>.Count;
        if ((vn % 2) != 0)
            return false;

        var need = checked(vn * nOut);
        if (need > PooledMonoDupScratchMaxFloats)
            return false;

        if (need <= AccumulateInterleaveScratchCap)
        {
            Span<float> scratch = stackalloc float[need];
            RunStereoToNVectorSwapped(src, dst, nOut, samplesPerChannel, uniformGain, scratch);
            return true;
        }

        var rented = ArrayPool<float>.Shared.Rent(need);
        try
        {
            RunStereoToNVectorSwapped(src, dst, nOut, samplesPerChannel, uniformGain, rented.AsSpan(0, need));
            return true;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    private static void RunStereoSilenceOrZeroDupVector(
        ReadOnlySpan<float> src, Span<float> dst, ReadOnlySpan<int> routing,
        int samplesPerChannel, float uniformGain, Span<float> scratch)
    {
        var nOut = routing.Length;
        var vn = Vector<float>.Count;
        var need = vn * nOut;
        var s = 0;
        var limit = samplesPerChannel - samplesPerChannel % vn;
        for (; s < limit; s += vn)
        {
            for (var j = 0; j < vn; j++)
            {
                var L = src[(s + j) * 2] * uniformGain;
                var R = src[(s + j) * 2 + 1] * uniformGain;
                var vo = j * nOut;
                for (var k = 0; k < nOut; k++)
                {
                    var ri = routing[k];
                    scratch[vo + k] = ri < 0 ? 0f : (ri == 0 ? L : R);
                }
            }

            var dstSlice = dst.Slice(s * nOut, need);
            for (var off = 0; off < need; off += vn)
            {
                var dVec = MemoryMarshal.Cast<float, Vector<float>>(dstSlice.Slice(off, vn))[0];
                var sVec = MemoryMarshal.Cast<float, Vector<float>>(scratch.Slice(off, vn))[0];
                MemoryMarshal.Cast<float, Vector<float>>(dstSlice.Slice(off, vn))[0] = dVec + sVec;
            }
        }

        for (; s < samplesPerChannel; s++)
        {
            var L = src[s * 2] * uniformGain;
            var R = src[s * 2 + 1] * uniformGain;
            var b = s * nOut;
            for (var k = 0; k < nOut; k++)
            {
                var ri = routing[k];
                if (ri >= 0)
                    dst[b + k] += ri == 0 ? L : R;
            }
        }
    }

    private static bool RunStereoSilenceOrZeroDupNPooled(
        ReadOnlySpan<float> src, Span<float> dst, ReadOnlySpan<int> routing,
        int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var nOut = routing.Length;
        var vn = Vector<float>.Count;
        if ((vn % 2) != 0)
            return false;

        var need = checked(vn * nOut);
        if (need > PooledMonoDupScratchMaxFloats)
            return false;

        if (need <= AccumulateInterleaveScratchCap)
        {
            Span<float> scratch = stackalloc float[need];
            RunStereoSilenceOrZeroDupVector(src, dst, routing, samplesPerChannel, uniformGain, scratch);
            return true;
        }

        var rented = ArrayPool<float>.Shared.Rent(need);
        try
        {
            RunStereoSilenceOrZeroDupVector(src, dst, routing, samplesPerChannel, uniformGain, rented.AsSpan(0, need));
            return true;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    /// <summary>Mono packed as one float per frame → duplicated stereo interleaved (<c>[0,0]</c>), uniform gain.</summary>
    internal static unsafe bool TryAccumulateMonoDupStereoInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var routing = map.AsSpan();
        if (routing.Length != 2 || srcChannels != 1 || dstChannels != 2) return false;
        if (routing[0] != 0 || routing[1] != 0 || map.RequiredInputChannels != 1) return false;

        var vn = Vector<float>.Count;

        var g = new Vector<float>(uniformGain);

        var s = 0;
        var limit = samplesPerChannel - samplesPerChannel % vn;

        if (Avx.IsSupported && vn == 16)
        {
            var g256 = Vector256.Create(uniformGain);
            var limit16 = samplesPerChannel - samplesPerChannel % 16;
            for (; s < limit16; s += 16)
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    var off = pass * 8;
                    fixed (float* pSrc = src.Slice(s + off))
                    fixed (float* pDst = dst.Slice((s + off) * 2))
                    {
                        var m = Avx.Multiply(Avx.LoadVector256(pSrc), g256);
                        var lo = m.GetLower();
                        var hi = m.GetUpper();
                        var d0 = Sse.UnpackLow(lo, lo);
                        var d1 = Sse.UnpackHigh(lo, lo);
                        var d2 = Sse.UnpackLow(hi, hi);
                        var d3 = Sse.UnpackHigh(hi, hi);
                        Sse.Store(pDst, Sse.Add(Sse.LoadVector128(pDst), d0));
                        Sse.Store(pDst + 4, Sse.Add(Sse.LoadVector128(pDst + 4), d1));
                        Sse.Store(pDst + 8, Sse.Add(Sse.LoadVector128(pDst + 8), d2));
                        Sse.Store(pDst + 12, Sse.Add(Sse.LoadVector128(pDst + 12), d3));
                    }
                }
            }
        }
        else if (Avx.IsSupported && vn == 8)
        {
            var g256 = Vector256.Create(uniformGain);
            for (; s < limit; s += 8)
            {
                fixed (float* pSrc = src.Slice(s))
                fixed (float* pDst = dst.Slice(s * 2))
                {
                    var m = Avx.Multiply(Avx.LoadVector256(pSrc), g256);
                    var lo = m.GetLower();
                    var hi = m.GetUpper();
                    var d0 = Sse.UnpackLow(lo, lo);
                    var d1 = Sse.UnpackHigh(lo, lo);
                    var d2 = Sse.UnpackLow(hi, hi);
                    var d3 = Sse.UnpackHigh(hi, hi);
                    Sse.Store(pDst, Sse.Add(Sse.LoadVector128(pDst), d0));
                    Sse.Store(pDst + 4, Sse.Add(Sse.LoadVector128(pDst + 4), d1));
                    Sse.Store(pDst + 8, Sse.Add(Sse.LoadVector128(pDst + 8), d2));
                    Sse.Store(pDst + 12, Sse.Add(Sse.LoadVector128(pDst + 12), d3));
                }
            }
        }
        else if (Sse.IsSupported && vn == 4)
        {
            var g128 = Vector128.Create(uniformGain);
            for (; s < limit; s += 4)
            {
                fixed (float* pSrc = src.Slice(s))
                fixed (float* pDst = dst.Slice(s * 2))
                {
                    var m = Sse.Multiply(Sse.LoadVector128(pSrc), g128);
                    var dupLo = Sse.UnpackLow(m, m);
                    var dupHi = Sse.UnpackHigh(m, m);
                    Sse.Store(pDst, Sse.Add(Sse.LoadVector128(pDst), dupLo));
                    Sse.Store(pDst + 4, Sse.Add(Sse.LoadVector128(pDst + 4), dupHi));
                }
            }
        }
        else
        {
            Span<float> scratch = stackalloc float[vn * 2];
            for (; s < limit; s += vn)
            {
                var m = MemoryMarshal.Cast<float, Vector<float>>(src.Slice(s, vn))[0] * g;
                ref var sc = ref MemoryMarshal.GetReference(scratch);
                for (var j = 0; j < vn; j++)
                {
                    var v = m[j];
                    Unsafe.Add(ref sc, 2 * j) = v;
                    Unsafe.Add(ref sc, 2 * j + 1) = v;
                }

                var dstBase = s * 2;
                var d0 = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase, vn))[0];
                var s0 = MemoryMarshal.Cast<float, Vector<float>>(scratch[..vn])[0];
                MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase, vn))[0] = d0 + s0;

                var d1 = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase + vn, vn))[0];
                var s1 = MemoryMarshal.Cast<float, Vector<float>>(scratch.Slice(vn, vn))[0];
                MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase + vn, vn))[0] = d1 + s1;
            }
        }

        for (; s < samplesPerChannel; s++)
        {
            var v = src[s] * uniformGain;
            var b = s * 2;
            dst[b] += v;
            dst[b + 1] += v;
        }

        return true;
    }

    /// <summary>Mono → N (every output maps to source channel 0), <c>N ≥ 3</c>, uniform gain.</summary>
    internal static bool TryAccumulateMonoDupNInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var routing = map.AsSpan();
        var nOut = dstChannels;
        if (srcChannels != 1 || nOut < 3)
            return false;
        if (routing.Length != nOut)
            return false;
        for (var i = 0; i < nOut; i++)
        {
            if (routing[i] != 0)
                return false;
        }

        if (map.RequiredInputChannels != 1)
            return false;

        return RunMonoDupNPooled(src, dst, routing, samplesPerChannel, uniformGain);
    }

    /// <summary>
    /// Stereo interleaved → N outputs with <c>map[i] = 1 - (i &amp; 1)</c> (R,L,R,L,… - swapped <see cref="StereoToN"/>).
    /// For <c>N == 4</c> and <c>[1,0,1,0]</c>, prefer <see cref="TryAccumulateStereoDuplexWideSwappedInterleaved"/> (call it first).
    /// </summary>
    internal static bool TryAccumulateStereoToNInterleavedSwapped(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var routing = map.AsSpan();
        var nOut = dstChannels;
        if (srcChannels != 2 || nOut < 3)
            return false;
        if (routing.Length != nOut)
            return false;

        for (var i = 0; i < nOut; i++)
        {
            if (routing[i] != 1 - (i & 1))
                return false;
        }

        if (map.RequiredInputChannels != 2)
            return false;

        return RunStereoToNPooledSwapped(src, dst, nOut, samplesPerChannel, uniformGain);
    }

    /// <summary>
    /// Stereo interleaved → N outputs with <c>map[i] = i &amp; 1</c> (see <see cref="StereoToN"/>), <c>N ≥ 3</c>.
    /// For <c>N == 4</c> and <c>[0,1,0,1]</c>, prefer <see cref="TryAccumulateStereoDuplexWideInterleaved"/> (call it first).
    /// </summary>
    internal static bool TryAccumulateStereoToNInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var routing = map.AsSpan();
        var nOut = dstChannels;
        if (srcChannels != 2 || nOut < 3)
            return false;
        if (routing.Length != nOut)
            return false;

        for (var i = 0; i < nOut; i++)
        {
            if (routing[i] != (i & 1))
                return false;
        }

        if (map.RequiredInputChannels != 2)
            return false;

        return RunStereoToNPooled(src, dst, nOut, samplesPerChannel, uniformGain);
    }

    /// <summary>
    /// Packed interleaved identity: <c>map[i] == i</c> for every output, and
    /// <c>srcChannels == dstChannels == OutputChannels</c> (e.g. 5.1 passthrough into a same-width output).
    /// </summary>
    internal static bool TryAccumulatePackedIdentityInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var routing = map.AsSpan();
        var ch = routing.Length;
        if (ch < 3 || srcChannels != ch || dstChannels != ch)
            return false;

        for (var i = 0; i < ch; i++)
        {
            if (routing[i] != i)
                return false;
        }

        if (map.RequiredInputChannels != ch)
            return false;

        var total = samplesPerChannel * ch;
        if (src.Length < total || dst.Length < total)
            return false;

        var vn = Vector<float>.Count;
        var g = new Vector<float>(uniformGain);
        var iFloat = 0;
        var limit = total - total % vn;
        for (; iFloat < limit; iFloat += vn)
        {
            var sin = MemoryMarshal.Cast<float, Vector<float>>(src.Slice(iFloat, vn))[0];
            var acc = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(iFloat, vn))[0];
            MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(iFloat, vn))[0] = acc + sin * g;
        }

        for (; iFloat < total; iFloat++)
            dst[iFloat] += src[iFloat] * uniformGain;

        return true;
    }

    /// <summary>
    /// Packed interleaved same-width gather: <c>srcChannels == dstChannels == N</c> for <c>N ∈ {3, 4, 5, 6, 7, 8}</c>,
    /// each <c>map[i] ∈ {0,..,N-1}</c> (no silence), uniform gain (any finite value). Bijective permutations and
    /// duplicate-lane gathers (multiple outputs read the same source channel) share the same shuffle / <c>PermuteVar8x32</c> kernels.
    /// </summary>
    internal static unsafe bool TryAccumulatePackedPermutationInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f || !float.IsFinite(uniformGain))
            return false;

        var routing = map.AsSpan();
        var ch = routing.Length;
        if (ch is not (3 or 4 or 5 or 6 or 7 or 8) || srcChannels != ch || dstChannels != ch)
            return false;

        if (!IsPackedGatherIndicesInRange(routing, ch))
            return false;

        var total = samplesPerChannel * ch;
        if (src.Length < total || dst.Length < total)
            return false;

        if (ch is 3 or 5 or 6 or 7)
        {
            // Odd widths don't fill a hardware vector. The previous "SIMD" kernels here
            // round-tripped every frame through a stack pad (four copies per frame) and
            // benchmarked SLOWER than scalar (6ch permutation: 9.9 us vs ~2 us per 480-frame
            // chunk) - a tight hoisted gather loop is the honest fast path for these shapes.
            for (var s = 0; s < samplesPerChannel; s++)
            {
                var off = s * ch;
                for (var c = 0; c < ch; c++)
                    dst[off + c] += src[off + routing[c]] * uniformGain;
            }

            return true;
        }

        if (ch == 4)
        {
            if (!Sse.IsSupported)
                return false;

            var imm = BuildShufpsImm4(routing);
            var g = Vector128.Create(uniformGain);
#pragma warning disable CA1857 // SHUFPS immediate is a validated permutation encoding, not a hot-spot literal.
            fixed (float* pSrc = src)
            fixed (float* pDst = dst)
            {
                for (var s = 0; s < samplesPerChannel; s++)
                {
                    var i = s * 4;
                    var v = Sse.LoadVector128(pSrc + i);
                    var p = Sse.Shuffle(v, v, imm);
                    var acc = Sse.LoadVector128(pDst + i);
                    Sse.Store(pDst + i, Sse.Add(acc, Sse.Multiply(p, g)));
                }
            }
#pragma warning restore CA1857

            return true;
        }

        if (!Avx2.IsSupported)
            return false;

        var idx8 = Vector256.Create(
            routing[0], routing[1], routing[2], routing[3],
            routing[4], routing[5], routing[6], routing[7]);
        var g256 = Vector256.Create(uniformGain);
        fixed (float* pSrc = src)
        fixed (float* pDst = dst)
        {
            for (var s = 0; s < samplesPerChannel; s++)
            {
                var off = s * 8;
                var v = Avx.LoadVector256(pSrc + off);
                var p = Avx2.PermuteVar8x32(v, idx8);
                var acc = Avx.LoadVector256(pDst + off);
                Avx.Store(pDst + off, Avx.Add(Avx.Multiply(p, g256), acc));
            }
        }

        return true;
    }

    /// <summary>Every output maps to a non-silence source index in <c>0..n-1</c> (duplicates allowed).</summary>
    private static bool IsPackedGatherIndicesInRange(ReadOnlySpan<int> routing, int n)
    {
        if (routing.Length != n || n > 16)
            return false;

        for (var i = 0; i < n; i++)
        {
            var c = routing[i];
            if (c < 0 || c >= n)
                return false;
        }

        return true;
    }

    private static byte BuildShufpsImm4(ReadOnlySpan<int> map)
    {
        byte imm = 0;
        for (var i = 0; i < 4; i++)
            imm |= (byte)((map[i] & 3) << (i * 2));

        return imm;
    }

    /// <summary>Stereo interleaved duplicated into 4‑channel quad (<c>[0,1,0,1]</c>), uniform gain.</summary>
    internal static unsafe bool TryAccumulateStereoDuplexWideInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var routing = map.AsSpan();
        if (routing.Length != 4 || srcChannels != 2 || dstChannels != 4) return false;
        if (routing[0] != 0 || routing[1] != 1 || routing[2] != 0 || routing[3] != 1) return false;
        if (map.RequiredInputChannels != 2) return false;

        var vn = Vector<float>.Count;
        if ((vn % 2) != 0) return false;

        var stereoFloats = samplesPerChannel * 2;

        var g = new Vector<float>(uniformGain);

        var i = 0;
        var limit = stereoFloats - stereoFloats % vn;

        if (Avx.IsSupported && vn == 16)
        {
            var g256 = Vector256.Create(uniformGain);
            var limit16 = stereoFloats - stereoFloats % 16;
            for (; i < limit16; i += 16)
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    var off = pass * 8;
                    fixed (float* pSrc = src.Slice(i + off))
                    fixed (float* pDst = dst.Slice((i + off) * 2))
                    {
                        var m = Avx.Multiply(Avx.LoadVector256(pSrc), g256);
                        var lo = m.GetLower();
                        var hi = m.GetUpper();
                        var expLo = Vector256.Create(Sse.Shuffle(lo, lo, 0x44), Sse.Shuffle(lo, lo, 0xEE));
                        var expHi = Vector256.Create(Sse.Shuffle(hi, hi, 0x44), Sse.Shuffle(hi, hi, 0xEE));
                        Avx.Store(pDst, Avx.Add(Avx.LoadVector256(pDst), expLo));
                        Avx.Store(pDst + 8, Avx.Add(Avx.LoadVector256(pDst + 8), expHi));
                    }
                }
            }
        }
        else if (Avx.IsSupported && vn == 8)
        {
            var g256 = Vector256.Create(uniformGain);
            for (; i < limit; i += 8)
            {
                fixed (float* pSrc = src.Slice(i))
                fixed (float* pDst = dst.Slice(i * 2))
                {
                    var m = Avx.Multiply(Avx.LoadVector256(pSrc), g256);
                    var lo = m.GetLower();
                    var hi = m.GetUpper();
                    var expLo = Vector256.Create(Sse.Shuffle(lo, lo, 0x44), Sse.Shuffle(lo, lo, 0xEE));
                    var expHi = Vector256.Create(Sse.Shuffle(hi, hi, 0x44), Sse.Shuffle(hi, hi, 0xEE));
                    Avx.Store(pDst, Avx.Add(Avx.LoadVector256(pDst), expLo));
                    Avx.Store(pDst + 8, Avx.Add(Avx.LoadVector256(pDst + 8), expHi));
                }
            }
        }
        else if (Sse.IsSupported && vn == 4)
        {
            var g128 = Vector128.Create(uniformGain);
            for (; i < limit; i += 4)
            {
                fixed (float* pSrc = src.Slice(i))
                fixed (float* pDst = dst.Slice(i * 2))
                {
                    var m = Sse.Multiply(Sse.LoadVector128(pSrc), g128);
                    var e0 = Sse.Shuffle(m, m, 0x44);
                    var e1 = Sse.Shuffle(m, m, 0xEE);
                    Sse.Store(pDst, Sse.Add(Sse.LoadVector128(pDst), e0));
                    Sse.Store(pDst + 4, Sse.Add(Sse.LoadVector128(pDst + 4), e1));
                }
            }
        }
        else
        {
            Span<float> scratch = stackalloc float[vn * 2];
            for (; i < limit; i += vn)
            {
                var m = MemoryMarshal.Cast<float, Vector<float>>(src.Slice(i, vn))[0] * g;
                ref var sc = ref MemoryMarshal.GetReference(scratch);
                for (var j = 0; j < vn; j += 2)
                {
                    var L = m[j];
                    var R = m[j + 1];
                    var p = j * 2;
                    Unsafe.Add(ref sc, p) = L;
                    Unsafe.Add(ref sc, p + 1) = R;
                    Unsafe.Add(ref sc, p + 2) = L;
                    Unsafe.Add(ref sc, p + 3) = R;
                }

                var dstBase = i * 2;
                var d0 = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase, vn))[0];
                var s0 = MemoryMarshal.Cast<float, Vector<float>>(scratch[..vn])[0];
                MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase, vn))[0] = d0 + s0;

                var d1 = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase + vn, vn))[0];
                var s1 = MemoryMarshal.Cast<float, Vector<float>>(scratch.Slice(vn, vn))[0];
                MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase + vn, vn))[0] = d1 + s1;
            }
        }

        for (; i < stereoFloats; i += 2)
        {
            var L = src[i] * uniformGain;
            var R = src[i + 1] * uniformGain;
            var dstBase = i * 2;
            dst[dstBase + 0] += L;
            dst[dstBase + 1] += R;
            dst[dstBase + 2] += L;
            dst[dstBase + 3] += R;
        }

        return true;
    }

    /// <summary>Stereo interleaved duplicated into 4‑channel quad (<c>[0,0,1,1]</c> - L,L,R,R per frame), uniform gain.</summary>
    internal static unsafe bool TryAccumulateStereoDuplexGroupedInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var routing = map.AsSpan();
        if (routing.Length != 4 || srcChannels != 2 || dstChannels != 4) return false;
        if (routing[0] != 0 || routing[1] != 0 || routing[2] != 1 || routing[3] != 1) return false;
        if (map.RequiredInputChannels != 2) return false;

        var vn = Vector<float>.Count;
        if ((vn % 2) != 0) return false;

        var stereoFloats = samplesPerChannel * 2;
        var g = new Vector<float>(uniformGain);

        var i = 0;
        var limit = stereoFloats - stereoFloats % vn;

        if (Avx.IsSupported && vn == 16)
        {
            var g256 = Vector256.Create(uniformGain);
            var limit16 = stereoFloats - stereoFloats % 16;
            for (; i < limit16; i += 16)
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    var off = pass * 8;
                    fixed (float* pSrc = src.Slice(i + off))
                    fixed (float* pDst = dst.Slice((i + off) * 2))
                    {
                        var m = Avx.Multiply(Avx.LoadVector256(pSrc), g256);
                        var lo = m.GetLower();
                        var hi = m.GetUpper();
                        var d0 = Sse.UnpackLow(lo, lo);
                        var d1 = Sse.UnpackHigh(lo, lo);
                        var d2 = Sse.UnpackLow(hi, hi);
                        var d3 = Sse.UnpackHigh(hi, hi);
                        Sse.Store(pDst, Sse.Add(Sse.LoadVector128(pDst), d0));
                        Sse.Store(pDst + 4, Sse.Add(Sse.LoadVector128(pDst + 4), d1));
                        Sse.Store(pDst + 8, Sse.Add(Sse.LoadVector128(pDst + 8), d2));
                        Sse.Store(pDst + 12, Sse.Add(Sse.LoadVector128(pDst + 12), d3));
                    }
                }
            }
        }
        else if (Avx.IsSupported && vn == 8)
        {
            var g256 = Vector256.Create(uniformGain);
            for (; i < limit; i += 8)
            {
                fixed (float* pSrc = src.Slice(i))
                fixed (float* pDst = dst.Slice(i * 2))
                {
                    var m = Avx.Multiply(Avx.LoadVector256(pSrc), g256);
                    var lo = m.GetLower();
                    var hi = m.GetUpper();
                    var d0 = Sse.UnpackLow(lo, lo);
                    var d1 = Sse.UnpackHigh(lo, lo);
                    var d2 = Sse.UnpackLow(hi, hi);
                    var d3 = Sse.UnpackHigh(hi, hi);
                    Sse.Store(pDst, Sse.Add(Sse.LoadVector128(pDst), d0));
                    Sse.Store(pDst + 4, Sse.Add(Sse.LoadVector128(pDst + 4), d1));
                    Sse.Store(pDst + 8, Sse.Add(Sse.LoadVector128(pDst + 8), d2));
                    Sse.Store(pDst + 12, Sse.Add(Sse.LoadVector128(pDst + 12), d3));
                }
            }
        }
        else if (Sse.IsSupported && vn == 4)
        {
            var g128 = Vector128.Create(uniformGain);
            for (; i < limit; i += 4)
            {
                fixed (float* pSrc = src.Slice(i))
                fixed (float* pDst = dst.Slice(i * 2))
                {
                    var m = Sse.Multiply(Sse.LoadVector128(pSrc), g128);
                    var d0 = Sse.UnpackLow(m, m);
                    var d1 = Sse.UnpackHigh(m, m);
                    Sse.Store(pDst, Sse.Add(Sse.LoadVector128(pDst), d0));
                    Sse.Store(pDst + 4, Sse.Add(Sse.LoadVector128(pDst + 4), d1));
                }
            }
        }
        else
        {
            Span<float> scratch = stackalloc float[vn * 2];
            for (; i < limit; i += vn)
            {
                var m = MemoryMarshal.Cast<float, Vector<float>>(src.Slice(i, vn))[0] * g;
                ref var sc = ref MemoryMarshal.GetReference(scratch);
                for (var j = 0; j < vn; j += 2)
                {
                    var L = m[j];
                    var R = m[j + 1];
                    var p = j * 2;
                    Unsafe.Add(ref sc, p) = L;
                    Unsafe.Add(ref sc, p + 1) = L;
                    Unsafe.Add(ref sc, p + 2) = R;
                    Unsafe.Add(ref sc, p + 3) = R;
                }

                var dstBase = i * 2;
                var d0 = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase, vn))[0];
                var s0 = MemoryMarshal.Cast<float, Vector<float>>(scratch[..vn])[0];
                MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase, vn))[0] = d0 + s0;

                var d1 = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase + vn, vn))[0];
                var s1 = MemoryMarshal.Cast<float, Vector<float>>(scratch.Slice(vn, vn))[0];
                MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase + vn, vn))[0] = d1 + s1;
            }
        }

        for (; i < stereoFloats; i += 2)
        {
            var L = src[i] * uniformGain;
            var R = src[i + 1] * uniformGain;
            var dstBase = i * 2;
            dst[dstBase + 0] += L;
            dst[dstBase + 1] += L;
            dst[dstBase + 2] += R;
            dst[dstBase + 3] += R;
        }

        return true;
    }

    /// <summary>Stereo interleaved duplicated into 4‑channel quad (<c>[1,1,0,0]</c> - R,R,L,L per frame), uniform gain.</summary>
    internal static unsafe bool TryAccumulateStereoDuplexGroupedSwappedInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var routing = map.AsSpan();
        if (routing.Length != 4 || srcChannels != 2 || dstChannels != 4) return false;
        if (routing[0] != 1 || routing[1] != 1 || routing[2] != 0 || routing[3] != 0) return false;
        if (map.RequiredInputChannels != 2) return false;

        var vn = Vector<float>.Count;
        if ((vn % 2) != 0) return false;

        var stereoFloats = samplesPerChannel * 2;
        var g = new Vector<float>(uniformGain);

        var i = 0;
        var limit = stereoFloats - stereoFloats % vn;

        if (Avx.IsSupported && vn == 16)
        {
            var g256 = Vector256.Create(uniformGain);
            var limit16 = stereoFloats - stereoFloats % 16;
            for (; i < limit16; i += 16)
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    var off = pass * 8;
                    fixed (float* pSrc = src.Slice(i + off))
                    fixed (float* pDst = dst.Slice((i + off) * 2))
                    {
                        var m = Avx.Multiply(Avx.LoadVector256(pSrc), g256);
                        var lo = Sse.Shuffle(m.GetLower(), m.GetLower(), 0xB1);
                        var hi = Sse.Shuffle(m.GetUpper(), m.GetUpper(), 0xB1);
                        var d0 = Sse.UnpackLow(lo, lo);
                        var d1 = Sse.UnpackHigh(lo, lo);
                        var d2 = Sse.UnpackLow(hi, hi);
                        var d3 = Sse.UnpackHigh(hi, hi);
                        Sse.Store(pDst, Sse.Add(Sse.LoadVector128(pDst), d0));
                        Sse.Store(pDst + 4, Sse.Add(Sse.LoadVector128(pDst + 4), d1));
                        Sse.Store(pDst + 8, Sse.Add(Sse.LoadVector128(pDst + 8), d2));
                        Sse.Store(pDst + 12, Sse.Add(Sse.LoadVector128(pDst + 12), d3));
                    }
                }
            }
        }
        else if (Avx.IsSupported && vn == 8)
        {
            var g256 = Vector256.Create(uniformGain);
            for (; i < limit; i += 8)
            {
                fixed (float* pSrc = src.Slice(i))
                fixed (float* pDst = dst.Slice(i * 2))
                {
                    var m = Avx.Multiply(Avx.LoadVector256(pSrc), g256);
                    var lo = Sse.Shuffle(m.GetLower(), m.GetLower(), 0xB1);
                    var hi = Sse.Shuffle(m.GetUpper(), m.GetUpper(), 0xB1);
                    var d0 = Sse.UnpackLow(lo, lo);
                    var d1 = Sse.UnpackHigh(lo, lo);
                    var d2 = Sse.UnpackLow(hi, hi);
                    var d3 = Sse.UnpackHigh(hi, hi);
                    Sse.Store(pDst, Sse.Add(Sse.LoadVector128(pDst), d0));
                    Sse.Store(pDst + 4, Sse.Add(Sse.LoadVector128(pDst + 4), d1));
                    Sse.Store(pDst + 8, Sse.Add(Sse.LoadVector128(pDst + 8), d2));
                    Sse.Store(pDst + 12, Sse.Add(Sse.LoadVector128(pDst + 12), d3));
                }
            }
        }
        else if (Sse.IsSupported && vn == 4)
        {
            var g128 = Vector128.Create(uniformGain);
            for (; i < limit; i += 4)
            {
                fixed (float* pSrc = src.Slice(i))
                fixed (float* pDst = dst.Slice(i * 2))
                {
                    var m = Sse.Multiply(Sse.LoadVector128(pSrc), g128);
                    var ms = Sse.Shuffle(m, m, 0xB1);
                    var d0 = Sse.UnpackLow(ms, ms);
                    var d1 = Sse.UnpackHigh(ms, ms);
                    Sse.Store(pDst, Sse.Add(Sse.LoadVector128(pDst), d0));
                    Sse.Store(pDst + 4, Sse.Add(Sse.LoadVector128(pDst + 4), d1));
                }
            }
        }
        else
        {
            Span<float> scratch = stackalloc float[vn * 2];
            for (; i < limit; i += vn)
            {
                var m = MemoryMarshal.Cast<float, Vector<float>>(src.Slice(i, vn))[0] * g;
                ref var sc = ref MemoryMarshal.GetReference(scratch);
                for (var j = 0; j < vn; j += 2)
                {
                    var L = m[j];
                    var R = m[j + 1];
                    var p = j * 2;
                    Unsafe.Add(ref sc, p) = R;
                    Unsafe.Add(ref sc, p + 1) = R;
                    Unsafe.Add(ref sc, p + 2) = L;
                    Unsafe.Add(ref sc, p + 3) = L;
                }

                var dstBase = i * 2;
                var d0 = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase, vn))[0];
                var s0 = MemoryMarshal.Cast<float, Vector<float>>(scratch[..vn])[0];
                MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase, vn))[0] = d0 + s0;

                var d1 = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase + vn, vn))[0];
                var s1 = MemoryMarshal.Cast<float, Vector<float>>(scratch.Slice(vn, vn))[0];
                MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase + vn, vn))[0] = d1 + s1;
            }
        }

        for (; i < stereoFloats; i += 2)
        {
            var L = src[i] * uniformGain;
            var R = src[i + 1] * uniformGain;
            var dstBase = i * 2;
            dst[dstBase + 0] += R;
            dst[dstBase + 1] += R;
            dst[dstBase + 2] += L;
            dst[dstBase + 3] += L;
        }

        return true;
    }

    /// <summary>Stereo interleaved duplicated into 4‑channel quad (<c>[1,0,1,0]</c> - R,L,R,L), uniform gain.</summary>
    internal static unsafe bool TryAccumulateStereoDuplexWideSwappedInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        if (!Vector.IsHardwareAccelerated || uniformGain == 0f)
            return false;

        var routing = map.AsSpan();
        if (routing.Length != 4 || srcChannels != 2 || dstChannels != 4) return false;
        if (routing[0] != 1 || routing[1] != 0 || routing[2] != 1 || routing[3] != 0) return false;
        if (map.RequiredInputChannels != 2) return false;

        var vn = Vector<float>.Count;
        if ((vn % 2) != 0) return false;

        var stereoFloats = samplesPerChannel * 2;

        var g = new Vector<float>(uniformGain);

        var i = 0;
        var limit = stereoFloats - stereoFloats % vn;

        if (Avx.IsSupported && vn == 16)
        {
            var g256 = Vector256.Create(uniformGain);
            var limit16 = stereoFloats - stereoFloats % 16;
            for (; i < limit16; i += 16)
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    var off = pass * 8;
                    fixed (float* pSrc = src.Slice(i + off))
                    fixed (float* pDst = dst.Slice((i + off) * 2))
                    {
                        var m = Avx.Multiply(Avx.LoadVector256(pSrc), g256);
                        var lo = Sse.Shuffle(m.GetLower(), m.GetLower(), 0xB1);
                        var hi = Sse.Shuffle(m.GetUpper(), m.GetUpper(), 0xB1);
                        var expLo = Vector256.Create(Sse.Shuffle(lo, lo, 0x44), Sse.Shuffle(lo, lo, 0xEE));
                        var expHi = Vector256.Create(Sse.Shuffle(hi, hi, 0x44), Sse.Shuffle(hi, hi, 0xEE));
                        Avx.Store(pDst, Avx.Add(Avx.LoadVector256(pDst), expLo));
                        Avx.Store(pDst + 8, Avx.Add(Avx.LoadVector256(pDst + 8), expHi));
                    }
                }
            }
        }
        else if (Avx.IsSupported && vn == 8)
        {
            var g256 = Vector256.Create(uniformGain);
            for (; i < limit; i += 8)
            {
                fixed (float* pSrc = src.Slice(i))
                fixed (float* pDst = dst.Slice(i * 2))
                {
                    var m = Avx.Multiply(Avx.LoadVector256(pSrc), g256);
                    var lo = Sse.Shuffle(m.GetLower(), m.GetLower(), 0xB1);
                    var hi = Sse.Shuffle(m.GetUpper(), m.GetUpper(), 0xB1);
                    var expLo = Vector256.Create(Sse.Shuffle(lo, lo, 0x44), Sse.Shuffle(lo, lo, 0xEE));
                    var expHi = Vector256.Create(Sse.Shuffle(hi, hi, 0x44), Sse.Shuffle(hi, hi, 0xEE));
                    Avx.Store(pDst, Avx.Add(Avx.LoadVector256(pDst), expLo));
                    Avx.Store(pDst + 8, Avx.Add(Avx.LoadVector256(pDst + 8), expHi));
                }
            }
        }
        else if (Sse.IsSupported && vn == 4)
        {
            var g128 = Vector128.Create(uniformGain);
            for (; i < limit; i += 4)
            {
                fixed (float* pSrc = src.Slice(i))
                fixed (float* pDst = dst.Slice(i * 2))
                {
                    var m = Sse.Multiply(Sse.LoadVector128(pSrc), g128);
                    m = Sse.Shuffle(m, m, 0xB1);
                    var e0 = Sse.Shuffle(m, m, 0x44);
                    var e1 = Sse.Shuffle(m, m, 0xEE);
                    Sse.Store(pDst, Sse.Add(Sse.LoadVector128(pDst), e0));
                    Sse.Store(pDst + 4, Sse.Add(Sse.LoadVector128(pDst + 4), e1));
                }
            }
        }
        else
        {
            Span<float> scratch = stackalloc float[vn * 2];
            for (; i < limit; i += vn)
            {
                var m = MemoryMarshal.Cast<float, Vector<float>>(src.Slice(i, vn))[0] * g;
                ref var sc = ref MemoryMarshal.GetReference(scratch);
                for (var j = 0; j < vn; j += 2)
                {
                    var L = m[j];
                    var R = m[j + 1];
                    var p = j * 2;
                    Unsafe.Add(ref sc, p) = R;
                    Unsafe.Add(ref sc, p + 1) = L;
                    Unsafe.Add(ref sc, p + 2) = R;
                    Unsafe.Add(ref sc, p + 3) = L;
                }

                var dstBase = i * 2;
                var d0 = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase, vn))[0];
                var s0 = MemoryMarshal.Cast<float, Vector<float>>(scratch[..vn])[0];
                MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase, vn))[0] = d0 + s0;

                var d1 = MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase + vn, vn))[0];
                var s1 = MemoryMarshal.Cast<float, Vector<float>>(scratch.Slice(vn, vn))[0];
                MemoryMarshal.Cast<float, Vector<float>>(dst.Slice(dstBase + vn, vn))[0] = d1 + s1;
            }
        }

        for (; i < stereoFloats; i += 2)
        {
            var L = src[i] * uniformGain;
            var R = src[i + 1] * uniformGain;
            var dstBase = i * 2;
            dst[dstBase + 0] += R;
            dst[dstBase + 1] += L;
            dst[dstBase + 2] += R;
            dst[dstBase + 3] += L;
        }

        return true;
    }

    // Per-vector pair shuffles. These were scalar stack-pad round-trips (copy out, permute with a
    // scalar loop, copy back - the perf review's "fake SIMD": the stereo-swap route benchmarked 9x
    // slower than stereo-identity). Constant-index Vector128/256/512.Shuffle JITs to real shuffle
    // instructions; exotic widths keep the portable pad fallback.

    private static Vector<float> StereoSwapAdjacentChannels(Vector<float> v)
    {
        if (Vector<float>.Count == 8)
            return Vector256.Shuffle(v.AsVector256(), Vector256.Create(1, 0, 3, 2, 5, 4, 7, 6)).AsVector();
        if (Vector<float>.Count == 4)
            return Vector128.Shuffle(v.AsVector128(), Vector128.Create(1, 0, 3, 2)).AsVector();
        if (Vector<float>.Count == 16)
            return Vector512.Shuffle(v.AsVector512(),
                Vector512.Create(1, 0, 3, 2, 5, 4, 7, 6, 9, 8, 11, 10, 13, 12, 15, 14)).AsVector();

        Span<float> buf = stackalloc float[Vector<float>.Count];
        v.CopyTo(buf);
        for (var j = 0; j < Vector<float>.Count; j += 2)
            (buf[j], buf[j + 1]) = (buf[j + 1], buf[j]);

        return MemoryMarshal.Cast<float, Vector<float>>(buf)[0];
    }

    private static Vector<float> StereoDupLeftAdjacent(Vector<float> v)
    {
        if (Vector<float>.Count == 8)
            return Vector256.Shuffle(v.AsVector256(), Vector256.Create(0, 0, 2, 2, 4, 4, 6, 6)).AsVector();
        if (Vector<float>.Count == 4)
            return Vector128.Shuffle(v.AsVector128(), Vector128.Create(0, 0, 2, 2)).AsVector();
        if (Vector<float>.Count == 16)
            return Vector512.Shuffle(v.AsVector512(),
                Vector512.Create(0, 0, 2, 2, 4, 4, 6, 6, 8, 8, 10, 10, 12, 12, 14, 14)).AsVector();

        Span<float> buf = stackalloc float[Vector<float>.Count];
        v.CopyTo(buf);
        for (var j = 0; j < Vector<float>.Count; j += 2)
            buf[j + 1] = buf[j];

        return MemoryMarshal.Cast<float, Vector<float>>(buf)[0];
    }

    private static Vector<float> StereoDupRightAdjacent(Vector<float> v)
    {
        if (Vector<float>.Count == 8)
            return Vector256.Shuffle(v.AsVector256(), Vector256.Create(1, 1, 3, 3, 5, 5, 7, 7)).AsVector();
        if (Vector<float>.Count == 4)
            return Vector128.Shuffle(v.AsVector128(), Vector128.Create(1, 1, 3, 3)).AsVector();
        if (Vector<float>.Count == 16)
            return Vector512.Shuffle(v.AsVector512(),
                Vector512.Create(1, 1, 3, 3, 5, 5, 7, 7, 9, 9, 11, 11, 13, 13, 15, 15)).AsVector();

        Span<float> buf = stackalloc float[Vector<float>.Count];
        v.CopyTo(buf);
        for (var j = 0; j < Vector<float>.Count; j += 2)
            buf[j] = buf[j + 1];

        return MemoryMarshal.Cast<float, Vector<float>>(buf)[0];
    }

}
