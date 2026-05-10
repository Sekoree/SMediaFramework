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
