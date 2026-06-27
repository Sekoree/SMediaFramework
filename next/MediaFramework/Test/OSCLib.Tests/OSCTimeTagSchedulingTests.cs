using Xunit;

namespace OSCLib.Tests;

public sealed class OSCTimeTagSchedulingTests
{
    [Fact]
    public void TimeTag_RoundTrips_DateTimeOffset()
    {
        var timestamp = new DateTimeOffset(2026, 5, 20, 12, 34, 56, 789, TimeSpan.Zero);

        var tag = OSCTimeTag.FromDateTimeOffset(timestamp);
        var roundTrip = tag.ToDateTimeOffset();

        Assert.InRange((roundTrip - timestamp).Duration(), TimeSpan.Zero, TimeSpan.FromTicks(1));
    }

    [Fact]
    public void Scheduler_ImmediateAndPastTags_ReturnZeroDelay()
    {
        var now = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var past = OSCTimeTag.FromDateTimeOffset(now - TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.Zero, OSCBundleScheduler.GetDelay(OSCTimeTag.Immediately, now));
        Assert.Equal(TimeSpan.Zero, OSCBundleScheduler.GetDelay(past, now));
    }

    [Fact]
    public void Scheduler_FutureTag_ReturnsPositiveDelay()
    {
        var now = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var future = OSCTimeTag.FromDateTimeOffset(now + TimeSpan.FromMilliseconds(250));

        var delay = OSCBundleScheduler.GetDelay(future, now);

        Assert.InRange(delay.TotalMilliseconds, 249.9, 250.1);
    }
}
