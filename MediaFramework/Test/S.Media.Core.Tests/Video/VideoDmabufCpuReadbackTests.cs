using System.Runtime.InteropServices;
using S.Media.Core.Video;
using S.Media.Effects;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class VideoDmabufCpuReadbackTests
{
    private const uint MfdCloexec = 1;
    private const int ProtRead = 1;
    private const int ProtWrite = 2;
    private const int MapShared = 0x01;

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int memfd_create(string name, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int ftruncate(int fd, long length);

    [DllImport("libc", EntryPoint = "mmap", SetLastError = true)]
    private static extern IntPtr mmap(IntPtr addr, UIntPtr length, int prot, int flags, int fd, IntPtr offset);

    [DllImport("libc", EntryPoint = "munmap", SetLastError = true)]
    private static extern int munmap(IntPtr addr, UIntPtr length);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int close(int fd);

    [Fact]
    public void TryCreateNv12CpuCopy_round_trips_single_fd_nv12_layout_on_linux()
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

        var fd = memfd_create("nv12rb", MfdCloexec);
        if (fd < 0)
            return;

        if (ftruncate(fd, total) != 0)
        {
            _ = close(fd);
            return;
        }

        var mapped = mmap(IntPtr.Zero, (UIntPtr)total, ProtRead | ProtWrite, MapShared, fd, IntPtr.Zero);
        if (mapped == new IntPtr(-1))
        {
            _ = close(fd);
            return;
        }

        try
        {
            Marshal.WriteByte(mapped, 0, 0xAA);
            Marshal.WriteByte(mapped, ySize, 0xBB);
        }
        finally
        {
            _ = munmap(mapped, (UIntPtr)total);
        }

        using var backing = new DmabufNv12Backing(fd, 0, yPitch, fd, ySize, uvPitch, 0, 0);
        var vf = new VideoFormat(w, h, PixelFormat.Nv12, new Rational(30, 1));
        using var frame = VideoFrame.CreateNv12Dmabuf(TimeSpan.Zero, vf, backing);
        Assert.True(VideoDmabufCpuReadback.TryCreateNv12CpuCopy(frame, out var cpu));
        using (cpu!)
        {
            Assert.Null(cpu.DmabufNv12);
            Assert.Equal(0xAA, cpu.Planes[0].Span[0]);
            Assert.Equal(0xBB, cpu.Planes[1].Span[0]);
        }
    }
}
