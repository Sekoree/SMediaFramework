using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class AudioRouterAutoResampleDefaultTests
{
    [Fact]
    public void AutoResampleDefault_Instance_OverridesStatic_AndIsOverriddenByPerCall()
    {
        // P3-3: a per-instance default lets a session set its own auto-resample policy without mutating
        // the process-wide static. Resolution order: per-call arg → instance default → static default.
        var savedStatic = AudioRouter.DefaultAutoResample;
        var savedWrapper = MediaFrameworkPlugins.AudioResampleSourceWrapper;
        try
        {
            AudioRouter.DefaultAutoResample = false;
            MediaFrameworkPlugins.AudioResampleSourceWrapper = null; // force the "no factory" path when resample is on

            using var r = new AudioRouter(48_000, chunkSamples: 480);
            var mismatched = new ConstSource(new AudioFormat(44_100, 2));

            // No instance default → static false → "off" (asks the caller to pass autoResample: true).
            Assert.Contains("autoResample: true",
                Assert.Throws<InvalidOperationException>(() => r.AddSource(mismatched, "a")).Message);

            // Instance default true → attempts resample → "no resampler factory" (none installed here).
            r.AutoResampleDefault = true;
            Assert.Contains("no resampler factory",
                Assert.Throws<InvalidOperationException>(() => r.AddSource(mismatched, "b")).Message);

            // Per-call false overrides the instance true → back to "off".
            Assert.Contains("autoResample: true",
                Assert.Throws<InvalidOperationException>(() => r.AddSource(mismatched, "c", autoResample: false)).Message);
        }
        finally
        {
            AudioRouter.DefaultAutoResample = savedStatic;
            MediaFrameworkPlugins.AudioResampleSourceWrapper = savedWrapper;
        }
    }

    private sealed class ConstSource(AudioFormat fmt) : IAudioSource
    {
        public AudioFormat Format { get; } = fmt;
        public bool IsExhausted => false;
        public int ReadInto(Span<float> dst) { dst.Clear(); return dst.Length; }
    }
}
