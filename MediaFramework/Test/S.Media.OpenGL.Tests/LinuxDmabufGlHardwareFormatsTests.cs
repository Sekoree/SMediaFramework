using S.Media.Core.Video;
using S.Media.OpenGL;
using Xunit;

namespace S.Media.OpenGL.Tests;

public sealed class LinuxDmabufGlHardwareFormatsTests
{
    [Fact]
    public void IsSupportedForPrimeGlImport_Nv12_True()
    {
        Assert.True(LinuxDmabufGlHardwareFormats.IsSupportedForPrimeGlImport(PixelFormat.Nv12));
    }

    [Fact]
    public void GetPrimeGlImportBlocker_Nv12_Null()
    {
        Assert.Null(LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker(PixelFormat.Nv12));
    }

    [Fact]
    public void IsSupportedForPrimeGlImport_P010_True()
    {
        Assert.True(LinuxDmabufGlHardwareFormats.IsSupportedForPrimeGlImport(PixelFormat.P010));
    }

    [Fact]
    public void GetPrimeGlImportBlocker_P010_Null()
    {
        Assert.Null(LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker(PixelFormat.P010));
    }

    [Fact]
    public void IsSupportedForPrimeGlImport_P016_True()
    {
        Assert.True(LinuxDmabufGlHardwareFormats.IsSupportedForPrimeGlImport(PixelFormat.P016));
    }

    [Fact]
    public void GetPrimeGlImportBlocker_P016_Null()
    {
        Assert.Null(LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker(PixelFormat.P016));
    }

    [Fact]
    public void GetPrimeGlImportBlocker_Bgra32_PackedPath()
    {
        var s = LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker(PixelFormat.Bgra32);
        Assert.NotNull(s);
        Assert.Contains("Packed", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetPrimeGlImportBlocker_I420_PlanarPath()
    {
        var s = LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker(PixelFormat.I420);
        Assert.NotNull(s);
        Assert.Contains("planar", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsSupportedForPrimeGlImport_MatchesBlocker()
    {
        foreach (PixelFormat pf in Enum.GetValues<PixelFormat>())
        {
            var blockerNull = LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker(pf) is null;
            Assert.Equal(blockerNull, LinuxDmabufGlHardwareFormats.IsSupportedForPrimeGlImport(pf));
        }
    }
}
