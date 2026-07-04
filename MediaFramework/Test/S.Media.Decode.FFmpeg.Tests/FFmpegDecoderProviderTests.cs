using System.Reflection;
using S.Media.Core.Video;
using S.Media.Decode.FFmpeg.Video;
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

    [Fact]
    public void StandaloneVideoOpen_DisablesHiddenAudioDemux()
    {
        var options = new VideoSourceOpenOptions
        {
            TryHardwareAcceleration = false,
            RetainDmabufForGl = true,
            AudioPacketQueueDepth = 12,
            VideoPacketQueueDepth = 34,
            FileReadBufferBytes = 1024 * 1024,
            AudioStreamIndex = 2,
            VideoStreamIndex = 3,
        };

        var mapped = MapStandaloneVideo(options);

        Assert.False(mapped.TryHardwareAcceleration);
        Assert.True(mapped.RetainDmabufForGl);
        Assert.Equal(12, mapped.AudioPacketQueueDepth);
        Assert.Equal(34, mapped.VideoPacketQueueDepth);
        Assert.Equal(1024 * 1024, mapped.FileReadBufferBytes);
        Assert.Equal(MediaStreamSelection.Disabled, mapped.AudioStreamIndex);
        Assert.Equal(3, mapped.VideoStreamIndex);
    }

    [Fact]
    public async Task OpenAsync_CancellingABlockedNetworkOpen_AbortsViaInterruptCallback()
    {
        // NXT-02/03: a blocked open (a TCP connect that hangs) aborts when the token is cancelled, via the AVIO
        // interrupt callback fed the open's token — instead of waiting out FFmpeg's full connect timeout.
        // Opt-in (network-dependent): set MFP_RUN_NETWORK_TESTS=1 to run.
        if (Environment.GetEnvironmentVariable("MFP_RUN_NETWORK_TESTS") != "1")
            return;

        var provider = CreateProvider();
        using var cts = new CancellationTokenSource();
        var request = new S.Media.Core.Registry.MediaOpenRequest("http://10.255.255.1/x.mkv")
        {
            Video = new VideoSourceOpenOptions(),
        };
        var open = provider.OpenAsync(request, null, cts.Token).AsTask();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8),
            $"blocked open took {sw.Elapsed} — the interrupt callback didn't abort it");
    }

    private static IMediaDecoderProvider CreateProvider()
    {
        var type = typeof(FFmpegModule).Assembly.GetType("S.Media.Decode.FFmpeg.FFmpegDecoderProvider", throwOnError: true)!;
        return (IMediaDecoderProvider)Activator.CreateInstance(type)!;
    }

    private static VideoDecoderOpenOptions MapStandaloneVideo(VideoSourceOpenOptions? options)
    {
        var type = typeof(FFmpegModule).Assembly.GetType("S.Media.Decode.FFmpeg.FFmpegDecoderProvider", throwOnError: true)!;
        var method = type.GetMethod("MapStandaloneVideo", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (VideoDecoderOpenOptions)method.Invoke(null, [options])!;
    }
}
