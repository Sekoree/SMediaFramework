using S.Media.Core;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

/// <summary>
/// Integration harness for the P2-2 "convert outside the router lock" refactor. Hammers a 2-output
/// topology — a primary that takes the native format plus a branch that needs a CPU converter — with a
/// continuous submit thread and a continuous route add/remove thread (each remove disposes the branch
/// converter). An injected instrumented converter flags any <c>Convert</c> that overlaps or follows its
/// own <c>Dispose</c>. On the current lock-serialized code this passes trivially (no overlap); it stays
/// the regression guard once submission converts outside <c>_gate</c> — a missing converter lifetime
/// lease would surface here as a use-after-dispose.
/// </summary>
public sealed class VideoRouterConcurrencyTests
{
    private const int W = 64, H = 64;
    private static readonly VideoFormat Nv12 = new(W, H, PixelFormat.Nv12, new Rational(60, 1));

    [Fact]
    public void MultiOutput_BranchConverter_ConcurrentSubmitAndRouteToggle_NeverConvertsAfterDispose()
    {
        var savedFactory = MediaFrameworkPlugins.VideoCpuFrameConverterFactory;
        var savedProbe = MediaFrameworkPlugins.VideoCpuFrameCanConvertProbe;
        var tracker = new LifetimeTracker();
        try
        {
            MediaFrameworkPlugins.VideoCpuFrameConverterFactory = () => new InstrumentedConverter(tracker);
            MediaFrameworkPlugins.VideoCpuFrameCanConvertProbe = (_, _, _, _) => true;

            using var router = new VideoRouter(null);
            var primary = new CountingOutput(PixelFormat.Nv12);                 // native — no converter
            var branch = new CountingOutput(PixelFormat.Bgra32);                // different fmt → needs converter
            var primId = router.AddOutput(primary, "primary", synchronous: true);
            var branchId = router.AddOutput(branch, "branch", synchronous: true);
            var input = router.AddInput(primId, "in");
            input.Output.Configure(Nv12);

            using var cts = new CancellationTokenSource();
            Exception? threadError = null;
            var reconfigures = 0;

            var submitThread = new Thread(() =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                        input.Output.Submit(MakeNv12Frame());
                }
                catch (Exception ex) { threadError = ex; }
            });

