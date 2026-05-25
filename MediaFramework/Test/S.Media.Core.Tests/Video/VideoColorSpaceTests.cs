using S.Media.Core.Video;
using S.Media.Effects;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class VideoColorSpaceTests
{
    [Fact]
    public void Enum_HasExpectedValues()
    {
        // Sanity check the public surface so renames trip the test rather than the
        // downstream consumer.
        Assert.Equal(0, (int)VideoColorSpace.Unspecified);
        Assert.Equal(1, (int)VideoColorSpace.Bt709);
        Assert.Equal(2, (int)VideoColorSpace.Bt601);
        Assert.Equal(3, (int)VideoColorSpace.Bt2020);
    }

    [Fact]
    public void Range_HasExpectedValues()
    {
        Assert.Equal(0, (int)VideoColorRange.Unspecified);
        Assert.Equal(1, (int)VideoColorRange.Limited);
        Assert.Equal(2, (int)VideoColorRange.Full);
    }

    [Fact]
    public void FieldOrder_HasExpectedValues()
    {
        Assert.Equal(0, (int)VideoFieldOrder.Progressive);
        Assert.Equal(1, (int)VideoFieldOrder.TopFieldFirst);
        Assert.Equal(2, (int)VideoFieldOrder.BottomFieldFirst);
    }
}
