using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Decode.FFmpeg.Tests;

public sealed class RegistryOutputResamplerTests
{
    private sealed class CountingOutput(AudioFormat format) : IAudioOutput
    {
        public AudioFormat Format => format;
        public int SubmittedSamples { get; private set; }
        public void Submit(ReadOnlySpan<float> packedSamples) => SubmittedSamples += packedSamples.Length;
    }

    [FFmpegNativeFact]
    public void FFmpegModule_AdaptsRouterRateIntoFixedRateOutput()
    {
        using var registry = MediaRegistry.Build(builder => builder.Use(new FFmpegModule()));
        var sink = new CountingOutput(new AudioFormat(48_000, 2));
        var adapted = registry.CreateResamplingOutput(sink, new AudioFormat(44_100, 2));

        Assert.NotNull(adapted);
        Assert.Equal(new AudioFormat(44_100, 2), adapted.Format);
        adapted.Submit(new float[441 * 2]);
        Assert.True(sink.SubmittedSamples > 0);

        Assert.IsAssignableFrom<IDisposable>(adapted).Dispose();
    }
}
