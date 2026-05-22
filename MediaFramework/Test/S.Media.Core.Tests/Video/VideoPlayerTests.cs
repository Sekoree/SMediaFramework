using System.Collections.Concurrent;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

public class VideoPlayerTests
{
    [Fact]
    public void Submits_Latest_Frame_At_Or_Before_Playhead()
    {
        var src = new FakeVideoSource(VideoFmt(16, 16), Frame(0, 16, 16), Frame(40, 16, 16),
            Frame(80, 16, 16), Frame(120, 16, 16));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 4)
        {
            EarlyTolerance = TimeSpan.FromMilliseconds(2),
            LateThreshold = TimeSpan.FromMilliseconds(200),
        };
        player.Play();
        output.WaitForConfigured();
        WaitFor(() => src.Reads >= 4, TimeSpan.FromSeconds(1));

        // Playhead at 50ms — both 0ms and 40ms frames are eligible. Latest wins → 40ms.
        clock.AdvanceTo(TimeSpan.FromMilliseconds(50));
        clock.RaiseVideoTick();
        WaitFor(() => output.Submitted.Count >= 1, TimeSpan.FromSeconds(1));

        Assert.Single(output.Submitted);
        Assert.Equal(TimeSpan.FromMilliseconds(40), output.Submitted[0].PresentationTime);

        // Advance to 90ms — 80ms is now displayable (40 already shown).
        clock.AdvanceTo(TimeSpan.FromMilliseconds(90));
        clock.RaiseVideoTick();
        WaitFor(() => output.Submitted.Count >= 2, TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromMilliseconds(80), output.Submitted[1].PresentationTime);

