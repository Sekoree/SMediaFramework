using FFmpeg.AutoGen;
using S.Media.Core.Video;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests.Video;

public class VideoTransferHintMappingTests
{
    [Fact]
    public void MapTransferHint_smpte2084_is_pq()
    {
        Assert.Equal(VideoTransferHint.FromPq,
            VideoFileDecoder.MapTransferHint(AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084));
    }

    [Fact]
    public void MapTransferHint_arib_b67_is_hlg()
    {
        Assert.Equal(VideoTransferHint.FromHlg,
            VideoFileDecoder.MapTransferHint(AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67));
    }

    [Fact]
    public void MapTransferHint_bt709_and_smpte170_map_sdr()
    {
        Assert.Equal(VideoTransferHint.Sdr,
            VideoFileDecoder.MapTransferHint(AVColorTransferCharacteristic.AVCOL_TRC_BT709));
        Assert.Equal(VideoTransferHint.Sdr,
            VideoFileDecoder.MapTransferHint(AVColorTransferCharacteristic.AVCOL_TRC_SMPTE170M));
    }

    [Fact]
    public void MapTransferHint_unspecified_stays_unspecified()
    {
        Assert.Equal(VideoTransferHint.Unspecified,
            VideoFileDecoder.MapTransferHint(AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED));
    }
}
