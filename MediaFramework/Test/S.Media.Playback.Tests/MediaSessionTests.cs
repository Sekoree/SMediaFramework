using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.Playback;
using Xunit;

namespace S.Media.Playback.Tests;

public sealed class MediaSessionTests
{
    private static MediaPlayer BuildLivePlayer()
    {
        var audio = new SilenceSource(new AudioFormat(48_000, 2));
        var video = new SolidVideoSource(new VideoFormat(32, 32, PixelFormat.Bgra32, new Rational(10, 1)));
        Assert.True(
            MediaPlayer.OpenLive(audio, video).WithDisposeSourcesOnPlayerDispose(true).TryBuild(out var p, out var err),
            err);
        return p!;
    }

    [Fact]
    public void Owning_DisposesPlayerFirst_ThenOwnedResources_InReverseOrder()
    {
        // P3-1: a session owns the player + the resources wired around it. On dispose the player goes
        // first (stopping its routers so nothing is still pushing to an output), then the registered
        // resources in reverse registration order.
        var order = new List<string>();
        var player = BuildLivePlayer();
        player.RegisterOwnedCompanion(new OrderFlag("player", order)); // disposed during player.Dispose()
        var session = MediaSession.Owning(player);
        session.Own(new OrderFlag("first", order));
        session.Own(new OrderFlag("second", order));

        session.Dispose();

        Assert.True(player.IsDisposed);
        Assert.Equal(new[] { "player", "second", "first" }, order);
    }

    [Fact]
    public void BuildSession_WrapsPlayerInOwningSession_DisposeTearsDownPlayerAndCompanions()
    {
        // The builder convenience for P3-1: one call yields a session that owns the player; disposing it
        // tears down the player (and its registered companions) — the "open with audio, dispose one thing"
        // path end to end.
        var audio = new SilenceSource(new AudioFormat(48_000, 2));
        var video = new SolidVideoSource(new VideoFormat(32, 32, PixelFormat.Bgra32, new Rational(10, 1)));
        var session = MediaPlayer.OpenLive(audio, video)
            .WithDisposeSourcesOnPlayerDispose(false)
            .BuildSession();

        Assert.False(session.Player.IsDisposed);
        var companion = new OrderFlag("companion", []);
        session.Player.RegisterOwnedCompanion(companion);

        session.Dispose();

        Assert.True(session.Player.IsDisposed);
        Assert.True(companion.Disposed);
    }

    [Fact]
    public void Borrowing_DoesNotDisposePlayer_ButDisposesOwnedResources()
    {
        using var player = BuildLivePlayer();
        var flag = new OrderFlag("x", []);
        var session = MediaSession.Borrowing(player);
        session.Own(flag);

        session.Dispose();

        Assert.False(player.IsDisposed); // borrowed — caller keeps it
        Assert.True(flag.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_AwaitsAsyncDisposableResources()
    {
        var player = BuildLivePlayer();
        var asyncRes = new AsyncDisposeFlag();
        var session = MediaSession.Owning(player);
        session.Own(asyncRes);

        await session.DisposeAsync();

        Assert.True(player.IsDisposed);
        Assert.True(asyncRes.DisposedAsync);
    }

    [Fact]
    public void Dispose_IsIdempotent_AndOwnAfterDisposeThrows()
    {
        var player = BuildLivePlayer();
        var flag = new OrderFlag("x", []);
        var session = MediaSession.Owning(player);
        session.Own(flag);

        session.Dispose();
        session.Dispose();

        Assert.Equal(1, flag.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => session.Own(new OrderFlag("late", [])));
    }

    private sealed class OrderFlag(string name, List<string> order) : IDisposable
    {
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            DisposeCount++;
            lock (order)
                order.Add(name);
        }
    }

    private sealed class AsyncDisposeFlag : IAsyncDisposable
    {
        public bool DisposedAsync { get; private set; }
        public ValueTask DisposeAsync()
        {
            DisposedAsync = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SilenceSource(AudioFormat fmt) : IAudioSource
    {
        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => false;
        public int ReadInto(Span<float> dst) { dst.Clear(); return dst.Length; }
    }

    private sealed class SolidVideoSource(VideoFormat fmt) : IVideoSource
    {
        public VideoFormat Format { get; } = fmt;
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = new[] { PixelFormat.Bgra32 };
        public bool IsExhausted => false;
        public void SelectOutputFormat(PixelFormat format) { }

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            frame = new VideoFrame(TimeSpan.Zero, Format, new byte[Format.Width * Format.Height * 4], Format.Width * 4, release: null);
            return true;
        }
    }
}
