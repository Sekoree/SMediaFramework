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

    [Fact]
    public void TryGetFormat_FalseAndWaitForStreams_TimesOut_WhenSourceNeverDelivers()
    {
        // P2-15: a receiver opened to a source that isn't sending reports "no format yet" without
        // throwing, and WaitForStreams returns false on timeout (doesn't hang or throw). Video-only so
        // the test never depends on a real sender being present on the network.
        NDISource source;
        try
        {
            source = NDISource.Open(
                new NDIDiscoveredSource($"nonexistent-{Guid.NewGuid():N}", null),
                new NDISourceOptions { ReceiveAudio = false, ReceiveVideo = true });
        }
        catch
        {
            return; // NDI runtime unavailable in this environment — skip.
        }

        using (source)
        {
            Assert.False(source.TryGetVideoFormat(out var fmt));
            Assert.Equal(default, fmt);
            Assert.False(source.WaitForStreams(TimeSpan.FromMilliseconds(150)));
        }
    }
}
