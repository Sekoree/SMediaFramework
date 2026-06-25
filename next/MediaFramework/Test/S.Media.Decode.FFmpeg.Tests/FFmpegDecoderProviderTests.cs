using Xunit;

namespace S.Media.Decode.FFmpeg.Tests;

public sealed class FFmpegDecoderProviderTests
{
    [Theory]
    [InlineData("/tmp/media.mp4", 0.5)]
    [InlineData("file:///tmp/media.mp4", 0.5)]
    [InlineData("http://example.invalid/media.mp4", 0.5)]
    [InlineData("https://example.invalid/media.mp4", 0.5)]
    [InlineData("rtsp://example.invalid/live", 0.5)]
    [InlineData("ndi://camera", 0.0)]
    [InlineData("capture://camera0", 0.0)]
    [InlineData("image:///tmp/logo.png", 0.0)]
    [InlineData("custom://source", 0.0)]
    public void Probe_ClaimsOnlyFfmpegOwnedSchemes(string uri, double expected)
    {
        var provider = CreateProvider();
        Assert.Equal(expected, provider.Probe(uri, MediaKind.Video));
        Assert.Equal(expected, provider.Probe(uri, MediaKind.Audio));
    }

    private static IMediaDecoderProvider CreateProvider()
    {
        var type = typeof(FFmpegModule).Assembly.GetType("S.Media.Decode.FFmpeg.FFmpegDecoderProvider", throwOnError: true)!;
        return (IMediaDecoderProvider)Activator.CreateInstance(type)!;
    }
}