        Assert.True(player.DroppedLate >= 1, "the 0ms frame should have been dropped as 'skipped'");
    }

    [Fact]
    public void Does_Not_Submit_Future_Frames()
    {
        var src = new FakeVideoSource(VideoFmt(16, 16), Frame(100, 16, 16), Frame(200, 16, 16));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 2);
        player.Play();
        output.WaitForConfigured();
        WaitFor(() => src.Reads >= 2, TimeSpan.FromSeconds(1));

        // Playhead at 50ms — first frame is at 100ms, in the future → no submit.
        clock.AdvanceTo(TimeSpan.FromMilliseconds(50));
        clock.RaiseVideoTick();
        Thread.Sleep(20);
        Assert.Empty(output.Submitted);
    }

    [Fact]
    public void Drops_Frames_Past_LateThreshold()
    {
        var src = new FakeVideoSource(VideoFmt(16, 16), Frame(0, 16, 16), Frame(50, 16, 16),
            Frame(500, 16, 16));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 4)
        {
            LateThreshold = TimeSpan.FromMilliseconds(100),
        };
        player.Play();
        output.WaitForConfigured();
        WaitFor(() => src.Reads >= 3, TimeSpan.FromSeconds(1));

        // Playhead jumped to 400ms — 0 and 50 are both >100ms late and should be discarded.
        clock.AdvanceTo(TimeSpan.FromMilliseconds(400));
        clock.RaiseVideoTick();
        Thread.Sleep(50);

        Assert.Empty(output.Submitted); // 500ms is in the future
        Assert.True(player.DroppedLate >= 2, $"expected ≥2 late drops, got {player.DroppedLate}");
    }

    [Fact]
    public void Pause_completes_when_decode_blocks_on_slot_semaphore_without_ticks()
    {
        var fmt = VideoFmt(8, 8);
        var src = new FakeVideoSource(fmt, Frame(0, 8, 8), Frame(10_000, 8, 8), Frame(20_000, 8, 8));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        clock.Start();

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 1, decodePollInterval: TimeSpan.FromMilliseconds(2));
        player.Play();
        output.WaitForConfigured();
        // Queue holds one frame; decode is blocked on SemaphoreSlim.Wait for the next slot.
        // No VideoTick is raised — regression for Ctrl+C where IsRunning is cleared before join.
        WaitFor(() => src.Reads >= 2, TimeSpan.FromSeconds(2));

        player.Pause();
        Assert.False(player.IsRunning);
    }

    [Fact]
    public void HoldLastFrameAtEnd_KeepsResubmittingFinalFrameAfterExhaustion()
    {
        var src = new FakeVideoSource(VideoFmt(16, 16),
            Frame(0, 16, 16), Frame(40, 16, 16), Frame(80, 16, 16));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 4)
        {
            EarlyTolerance = TimeSpan.FromMilliseconds(2),
            LateThreshold = TimeSpan.FromMilliseconds(500),
            HoldLastFrameAtEnd = true,
        };
        player.Play();
        output.WaitForConfigured();
        WaitFor(() => src.Reads >= 3, TimeSpan.FromSeconds(1));

        // Tick well past every frame so the last (80ms) is the chosen submission AND
        // the source has already exhausted by the time we tick.
        WaitFor(() => src.IsExhausted, TimeSpan.FromSeconds(1));
        clock.AdvanceTo(TimeSpan.FromMilliseconds(100));
        clock.RaiseVideoTick();

        WaitFor(() => output.Submitted.Count >= 1, TimeSpan.FromSeconds(1));
        Assert.True(player.IsHoldingLastFrame, "captured a held frame on the last-frame submission");
        var baselineSubmissions = output.Submitted.Count;

        // Additional ticks past exhaustion should keep delivering frames (the held one) —
        // not signal CompletedNaturally.
        clock.AdvanceTo(TimeSpan.FromMilliseconds(500));
        clock.RaiseVideoTick();
        clock.AdvanceTo(TimeSpan.FromMilliseconds(1000));
        clock.RaiseVideoTick();
        WaitFor(() => output.Submitted.Count >= baselineSubmissions + 2, TimeSpan.FromSeconds(1));

        Assert.False(player.CompletedNaturally);
        Assert.True(player.HeldFrameSubmitCount >= 2);
        // Held frames advance PTS with the playhead.
        var lastPts = output.Submitted[^1].PresentationTime;
        Assert.True(lastPts >= TimeSpan.FromMilliseconds(500),
            $"held frame PTS should track the playhead; got {lastPts}");
    }

    [Fact]
    public void HoldLastFrameAtEnd_Off_StillSignalsCompletedNaturally()
    {
        var src = new FakeVideoSource(VideoFmt(16, 16),
            Frame(0, 16, 16), Frame(40, 16, 16));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 4)
        {
            LateThreshold = TimeSpan.FromMilliseconds(500),
            HoldLastFrameAtEnd = false,
        };
        player.Play();
        output.WaitForConfigured();
        WaitFor(() => src.Reads >= 2, TimeSpan.FromSeconds(1));
        WaitFor(() => src.IsExhausted, TimeSpan.FromSeconds(1));

        clock.AdvanceTo(TimeSpan.FromMilliseconds(100));
        clock.RaiseVideoTick();
        WaitFor(() => output.Submitted.Count >= 1, TimeSpan.FromSeconds(1));

        // A second tick after exhaustion — without HoldLastFrameAtEnd, completes naturally.
        clock.AdvanceTo(TimeSpan.FromMilliseconds(500));
        clock.RaiseVideoTick();
        WaitFor(() => player.CompletedNaturally, TimeSpan.FromSeconds(1));

        Assert.False(player.IsHoldingLastFrame);
        Assert.Equal(0, player.HeldFrameSubmitCount);
    }

    [Fact]
    public void Stop_Drains_Queue_And_Disposes_Frames()
    {
        var src = new FakeVideoSource(VideoFmt(16, 16), Frame(0, 16, 16), Frame(40, 16, 16),
            Frame(80, 16, 16));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();

        var player = new VideoPlayer(src, output, clock, queueCapacity: 4);
        player.Play();
        output.WaitForConfigured();
        WaitFor(() => src.Reads >= 3, TimeSpan.FromSeconds(1));

        // Decode thread will have queued all 3 frames; they're outstanding.
        Assert.True(src.UndisposedFramesHandedOut > 0);
        player.Stop();
        // After stop, every frame the source produced should have been disposed
        // (either submitted to output which would dispose, or drained on stop).
        Assert.Equal(0, src.UndisposedFramesHandedOut);
        Assert.True(player.DroppedDrain >= 3);
        player.Dispose();
    }

    private static VideoFormat VideoFmt(int w, int h)
        => new(w, h, PixelFormat.Bgra32, new Rational(30, 1));

    private static (TimeSpan pts, byte[] data, int stride) Frame(int ms, int w, int h)
    {
        var stride = w * 4;
        var bytes = new byte[stride * h];
        return (TimeSpan.FromMilliseconds(ms), bytes, stride);
    }

    private static void WaitFor(Func<bool> cond, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException("WaitFor condition never became true");
            Thread.Sleep(5);
        }
    }
}

