using Xunit;

namespace S.Media.Core.Tests.Audio;

public class AudioRouterRouteLastTests
{
    [Fact]
    public void RouteLast_WiresLastAddedSourceAndOutput()
    {
        using var router = new AudioRouter(48_000);
        var srcId = router.AddSource(new SilentSource(new AudioFormat(48_000, 2)));
        var outId = router.AddOutput(new NullOutput(new AudioFormat(48_000, 2)));
        var routeId = router.RouteLast();
        Assert.False(string.IsNullOrEmpty(routeId));
        Assert.Equal(srcId, router.LastSourceId);
        Assert.Equal(outId, router.LastOutputId);
        Assert.Single(router.Routes);
    }

    private sealed class SilentSource(AudioFormat fmt) : IAudioSource
    {
        public AudioFormat Format => fmt;
        public bool IsExhausted { get; private set; }
        public int ReadInto(Span<float> dst)
        {
            if (IsExhausted) return 0;
            dst.Clear();
            IsExhausted = true;
            return dst.Length;
        }
    }

    private sealed class NullOutput(AudioFormat fmt) : IAudioOutput
    {
        public AudioFormat Format => fmt;
        public void Submit(ReadOnlySpan<float> samples) { }
    }
}
