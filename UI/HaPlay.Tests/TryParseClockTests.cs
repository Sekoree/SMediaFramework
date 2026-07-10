using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>The jump-to-position box's timecode parser: accepts <c>ss</c> / <c>mm:ss</c> / <c>hh:mm:ss</c> with an
/// optional fractional-seconds (<c>.ms</c>) part, and rejects anything malformed. Drives
/// <see cref="MediaPlayerViewModel.TryParseClock"/>.</summary>
public sealed class TryParseClockTests
{
    [Theory]
    [InlineData("0", 0, 0, 0, 0)]
    [InlineData("90", 0, 0, 90, 0)]           // seconds only (over 60 is fine - clamped to duration at the call site)
    [InlineData("01:30", 0, 1, 30, 0)]
    [InlineData("1:2:3", 1, 2, 3, 0)]
    [InlineData("01:12.500", 0, 1, 12, 500)]  // fractional seconds → milliseconds
    [InlineData("00:00:02.250", 0, 0, 2, 250)]
    [InlineData("12.5", 0, 0, 12, 500)]
    public void Parses_ValidTimecodes(string text, int h, int m, int s, int ms)
    {
        Assert.True(MediaPlayerViewModel.TryParseClock(text, out var t));
        Assert.Equal(new TimeSpan(0, h, m, s, ms), t);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]   // too many components
    [InlineData("1::30")]     // empty component
    [InlineData("-5")]        // negative
    [InlineData("1:-1")]
    [InlineData("1e3")]       // no scientific notation
    public void Rejects_Malformed(string? text)
    {
        Assert.False(MediaPlayerViewModel.TryParseClock(text, out var t));
        Assert.Equal(TimeSpan.Zero, t);
    }
}
