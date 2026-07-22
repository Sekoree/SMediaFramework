using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class AudioBusTests
{
    private static readonly AudioFormat Stereo48k = new(48000, 2);

    [Fact]
    public void SubmitThenRead_RoundTripsSamples()
    {
        var bus = new AudioBus(Stereo48k);
        Span<float> src = stackalloc float[480 * 2];
        for (var i = 0; i < src.Length; i++) src[i] = i * 0.001f;

        bus.Submit(src);

        Span<float> dst = stackalloc float[480 * 2];
        var read = bus.ReadInto(dst);
        Assert.Equal(480 * 2, read);
        for (var i = 0; i < dst.Length; i++)
            Assert.Equal(src[i], dst[i]);
    }

    [Fact]
    public void Read_BeforeWrite_ReportsUnderflow_ReturnsZero()
    {
        var bus = new AudioBus(Stereo48k);
        Span<float> dst = stackalloc float[480 * 2];
        var read = bus.ReadInto(dst);
        Assert.Equal(0, read);
        Assert.Equal(960, bus.UnderflowFloats);
    }

    [Fact]
    public void Submit_PastCapacity_DropsAndCountsOverflow()
    {
        // ~80 ms @ 48 kHz stereo ≈ 7680 floats; ring rounds up to 8192. Push way past that.
        var bus = new AudioBus(Stereo48k);
        var oneSecondFloats = 48000 * 2;
        var big = new float[oneSecondFloats];
        bus.Submit(big);
        Assert.True(bus.OverflowFloats > 0, "expected overflow when submitting >> ring capacity");
    }

    [Fact]
    public void Flush_DiscardsOnlyQueuedSamples_AndBusRemainsUsable()
    {
        var bus = new AudioBus(Stereo48k);
        bus.Submit(Enumerable.Repeat(1f, 64 * 2).ToArray());

        bus.Flush();

        var dst = new float[64 * 2];
        Assert.Equal(0, bus.ReadInto(dst));
        bus.Submit(Enumerable.Repeat(2f, dst.Length).ToArray());
        Assert.Equal(dst.Length, bus.ReadInto(dst));
        Assert.All(dst, sample => Assert.Equal(2f, sample));
    }

    [Fact]
    public void IsExhausted_AlwaysFalse()
    {
        var bus = new AudioBus(Stereo48k);
        Assert.False(bus.IsExhausted);
        Span<float> tiny = stackalloc float[2];
        bus.Submit(tiny);
        bus.ReadInto(tiny);
        Assert.False(bus.IsExhausted);
    }

    [Fact]
    public void MismatchedChannelLength_Throws()
    {
        var bus = new AudioBus(Stereo48k);
        Span<float> odd = stackalloc float[3]; // not a multiple of 2 channels
        Assert.Throws<ArgumentException>(() =>
        {
            // can't capture Span across a lambda; do the work directly
            var b = new AudioBus(Stereo48k);
            b.Submit(new float[3]);
        });
        _ = bus;
    }
}
