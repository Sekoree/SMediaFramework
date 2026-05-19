using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class VideoTimecodeTests
{
    private static readonly Rational R30 = new(30, 1);
    private static readonly Rational R2997 = new(30000, 1001);
    private static readonly Rational R5994 = new(60000, 1001);

    [Fact]
    public void ToTimecodeString_NonDropUsesColons()
    {
        var tc = new VideoTimecode(1, 2, 3, 4, false, R30);
        Assert.Equal("01:02:03:04", tc.ToTimecodeString());
    }

    [Fact]
    public void ToTimecodeString_DropFrameUsesSemicolon()
    {
        var tc = new VideoTimecode(1, 2, 3, 4, true, R2997);
        Assert.Equal("01:02:03;04", tc.ToTimecodeString());
    }

    [Fact]
    public void ToFrameNumber_NonDrop_1HourAt30()
    {
        var tc = new VideoTimecode(1, 0, 0, 0, false, R30);
        Assert.Equal(3600L * 30, tc.ToFrameNumber()); // 108000
    }

    [Fact]
    public void ToFrameNumber_DropFrame_2997_OneMinute()
    {
        // At 29.97 drop-frame, 1 minute = 60 * 30 - 2 = 1798 frames (first minute drops 2; minute 10
        // does not). We're at 00:01:00;02 which represents the smallest valid frame after the drop.
        var tc = new VideoTimecode(0, 1, 0, 2, true, R2997);
        Assert.Equal(1800L, tc.ToFrameNumber()); // 60s nominal - 2 dropped + 2 frames into the minute
    }

    [Fact]
    public void FromFrameNumber_RoundTrips_DropFrame2997()
    {
        // At 29.97 drop-frame: 1 hour = 3600 nominal seconds * 30 - dropped frames.
        // Dropped frames in 1 hour: 9 of every 10 minutes drop 2 → (60-6) * 2 = 108. So 107892 frames.
        var n = 107892L;
        var tc = VideoTimecode.FromFrameNumber(n, R2997, dropFrame: true);
        Assert.Equal(1, tc.Hours);
        Assert.Equal(0, tc.Minutes);
        Assert.Equal(0, tc.Seconds);
        Assert.Equal(0, tc.Frames);
        Assert.Equal(n, tc.ToFrameNumber());
    }

    [Fact]
    public void ToTicksAtRate_1HourAt30_Matches3600Sec()
    {
        var tc = new VideoTimecode(1, 0, 0, 0, false, R30);
        var expected = TimeSpan.FromHours(1).Ticks;
        Assert.Equal(expected, tc.ToTicksAtRate());
    }

    [Fact]
    public void TryParse_RoundTrip()
    {
        Assert.True(VideoTimecode.TryParse("12:34:56:21", R30, out var tc));
        Assert.NotNull(tc);
        Assert.Equal(12, tc!.Value.Hours);
        Assert.Equal(34, tc.Value.Minutes);
        Assert.Equal(56, tc.Value.Seconds);
        Assert.Equal(21, tc.Value.Frames);
        Assert.False(tc.Value.IsDropFrame);
    }

    [Fact]
    public void TryParse_DropFrameSeparator()
    {
        Assert.True(VideoTimecode.TryParse("00:01:00;02", R2997, out var tc));
        Assert.NotNull(tc);
        Assert.True(tc!.Value.IsDropFrame);
    }

    [Fact]
    public void TryParse_DropFrameAtNonDropRate_Fails()
    {
        Assert.False(VideoTimecode.TryParse("00:01:00;02", R30, out _));
    }

    [Fact]
    public void TryParse_MalformedInput_Fails()
    {
        Assert.False(VideoTimecode.TryParse("not a timecode", R30, out _));
        Assert.False(VideoTimecode.TryParse("1:2:3:4", R30, out _)); // wrong digit count
    }

    [Fact]
    public void Constructor_RejectsDropFrameAtNonEligibleRate()
    {
        Assert.Throws<ArgumentException>(() => new VideoTimecode(0, 0, 0, 0, true, R30));
    }

    [Fact]
    public void Math_IsDropFrameRate()
    {
        Assert.True(VideoTimecodeMath.IsDropFrameRate(R2997));
        Assert.True(VideoTimecodeMath.IsDropFrameRate(R5994));
        Assert.False(VideoTimecodeMath.IsDropFrameRate(R30));
        Assert.False(VideoTimecodeMath.IsDropFrameRate(new Rational(24000, 1001))); // 23.976 is not DF
    }
}
