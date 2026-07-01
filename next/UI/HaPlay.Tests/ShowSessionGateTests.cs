using HaPlay;
using Xunit;

namespace HaPlay.Tests;

/// <summary>The convergence gate flipped on 2026-07-01: ShowSession is the default and only an explicit falsey
/// <c>HAPLAY_USE_SHOWSESSION</c> value opts back out to the legacy engines.</summary>
public sealed class ShowSessionGateTests
{
    [Theory]
    [InlineData(null)]   // unset → default on
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("")]     // set-but-empty is not an explicit opt-out → on
    [InlineData("anything")]
    public void UnsetOrTruthy_UsesShowSession(string? value) =>
        Assert.True(ShowSessionGate.IsEnabled(value));

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("off")]
    [InlineData("No")]
    public void ExplicitFalsey_FallsBackToEngine(string value) =>
        Assert.False(ShowSessionGate.IsEnabled(value));
}
