using S.Media.Core.Audio;
using S.Media.Decode.FFmpeg.Audio;
using Xunit;

namespace S.Media.Decode.FFmpeg.Tests;

public sealed class AdaptiveRateAudioOutputTests
{
    private sealed class RecordingOutput(AudioFormat format) : IAudioOutput
    {
        public AudioFormat Format { get; } = format;
        public long FramesSubmitted { get; private set; }
        public void Submit(ReadOnlySpan<float> packedSamples) => FramesSubmitted += packedSamples.Length / Format.Channels;
    }

    private static float[] Stereo(int frames) => new float[checked(frames * 2)];

    [FFmpegNativeFact]
    public void NegativeBias_ClampsToMaxDelta_LoweringOutputRate()
    {
        var inner = new RecordingOutput(new AudioFormat(48_000, 2));
        using var aro = new AdaptiveRateAudioOutput(inner, getPlaybackPpmBias: () => -1000.0, maxRateDeltaHz: 3);

        aro.Submit(Stereo(4_800));

        Assert.Equal(48_000 - 3, aro.EffectiveOutputRate); // clamp to nominal - maxRateDeltaHz
    }

    [FFmpegNativeFact]
    public void PositiveBias_ClampsToMaxDelta_RaisingOutputRate()
    {
        var inner = new RecordingOutput(new AudioFormat(48_000, 2));
        using var aro = new AdaptiveRateAudioOutput(inner, () => 1000.0, maxRateDeltaHz: 3);

        aro.Submit(Stereo(4_800));

        Assert.Equal(48_003, aro.EffectiveOutputRate);
    }

    [FFmpegNativeFact]
    public void ZeroBias_KeepsNominalRate_ForwardsRoughlyAllFrames()
    {
        var inner = new RecordingOutput(new AudioFormat(48_000, 2));
        using var aro = new AdaptiveRateAudioOutput(inner, () => 0.0);

        const int inFrames = 48_000;
        aro.Submit(Stereo(inFrames));

        Assert.Equal(48_000, aro.EffectiveOutputRate);
        Assert.InRange(inner.FramesSubmitted, inFrames - 1024, inFrames); // ~1:1, allow small resampler latency
    }

    [FFmpegNativeFact]
    public void NegativeBias_ShavesFramesOverTime()
    {
        var inner = new RecordingOutput(new AudioFormat(48_000, 2));
        using var aro = new AdaptiveRateAudioOutput(inner, () => -50.0, maxRateDeltaHz: 3);

        const int inFrames = 48_000;
        aro.Submit(Stereo(inFrames));

        // 47997/48000 → fewer output frames than input: the wrapper is shedding samples to ease a slow output.
        Assert.True(inner.FramesSubmitted < inFrames,
            $"expected < {inFrames} forwarded frames, got {inner.FramesSubmitted}");
    }

    [FFmpegNativeFact]
    public void NullBias_Throws()
    {
        var inner = new RecordingOutput(new AudioFormat(48_000, 2));
        Assert.Throws<ArgumentNullException>(() => new AdaptiveRateAudioOutput(inner, null!));
    }
}
