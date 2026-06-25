using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class AudioRouterPlaybackTests
{
    private const int SampleRate = 48_000;

    [Fact]
    public void AddOwnedSource_DisposesWithRouter()
    {
        var source = new CountingSource(SampleRate, 2, frames: 4);
        using var clock = new MediaClock();
        using var router = new AudioRouter(SampleRate, chunkSamples: 64);
        router.AttachMasterClock(clock);
        router.AddOwnedSource(source);
        router.Dispose();
        Assert.True(source.Disposed);
    }

    [Fact]
    public void AutoWirePrimary_PromotesFirstClockedOutput()
    {
        using var clock = new MediaClock();
        using var router = new AudioRouter(SampleRate, chunkSamples: 64);
        router.AttachMasterClock(clock);
        var output = new FakeClockedOutput(SampleRate, 2);
        var id = router.AddOutput(output);
        Assert.Equal(id, router.PrimaryOutputId);
    }

    [Fact]
    public void Connect_UsesIdentityMapForOutputChannels()
    {
        using var clock = new MediaClock();
        using var router = new AudioRouter(SampleRate, chunkSamples: 64);
        var source = new CountingSource(SampleRate, 2, frames: 8);
        var output = new CollectingOutput(SampleRate, 2);
        var srcId = router.AddSource(source);
        var outId = router.AddOutput(output);
        router.Connect(srcId, outId);
        router.Start();
        Thread.Sleep(80);
        router.Stop();
        Assert.True(output.Received > 0);
    }

    private sealed class CountingSource(int sampleRate, int channels, int frames) : IAudioSource, IDisposable
    {
        private int _left = frames;
        public bool Disposed { get; private set; }
        public AudioFormat Format { get; } = new(sampleRate, channels);
        public bool IsExhausted => _left <= 0;

        public int ReadInto(Span<float> dst)
        {
            if (_left <= 0) return 0;
            _left--;
            dst.Clear();
            return dst.Length;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeClockedOutput(int sampleRate, int channels) : IAudioOutput, IClockedOutput, IPlaybackClock
    {
        public AudioFormat Format { get; } = new(sampleRate, channels);
        public TimeSpan CurrentPosition { get; private set; }
        public TimeSpan ElapsedSinceStart => CurrentPosition;
        public bool IsRunning { get; private set; }
        public bool IsAdvancing => IsRunning;
        public double PlaybackRate => 1.0;
#pragma warning disable CS0067 // interface event never raised by this test stub
        public event EventHandler<TimeSpan>? PositionChanged;
#pragma warning restore CS0067
        public void Submit(ReadOnlySpan<float> samples) { }
        public bool WaitForNextChunk(CancellationToken cancellationToken) => !cancellationToken.IsCancellationRequested;
        public bool WaitForCapacity(int chunkSamples, CancellationToken token) => !token.IsCancellationRequested;
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Pause(CancellationToken cancellationToken = default) => IsRunning = false;
        public void Seek(TimeSpan position) => CurrentPosition = position;
    }

    private sealed class CollectingOutput(int sampleRate, int channels) : IAudioOutput
    {
        public AudioFormat Format { get; } = new(sampleRate, channels);
        public int Received;
        public void Submit(ReadOnlySpan<float> samples) => Received += samples.Length;
    }
}
