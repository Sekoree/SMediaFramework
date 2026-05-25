using FFmpeg.AutoGen;
using S.Media.Core.Video;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests.Video;

public class VideoColorSpaceMappingTests
{
    [Fact]
    public void MapColorSpace_BT709()
    {
        Assert.Equal(VideoColorSpace.Bt709, VideoFileDecoder.MapColorSpace(AVColorSpace.AVCOL_SPC_BT709));
    }

    [Fact]
    public void MapColorSpace_BT601_VariantsAreEquivalent()
    {
        Assert.Equal(VideoColorSpace.Bt601, VideoFileDecoder.MapColorSpace(AVColorSpace.AVCOL_SPC_SMPTE170M));
        Assert.Equal(VideoColorSpace.Bt601, VideoFileDecoder.MapColorSpace(AVColorSpace.AVCOL_SPC_BT470BG));
    }

    [Fact]
    public void MapColorSpace_BT2020NCL()
    {
        Assert.Equal(VideoColorSpace.Bt2020, VideoFileDecoder.MapColorSpace(AVColorSpace.AVCOL_SPC_BT2020_NCL));
    }

    [Fact]
    public void MapColorSpace_BT2020CL_PicksClVariant()
    {
        Assert.Equal(VideoColorSpace.Bt2020Cl, VideoFileDecoder.MapColorSpace(AVColorSpace.AVCOL_SPC_BT2020_CL));
    }

    [Fact]
    public void MapColorSpace_Unspecified_StaysUnspecified()
    {
        Assert.Equal(VideoColorSpace.Unspecified, VideoFileDecoder.MapColorSpace(AVColorSpace.AVCOL_SPC_UNSPECIFIED));
    }

    [Fact]
    public void MapColorRange_MapsLimitedFull()
    {
        Assert.Equal(VideoColorRange.Limited, VideoFileDecoder.MapColorRange(AVColorRange.AVCOL_RANGE_MPEG));
        Assert.Equal(VideoColorRange.Full, VideoFileDecoder.MapColorRange(AVColorRange.AVCOL_RANGE_JPEG));
        Assert.Equal(VideoColorRange.Unspecified, VideoFileDecoder.MapColorRange(AVColorRange.AVCOL_RANGE_UNSPECIFIED));
    }
}
