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
