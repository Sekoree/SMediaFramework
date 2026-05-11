using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class HardwareVideoInteropTests
{
    [Fact]
    public void NoOpInterop_DisablesImports()
    {
        IHardwareVideoInterop h = new NoOpHardwareVideoInterop();
        Assert.False(h.IsGpuImportSupported);
        Assert.Equal(0, h.PlatformContextHandle);
        Assert.False(h.TryDescribeImportedSurface((nint)1, out var d));
        Assert.Equal(default, d);
    }
}
