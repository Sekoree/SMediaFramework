namespace S.Media.Core.Audio;

/// <summary>
/// In-memory PCM clip shared by many <see cref="AudioClipVoice"/> instances (soundboard / cue grid).
/// </summary>
/// <remarks>
/// Core owns the resident-PCM container and voice minting only. Decoding a file/stream into a clip is a
/// decoder concern, so the <c>open a URI → AudioClip</c> convenience lives at the registry/Session layer
/// (it calls <see cref="LoadFromSource"/> with a source from <c>IMediaRegistry.TryOpenAudio</c> and, for
/// resampling, the registry's resampler factory) — Core never references a decoder (P2/P3).
/// </remarks>
public sealed class AudioClip
{
    private readonly float[] _interleaved;

    private AudioClip(AudioFormat format, float[] interleaved, int samplesPerChannel)
    {
        Format = format;
        _interleaved = interleaved;
        SamplesPerChannel = samplesPerChannel;
        Duration = TimeSpan.FromSeconds((double)samplesPerChannel / format.SampleRate);
    }

    public AudioFormat Format { get; }

    public TimeSpan Duration { get; }

    public int SamplesPerChannel { get; }

    internal ReadOnlyMemory<float> Interleaved => _interleaved;

    /// <summary>Wraps caller-owned interleaved float PCM (length must be a multiple of channel count).</summary>
    public static AudioClip FromSamples(AudioFormat format, ReadOnlyMemory<float> interleaved)
    {
        format.Validate(nameof(format));
        if (interleaved.Length % format.Channels != 0)
            throw new ArgumentException("interleaved length must be a multiple of channel count.", nameof(interleaved));

        var copy = interleaved.Span.ToArray();
        var samplesPerChannel = copy.Length / format.Channels;
        return new AudioClip(format, copy, samplesPerChannel);
    }

    /// <summary>Mints a new voice that reads from this clip's shared buffer.</summary>
    public AudioClipVoice CreateVoice(AudioClipVoiceOptions? options = null) =>
        new(this, options ?? AudioClipVoiceOptions.Default);

    /// <summary>
    /// Drains <paramref name="source"/> (caller-owned — not disposed here) into a resident PCM clip,
    /// optionally mixing down and resampling. Resampling to <paramref name="targetSampleRate"/> needs a
    /// <paramref name="resamplerFactory"/> (wire <c>IMediaRegistry.CreateResampler</c>); Core has no decoder.
    /// </summary>
    public static AudioClip LoadFromSource(
        IAudioSource source,
        int? targetSampleRate = null,
        ChannelMap? mixdown = null,
        Func<IAudioSource, int, IAudioSource>? resamplerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (format, interleaved, samplesPerChannel) = DecodeToPcm(source);
        if (mixdown is { } map)
            (format, interleaved, samplesPerChannel) = ApplyMixdown(format, interleaved, samplesPerChannel, map);

        if (targetSampleRate is { } rate && rate != format.SampleRate)
        {
            if (resamplerFactory is not { } factory)
                throw new InvalidOperationException(
                    "AudioClip: targetSampleRate requires a resamplerFactory (wire IMediaRegistry.CreateResampler).");
            var pcm = new PcmBufferAudioSource(format, interleaved, samplesPerChannel);
            var wrapped = factory(pcm, rate);
            try
            {
                return LoadFromSource(wrapped);
            }
            finally
            {
                if (wrapped is IDisposable d)
                    d.Dispose();
            }
        }

        return new AudioClip(format, interleaved, samplesPerChannel);
    }

    private static (AudioFormat Format, float[] Interleaved, int SamplesPerChannel) DecodeToPcm(IAudioSource source)
    {
        var chunks = new List<float[]>();
        var scratch = new float[source.Format.SampleRate * Math.Max(1, source.Format.Channels)];
        int read;
        var totalSamples = 0;
        while ((read = source.ReadInto(scratch)) > 0)
        {
            var chunk = new float[read];
            scratch.AsSpan(0, read).CopyTo(chunk);
            chunks.Add(chunk);
            totalSamples += read / source.Format.Channels;
        }

        var channels = source.Format.Channels;
        var interleaved = new float[totalSamples * channels];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(interleaved.AsSpan(offset));
            offset += chunk.Length;
        }

        return (source.Format, interleaved, totalSamples);
    }

    private static (AudioFormat Format, float[] Interleaved, int SamplesPerChannel) ApplyMixdown(
        AudioFormat format,
        float[] interleaved,
        int samplesPerChannel,
        ChannelMap mixdown)
    {
        var srcChannels = format.Channels;
        if (srcChannels < mixdown.RequiredInputChannels)
            throw new ArgumentException(
                $"mixdown requires {mixdown.RequiredInputChannels} source channels but clip has {srcChannels}",
                nameof(mixdown));

        var outChannels = mixdown.OutputChannels;
        var result = new float[samplesPerChannel * outChannels];
        for (var s = 0; s < samplesPerChannel; s++)
        {
            mixdown.Apply(
                interleaved.AsSpan(s * srcChannels, srcChannels),
                srcChannels,
                result.AsSpan(s * outChannels, outChannels),
                1);
        }

        return (new AudioFormat(format.SampleRate, outChannels), result, samplesPerChannel);
    }
}
