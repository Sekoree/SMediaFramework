using S.Media.Core.Video;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests.Video;

public class VideoOutputPumpTests
{
    public VideoOutputPumpTests() => FFmpegRuntime.EnsureInitialized();

    [Fact]
    public void Submit_EnqueuesAndDrainsToInner()
    {
        var inner = new CountingOutput([PixelFormat.I420]);
        using var pump = new VideoOutputPump(inner, maxQueuedFrames: 4, disposeInnerOnDispose: true);
        var vf = new VideoFormat(32, 32, PixelFormat.I420, new Rational(24, 1));
        pump.Configure(vf);

        var y = new byte[32 * 32];
        var u = new byte[16 * 16];
        var v = new byte[16 * 16];
        for (var i = 0; i < 3; i++)
        {
            var f = new VideoFrame(TimeSpan.FromMilliseconds(i * 40), vf, [y, u, v], [32, 16, 16]);
            pump.Submit(f);
        }

        Thread.Sleep(200);
        Assert.Equal(3, inner.SubmitCount);
        Assert.True(pump.SubmittedFrames >= 3);
    }

    [Fact]
    public void Metrics_max_depth_and_slow_inner_allow_queued_depth()
    {
        var inner = new SlowOutput([PixelFormat.I420], delayMs: 60);
        using var pump = new VideoOutputPump(inner, maxQueuedFrames: 3, disposeInnerOnDispose: true);
        var vf = new VideoFormat(32, 32, PixelFormat.I420, new Rational(24, 1));
        pump.Configure(vf);
        Assert.Equal(3, pump.MaxQueueDepth);

        var y = new byte[32 * 32];
        var u = new byte[16 * 16];
        var v = new byte[16 * 16];
        for (var i = 0; i < 8; i++)
        {
            var f = new VideoFrame(TimeSpan.FromMilliseconds(i * 40), vf, [y, u, v], [32, 16, 16]);
            pump.Submit(f);
        }

        Thread.Sleep(120);
        Assert.InRange(pump.CurrentQueuedDepth, 0, 3);
        Assert.True(pump.DroppedFrames >= 0);
        Thread.Sleep(800);
        Assert.Equal(0, pump.CurrentQueuedDepth);
    }

    [Fact]
    public void PumpPressure_fires_on_drop_with_monotonic_total_and_pump_name()
    {
        var inner = new SlowOutput([PixelFormat.I420], delayMs: 80);
        using var pump = new VideoOutputPump(inner, maxQueuedFrames: 2, name: "probe-pump", disposeInnerOnDispose: true);
        var totals = new List<long>();
        pump.PumpPressure += (_, e) =>
        {
            Assert.Equal("probe-pump", e.PumpName);
            totals.Add(e.DroppedFramesTotal);
        };

        var vf = new VideoFormat(32, 32, PixelFormat.I420, new Rational(24, 1));
        pump.Configure(vf);
        var y = new byte[32 * 32];
        var u = new byte[16 * 16];
        var v = new byte[16 * 16];
        for (var i = 0; i < 16; i++)
        {
            var f = new VideoFrame(TimeSpan.FromMilliseconds(i * 5), vf, [y, u, v], [32, 16, 16]);
            pump.Submit(f);
        }

        Thread.Sleep(400);
        Assert.NotEmpty(totals);
        for (var i = 1; i < totals.Count; i++)
            Assert.True(totals[i] >= totals[i - 1]);
        Assert.True(pump.DroppedFrames >= totals[^1]);
    }

    [Fact]
    public void Dispose_signals_cooperative_abort_so_drainer_exits_promptly()
    {
        var inner = new BlockingAbortableOutput([PixelFormat.I420]);
        var pump = new VideoOutputPump(inner, maxQueuedFrames: 2, disposeInnerOnDispose: true);
        var vf = new VideoFormat(32, 32, PixelFormat.I420, new Rational(24, 1));
        pump.Configure(vf);

        // One frame: the drain thread enters BlockingAbortableOutput.Submit and parks until aborted.
        var f = new VideoFrame(TimeSpan.Zero, vf, [new byte[32 * 32], new byte[16 * 16], new byte[16 * 16]], [32, 16, 16]);
        pump.Submit(f);
        Assert.True(inner.SubmitEntered.Wait(TimeSpan.FromSeconds(2)), "drainer never entered inner Submit");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        pump.Dispose();
        sw.Stop();

        Assert.True(inner.AbortRequested, "Dispose did not call RequestSubmitAbort on the inner");
        // The abort unblocks the inner Submit immediately, so the drainer exits and Dispose returns well
        // under the 2 s join cap — i.e. it took the clean teardown path, not the leak fallback.
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1500), $"Dispose took {sw.ElapsedMilliseconds} ms; cooperative abort did not unblock the drainer");
    }

    private sealed class BlockingAbortableOutput : IVideoOutput, IVideoOutputCooperativeAbort
    {
        private readonly PixelFormat[] _acc;
        private readonly ManualResetEventSlim _release = new(false);

        public BlockingAbortableOutput(PixelFormat[] acc) => _acc = acc;

        public ManualResetEventSlim SubmitEntered { get; } = new(false);
        public volatile bool AbortRequested;
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _acc;
        public void Configure(VideoFormat format) => Format = format;

        public void Submit(VideoFrame frame)
        {
            SubmitEntered.Set();
            _release.Wait();   // block until RequestSubmitAbort releases us (or would block out the join cap)
            frame.Dispose();
        }

        public void RequestSubmitAbort()
        {
            AbortRequested = true;
            _release.Set();
        }
    }

    private sealed class SlowOutput : IVideoOutput
    {
        private readonly PixelFormat[] _acc;
        private readonly int _delayMs;

        public SlowOutput(PixelFormat[] acc, int delayMs)
        {
            _acc = acc;
            _delayMs = delayMs;
        }

        public int SubmitCount { get; private set; }
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _acc;
        public void Configure(VideoFormat format) => Format = format;

        public void Submit(VideoFrame frame)
        {
            Thread.Sleep(_delayMs);
            SubmitCount++;
            frame.Dispose();
        }
    }

    private sealed class CountingOutput : IVideoOutput
    {
        private readonly PixelFormat[] _acc;
        public CountingOutput(PixelFormat[] acc) => _acc = acc;
        public int SubmitCount { get; private set; }
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _acc;
        public void Configure(VideoFormat format) => Format = format;
        public void Submit(VideoFrame frame)
        {
            SubmitCount++;
            Thread.Sleep(5);
            frame.Dispose();
        }
    }
}