internal sealed class FakeVideoSource : IVideoSource
{
    private readonly VideoFormat _format;
    private readonly ConcurrentQueue<(TimeSpan pts, byte[] data, int stride)> _frames;
    private int _outstanding;
    public int Reads;

    public FakeVideoSource(VideoFormat format, params (TimeSpan pts, byte[] data, int stride)[] frames)
    {
        _format = format;
        _frames = new ConcurrentQueue<(TimeSpan, byte[], int)>(frames);
    }

    public VideoFormat Format => _format;
    public IReadOnlyList<PixelFormat> NativePixelFormats => new[] { _format.PixelFormat };
    public bool IsExhausted => _frames.IsEmpty;
    public int UndisposedFramesHandedOut => Volatile.Read(ref _outstanding);

    public void SelectOutputFormat(PixelFormat format) { /* no-op for tests */ }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        Interlocked.Increment(ref Reads);
        if (!_frames.TryDequeue(out var f))
        {
            frame = null!;
            return false;
        }
        Interlocked.Increment(ref _outstanding);
        frame = new VideoFrame(f.pts, _format, f.data, f.stride,
            release: () => Interlocked.Decrement(ref _outstanding));
        return true;
    }
}

internal sealed class FakeVideoOutput : IVideoOutput
{
    private readonly PixelFormat[] _accepted;
    private readonly ManualResetEventSlim _configured = new(false);
    public List<VideoFrame> Submitted { get; } = new();
    public VideoFormat Format { get; private set; }

    public FakeVideoOutput(PixelFormat[] accepted) => _accepted = accepted;

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _accepted;

    public void Configure(VideoFormat format)
    {
        Format = format;
        _configured.Set();
    }

    public void Submit(VideoFrame frame)
    {
        // Take a snapshot so Submitted survives frame.Dispose, then dispose
        // the frame so the source's outstanding counter decrements.
        Submitted.Add(frame);
        frame.Dispose();
    }

    public void WaitForConfigured() => _configured.Wait(TimeSpan.FromSeconds(1));
}

internal sealed class SeekableFakeVideoSource : IVideoSource, ISeekableSource
{
    private readonly FakeVideoSource _inner;

    public SeekableFakeVideoSource(FakeVideoSource inner) => _inner = inner;

    public TimeSpan Duration => TimeSpan.FromHours(1);
    public TimeSpan Position => TimeSpan.Zero;

    public void Seek(TimeSpan position)
    {
    }

    public VideoFormat Format => _inner.Format;
    public IReadOnlyList<PixelFormat> NativePixelFormats => _inner.NativePixelFormats;
    public bool IsExhausted => _inner.IsExhausted;
    public int Reads => _inner.Reads;
    public int UndisposedFramesHandedOut => _inner.UndisposedFramesHandedOut;
    public void SelectOutputFormat(PixelFormat format) => _inner.SelectOutputFormat(format);

    public bool TryReadNextFrame(out VideoFrame frame) => _inner.TryReadNextFrame(out frame);
}

internal sealed class FakeMediaClock : IMediaClock
{
    private TimeSpan _position;
    public TimeSpan CurrentPosition { get { lock (this) return _position; } }
    public bool IsRunning { get; private set; }
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? AudioTick;
    public event EventHandler? VideoTick;

    public void AdvanceTo(TimeSpan p) { lock (this) _position = p; PositionChanged?.Invoke(this, p); }
    public void RaiseVideoTick() => VideoTick?.Invoke(this, EventArgs.Empty);
    public void RaiseAudioTick() => AudioTick?.Invoke(this, EventArgs.Empty);

    public void Start() => IsRunning = true;
    public void Stop(CancellationToken cancellationToken = default) => IsRunning = false;
    public void Pause(CancellationToken cancellationToken = default) => IsRunning = false;
    public void Reset() { _position = TimeSpan.Zero; }
    public void Seek(TimeSpan position) => AdvanceTo(position);
    public void SetMaster(IPlaybackClock? master) { /* no-op */ }

    public double PlaybackRate => 1.0;
}
