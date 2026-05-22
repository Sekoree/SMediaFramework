using S.Media.Core.Audio;
using S.Media.Core.Clock;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public class AudioPlayerTests
{
    private const int SampleRate = 48000;
    private static readonly AudioFormat Stereo = new(SampleRate, 2);

    [Fact]
    public void AddOutput_AutoWiresFirstClockedPlaybackOutput_AsPrimary()
    {
        using var player = new AudioPlayer(SampleRate);
        var primary = new ClockedPlaybackOutput(Stereo);

        var id = player.AddOutput(primary, "speakers");

        Assert.Equal("speakers", player.PrimaryOutputId);
        Assert.Same(primary, player.Clock.Master);
    }

    [Fact]
    public void AddOutput_OutputPumpCapacity_IsVisibleInRouterStats()
    {
        using var player = new AudioPlayer(SampleRate, chunkSamples: 480);
        var id = player.AddOutput(new PlainOutput(Stereo), "x", outputPumpCapacityChunks: 40);
        Assert.Equal(40, player.Router.GetPumpStats(id).PumpCapacityChunks);
    }

    [Fact]
    public void AddOutput_NonClockedOutput_NotPrimary()
    {
        using var player = new AudioPlayer(SampleRate);
        player.AddOutput(new PlainOutput(Stereo), "out");
        Assert.Null(player.PrimaryOutputId);
        Assert.Null(player.Clock.Master);
    }

    [Fact]
    public void AutoWirePrimary_False_DoesNotWire()
    {
        using var player = new AudioPlayer(SampleRate) { AutoWirePrimary = false };
        player.AddOutput(new ClockedPlaybackOutput(Stereo), "speakers");
        Assert.Null(player.PrimaryOutputId);
        Assert.Null(player.Clock.Master);
    }

    [Fact]
    public void Connect_WithoutMap_UsesIdentitySizedToOutput()
    {
        using var player = new AudioPlayer(SampleRate);
        player.AddOwnedSource(new ConstantSource(Stereo, 1f), "src");
        player.AddOutput(new PlainOutput(Stereo), "out");

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
        player.AddOutput(new PlainOutput(Stereo), "out");
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
        player.AddOutput(new PlainOutput(Stereo), "out");
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
        player.AddOutput(new PlainOutput(Stereo), "out");
        player.Connect("src", "out");

        player.Play();
        Thread.Sleep(20);

        player.Seek("src", TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromMinutes(1), src.LastSeekTo);
        Assert.True(player.Position >= TimeSpan.FromMinutes(1));
        player.Stop();
    }

    [Fact]
    public void RemoveOutput_AfterPrimaryRemoved_SecondClockedOutputBecomesPrimary()
    {
        using var player = new AudioPlayer(SampleRate);
        var first = new ClockedPlaybackOutput(Stereo);
        var second = new ClockedPlaybackOutput(Stereo);
        player.AddOutput(first, "a");
        player.AddOutput(second, "b");

        Assert.Equal("a", player.PrimaryOutputId);
        Assert.True(player.RemoveOutput("a"));
        Assert.Equal("b", player.PrimaryOutputId);
        Assert.Same(second, player.Clock.Master);
    }

    [Fact]
    public void RemoveOutput_PrimaryOutput_ClearsMasterAndPrimaryId()
    {
        using var player = new AudioPlayer(SampleRate);
        var primary = new ClockedPlaybackOutput(Stereo);
        player.AddOutput(primary, "speakers");

        Assert.True(player.RemoveOutput("speakers"));
        Assert.Null(player.PrimaryOutputId);
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
    public void Dispose_DoesNotDisposeOutputs()
    {
        var output = new DisposableTrackingOutput(Stereo);
        using (var player = new AudioPlayer(SampleRate))
        {
            player.AddOutput(output, "out");
        }
        Assert.False(output.Disposed);
    }

    // --- helpers ----------------------------------------------------------

    private sealed class PlainOutput(AudioFormat fmt) : IAudioOutput
    {
        public AudioFormat Format { get; } = fmt;
        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }

    private sealed class ClockedPlaybackOutput(AudioFormat fmt) : IAudioOutput, IClockedOutput, IPlaybackClock
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

    private sealed class DisposableTrackingOutput(AudioFormat fmt) : IAudioOutput, IDisposable
    {
        public AudioFormat Format { get; } = fmt;
        public bool Disposed { get; private set; }
        public void Submit(ReadOnlySpan<float> packedSamples) { }
        public void Dispose() => Disposed = true;
    }
}
