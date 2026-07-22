using System.Buffers;
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
/// **dropped** - this is the explicit contract the router enforces, so the
/// caller never gets surprise leakage from unmapped channels.
/// </para>
/// <para>
/// Examples:
/// <list type="bullet">
/// <item><c>new ChannelMap([0, 1])</c> - identity stereo.</item>
/// <item><c>new ChannelMap([1, 0])</c> - swap L/R.</item>
/// <item><c>new ChannelMap([0, 0, 1, 1])</c> - stereo into a 4-channel output, L→1+2, R→3+4.</item>
/// <item><c>new ChannelMap([-1, 0, 0, -1])</c> - center channels carry the source, sides silent.</item>
/// <item><c>new ChannelMap([0, 0])</c> - duplicate L to both outputs (stereo source).</item>
/// <item><c>new ChannelMap([1, 1])</c> - duplicate R to both outputs.</item>
/// <item><c>new ChannelMap([-1, -1])</c> - both outputs silent (additive mix leaves dst unchanged).</item>
/// <item><c>new ChannelMap([0])</c> / <c>[1]</c> - stereo source downmix to mono left or right only (additive).</item>
/// <item><c>new ChannelMap([p])</c> from a **wider** packed source - one mono output taken from channel <c>p</c> each frame (additive).</item>
/// <item><c>new ChannelMap([0, 1])</c> / <c>[2, 3]</c> / <c>[4, 5]</c> with a **wider** packed source - take one consecutive L/R pair per frame, drop unmapped channels (additive).</item>
/// <item><c>new ChannelMap([0, 0])</c> / <c>[1, 1]</c> from a **wider** source - duplicate one packed channel to both stereo outputs (additive).</item>
/// <item><c>ChannelMap.StereoToNSwapped(4)</c> - same as <c>[1,0,1,0]</c> (R,L,R,L into quad).</item>
/// </list>
/// </para>
/// <para>
/// SIMD fast paths cover same-width packed gathers with indices in <c>0..N-1</c> (<c>N ∈ {3, 4, 5, 6, 7, 8}</c> via
/// <see cref="TryAccumulatePackedPermutationInterleaved"/> - bijective permutations and duplicate-lane gathers, no silence; <c>N = 4</c>: SSE <c>SHUFPS</c>; <c>N ∈ {3, 5, 6, 7, 8}</c>: AVX2 <c>PermuteVar8x32</c>),
/// stereo → quad paired duplicates (<c>[0,0,1,1]</c>, <c>[1,1,0,0]</c> via
/// <see cref="TryAccumulateStereoDuplexGroupedInterleaved"/> / <see cref="TryAccumulateStereoDuplexGroupedSwappedInterleaved"/>),
/// and stereo routes using only L/R/silence (<see cref="TryAccumulateStereoSilenceOrZeroDupInterleaved"/>, e.g. <c>[-1,0,0,-1]</c>),
/// stereo → mono single-output (<c>[0]</c>/<c>[1]</c> via <see cref="TryAccumulateStereoToMonoSingleOutputInterleaved"/>),
/// wide interleaved → mono single packed channel <c>[p]</c> (<see cref="TryAccumulateWideSourceMonoSingleOutputInterleaved"/>),
/// wide interleaved → stereo duplicate single channel <c>[p,p]</c> (<see cref="TryAccumulateWideSourceSingleChannelDupStereoInterleaved"/>),
/// wide interleaved → stereo consecutive pair <c>[p,p+1]</c> (<see cref="TryAccumulateWideSourceStereoConsecutivePairInterleaved"/>),
/// plus the other SIMD paths ordered in <see cref="ApplyAdditive"/> before its scalar fallback.
/// Maps that do not match any of those fast paths use the scalar accumulation loop.
/// </para>
/// </remarks>
public readonly partial struct ChannelMap : IEquatable<ChannelMap>
{
    public const int Silence = -1;

    private const int AccumulateInterleaveScratchCap = 512;

    /// <summary>Max floats for pooled <see cref="ArrayPool{T}"/> scratch in mono / stereo spread SIMD paths.</summary>
    private const int PooledMonoDupScratchMaxFloats = 262_144;

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
    /// Output is overwritten, not summed - caller does any mixing. <paramref name="dst"/>
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

        if (TryAccumulateAnyInterleaved(src, srcChannels, dst, outChannels,
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
    /// The single, ordered SIMD fast-path chain shared by <see cref="ApplyAdditive"/> (gain 1) and the
    /// router's uniform-gain mix (<c>AudioRouter.ApplyRoute</c>). Returns <c>true</c> when a fast path
    /// accumulated <paramref name="src"/> × <paramref name="uniformGain"/> into <paramref name="dst"/>;
    /// <c>false</c> means the caller must run its scalar fallback. Keeping the chain in ONE place
    /// guarantees the two call sites can't drift (they had: six shapes were SIMD at gain 1.0 but
    /// scalar at any other uniform gain) and each mix probes the chain at most once.
    /// </summary>
    /// <remarks>Callers validate buffer sizes; the probes only inspect map shape. Every probe honors
    /// an arbitrary <paramref name="uniformGain"/> (several bail to scalar on exactly 0, which callers
    /// already short-circuit).</remarks>
    internal static bool TryAccumulateAnyInterleaved(
        ReadOnlySpan<float> src, int srcChannels,
        Span<float> dst, int dstChannels,
        in ChannelMap map, int samplesPerChannel, float uniformGain)
    {
        return TryAccumulateStereoFullSilenceStereoInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateStereoIdentityInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateWideSourceStereoConsecutivePairInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateWideSourceSingleChannelDupStereoInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateStereoDupSingleChannelInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateStereoToMonoSingleOutputInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateWideSourceMonoSingleOutputInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateStereoSilenceOrZeroDupInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateMonoSilenceOrZeroDupInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateMonoDupStereoInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateMonoDupNInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateStereoDuplexWideInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateStereoDuplexGroupedInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateStereoDuplexGroupedSwappedInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateStereoDuplexWideSwappedInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateStereoToNInterleavedSwapped(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulateStereoToNInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulatePackedIdentityInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain)
            || TryAccumulatePackedPermutationInterleaved(src, srcChannels, dst, dstChannels, map, samplesPerChannel, uniformGain);
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

    /// <summary>Stereo into N channels by repeating R,L,R,L,… (swapped <see cref="StereoToN"/>).</summary>
    public static ChannelMap StereoToNSwapped(int outputChannels)
    {
        if (outputChannels <= 0) throw new ArgumentOutOfRangeException(nameof(outputChannels));
        Span<int> tmp = stackalloc int[outputChannels];
        for (var i = 0; i < outputChannels; i++) tmp[i] = 1 - (i & 1);
        return new ChannelMap(tmp);
    }

    /// <summary>
    /// Default map from a source of <paramref name="inputChannels"/> onto an output of
    /// <paramref name="outputChannels"/>, chosen <em>without</em> assuming the two counts match: equal
    /// counts pass straight through (<see cref="Identity"/>), mono fans out to every output channel
    /// (<see cref="MonoToN"/>), stereo repeats the L/R pattern (<see cref="StereoToN"/>), and any other
    /// N→M repeats the source channels in order (<c>map[outCh] = outCh % inputChannels</c>). Because the
    /// indices are taken modulo <paramref name="inputChannels"/>, the map never references a channel the
    /// source lacks, so it always satisfies the router's <c>RequiredInputChannels ≤ source channels</c>
    /// contract - a mono (or otherwise mismatched) source up/down-mixes instead of failing the route.
    /// </summary>
    public static ChannelMap DefaultFor(int inputChannels, int outputChannels)
    {
        if (inputChannels <= 0) throw new ArgumentOutOfRangeException(nameof(inputChannels));
        if (outputChannels <= 0) throw new ArgumentOutOfRangeException(nameof(outputChannels));
        if (inputChannels == outputChannels) return Identity(inputChannels);
        if (inputChannels == 1) return MonoToN(outputChannels);
        if (inputChannels == 2) return StereoToN(outputChannels);

        Span<int> tmp = stackalloc int[outputChannels];
        for (var i = 0; i < outputChannels; i++) tmp[i] = i % inputChannels;
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