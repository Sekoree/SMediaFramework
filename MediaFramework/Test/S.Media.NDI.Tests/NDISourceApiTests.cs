using NDILib;
using S.Media.NDI;
using Xunit;

namespace S.Media.NDI.Tests;

public sealed class NDISourceApiTests
{
    [Fact]
    public void Find_returns_array_never_null()
    {
        var sources = NDISource.Find(TimeSpan.FromMilliseconds(100));
        Assert.NotNull(sources);
    }

    [Fact]
    public void Open_throws_when_both_streams_disabled()
    {
        var source = new NDIDiscoveredSource("test", null);
        Assert.Throws<ArgumentException>(() =>
            NDISource.Open(source, new NDISourceOptions { ReceiveAudio = false, ReceiveVideo = false }));
    }

    [Fact]
    public void NDISourceOptions_Default_enables_both_streams()
    {
        var o = NDISourceOptions.Default;
        Assert.True(o.ReceiveAudio);
        Assert.True(o.ReceiveVideo);
    }
}
