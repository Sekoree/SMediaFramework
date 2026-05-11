using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

public class PixelFormatInfoTests
{
    [Theory]
    [InlineData(PixelFormat.I420, 1281, 721, 0, 1281, 721)]
    [InlineData(PixelFormat.I420, 1281, 721, 1, 641, 361)]
    [InlineData(PixelFormat.I420, 1281, 721, 2, 641, 361)]
    [InlineData(PixelFormat.Nv12, 1281, 721, 1, 1282, 361)]
    [InlineData(PixelFormat.Yuv422P, 1921, 1080, 1, 961, 1080)]
    [InlineData(PixelFormat.P010, 1279, 719, 1, 2560, 360)]
    public void Plane_dimensions_round_up_for_subsampled_formats(
        PixelFormat format, int width, int height, int plane, int expectByteWidth, int expectHeight)
    {
        Assert.Equal(expectHeight, PixelFormatInfo.PlaneHeight(format, height, plane));
        Assert.Equal(expectByteWidth, PixelFormatInfo.PlaneByteWidth(format, width, plane));
    }

    [Fact]
    public void BytesPerSample_high_depth()
    {
        Assert.Equal(2, PixelFormatInfo.BytesPerSample(PixelFormat.Yuv422P10Le));
        Assert.Equal(2, PixelFormatInfo.BytesPerSample(PixelFormat.P010));
        Assert.Equal(1, PixelFormatInfo.BytesPerSample(PixelFormat.I420));
    }

    [Fact]
    public void PlanePitchBufferLength_matches_stride_times_plane_rows()
    {
        Assert.Equal(
            4096 * 721,
            PixelFormatInfo.PlanePitchBufferLength(PixelFormat.I420, 1281, 721, 0, strideBytes: 4096));

        Assert.Equal(
            641 * 361,
            PixelFormatInfo.PlanePitchBufferLength(PixelFormat.I420, 1281, 721, 1, strideBytes: 641));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PixelFormatInfo.PlanePitchBufferLength(PixelFormat.I420, 1281, 721, 0, strideBytes: 100));
    }

    [Fact]
    public void IsAlphaCarrying()
    {
        Assert.True(PixelFormatInfo.IsAlphaCarrying(PixelFormat.Bgra32));
        Assert.True(PixelFormatInfo.IsAlphaCarrying(PixelFormat.Rgba32));
        Assert.True(PixelFormatInfo.IsAlphaCarrying(PixelFormat.Argb32));
        Assert.True(PixelFormatInfo.IsAlphaCarrying(PixelFormat.Abgr32));
        Assert.True(PixelFormatInfo.IsAlphaCarrying(PixelFormat.Yuva420p));
        Assert.False(PixelFormatInfo.IsAlphaCarrying(PixelFormat.I420));
    }

    [Fact]
    public void Yuva420p_has_four_planes_layout()
    {
        Assert.Equal(4, PixelFormatInfo.PlaneCount(PixelFormat.Yuva420p));

        Assert.Equal(1281, PixelFormatInfo.PlaneByteWidth(PixelFormat.Yuva420p, 1281, 0));
        Assert.Equal(641, PixelFormatInfo.PlaneByteWidth(PixelFormat.Yuva420p, 1281, 1));

        Assert.Equal(721, PixelFormatInfo.PlaneHeight(PixelFormat.Yuva420p, 721, 0));
        Assert.Equal(361, PixelFormatInfo.PlaneHeight(PixelFormat.Yuva420p, 721, 1));
        Assert.Equal(721, PixelFormatInfo.PlaneHeight(PixelFormat.Yuva420p, 721, 3));
    }

    [Fact]
    public void Gray16_is_high_depth()
    {
        Assert.True(PixelFormatInfo.IsHighBitDepth(PixelFormat.Gray16));
        Assert.False(PixelFormatInfo.IsHighBitDepth(PixelFormat.Gray8));
    }
}