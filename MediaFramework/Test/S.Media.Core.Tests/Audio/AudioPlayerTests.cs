using S.Media.Core.Audio;
using S.Media.Core.Clock;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public class AudioPlayerTests
{
    private const int SampleRate = 48000;
    private static readonly AudioFormat Stereo = new(SampleRate, 2);

    [Fact]
    public void AddOutput_AutoWiresFirstClockedPlaybackSink_AsPrimary()
    {
        using var player = new AudioPlayer(SampleRate);
        var primary = new ClockedPlaybackSink(Stereo);

        var id = player.AddOutput(primary, "speakers");

        Assert.Equal("speakers", player.PrimarySinkId);
        Assert.Same(primary, player.Clock.Master);
    }

    [Fact]
    public void AddOutput_SinkPumpCapacity_IsVisibleInRouterStats()
    {
        using var player = new AudioPlayer(SampleRate, chunkSamples: 480);
        var id = player.AddOutput(new PlainSink(Stereo), "x", sinkPumpCapacityChunks: 40);
        Assert.Equal(40, player.Router.GetPumpStats(id).PumpCapacityChunks);
    }

    [Fact]
    public void AddOutput_NonClockedSink_NotPrimary()
    {
        using var player = new AudioPlayer(SampleRate);
        player.AddOutput(new PlainSink(Stereo), "out");
        Assert.Null(player.PrimarySinkId);
        Assert.Null(player.Clock.Master);
    }

    [Fact]
    public void AutoWirePrimary_False_DoesNotWire()
    {
        using var player = new AudioPlayer(SampleRate) { AutoWirePrimary = false };
        player.AddOutput(new ClockedPlaybackSink(Stereo), "speakers");
        Assert.Null(player.PrimarySinkId);
        Assert.Null(player.Clock.Master);
    }

    [Fact]
    public void Connect_WithoutMap_UsesIdentitySizedToSink()
    {
        using var player = new AudioPlayer(SampleRate);
        player.AddOwnedSource(new ConstantSource(Stereo, 1f), "src");
        player.AddOutput(new PlainSink(Stereo), "out");

        player.Connect("src", "out");
        // Identity stereo created and registered.
        Assert.Single(player.Router.Routes);
        Assert.Equal(2, player.Router.Routes[0].Map.OutputChannels);
    }

    [Fact]
    public void Play_StartsBothRouterAndClock()
    {
        using var player = new AudioPlayer(SampleRate, chunkSamples: 64);
        player.AddOwnedSource(new ConstantSource(Stereo, 1f), "src");
        player.AddOutput(new PlainSink(Stereo), "out");
        player.Connect("src", "out");

        player.Play();
        Assert.True(player.IsPlaying);
        Assert.True(player.Clock.IsRunning);
        Thread.Sleep(40);
        Assert.True(player.Position > TimeSpan.Zero);
        player.Stop();
    }

    [Fact]
    public void Pause_FreezesClockAndStopsRouter()
    {
        using var player = new AudioPlayer(SampleRate, chunkSamples: 64);
        player.AddOwnedSource(new ConstantSource(Stereo, 1f), "src");
        player.AddOutput(new PlainSink(Stereo), "out");
        player.Connect("src", "out");

        player.Play();
        Thread.Sleep(40);

        player.Pause();
        Assert.False(player.IsPlaying);
        Assert.False(player.Clock.IsRunning);
        var posAtPause = player.Position;
        Thread.Sleep(40);
        Assert.Equal(posAtPause, player.Position);
    }

    [Fact]
    public void Seek_RepositionsBothSourceAndClock()
    {
        using var player = new AudioPlayer(SampleRate);
        var src = new SeekableSource(Stereo);
        player.AddOwnedSource(src, "src");
        player.AddOutput(new PlainSink(Stereo), "out");
        player.Connect("src", "out");

        player.Play();
        Thread.Sleep(20);

        player.Seek("src", TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromMinutes(1), src.LastSeekTo);
        Assert.True(player.Position >= TimeSpan.FromMinutes(1));
        player.Stop();
    }

    [Fact]
    public void RemoveOutput_AfterPrimaryRemoved_SecondClockedSinkBecomesPrimary()
    {
        using var player = new AudioPlayer(SampleRate);
        var first = new ClockedPlaybackSink(Stereo);
        var second = new ClockedPlaybackSink(Stereo);
        player.AddOutput(first, "a");
        player.AddOutput(second, "b");

        Assert.Equal("a", player.PrimarySinkId);
        Assert.True(player.RemoveOutput("a"));
        Assert.Equal("b", player.PrimarySinkId);
        Assert.Same(second, player.Clock.Master);
    }

    [Fact]
    public void RemoveOutput_PrimarySink_ClearsMasterAndPrimaryId()
    {
        using var player = new AudioPlayer(SampleRate);
        var primary = new ClockedPlaybackSink(Stereo);
        player.AddOutput(primary, "speakers");

        Assert.True(player.RemoveOutput("speakers"));
        Assert.Null(player.PrimarySinkId);
        Assert.Null(player.Clock.Master);
    }

    [Fact]
    public void Dispose_DisposesOwnedSources()
    {
        var src = new DisposableTrackingSource(Stereo);
        using (var player = new AudioPlayer(SampleRate))
        {
            player.AddOwnedSource(src, "src");
        }
        Assert.True(src.Disposed);
    }

    [Fact]
    public void Dispose_DoesNotDisposeSinks()
    {
        var sink = new DisposableTrackingSink(Stereo);
        using (var player = new AudioPlayer(SampleRate))
        {
            player.AddOutput(sink, "out");
        }
        Assert.False(sink.Disposed);
    }

    // --- helpers ----------------------------------------------------------

    private sealed class PlainSink(AudioFormat fmt) : IAudioSink
    {
        public AudioFormat Format { get; } = fmt;
        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }

    private sealed class ClockedPlaybackSink(AudioFormat fmt) : IAudioSink, IClockedSink, IPlaybackClock
    {
        public AudioFormat Format { get; } = fmt;
        public TimeSpan ElapsedSinceStart { get; set; } = TimeSpan.Zero;
        public bool IsAdvancing { get; set; } = true;
        public void Submit(ReadOnlySpan<float> packedSamples) { }
        public bool WaitForCapacity(int chunkSamples, CancellationToken token)
        {
            // Always ready — pace via the router's wall-clock fallback in tests.
            return !token.IsCancellationRequested;
        }
    }

    private sealed class ConstantSource(AudioFormat fmt, float value) : IAudioSource
    {
        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => false;
        public int ReadInto(Span<float> dst)
        {
            dst.Fill(value);
            return dst.Length;
        }
    }

    private sealed class SeekableSource(AudioFormat fmt) : IAudioSource, ISeekableSource
    {
        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => false;
        public TimeSpan Duration => TimeSpan.FromMinutes(10);
        public TimeSpan Position { get; private set; }
        public TimeSpan? LastSeekTo { get; private set; }
        public int ReadInto(Span<float> dst) { dst.Clear(); return dst.Length; }
        public void Seek(TimeSpan position) { LastSeekTo = position; Position = position; }
    }

    private sealed class DisposableTrackingSource(AudioFormat fmt) : IAudioSource, IDisposable
    {
        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => false;
        public bool Disposed { get; private set; }
        public int ReadInto(Span<float> dst) { dst.Clear(); return dst.Length; }
        public void Dispose() => Disposed = true;
    }

    private sealed class DisposableTrackingSink(AudioFormat fmt) : IAudioSink, IDisposable
    {
        public AudioFormat Format { get; } = fmt;
        public bool Disposed { get; private set; }
        public void Submit(ReadOnlySpan<float> packedSamples) { }
        public void Dispose() => Disposed = true;
    }
}
