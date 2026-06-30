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

/// <summary>A decoder provider with a fixed confidence score; opened sources carry its name as a tag.
/// Uses the default <see cref="IMediaDecoderProvider.OpenAsync"/> bridge (no override).</summary>
internal sealed class FakeDecoderProvider(string name, double score) : IMediaDecoderProvider
{
    public string Name => name;
    public double Probe(string uri, MediaKind kind) => score;
    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => new FakeVideoSource { Tag = name };
    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => new FakeAudioSource { Tag = name };
}

/// <summary>A provider that overrides <see cref="IMediaDecoderProvider.OpenAsync"/> to return one result owning
/// a single shared asset (like FFmpeg's shared demux): both tracks are views, and disposing the result tears
/// the asset down exactly once. <see cref="AssetDisposeCount"/> records the teardown.</summary>
internal sealed class SharedAssetProvider : IMediaDecoderProvider
{
    public int AssetDisposeCount;
    public string Name => "SharedAsset";
    public double Probe(string uri, MediaKind kind) => 0.9;
    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => new FakeVideoSource { Tag = Name };
    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => new FakeAudioSource { Tag = Name };

    public ValueTask<MediaOpenResult> OpenAsync(
        MediaOpenRequest request, IProgress<MediaPrepareProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var video = request.Video is not null ? new FakeVideoSource { Tag = "shared" } : null;
        var audio = request.Audio is not null ? new FakeAudioSource { Tag = "shared" } : null;
        return ValueTask.FromResult(new MediaOpenResult(
            Name, video, audio, TimeSpan.FromSeconds(5), isLive: false, canSeek: true,
            disposeAsync: () => { Interlocked.Increment(ref AssetDisposeCount); return ValueTask.CompletedTask; }));
    }
}
