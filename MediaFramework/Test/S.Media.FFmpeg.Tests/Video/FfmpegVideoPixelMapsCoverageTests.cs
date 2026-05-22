using System.Linq;
using FFmpeg.AutoGen;
using S.Media.Core.Video;
using S.Media.FFmpeg.Video.Internal;
using Xunit;

namespace S.Media.FFmpeg.Tests.Video;

public sealed class FfmpegVideoPixelMapsCoverageTests
{
    /// <summary>
    /// Every <see cref="PixelFormat"/> except <see cref="PixelFormat.Unknown"/> must have an
    /// FFmpeg mapping. Catches future enum additions that forget to wire the libav side —
    /// otherwise the decoder silently falls back to BGRA32 via swscale.
    /// </summary>
    [Fact]
    public void EveryPixelFormat_HasFfmpegMapping()
    {
        var missing = System.Enum.GetValues<PixelFormat>()
            .Where(pf => pf != PixelFormat.Unknown && FfmpegVideoPixelMaps.ToAvPixelFormat(pf) is null)
            .ToArray();

        Assert.Empty(missing);
    }

    [Theory]
    [InlineData(PixelFormat.Yuva422P)]
    [InlineData(PixelFormat.Yuva444P)]
    [InlineData(PixelFormat.Yuva420P10Le)]
    [InlineData(PixelFormat.Yuva422P10Le)]
    [InlineData(PixelFormat.Yuva444P10Le)]
    [InlineData(PixelFormat.Yuva422P12Le)]
    [InlineData(PixelFormat.Yuva444P12Le)]
    [InlineData(PixelFormat.Yuva420P16Le)]
    [InlineData(PixelFormat.Yuva422P16Le)]
    [InlineData(PixelFormat.Yuva444P16Le)]
    [InlineData(PixelFormat.Yuv422P12Le)]
    [InlineData(PixelFormat.Yuv444P12Le)]
    [InlineData(PixelFormat.Rgba16)]
    [InlineData(PixelFormat.Rgba16F)]
    [InlineData(PixelFormat.P216)]
    public void NewFormats_MapToSpecificAvPixelFormat(PixelFormat fmt)
    {
        var av = FfmpegVideoPixelMaps.ToAvPixelFormat(fmt);
        Assert.NotNull(av);

        var expected = fmt switch
        {
            PixelFormat.Yuva422P => AVPixelFormat.AV_PIX_FMT_YUVA422P,
            PixelFormat.Yuva444P => AVPixelFormat.AV_PIX_FMT_YUVA444P,
            PixelFormat.Yuva420P10Le => AVPixelFormat.AV_PIX_FMT_YUVA420P10LE,
            PixelFormat.Yuva422P10Le => AVPixelFormat.AV_PIX_FMT_YUVA422P10LE,
            PixelFormat.Yuva444P10Le => AVPixelFormat.AV_PIX_FMT_YUVA444P10LE,
            PixelFormat.Yuva422P12Le => AVPixelFormat.AV_PIX_FMT_YUVA422P12LE,
            PixelFormat.Yuva444P12Le => AVPixelFormat.AV_PIX_FMT_YUVA444P12LE,
            PixelFormat.Yuva420P16Le => AVPixelFormat.AV_PIX_FMT_YUVA420P16LE,
            PixelFormat.Yuva422P16Le => AVPixelFormat.AV_PIX_FMT_YUVA422P16LE,
            PixelFormat.Yuva444P16Le => AVPixelFormat.AV_PIX_FMT_YUVA444P16LE,
            PixelFormat.Yuv422P12Le => AVPixelFormat.AV_PIX_FMT_YUV422P12LE,
            PixelFormat.Yuv444P12Le => AVPixelFormat.AV_PIX_FMT_YUV444P12LE,
            PixelFormat.Rgba16 => AVPixelFormat.AV_PIX_FMT_RGBA64LE,
            PixelFormat.Rgba16F => AVPixelFormat.AV_PIX_FMT_RGBAF16LE,
            PixelFormat.P216 => AVPixelFormat.AV_PIX_FMT_P216LE,
            _ => throw new System.ArgumentOutOfRangeException(nameof(fmt)),
        };
        Assert.Equal(expected, av);
    }

    /// <summary>
    /// PixelFormatInfo metadata must agree across the new family: alpha-carrying flag set on YUVA
    /// variants, high-bit-depth flag set on every LE 10/12/16 variant, plane count = 3 for non-alpha
    /// planar / 4 for YUVA.
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Yuva422P, 4, true, false)]
    [InlineData(PixelFormat.Yuva444P, 4, true, false)]
    [InlineData(PixelFormat.Yuva420P10Le, 4, true, true)]
    [InlineData(PixelFormat.Yuva422P10Le, 4, true, true)]
    [InlineData(PixelFormat.Yuva444P10Le, 4, true, true)]
    [InlineData(PixelFormat.Yuva422P12Le, 4, true, true)]
    [InlineData(PixelFormat.Yuva444P12Le, 4, true, true)]
    [InlineData(PixelFormat.Yuva420P16Le, 4, true, true)]
    [InlineData(PixelFormat.Yuva422P16Le, 4, true, true)]
    [InlineData(PixelFormat.Yuva444P16Le, 4, true, true)]
    [InlineData(PixelFormat.Yuv422P12Le, 3, false, true)]
    [InlineData(PixelFormat.Yuv444P12Le, 3, false, true)]
    [InlineData(PixelFormat.Rgba16, 1, true, true)]
    [InlineData(PixelFormat.Rgba16F, 1, true, true)]
    [InlineData(PixelFormat.P216, 2, false, true)]
    [InlineData(PixelFormat.Pa16, 3, true, true)]
    public void NewFormats_PixelFormatInfo_Coherent(PixelFormat fmt, int planeCount, bool alpha, bool highBit)
    {
        Assert.Equal(planeCount, PixelFormatInfo.PlaneCount(fmt));
        Assert.Equal(alpha, PixelFormatInfo.IsAlphaCarrying(fmt));
        Assert.Equal(highBit, PixelFormatInfo.IsHighBitDepth(fmt));
    }
}
