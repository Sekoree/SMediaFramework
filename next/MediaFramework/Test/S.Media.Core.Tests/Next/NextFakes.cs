namespace S.Media.Core.Tests.Next;

/// <summary>Minimal fakes for the Phase 1 new-primitive tests (registry, clocks).</summary>
internal sealed class FakePlaybackClock : IPlaybackClock
{
    public TimeSpan ElapsedSinceStart { get; set; }
    public bool IsAdvancing { get; set; } = true;
}

internal sealed class FakeVideoSource : IVideoSource
{
    public string Tag { get; init; } = "";
    public VideoFormat Format => default;
    public IReadOnlyList<PixelFormat> NativePixelFormats => [];
    public bool IsExhausted => true;
    public void SelectOutputFormat(PixelFormat format) { }
    public bool TryReadNextFrame(out VideoFrame frame) { frame = null!; return false; }
}

internal sealed class FakeAudioSource : IAudioSource
{
    public string Tag { get; init; } = "";
    public AudioFormat Format => default;
    public bool IsExhausted => true;
    public int ReadInto(Span<float> destination) => 0;
}

/// <summary>A decoder provider with a fixed confidence score; opened sources carry its name as a tag.</summary>
internal sealed class FakeDecoderProvider(string name, double score) : IMediaDecoderProvider
{
    public string Name => name;
    public double Probe(string uri, MediaKind kind) => score;
    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => new FakeVideoSource { Tag = name };
    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => new FakeAudioSource { Tag = name };
}
