namespace S.Media.Session.Tests;

/// <summary>A registry decoder that opens any URI as a short synthetic silent audio clip (no FFmpeg/device).</summary>
internal sealed class FakeAudioDecoderProvider : IMediaDecoderProvider
{
    public string Name => "fake-audio";

    public double Probe(string uri, MediaKind kind) => kind == MediaKind.Audio ? 1.0 : 0.0;

    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) =>
        throw new NotSupportedException("fake provider is audio-only");

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => new SyntheticSilentSource();

    public static IMediaRegistry Registry() => MediaRegistry.Build(b => b.AddDecoder(new FakeAudioDecoderProvider()));
}

/// <summary>A finite stereo source that yields a few chunks of silence then exhausts — enough for a clip
/// to open and Play() without a real decoder or device.</summary>
internal sealed class SyntheticSilentSource : IAudioSource
{
    private int _remaining = 8;

    public AudioFormat Format { get; } = new(48_000, 2);

    public bool IsExhausted => Volatile.Read(ref _remaining) <= 0;

    public int ReadInto(Span<float> destination)
    {
        if (Interlocked.Decrement(ref _remaining) < 0)
            return 0;
        destination.Clear();
        return destination.Length - (destination.Length % Format.Channels);
    }
}

/// <summary>A fake audio backend that records every output it creates (channel count + device id) — lets a
/// headless test assert the routing scene's N→M channel counts and the per-group multi-output fan-out.</summary>
internal sealed class RecordingAudioBackend : IAudioBackend
{
    public string Name => "recording";
    private readonly List<(int Channels, string? DeviceId)> _created = [];

    public IReadOnlyList<(int Channels, string? DeviceId)> Created => _created;
    public int OutputCount => _created.Count;
    public int LastOutputChannels => _created.Count > 0 ? _created[^1].Channels : 0;

    public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() =>
        [new AudioDeviceInfo("dev0", "Recording Output", MaxChannels: 8, DefaultSampleRate: 48_000, IsDefault: true)];

    public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() => [];

    public IAudioOutput CreateOutput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null)
    {
        _created.Add((format.Channels, deviceId));
        return new SinkAudioOutput(format);
    }

    public IAudioSource CreateInput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null) =>
        throw new NotSupportedException();
}

internal sealed class SinkAudioOutput(AudioFormat format) : IAudioOutput
{
    public AudioFormat Format => format;
    public void Submit(ReadOnlySpan<float> packedSamples) { }
}
