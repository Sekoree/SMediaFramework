using S.Media.Core.Diagnostics;

namespace S.Media.Core.Audio;

/// <summary>
/// In-memory PCM clip shared by many <see cref="AudioClipVoice"/> instances (soundboard / cue grid).
/// </summary>
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

    /// <summary>Decodes a media file into a resident PCM buffer. Requires FFmpeg init.</summary>
    public static AudioClip OpenFile(string path, int? targetSampleRate = null, ChannelMap? mixdown = null)
    {
        var source = AudioSource.OpenFile(path);
        try
        {
            return LoadFromSource(source, targetSampleRate, mixdown);
        }
        finally
        {
            DisposeSource(source);
        }
    }

    /// <summary>Decodes a media stream into a resident PCM buffer. Requires FFmpeg init.</summary>
    public static AudioClip OpenStream(Stream stream, int? targetSampleRate = null, ChannelMap? mixdown = null)
    {
        var source = AudioSource.OpenStream(stream);
        try
        {
            return LoadFromSource(source, targetSampleRate, mixdown);
        }
        finally
        {
            DisposeSource(source);
        }
    }

    private static void DisposeSource(IAudioSource source)
    {
        if (source is IDisposable d)
            d.Dispose();
    }

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
        new(this, options ?? default);

    private static AudioClip LoadFromSource(IAudioSource source, int? targetSampleRate, ChannelMap? mixdown)
    {
        var (format, interleaved, samplesPerChannel) = DecodeToPcm(source);
        if (mixdown is { } map)
            (format, interleaved, samplesPerChannel) = ApplyMixdown(format, interleaved, samplesPerChannel, map);

        if (targetSampleRate is { } rate && rate != format.SampleRate)
        {
            if (MediaFrameworkPlugins.AudioResampleSourceWrapper is not { } factory)
                throw new InvalidOperationException(
                    "AudioClip: targetSampleRate requires FFmpeg init (MediaFrameworkRuntime.Init().UseFFmpeg()).");
            var pcm = new PcmBufferAudioSource(format, interleaved, samplesPerChannel);
            var wrapped = factory(pcm, rate);
            try
            {
                return LoadFromSource(wrapped, targetSampleRate: null, mixdown: null);
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
