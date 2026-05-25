using System.Collections.Generic;
using S.Media.Core.Audio;
using S.Media.FFmpeg.Audio;
using S.Media.FFmpeg.Internal;
using Xunit;

namespace S.Media.FFmpeg.Tests.Audio;

public sealed class AudioResamplerTests
{
    [Fact]
    public void IdentityRate_CopiesStereoChunk()
    {
        using var resampler = AudioResampler.Create(new AudioFormat(48000, 2), new AudioFormat(48000, 2));
        var src = new float[200]; // 100 stereo frames ramp
        for (var i = 0; i < 100; i++)
        {
            src[i * 2] = i;
            src[i * 2 + 1] = -i;
        }

        var dst = new float[220 * 2];
        var written = resampler.Convert(src, 100, dst, 110);
        Assert.Equal(100, written);

        Assert.Equal(src.ToArray(), dst.AsSpan(0, src.Length).ToArray());

        var tailRoom = dst.Length / 2 - written;
        var drained = 0;
        if (tailRoom > 0)
            drained += resampler.Drain(dst.AsSpan(written * 2, tailRoom * 2), tailRoom);

        Assert.Equal(0, drained);
    }

    [Fact]
    public void UpsampleRoughlyDoublesStereoFrames()
    {
        using var resampler = AudioResampler.Create(new AudioFormat(44100, 2), new AudioFormat(88200, 2));
        var srcFrames = 1024;
        var src = new float[srcFrames * 2];
        for (var i = 0; i < srcFrames; i++)
        {
            src[i * 2] = MathF.Sin(i * (MathF.PI / 180f));
            src[i * 2 + 1] = MathF.Cos(i * (MathF.PI / 220f));
        }

        var maxOutFrames = srcFrames * 3;
        var dst = new float[maxOutFrames * 2];

        var outFrames = resampler.Convert(src, srcFrames, dst, maxOutFrames);

        while (outFrames < maxOutFrames)
        {
            var room = maxOutFrames - outFrames;
            var slice = dst.AsSpan(outFrames * 2);
            var d = resampler.Drain(slice, room);
            if (d == 0) break;
            outFrames += d;
        }

        Assert.InRange(outFrames, (int)(srcFrames * 1.96), (int)(srcFrames * 2.04));
    }

    public static IEnumerable<object[]> ResampleRates()
    {
        yield return new object[] { 8000, 16000 };
        yield return new object[] { 48000, 44100 };
        yield return new object[] { 22050, 48000 };
    }

    /// <summary>Deterministic multi-ratio resampling smoke (pseudo-property).</summary>
    [Theory]
    [MemberData(nameof(ResampleRates))]
    public void Resampler_RandomishBuffer_DoesNotBlowUp(int rateIn, int rateOut)
    {
        using var r = AudioResampler.Create(new AudioFormat(rateIn, 2), new AudioFormat(rateOut, 2));
        var rnd = new Random(rateIn ^ rateOut ^ 911);
        const int frames = 512;
        var src = new float[frames * 2];
        for (var i = 0; i < src.Length; i++)
            src[i] = (float)(rnd.NextDouble() * 2 - 1);

        var maxOut = frames * 4;
        var dst = new float[maxOut * 2];
        var produced = r.Convert(src, frames, dst, maxOut);
        Assert.True(produced > 0);
        foreach (var f in dst.AsSpan(0, produced * 2))
            Assert.False(float.IsNaN(f) || float.IsInfinity(f));

        while (produced < maxOut)
        {
            var roomFrames = maxOut - produced;
            var d = r.Drain(dst.AsSpan(produced * 2, roomFrames * 2), roomFrames);
            if (d == 0) break;
            foreach (var f in dst.AsSpan(produced * 2, d * 2))
                Assert.False(float.IsNaN(f) || float.IsInfinity(f));
            produced += d;
        }
    }
}
