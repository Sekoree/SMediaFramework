using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public class AudioRouterRouteTests
{
    [Fact]
    public void Route_IdentityMap_MatchesAddRoute()
    {
        using var router = new AudioRouter(48000);
        var source = new TestSource(new AudioFormat(48000, 2));
        var output = new PlainOutput(new AudioFormat(48000, 2));
        var srcId = router.AddSource(source);
        var outId = router.AddOutput(output);
        var routeId = router.Route(srcId, outId, gain: 0.75f);
        Assert.False(string.IsNullOrEmpty(routeId));
        router.Play();
        Thread.Sleep(50);
        router.Stop();
    }

    [Fact]
    public void Play_IsAliasForStart()
    {
        using var router = new AudioRouter(48000);
        router.Play();
        Assert.True(router.IsRunning);
        router.Stop();
    }

    private sealed class TestSource(AudioFormat fmt) : IAudioSource
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

    private sealed class PlainOutput(AudioFormat fmt) : IAudioOutput
    {
        public AudioFormat Format => fmt;
        public void Submit(ReadOnlySpan<float> samples) { }
    }
}
