using System.Collections.Concurrent;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Video;
using S.Media.Effects;
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
    public void PlayheadOffset_HoldsVideoBack_ToMatchAudioLatency()
    {
        var src = new FakeVideoSource(VideoFmt(16, 16), Frame(0, 16, 16), Frame(40, 16, 16), Frame(80, 16, 16));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 4)
        {
            EarlyTolerance = TimeSpan.FromMilliseconds(2),
            LateThreshold = TimeSpan.FromMilliseconds(200),
            // Audio is heard 30 ms after the clock; video should be held back the same so it lines up.
            PlayheadOffset = TimeSpan.FromMilliseconds(30),
        };
        player.Play();
        output.WaitForConfigured();
        WaitFor(() => src.Reads >= 3, TimeSpan.FromSeconds(1));

        // Clock at 50 ms, but offset 30 ms → effective playhead 20 ms. The 40 ms frame is still "future",
        // so the 0 ms frame is shown — without the offset the 40 ms frame would already be displayed.
        clock.AdvanceTo(TimeSpan.FromMilliseconds(50));
        clock.RaiseVideoTick();
        WaitFor(() => output.Submitted.Count >= 1, TimeSpan.FromSeconds(1));

        Assert.Single(output.Submitted);
        Assert.Equal(TimeSpan.FromMilliseconds(0), output.Submitted[0].PresentationTime);
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
    public void PresentsNewestLateFrame_AsCatchUp_DroppingOlderLateFrames()
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

        // Playhead jumped to 400ms — 0 and 50 are both >100ms late. Rather than freeze (present nothing),
        // the player shows the NEWEST late frame (50ms) so the picture keeps moving and can catch up, and
        // drops the older late frame (0ms). 500ms is still in the future.
        clock.AdvanceTo(TimeSpan.FromMilliseconds(400));
        clock.RaiseVideoTick();
        WaitFor(() => output.Submitted.Count >= 1, TimeSpan.FromSeconds(1));

        Assert.Single(output.Submitted);
        Assert.Equal(TimeSpan.FromMilliseconds(50), output.Submitted[0].PresentationTime);
        Assert.True(player.DroppedLate >= 1, $"expected ≥1 late drop (the 0ms frame), got {player.DroppedLate}");
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

    [Fact]
    public void Submit_Success_With_Throwing_PresentationEvent_Does_Not_Release_Frame()
    {
        // Regression for the post-submit double-dispose: once Submit succeeds the output owns
        // the frame. A throwing FramePresentationTimePresented subscriber must not make the
        // player release it — an async output may still hold it queued and an early release
        // would free the backing out from under it. (VideoFrame.Dispose is idempotent, so the
        // real symptom is a premature release, not a literal double free.)
        var src = new FakeVideoSource(VideoFmt(16, 16), Frame(0, 16, 16));
        var output = new HoldingVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 4)
        {
            EarlyTolerance = TimeSpan.FromMilliseconds(2),
            LateThreshold = TimeSpan.FromMilliseconds(500),
        };
        player.FramePresentationTimePresented += _ => throw new InvalidOperationException("subscriber boom");
        player.Play();
        output.WaitForConfigured();
        WaitFor(() => src.Reads >= 1, TimeSpan.FromSeconds(1));

        // Re-tick until the decode thread has enqueued the frame and it gets submitted.
        clock.AdvanceTo(TimeSpan.FromMilliseconds(10));
        WaitFor(() =>
        {
            clock.RaiseVideoTick();
            return output.Held.Count >= 1;
        }, TimeSpan.FromSeconds(1));

        // The output holds the frame; despite the throwing event the player must not have
        // released it. The source's outstanding counter only decrements when the backing is
        // released, so it must still read 1.
        Assert.Equal(1, src.UndisposedFramesHandedOut);

        // The output owns it — disposing now runs the release exactly once.
        output.DisposeHeld();
        Assert.Equal(0, src.UndisposedFramesHandedOut);
    }

    [Fact]
    public void DecodeLoop_SourceThrows_FaultsPlayerInsteadOfRethrowing()
    {
        var src = new ThrowingVideoSource(VideoFmt(16, 16));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 2);
        Exception? fault = null;
        using var faulted = new ManualResetEventSlim(false);
        player.Faulted += (_, e) =>
        {
            fault = e.Exception;
            faulted.Set();
        };

        player.Play();

        Assert.True(faulted.Wait(TimeSpan.FromSeconds(2)), "player should surface decode source faults");
        Assert.IsType<InvalidOperationException>(fault);
        Assert.NotNull(player.Fault);
        WaitFor(() => !player.IsRunning, TimeSpan.FromSeconds(1));
        Assert.Throws<InvalidOperationException>(() => player.Play());
    }

    [Fact]
    public void Dispose_AfterDecodeFault_IsIdempotent()
    {
        var src = new ThrowingVideoSource(VideoFmt(16, 16));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();

        var player = new VideoPlayer(src, output, clock, queueCapacity: 2);
        using var faulted = new ManualResetEventSlim(false);
        player.Faulted += (_, _) => faulted.Set();

        player.Play();
        Assert.True(faulted.Wait(TimeSpan.FromSeconds(2)), "player should fault before dispose");

        player.Dispose();
        var secondDispose = Record.Exception(player.Dispose);

        Assert.Null(secondDispose);
    }

    [Fact]
    public void ObjectDisposedException_DuringCooperativeShutdown_DoesNotFaultPlayer()
    {
        var src = new ObjectDisposedOnYieldVideoSource(VideoFmt(16, 16));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 2);
        var faulted = false;
        player.Faulted += (_, _) => faulted = true;

        player.Play();
        Assert.True(src.Entered.Wait(TimeSpan.FromSeconds(2)), "decode loop should enter source read");

        player.Stop();

        Assert.False(faulted);
        Assert.Null(player.Fault);
        Assert.False(player.IsRunning);
    }

    [Fact]
    public void StopCancellation_WithLiveDecodeThread_MakesPlayerNonRestartable()
    {
        var src = new BlockingVideoSource(VideoFmt(16, 16));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        using var player = new VideoPlayer(src, output, clock, queueCapacity: 2);

        try
        {
            player.Play();
            Assert.True(src.Entered.Wait(TimeSpan.FromSeconds(2)), "decode loop should be blocked in source read");

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.Throws<OperationCanceledException>(() => player.Stop(cts.Token));
            Assert.NotNull(player.Fault);
            Assert.Throws<InvalidOperationException>(() => player.Play());
        }
        finally
        {
            src.Release();
        }
    }

    [Fact]
    public void Play_RealignsSeekableSourceWhenDemuxDriftExceedsStartupLead()
    {
        var inner = new FakeVideoSource(VideoFmt(16, 16), Frame(0, 16, 16), Frame(42, 16, 16));
        var src = new SeekableFakeVideoSource(inner) { Position = TimeSpan.FromSeconds(2) };
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        clock.AdvanceTo(TimeSpan.FromMilliseconds(500));

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 2);
        player.Play();
        output.WaitForConfigured();

        Assert.Equal(1, src.SeekCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(500), src.LastSeek);
    }

    [Fact]
    public void Play_AlwaysRealignsSeekableSourceOnResumeEvenWhenDriftIsWithinStartupLead()
    {
        var inner = new FakeVideoSource(VideoFmt(16, 16), Frame(0, 16, 16), Frame(42, 16, 16));
        var src = new SeekableFakeVideoSource(inner) { Position = TimeSpan.FromMilliseconds(480) };
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        clock.AdvanceTo(TimeSpan.FromMilliseconds(500));

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 2);
        player.Play();
        output.WaitForConfigured();

        Assert.Equal(1, src.SeekCalls);
        Assert.Equal(TimeSpan.FromMilliseconds(500), src.LastSeek);
    }

    [Fact]
    public void Play_SkipsSeekableSourceRealignWhenAlreadyAtClock()
    {
        var inner = new FakeVideoSource(VideoFmt(16, 16), Frame(500, 16, 16), Frame(542, 16, 16));
        var src = new SeekableFakeVideoSource(inner) { Position = TimeSpan.FromMilliseconds(500) };
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        clock.AdvanceTo(TimeSpan.FromMilliseconds(500));

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 2);
        player.Play();
        output.WaitForConfigured();

        Assert.Equal(0, src.SeekCalls);
    }

    [Fact]
    public void HasPresentableFrameAt_RequiresFrameAtOrBeforePlayheadPlusEarlyTolerance()
    {
        var src = new FakeVideoSource(VideoFmt(16, 16), Frame(100, 16, 16), Frame(200, 16, 16));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 2)
        {
            EarlyTolerance = TimeSpan.FromMilliseconds(8),
        };
        player.Play();
        output.WaitForConfigured();
        WaitFor(() => player.QueuedFrameCount >= 2, TimeSpan.FromSeconds(1));

        Assert.False(player.HasPresentableFrameAt(TimeSpan.FromMilliseconds(50)));
        Assert.True(player.HasPresentableFrameAt(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public void PresentBufferedFrameForSync_SubmitsFrameBeforeClockTick()
    {
        var src = new FakeVideoSource(VideoFmt(16, 16), Frame(1000, 16, 16), Frame(1042, 16, 16));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();
        clock.AdvanceTo(TimeSpan.FromSeconds(1));

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 4);
        player.Play();
        output.WaitForConfigured();
        WaitFor(() => player.QueuedFrameCount >= 2, TimeSpan.FromSeconds(1));

        Assert.Empty(output.Submitted);

        Assert.True(player.TryPresentBufferedFrameForSync(clock.CurrentPosition, TimeSpan.Zero));

        Assert.Single(output.Submitted);
        Assert.Equal(TimeSpan.FromSeconds(1), output.Submitted[0].PresentationTime);
        Assert.Equal(1, player.DisplayedCount);
    }

    [Fact]
    public void PresentBufferedFrameForSync_AcceptsFirstFutureFrameWithinStartupLead()
    {
        var src = new FakeVideoSource(VideoFmt(16, 16), Frame(42, 16, 16), Frame(84, 16, 16));
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 4);
        player.Play();
        output.WaitForConfigured();
        WaitFor(() => player.QueuedFrameCount >= 1, TimeSpan.FromSeconds(1));

        Assert.True(player.TryPresentBufferedFrameForSync(TimeSpan.Zero, TimeSpan.Zero));

        Assert.Single(output.Submitted);
        Assert.Equal(TimeSpan.FromMilliseconds(42), output.Submitted[0].PresentationTime);
        Assert.Equal(1, player.DisplayedCount);
    }

    [Fact]
    public void Pause_DrainsQueue_SoQueuedFrameCountIsZeroWhileLifetimePendingMayRemain()
    {
        var frames = Enumerable.Range(0, 24)
            .Select(i => Frame(i * 42, 16, 16))
            .ToArray();
        var src = new FakeVideoSource(VideoFmt(16, 16), frames);
        var output = new FakeVideoOutput([PixelFormat.Bgra32]);
        var clock = new FakeMediaClock();

        using var player = new VideoPlayer(src, output, clock, queueCapacity: 16);
        player.Play();
        output.WaitForConfigured();
        WaitFor(() => player.QueuedFrameCount >= 2, TimeSpan.FromSeconds(2));

        clock.AdvanceTo(TimeSpan.FromMilliseconds(100));
        clock.RaiseVideoTick();
        WaitFor(() => player.DisplayedCount >= 1, TimeSpan.FromSeconds(1));

        player.Pause();

        Assert.Equal(0, player.QueuedFrameCount);
        Assert.True(player.PendingBufferedCount >= 2);
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
            release: DisposableRelease.Wrap(() => Interlocked.Decrement(ref _outstanding)));
        return true;
    }
}

