using S.Media.Core.Audio;
using S.Media.FFmpeg.Audio;
using Xunit;

namespace S.Media.FFmpeg.Tests.Audio;

/// <summary>
/// Phase 2 P2.5 — exercises <see cref="ResamplingAudioSource"/> directly and through
/// <see cref="AudioRouter.AddSource(IAudioSource, string?, bool)"/>'s autoResample path.
/// </summary>
public sealed class ResamplingAudioSourceTests
{
    public ResamplingAudioSourceTests() => FFmpegRuntime.EnsureInitialized();

    [Fact]
    public void Wrapper_PresentsTargetRate_AndProducesRoughlyExpectedFrameCount()
    {
        const int srcRate = 44100;
        const int dstRate = 48000;
        const int channels = 2;
        const int srcFrames = 4410; // 100 ms @ 44.1 kHz

        var src = new ConstantToneSource(srcRate, channels, totalFrames: srcFrames);
        using var wrapper = new ResamplingAudioSource(src, dstRate, disposeInnerWhenDisposed: false);
        Assert.Equal(dstRate, wrapper.Format.SampleRate);
        Assert.Equal(channels, wrapper.Format.Channels);

        // Pull in 480-frame (10 ms @ 48 kHz) chunks until exhausted.
        var dst = new float[480 * channels];
        var totalDstFrames = 0;
        for (var i = 0; i < 32 && !wrapper.IsExhausted; i++)
        {
            var read = wrapper.ReadInto(dst);
            totalDstFrames += read / channels;
        }

        // 100 ms at 48 kHz ≈ 4800 dst frames (allow some swresample lag).
        Assert.InRange(totalDstFrames, 4700, 4900);
    }

    [Fact]
    public void Router_AddSource_AutoResample_True_AcceptsMismatchedSource()
    {
        using var router = new AudioRouter(sampleRate: 48000);
        var src = new ConstantToneSource(sampleRate: 44100, channels: 2, totalFrames: 1024);

        // Would throw without autoResample; should succeed with it.
        var id = router.AddSource(src, "tone44k", autoResample: true);
        Assert.Equal("tone44k", id);

        // The router's tracked source presents at the router rate (the wrapper).
        // Remove disposes the wrapper but not the caller's source.
        Assert.True(router.RemoveSource(id));
        Assert.False(src.Disposed, "router must not dispose the caller's original source on RemoveSource");
    }

    [Fact]
    public void Router_AddSource_AutoResample_False_ThrowsOnMismatchedRate()
    {
        using var router = new AudioRouter(sampleRate: 48000);
        var src = new ConstantToneSource(sampleRate: 44100, channels: 2, totalFrames: 1024);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            router.AddSource(src, autoResample: false));
        Assert.Contains("doesn't match router", ex.Message);
    }

    private sealed class ConstantToneSource : IAudioSource, IDisposable
    {
        private readonly AudioFormat _fmt;
        private int _framesEmitted;
        private readonly int _totalFrames;

        public ConstantToneSource(int sampleRate, int channels, int totalFrames)
        {
            _fmt = new AudioFormat(sampleRate, channels);
            _totalFrames = totalFrames;
        }

        public AudioFormat Format => _fmt;
        public bool IsExhausted => _framesEmitted >= _totalFrames;
        public bool Disposed { get; private set; }

        public int ReadInto(Span<float> destination)
        {
            var remaining = _totalFrames - _framesEmitted;
            if (remaining <= 0) return 0;
            var dstFrames = destination.Length / _fmt.Channels;
            var toEmit = Math.Min(dstFrames, remaining);
            for (var f = 0; f < toEmit; f++)
            {
                var t = (_framesEmitted + f) / (float)_fmt.SampleRate;
                var s = MathF.Sin(2 * MathF.PI * 440f * t);
                for (var c = 0; c < _fmt.Channels; c++)
                    destination[f * _fmt.Channels + c] = s;
            }
            _framesEmitted += toEmit;
            return toEmit * _fmt.Channels;
        }

        public void Dispose() => Disposed = true;
    }
}
