using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace S.Media.Core.Audio;

/// <summary>
/// Maps input audio channels onto output channels. Encoded as
/// <c>outputChannel → sourceChannel</c>: <c>map[outCh] = inCh</c>, where
/// <c>inCh = -1</c> writes silence to that output channel.
/// </summary>
/// <remarks>
/// <para>
/// The map is intentionally one-directional and exhaustive on the *output*
/// side: every output channel must be assigned (to a source channel or to
/// silence). Source channels not referenced anywhere in the map are
/// **dropped** — this is the explicit contract the router enforces, so the
/// caller never gets surprise leakage from unmapped channels.
/// </para>
/// <para>
/// Examples:
/// <list type="bullet">
/// <item><c>new ChannelMap([0, 1])</c> — identity stereo.</item>
/// <item><c>new ChannelMap([1, 0])</c> — swap L/R.</item>
/// <item><c>new ChannelMap([0, 0, 1, 1])</c> — stereo into a 4-channel sink, L→1+2, R→3+4.</item>
/// <item><c>new ChannelMap([-1, 0, 0, -1])</c> — center channels carry the source, sides silent.</item>
/// </list>
/// </para>
/// </remarks>
public readonly struct ChannelMap : IEquatable<ChannelMap>
{
    public const int Silence = -1;

    private readonly int[] _outToIn;

    public ChannelMap(ReadOnlySpan<int> outToIn)
    {
        if (outToIn.Length == 0)
            throw new ArgumentException("channel map must specify at least one output channel", nameof(outToIn));

        _outToIn = outToIn.ToArray();

        var maxIn = -1;
        for (var i = 0; i < _outToIn.Length; i++)
        {
            var src = _outToIn[i];
            if (src < Silence)
                throw new ArgumentOutOfRangeException(nameof(outToIn),
                    $"map[{i}] = {src}; valid values are non-negative source channel indices or {Silence} (silence)");
            if (src > maxIn) maxIn = src;
        }
        RequiredInputChannels = maxIn + 1;
    }

    public int OutputChannels => _outToIn?.Length ?? 0;

    /// <summary>The minimum number of input channels the source must provide for this map to work (max referenced channel + 1).</summary>
    public int RequiredInputChannels { get; }

    public int this[int outputChannel]
        => _outToIn[outputChannel];

    public ReadOnlySpan<int> AsSpan() => _outToIn ?? [];

    /// <summary>
    /// Apply this map: copy <paramref name="samplesPerChannel"/> samples from
    /// <paramref name="src"/> (packed, <paramref name="srcChannels"/> channels)
    /// into <paramref name="dst"/> (packed, <see cref="OutputChannels"/> channels).
    /// </summary>
    /// <remarks>
    /// Output is overwritten, not summed — caller does any mixing. <paramref name="dst"/>
    /// channels mapped to <see cref="Silence"/> are zeroed.
    /// </remarks>
    public void Apply(ReadOnlySpan<float> src, int srcChannels, Span<float> dst, int samplesPerChannel)
    {
        if (srcChannels < RequiredInputChannels)
            throw new ArgumentException(
                $"source has {srcChannels} channels but map requires at least {RequiredInputChannels}",
                nameof(srcChannels));

        var outChannels = OutputChannels;
        if (src.Length < samplesPerChannel * srcChannels)
            throw new ArgumentException("src is shorter than samplesPerChannel * srcChannels", nameof(src));
        if (dst.Length < samplesPerChannel * outChannels)
            throw new ArgumentException("dst is shorter than samplesPerChannel * OutputChannels", nameof(dst));

        var map = _outToIn;
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var srcBase = s * srcChannels;
            var dstBase = s * outChannels;
            for (var oc = 0; oc < outChannels; oc++)
            {
                var ic = map[oc];
                dst[dstBase + oc] = ic < 0 ? 0f : src[srcBase + ic];
            }
        }
    }

    /// <summary>
    /// Apply this map and *add* (sum) into <paramref name="dst"/>. Used by the
    /// mixer to accumulate contributions from multiple sources.
    /// </summary>
    public void ApplyAdditive(ReadOnlySpan<float> src, int srcChannels, Span<float> dst, int samplesPerChannel)
    {
        if (srcChannels < RequiredInputChannels)
            throw new ArgumentException(
                $"source has {srcChannels} channels but map requires at least {RequiredInputChannels}",
                nameof(srcChannels));

        var outChannels = OutputChannels;
        if (src.Length < samplesPerChannel * srcChannels)
            throw new ArgumentException("src is shorter than samplesPerChannel * srcChannels", nameof(src));
        if (dst.Length < samplesPerChannel * outChannels)
            throw new ArgumentException("dst is shorter than samplesPerChannel * OutputChannels", nameof(dst));

        if (TryAccumulateStereoIdentityInterleaved(src, srcChannels, dst, outChannels,
                this, samplesPerChannel, uniformGain: 1f))
            return;

        if (TryAccumulateMonoDupStereoInterleaved(src, srcChannels, dst, outChannels,
                this, samplesPerChannel, uniformGain: 1f))
            return;

        if (TryAccumulateStereoDuplexWideInterleaved(src, srcChannels, dst, outChannels,
                this, samplesPerChannel, uniformGain: 1f))
            return;

        var map = _outToIn;
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var srcBase = s * srcChannels;
            var dstBase = s * outChannels;
            for (var oc = 0; oc < outChannels; oc++)
            {
                var ic = map[oc];
                if (ic >= 0) dst[dstBase + oc] += src[srcBase + ic];
            }
        }
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

        if (Avx.IsSupported && vn == 8)
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

        if (Avx.IsSupported && vn == 8)
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

    private static Vector<float> StereoSwapAdjacentChannels(Vector<float> v)
    {
        Span<float> buf = stackalloc float[Vector<float>.Count];
        v.CopyTo(buf);
        for (var j = 0; j < Vector<float>.Count; j += 2)
            (buf[j], buf[j + 1]) = (buf[j + 1], buf[j]);

        return MemoryMarshal.Cast<float, Vector<float>>(buf)[0];
    }

    // --- helpers ----------------------------------------------------------

    /// <summary>Identity map for <paramref name="channels"/> (out_n = in_n).</summary>
    public static ChannelMap Identity(int channels)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        Span<int> tmp = stackalloc int[channels];
        for (var i = 0; i < channels; i++) tmp[i] = i;
        return new ChannelMap(tmp);
    }

    /// <summary>Mono → N: the single source channel is duplicated to every output channel.</summary>
    public static ChannelMap MonoToN(int outputChannels)
    {
        if (outputChannels <= 0) throw new ArgumentOutOfRangeException(nameof(outputChannels));
        Span<int> tmp = stackalloc int[outputChannels];
        tmp.Clear(); // all zeros = source channel 0
        return new ChannelMap(tmp);
    }

    /// <summary>Stereo into N channels by repeating the L/R pattern (L,R,L,R,...).</summary>
    public static ChannelMap StereoToN(int outputChannels)
    {
        if (outputChannels <= 0) throw new ArgumentOutOfRangeException(nameof(outputChannels));
        Span<int> tmp = stackalloc int[outputChannels];
        for (var i = 0; i < outputChannels; i++) tmp[i] = i & 1;
        return new ChannelMap(tmp);
    }

    public bool Equals(ChannelMap other)
    {
        if (OutputChannels != other.OutputChannels) return false;
        return AsSpan().SequenceEqual(other.AsSpan());
    }

    public override bool Equals(object? obj) => obj is ChannelMap m && Equals(m);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var v in AsSpan()) hash.Add(v);
        return hash.ToHashCode();
    }

    public override string ToString() => $"[{string.Join(",", AsSpan().ToArray())}]";

    public static bool operator ==(ChannelMap a, ChannelMap b) => a.Equals(b);
    public static bool operator !=(ChannelMap a, ChannelMap b) => !a.Equals(b);
}
