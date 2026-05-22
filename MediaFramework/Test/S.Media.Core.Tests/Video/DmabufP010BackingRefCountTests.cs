using System.Runtime.InteropServices;
using S.Media.Core.Video;
using S.Media.Effects;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class DmabufP010BackingRefCountTests
{
    [DllImport("libc", EntryPoint = "dup")]
    private static extern int dup(int fd);

    [Fact]
    public void AddReference_TwoDisposes_ClosesFdsOnce()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var h = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var baseFd = checked((int)h.DangerousGetHandle());
        var y = dup(baseFd);
        var uv = dup(baseFd);
        Assert.True(y >= 0 && uv >= 0);

        var b = new DmabufP010Backing(y, 0, 4, uv, 0, 4, 0, 0);
        b.AddReference();
        b.Dispose();
        b.Dispose();

        Assert.Throws<ObjectDisposedException>(() => b.AddReference());
    }
}
