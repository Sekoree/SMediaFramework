using S.Media.Core.Video;
using S.Media.Gpu;
using Xunit;

namespace S.Media.Gpu.Tests;

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
    public void GetPrimeGlImportBlocker_Gray8_LumaPath()
    {
        var s = LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker(PixelFormat.Gray8);
        Assert.NotNull(s);
        Assert.Contains("luma", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetPrimeGlImportBlocker_Unknown()
    {
        var s = LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker(PixelFormat.Unknown);
        Assert.NotNull(s);
        Assert.Contains("Unknown", s, StringComparison.Ordinal);
        Assert.Contains("NV12", s, StringComparison.Ordinal);
    }

    [Fact]
    public void GetPrimeGlImportBlocker_Yuyv_PackedPath()
    {
        var s = LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker(PixelFormat.Yuyv);
        Assert.NotNull(s);
        Assert.Contains("Packed", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("P016", s, StringComparison.Ordinal);
    }

    [Fact]
    public void GetPrimeGlImportBlocker_I420_PlanarPath()
    {
        var s = LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker(PixelFormat.I420);
        Assert.NotNull(s);
        Assert.Contains("planar", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetPrimeGlImportBlocker_Uyvy_PackedPath()
    {
        var s = LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker(PixelFormat.Uyvy);
        Assert.NotNull(s);
        Assert.Contains("Packed", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetPrimeGlImportBlocker_Yuv420P10Le_10bitBranch()
    {
        var s = LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker(PixelFormat.Yuv420P10Le);
        Assert.NotNull(s);
        Assert.Contains("10/12-bit", s, StringComparison.Ordinal);
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