internal sealed class ThrowingVideoSource : IVideoSource
{
    public ThrowingVideoSource(VideoFormat format)
    {
        Format = format;
        NativePixelFormats = new[] { format.PixelFormat };
    }

    public VideoFormat Format { get; }
    public IReadOnlyList<PixelFormat> NativePixelFormats { get; }
    public bool IsExhausted => false;
    public void SelectOutputFormat(PixelFormat format) { }
    public bool TryReadNextFrame(out VideoFrame frame) => throw new InvalidOperationException("source boom");
}

internal sealed class ObjectDisposedOnYieldVideoSource : IVideoSource, ICooperativeVideoReadInterrupt
{
    private readonly ManualResetEventSlim _yieldRequested = new(false);

    public ObjectDisposedOnYieldVideoSource(VideoFormat format)
    {
        Format = format;
        NativePixelFormats = new[] { format.PixelFormat };
    }

    public ManualResetEventSlim Entered { get; } = new(false);
    public VideoFormat Format { get; }
    public IReadOnlyList<PixelFormat> NativePixelFormats { get; }
    public bool IsExhausted => false;
    public void SelectOutputFormat(PixelFormat format) { }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        Entered.Set();
        _yieldRequested.Wait(TimeSpan.FromSeconds(5));
        throw new ObjectDisposedException(nameof(ObjectDisposedOnYieldVideoSource));
    }

    public void RequestYieldBetweenReads() => _yieldRequested.Set();

    public void ClearYieldRequest() => _yieldRequested.Reset();
}