            var routeThread = new Thread(() =>
            {
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        if (router.TryAddRoute(input.Id, branchId, out _))
                            Interlocked.Increment(ref reconfigures);
                        Thread.SpinWait(200);
                        router.TryRemoveRoute(input.Id, branchId, out _);
                        Interlocked.Increment(ref reconfigures);
                    }
                }
                catch (Exception ex) { threadError = ex; }
            });

            submitThread.Start();
            routeThread.Start();
            Thread.Sleep(600);
            cts.Cancel();
            submitThread.Join(5000);
            routeThread.Join(5000);

            Assert.Null(threadError);
            // The harness must have actually exercised conversion + reconfiguration, or it proves nothing.
            Assert.True(tracker.TotalConverts > 50, $"expected real conversion traffic, got {tracker.TotalConverts}");
            Assert.True(reconfigures > 50, $"expected real route churn, got {reconfigures}");
            // The property under test: a converter must never be used while/after it is disposed.
            Assert.Equal(0, tracker.ConvertAfterDispose);
            Assert.Equal(0, tracker.DisposeDuringConvert);
        }
        finally
        {
            MediaFrameworkPlugins.VideoCpuFrameConverterFactory = savedFactory;
            MediaFrameworkPlugins.VideoCpuFrameCanConvertProbe = savedProbe;
        }
    }

    /// <summary>
    /// P2-7: a converting branch whose output is asynchronous (a <see cref="VideoOutputPump"/>) must repack on
    /// the pump's drain thread, NOT the player submit thread. Proves the conversion ran off-thread, that the
    /// converter saw the raw negotiated frame (a zero-copy fan-out view), that the primary got the native
    /// format while the branch inner got the converted format, and that every shared backing was released
    /// (no leak from the fan-out refcount).
    /// </summary>
    [Fact]
    public void MultiOutput_PumpBranchConverter_RepacksOnPumpThread_NotSubmitThread()
    {
        var savedFactory = MediaFrameworkPlugins.VideoCpuFrameConverterFactory;
        var savedProbe = MediaFrameworkPlugins.VideoCpuFrameCanConvertProbe;
        var tracker = new LifetimeTracker();
        try
        {
            MediaFrameworkPlugins.VideoCpuFrameConverterFactory = () => new InstrumentedConverter(tracker);
            MediaFrameworkPlugins.VideoCpuFrameCanConvertProbe = (_, _, _, _) => true;

            using var router = new VideoRouter(null);
            var primary = new CountingOutput(PixelFormat.Nv12);            // native — receives a raw fan-out view
            var branchInner = new CountingOutput(PixelFormat.Bgra32);      // different fmt → needs a converter
            var primId = router.AddOutput(primary, "primary", synchronous: true);
            // Branch is asynchronous (pump-wrapped): the conversion must run on the pump's drain thread.
            var branchId = router.AddOutput(branchInner, "branch",
                asyncPump: new VideoOutputPumpAttachOptions(MaxQueuedFrames: 512));
            var input = router.AddInput(primId, "in");
            Assert.True(router.TryAddRoute(input.Id, branchId, out _));
            input.Output.Configure(Nv12);

            var submitThreadId = Environment.CurrentManagedThreadId;
            var released = 0;
            const int frames = 120;
            for (var i = 0; i < frames; i++)
                input.Output.Submit(MakeNv12Frame(new CountingRelease(() => Interlocked.Increment(ref released))));

            // Let the pump drain every frame to the inner branch output.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((branchInner.SubmitCount < frames || Volatile.Read(ref released) < frames) && DateTime.UtcNow < deadline)
                Thread.Sleep(10);

            // Primary saw every frame synchronously, in the NATIVE format (zero-copy fan-out view).
            Assert.Equal(frames, primary.SubmitCount);
            Assert.Equal(PixelFormat.Nv12, primary.LastFormat);

            // Branch saw every frame, CONVERTED to its accepted format.
            Assert.Equal(frames, branchInner.SubmitCount);
            Assert.Equal(PixelFormat.Bgra32, branchInner.LastFormat);

            // The conversion ran on the pump thread (off the submit thread) and consumed the raw negotiated
            // frame handed to it as a fan-out view — the whole point of moving it off the submit path.
            Assert.Equal(frames, tracker.TotalConverts);
            Assert.Equal(PixelFormat.Nv12, tracker.LastSourceFormat);
            Assert.NotEqual(0, tracker.LastConvertThreadId);
            Assert.NotEqual(submitThreadId, tracker.LastConvertThreadId);

            // No use-after-dispose, and every shared backing was released (fan-out refcount balanced).
            Assert.Equal(0, tracker.ConvertAfterDispose);
            Assert.Equal(0, tracker.DisposeDuringConvert);
            Assert.Equal(frames, Volatile.Read(ref released));
        }
        finally
        {
            MediaFrameworkPlugins.VideoCpuFrameConverterFactory = savedFactory;
            MediaFrameworkPlugins.VideoCpuFrameCanConvertProbe = savedProbe;
        }
    }

    private static VideoFrame MakeNv12Frame(IDisposable? release = null) =>
        new(TimeSpan.Zero, Nv12, [new byte[W * H], new byte[W * (H / 2)]], [W, W], release: release);

    private sealed class CountingRelease(Action onDispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) onDispose();
        }
    }

    private sealed class LifetimeTracker
    {
        private int _total, _afterDispose, _duringConvert, _lastConvertThreadId;
        private volatile object _lastSourceFormat = PixelFormat.Unknown;
        public int TotalConverts => Volatile.Read(ref _total);
        public int ConvertAfterDispose => Volatile.Read(ref _afterDispose);
        public int DisposeDuringConvert => Volatile.Read(ref _duringConvert);
        public int LastConvertThreadId => Volatile.Read(ref _lastConvertThreadId);
        public PixelFormat LastSourceFormat => (PixelFormat)_lastSourceFormat;
        public void Convert() => Interlocked.Increment(ref _total);
        public void RecordConvert(int threadId, PixelFormat sourceFormat)
        {
            Interlocked.Exchange(ref _lastConvertThreadId, threadId);
            _lastSourceFormat = sourceFormat;
        }
        public void RecordConvertAfterDispose() => Interlocked.Increment(ref _afterDispose);
        public void RecordDisposeDuringConvert() => Interlocked.Increment(ref _duringConvert);
    }

    private sealed class InstrumentedConverter(LifetimeTracker tracker) : IVideoCpuFrameConverter
    {
        private volatile bool _disposed;
        private volatile bool _inConvert;
        private PixelFormat _dst = PixelFormat.Bgra32;
        private int _w = W, _h = H;

        public void Configure(PixelFormat src, PixelFormat dst, int width, int height)
        {
            _dst = dst; _w = width; _h = height;
        }

        public VideoFrame Convert(VideoFrame source, VideoTransferHint hint)
        {
            if (_disposed) tracker.RecordConvertAfterDispose();
            _inConvert = true;
            tracker.Convert();
            tracker.RecordConvert(Environment.CurrentManagedThreadId, source.Format.PixelFormat);
            Thread.SpinWait(200);                 // widen the race window
            if (_disposed) tracker.RecordDisposeDuringConvert();
            _inConvert = false;
            return new VideoFrame(source.PresentationTime, new VideoFormat(_w, _h, _dst, source.Format.FrameRate),
                new byte[_w * _h * 4], _w * 4, release: null);
        }

        public void Dispose()
        {
            if (_inConvert) tracker.RecordDisposeDuringConvert();
            _disposed = true;
        }
    }

    private sealed class CountingOutput(PixelFormat accepted) : IVideoOutput
    {
        private int _submits;
        private volatile object _lastFormat = PixelFormat.Unknown;
        public int SubmitCount => Volatile.Read(ref _submits);
        public PixelFormat LastFormat => (PixelFormat)_lastFormat;
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = new[] { accepted };
        public VideoFormat Format { get; private set; }
        public void Configure(VideoFormat format) => Format = format;
        public void Submit(VideoFrame frame)
        {
            _lastFormat = frame.Format.PixelFormat;
            Interlocked.Increment(ref _submits);
            frame.Dispose(); // output takes ownership
        }
    }
}
