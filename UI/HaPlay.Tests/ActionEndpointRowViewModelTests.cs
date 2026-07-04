using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public class ActionEndpointRowViewModelTests
{
    [Theory]
    [InlineData(ActionEndpointHealthState.Ok, "#4CAF50")]
    [InlineData(ActionEndpointHealthState.Failed, "#E53935")]
    [InlineData(ActionEndpointHealthState.Checking, "#FFC107")]
    [InlineData(ActionEndpointHealthState.Unknown, "#666666")]
    public void HealthColor_MapsState(ActionEndpointHealthState state, string expected)
    {
        var row = new ActionEndpointRowViewModel(new OSCActionEndpoint { Name = "X" });
        row.Health = state;
        Assert.Equal(expected, row.HealthColor);
    }

    [Fact]
    public void ReplaceEndpoint_UpdatesWrappedEndpoint()
    {
        var original = new OSCActionEndpoint { Name = "A", Host = "127.0.0.1", Port = 9000 };
        var row = new ActionEndpointRowViewModel(original);
        var updated = original with { Name = "B", Port = 9001 };
        row.ReplaceEndpoint(updated);
        Assert.Equal("B", row.Endpoint.Name);
        Assert.Equal(9001, Assert.IsType<OSCActionEndpoint>(row.Endpoint).Port);
    }
}