internal sealed class BlockingVideoSource : IVideoSource
{
    private readonly ManualResetEventSlim _release = new(false);

    public BlockingVideoSource(VideoFormat format)
    {
        Format = format;
        NativePixelFormats = new[] { format.PixelFormat };
    }

    public ManualResetEventSlim Entered { get; } = new(false);
    public VideoFormat Format { get; }
    public IReadOnlyList<PixelFormat> NativePixelFormats { get; }
    public bool IsExhausted => false;
    public void SelectOutputFormat(PixelFormat format) { }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        Entered.Set();
        _release.Wait();
        frame = null!;
        return false;
    }

    public void Release() => _release.Set();
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

internal sealed class HoldingVideoOutput : IVideoOutput
{
    private readonly PixelFormat[] _accepted;
    private readonly ManualResetEventSlim _configured = new(false);

    /// <summary>Frames the output has taken ownership of and is still holding (mimics an async output).</summary>
    public List<VideoFrame> Held { get; } = new();
    public VideoFormat Format { get; private set; }

    public HoldingVideoOutput(PixelFormat[] accepted) => _accepted = accepted;

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _accepted;

    public void Configure(VideoFormat format)
    {
        Format = format;
        _configured.Set();
    }

    // Takes ownership but does not dispose in Submit — like a pump/render-thread output that
    // disposes later. The player must never dispose a frame after a successful Submit.
    public void Submit(VideoFrame frame) => Held.Add(frame);

    public void DisposeHeld()
    {
        foreach (var f in Held) f.Dispose();
        Held.Clear();
    }

    public void WaitForConfigured() => _configured.Wait(TimeSpan.FromSeconds(1));
}

internal sealed class SeekableFakeVideoSource : IVideoSource, ISeekableSource
{
    private readonly FakeVideoSource _inner;

    public SeekableFakeVideoSource(FakeVideoSource inner) => _inner = inner;

    public TimeSpan Duration => TimeSpan.FromHours(1);
    public TimeSpan Position { get; set; }
    public int SeekCalls { get; private set; }
    public TimeSpan LastSeek { get; private set; }

    public void Seek(TimeSpan position)
    {
        SeekCalls++;
        LastSeek = position;
        Position = position;
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
