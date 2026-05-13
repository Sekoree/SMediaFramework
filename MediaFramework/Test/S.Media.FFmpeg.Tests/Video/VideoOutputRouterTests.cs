using System.Runtime.InteropServices;
using S.Media.Core.Video;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests.Video;

public sealed class VideoOutputRouterTests
{
    private static class LinuxSyscall
    {
        [DllImport("libc", EntryPoint = "dup")]
        public static extern int dup(int fd);
    }

    [Fact]
    public void CpuNv12_SameNv12Branch_InvokesBackingReleaseOnce()
    {
        var primary = new CountSink(PixelFormat.Nv12);
        var branch = new CountSink(PixelFormat.Nv12);
        using var router = new VideoOutputRouter(primary, branch, disposeBranch: false);

        var fmt = new VideoFormat(64, 64, PixelFormat.Nv12, new Rational(24, 1));
        router.Configure(fmt);

        var releaseCalls = 0;
        var y = new byte[64 * 64];
        var uv = new byte[64 * 32];
        var frame = new VideoFrame(TimeSpan.Zero, fmt, [y, uv], [64, 64], release: () => Interlocked.Increment(ref releaseCalls));
        router.Submit(frame);

        Assert.Equal(1, primary.Count);
        Assert.Equal(1, branch.Count);
        Assert.Equal(1, releaseCalls);
    }

    [Fact]
    public void DmabufNv12_SameNv12Branch_SubmitsBoth_OnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var primary = new CountSink(PixelFormat.Nv12);
        var branch = new CountSink(PixelFormat.Nv12);
        using var router = new VideoOutputRouter(primary, branch, disposeBranch: false);

        var fmt = new VideoFormat(64, 64, PixelFormat.Nv12, new Rational(24, 1));
        router.Configure(fmt);

        using var h = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var baseFd = checked((int)h.DangerousGetHandle());
        var y = LinuxSyscall.dup(baseFd);
        var uv = LinuxSyscall.dup(baseFd);
        var backing = new VideoDmabufNv12Backing(y, 0, 64, uv, 0, 64, 0, 0);
        var frame = VideoFrame.CreateNv12Dmabuf(TimeSpan.Zero, fmt, backing);
        router.Submit(frame);

        Assert.Equal(1, primary.Count);
        Assert.Equal(1, branch.Count);
    }

    [Fact]
    public void DmabufP016_SameP016Branch_SubmitsBoth_OnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var primary = new CountSink(PixelFormat.P016);
        var branch = new CountSink(PixelFormat.P016);
        using var router = new VideoOutputRouter(primary, branch, disposeBranch: false);

        var fmt = new VideoFormat(64, 64, PixelFormat.P016, new Rational(24, 1));
        router.Configure(fmt);

        using var h = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var baseFd = checked((int)h.DangerousGetHandle());
        var y = LinuxSyscall.dup(baseFd);
        var uv = LinuxSyscall.dup(baseFd);
        var backing = new VideoDmabufP016Backing(y, 0, 4, uv, 0, 4, 0, 0);
        var frame = VideoFrame.CreateP016Dmabuf(TimeSpan.Zero, fmt, backing);
        router.Submit(frame);

        Assert.Equal(1, primary.Count);
        Assert.Equal(1, branch.Count);
    }

    private sealed class CountSink : IVideoSink
    {
        private readonly PixelFormat[] _accepted;

        public CountSink(params PixelFormat[] accepted) => _accepted = accepted;

        public int Count { get; private set; }
        public VideoFormat Format { get; private set; }
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _accepted;

        public void Configure(VideoFormat format) => Format = format;

        public void Submit(VideoFrame f)
        {
            Count++;
            f.Dispose();
        }
    }
}
