using System.Runtime.InteropServices;
using S.Media.Core.Video;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests.Video;

public sealed class VideoRouterTests
{
    private static class LinuxSyscall
    {
        [DllImport("libc", EntryPoint = "dup")]
        public static extern int dup(int fd);
    }

    private static class LinuxMemFd
    {
        [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern int memfd_create(string name, uint flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int ftruncate(int fd, long length);

        [DllImport("libc", EntryPoint = "mmap", SetLastError = true)]
        public static extern IntPtr mmap(IntPtr addr, UIntPtr length, int prot, int flags, int fd, IntPtr offset);

        [DllImport("libc", EntryPoint = "munmap", SetLastError = true)]
        public static extern int munmap(IntPtr addr, UIntPtr length);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        public static extern int close(int fd);
    }

    public VideoRouterTests() => FFmpegRuntime.EnsureInitialized();

    /// <summary>
    /// Tests use synchronous output registration so <see cref="VideoRouterInputSink.Submit"/> delivers
    /// straight to the test sink (no <see cref="VideoSinkPump"/> drainer thread to race with assertions).
    /// Sites that explicitly exercise the async-pump path keep their <c>asyncPump:</c> argument.
    /// </summary>
    private static string AddSyncOutput(VideoRouter router, IVideoSink sink, string id,
        bool disposeSinkOnRouterDispose = false) =>
        router.AddOutput(sink, id, disposeSinkOnRouterDispose: disposeSinkOnRouterDispose, synchronous: true);

    [Fact]
    public void AddInput_ThrowsWhenPrimaryOutputAlreadyRouted()
    {
        using var router = new VideoRouter(null);
        var a = new CapturingSink(PixelFormat.I420);
        var oa = AddSyncOutput(router, a, "a");
        _ = router.AddInput(oa);
        var ex = Assert.Throws<InvalidOperationException>(() => router.AddInput(oa));
        Assert.Contains("already routed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryAddRoute_ReturnsFalseWhenOutputOwnedByAnotherInput()
    {
        using var router = new VideoRouter(null);
        var s1 = new CapturingSink(PixelFormat.I420);
        var s2 = new CapturingSink(PixelFormat.I420);
        var s3 = new CapturingSink(PixelFormat.I420);
        var o1 = AddSyncOutput(router, s1, "o1");
        var o2 = AddSyncOutput(router, s2, "o2");
        var o3 = AddSyncOutput(router, s3, "o3");
        var inA = router.AddInput(o1);
        Assert.True(router.TryAddRoute(inA.Id, o2, out _));
        var inB = router.AddInput(o3);
        Assert.False(router.TryAddRoute(inB.Id, o2, out var err));
        Assert.NotNull(err);
        Assert.Contains("already routed", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FanOut_CpuNv12_ThreeSinks_InvokesBackingReleaseOnce()
    {
        using var router = new VideoRouter(null);
        var s0 = new CapturingSink(PixelFormat.Nv12);
        var s1 = new CapturingSink(PixelFormat.Nv12);
        var s2 = new CapturingSink(PixelFormat.Nv12);
        var o0 = AddSyncOutput(router, s0, "o0");
        var o1 = AddSyncOutput(router, s1, "o1");
        var o2 = AddSyncOutput(router, s2, "o2");
        var vin = router.AddInput(o0);
        Assert.True(router.TryAddRoute(vin.Id, o1, out _));
        Assert.True(router.TryAddRoute(vin.Id, o2, out _));

        var vf = new VideoFormat(64, 64, PixelFormat.Nv12, new Rational(24, 1));
        vin.Sink.Configure(vf);

        var releaseCalls = 0;
        var y = new byte[64 * 64];
        var uv = new byte[64 * 32];
        var frame = new VideoFrame(TimeSpan.Zero, vf, [y, uv], [64, 64], release: () => Interlocked.Increment(ref releaseCalls));
        vin.Sink.Submit(frame);

        Assert.Equal(1, s0.SubmitCount);
        Assert.Equal(1, s1.SubmitCount);
        Assert.Equal(1, s2.SubmitCount);
        Assert.Equal(1, releaseCalls);
    }

    [Fact]
    public void FanOut_SubmitHitsAllRoutedSinks()
    {
        using var router = new VideoRouter(null);
        var primary = new CapturingSink(PixelFormat.I420);
        var branch = new CapturingSink(PixelFormat.I420);
        var op = AddSyncOutput(router, primary, "p");
        var ob = AddSyncOutput(router, branch, "b");
        var vin = router.AddInput(op);
        Assert.True(router.TryAddRoute(vin.Id, ob, out _));

        var vf = new VideoFormat(64, 64, PixelFormat.I420, new Rational(24, 1));
        vin.Sink.Configure(vf);

        var y = new byte[64 * 64];
        var u = new byte[32 * 32];
        var v = new byte[32 * 32];
        using var frame = new VideoFrame(TimeSpan.Zero, vf,
            [y, u, v],
            [64, 32, 32]);
        vin.Sink.Submit(frame);

        Assert.Equal(1, primary.SubmitCount);
        Assert.Equal(1, branch.SubmitCount);
    }

    [Fact]
    public void TryGetInputFanOutPixelFormats_reports_branch_cpu_converter()
    {
        using var router = new VideoRouter(null);
        var primary = new CapturingSink(PixelFormat.Yuv422P10Le);
        var branch = new CapturingSink(PixelFormat.Uyvy, PixelFormat.Bgra32, PixelFormat.Nv12);
        var op = AddSyncOutput(router, primary, "p");
        var ob = AddSyncOutput(router, branch, "b");
        var vin = router.AddInput(op);
        Assert.True(router.TryAddRoute(vin.Id, ob, out _));

        var vf = new VideoFormat(128, 64, PixelFormat.Yuv422P10Le, new Rational(30, 1));
        vin.Sink.Configure(vf);

        Assert.True(router.TryGetInputFanOutPixelFormats(vin.Id, out var neg, out var list));
        Assert.NotNull(list);
        Assert.Equal(PixelFormat.Yuv422P10Le, neg.PixelFormat);
        Assert.Equal(2, list!.Count);
        Assert.Equal("p", list[0].OutputId);
        Assert.Equal(PixelFormat.Yuv422P10Le, list[0].PixelFormat);
        Assert.False(list[0].UsesRouterCpuConverter);
        Assert.Equal("b", list[1].OutputId);
        Assert.Equal(PixelFormat.Uyvy, list[1].PixelFormat);
        Assert.True(list[1].UsesRouterCpuConverter);
    }

    [Fact]
    public void FanOut_DmabufNv12_HitsBothNv12Sinks_OnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var router = new VideoRouter(null);
        var primary = new CapturingSink(PixelFormat.Nv12);
        var branch = new CapturingSink(PixelFormat.Nv12);
        var op = AddSyncOutput(router, primary, "p");
        var ob = AddSyncOutput(router, branch, "b");
        var vin = router.AddInput(op);
        Assert.True(router.TryAddRoute(vin.Id, ob, out _));

        var vfFormat = new VideoFormat(64, 64, PixelFormat.Nv12, new Rational(24, 1));
        vin.Sink.Configure(vfFormat);

        using var h = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var baseFd = checked((int)h.DangerousGetHandle());
        var y = LinuxSyscall.dup(baseFd);
        var uv = LinuxSyscall.dup(baseFd);
        var backing = new VideoDmabufNv12Backing(y, 0, 64, uv, 0, 64, 0, 0);
        var frame = VideoFrame.CreateNv12Dmabuf(TimeSpan.Zero, vfFormat, backing);
        vin.Sink.Submit(frame);

        Assert.Equal(1, primary.SubmitCount);
        Assert.Equal(1, branch.SubmitCount);
    }

    [Fact]
    public void FanOut_DmabufP016_HitsBothP016Sinks_OnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var router = new VideoRouter(null);
        var primary = new CapturingSink(PixelFormat.P016);
        var branch = new CapturingSink(PixelFormat.P016);
        var op = AddSyncOutput(router, primary, "p");
        var ob = AddSyncOutput(router, branch, "b");
        var vin = router.AddInput(op);
        Assert.True(router.TryAddRoute(vin.Id, ob, out _));

        var vfFormat = new VideoFormat(64, 64, PixelFormat.P016, new Rational(24, 1));
        vin.Sink.Configure(vfFormat);

        using var h = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var baseFd = checked((int)h.DangerousGetHandle());
        var y = LinuxSyscall.dup(baseFd);
        var uv = LinuxSyscall.dup(baseFd);
        var backing = new VideoDmabufP016Backing(y, 0, 4, uv, 0, 4, 0, 0);
        var frame = VideoFrame.CreateP016Dmabuf(TimeSpan.Zero, vfFormat, backing);
        vin.Sink.Submit(frame);

        Assert.Equal(1, primary.SubmitCount);
        Assert.Equal(1, branch.SubmitCount);
    }

    [Fact]
    public void FanOut_DmabufNv12_BranchConversion_Readback_Converts_OnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        const int w = 64;
        const int h = 64;
        const int yPitch = 64;
        const int uvPitch = 64;
        var ySize = yPitch * h;
        var uvSize = uvPitch * (h / 2);
        var total = ySize + uvSize;

        var fd = LinuxMemFd.memfd_create("vrtr", 1);
        if (fd < 0)
            return;
        if (LinuxMemFd.ftruncate(fd, total) != 0)
        {
            _ = LinuxMemFd.close(fd);
            return;
        }

        var mapped = LinuxMemFd.mmap(IntPtr.Zero, (UIntPtr)total, 3, 0x01, fd, IntPtr.Zero);
        if (mapped == new IntPtr(-1))
        {
            _ = LinuxMemFd.close(fd);
            return;
        }

        try
        {
            Marshal.WriteByte(mapped, 0, 0x11);
            Marshal.WriteByte(mapped, ySize, 0x22);
        }
        finally
        {
            _ = LinuxMemFd.munmap(mapped, (UIntPtr)total);
        }

        using var router = new VideoRouter(null);
        var primary = new CapturingSink(PixelFormat.Nv12);
        var branch = new CapturingSink(PixelFormat.Bgra32);
        var op = AddSyncOutput(router, primary, "p");
        var ob = AddSyncOutput(router, branch, "b");
        var vin = router.AddInput(op);
        Assert.True(router.TryAddRoute(vin.Id, ob, out _));

        var vfFormat = new VideoFormat(w, h, PixelFormat.Nv12, new Rational(24, 1));
        vin.Sink.Configure(vfFormat);

        using var backing = new VideoDmabufNv12Backing(fd, 0, yPitch, fd, ySize, uvPitch, 0, 0);
        var frame = VideoFrame.CreateNv12Dmabuf(TimeSpan.Zero, vfFormat, backing);
        vin.Sink.Submit(frame);

        Assert.Equal(1, primary.SubmitCount);
        Assert.Equal(1, branch.SubmitCount);
    }

    [Fact]
    public void FanOut_DmabufNv12_BranchConverter_UnreadableDmaBuf_Throws_OnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var router = new VideoRouter(null);
        var primary = new CapturingSink(PixelFormat.Nv12);
        var branch = new CapturingSink(PixelFormat.Bgra32);
        var op = AddSyncOutput(router, primary, "p");
        var ob = AddSyncOutput(router, branch, "b");
        var vin = router.AddInput(op);
        Assert.True(router.TryAddRoute(vin.Id, ob, out _));

        var vfFormat = new VideoFormat(64, 64, PixelFormat.Nv12, new Rational(24, 1));
        vin.Sink.Configure(vfFormat);

        using var h = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var baseFd = checked((int)h.DangerousGetHandle());
        var y = LinuxSyscall.dup(baseFd);
        var uv = LinuxSyscall.dup(baseFd);
        var backing = new VideoDmabufNv12Backing(y, 0, 64, uv, 0, 64, 0, 0);
        var frame = VideoFrame.CreateNv12Dmabuf(TimeSpan.Zero, vfFormat, backing);

        var ex = Assert.Throws<NotSupportedException>(() => vin.Sink.Submit(frame));
        Assert.Contains("mmap-read", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddOutput_AsyncPump_RouterDisposesPump_InnerNotDisposedWhenOptedOut()
    {
        using var router = new VideoRouter(null);
        var inner = new TrackingSink(PixelFormat.I420);
        var oid = router.AddOutput(inner, "p", disposeSinkOnRouterDispose: true,
            asyncPump: new VideoSinkPumpAttachOptions(2, "test-pump", null, DisposeInnerSinkWhenPumpDisposes: false));

        Assert.Equal("p", oid);
        router.Dispose();
        Assert.False(inner.Disposed, "inner must stay alive when pump does not own it");
    }

    [Fact]
    public void AddOutput_AsyncPump_DisposesInnerWhenPumpOwnsIt()
    {
        using var router = new VideoRouter(null);
        var inner = new TrackingSink(PixelFormat.I420);
        _ = router.AddOutput(inner, "p", disposeSinkOnRouterDispose: true,
            asyncPump: new VideoSinkPumpAttachOptions(2, DisposeInnerSinkWhenPumpDisposes: true));
        router.Dispose();
        Assert.True(inner.Disposed);
    }

    [Fact]
    public void TryGetVideoSinkPumpMetrics_returns_depth_and_capacity()
    {
        using var router = new VideoRouter(null);
        var inner = new CapturingSink(PixelFormat.I420);
        var oid = router.AddOutput(inner, "p", disposeSinkOnRouterDispose: true,
            asyncPump: new VideoSinkPumpAttachOptions(5, "probe-pump", null, DisposeInnerSinkWhenPumpDisposes: true));

        Assert.True(router.TryGetVideoSinkPumpMetrics(oid, out var m));
        Assert.Equal(5, m.MaxQueueDepth);
        Assert.Equal(0, m.CurrentQueuedDepth);
        Assert.Equal(0, m.DroppedFrames);
    }

    [Fact]
    public void PumpPressure_on_async_branch_reports_output_id_and_non_decreasing_totals()
    {
        using var router = new VideoRouter(null);
        var primary = new CapturingSink(PixelFormat.I420);
        var branchInner = new RouterSlowSink(PixelFormat.I420, delayMs: 80);
        var op = AddSyncOutput(router, primary, "p");
        var ob = router.AddOutput(branchInner, "b", disposeSinkOnRouterDispose: true,
            asyncPump: new VideoSinkPumpAttachOptions(2, "pump-b", null, DisposeInnerSinkWhenPumpDisposes: true));
        var vin = router.AddInput(op);
        Assert.True(router.TryAddRoute(vin.Id, ob, out _));

        var branchEvents = new List<(string OutputId, long Total)>();
        router.PumpPressure += (_, e) => branchEvents.Add((e.OutputId, e.DroppedFramesTotal));

        var vf = new VideoFormat(32, 32, PixelFormat.I420, new Rational(24, 1));
        vin.Sink.Configure(vf);
        var y = new byte[32 * 32];
        var u = new byte[16 * 16];
        var v = new byte[16 * 16];
        for (var i = 0; i < 20; i++)
        {
            var f = new VideoFrame(TimeSpan.FromMilliseconds(i * 5), vf, [y, u, v], [32, 16, 16]);
            vin.Sink.Submit(f);
        }

        Thread.Sleep(500);
        var bOnly = branchEvents.Where(e => e.OutputId == "b").Select(e => e.Total).ToList();
        Assert.NotEmpty(bOnly);
        for (var i = 1; i < bOnly.Count; i++)
            Assert.True(bOnly[i] >= bOnly[i - 1]);
    }

    private sealed class RouterSlowSink : IVideoSink
    {
        private readonly PixelFormat[] _acc;
        private readonly int _delayMs;

        public RouterSlowSink(PixelFormat acc, int delayMs)
        {
            _acc = [acc];
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

    private sealed class TrackingSink : IVideoSink, IDisposable
    {
        private readonly PixelFormat[] _accepted;

        public TrackingSink(params PixelFormat[] accepted) => _accepted = accepted;

        public bool Disposed { get; private set; }
        public int SubmitCount { get; private set; }
        public VideoFormat Format { get; private set; }

        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _accepted;

        public void Configure(VideoFormat format) => Format = format;

        public void Submit(VideoFrame frame)
        {
            SubmitCount++;
            frame.Dispose();
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class CapturingSink : IVideoSink
    {
        private readonly PixelFormat[] _accepted;

        public CapturingSink(params PixelFormat[] accepted) => _accepted = accepted;

        public int SubmitCount { get; private set; }
        public VideoFormat Format { get; private set; }

        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _accepted;

        public void Configure(VideoFormat format) => Format = format;

        public void Submit(VideoFrame frame)
        {
            SubmitCount++;
            frame.Dispose();
        }
    }
}
