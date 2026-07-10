using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class CueVideoPlacementViewModelTests
{
    [Fact]
    public void RotationDegrees_RoundTripsThroughModel()
    {
        var vm = new CueVideoPlacementViewModel { RotationDegrees = 30 };

        var model = vm.ToModel();
        Assert.Equal(30, model.RotationDegrees, 6);

        var back = CueVideoPlacementViewModel.FromModel(model);
        Assert.Equal(30, back.RotationDegrees, 6);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(45, 45)]
    [InlineData(180, 180)]
    [InlineData(270, -90)]   // wraps into (-180, 180]
    [InlineData(360, 0)]
    [InlineData(-270, 90)]
    public void ToModel_NormalizesRotationIntoHalfOpenTurn(double input, double expected)
    {
        var vm = new CueVideoPlacementViewModel { RotationDegrees = input };
        Assert.Equal(expected, vm.ToModel().RotationDegrees, 6);
    }

    [Fact]
    public void DefaultPlacement_HasNoRotation()
    {
        // Existing cues (and freshly added placements) must stay upright - rotation is purely additive.
        Assert.Equal(0, new CueVideoPlacement().RotationDegrees, 6);
        Assert.Equal(0, new CueVideoPlacementViewModel().ToModel().RotationDegrees, 6);
    }

    [Fact]
    public void VideoFx_RoundTripsThroughViewModel()
    {
        var mapping = new CueOutputMapping
        {
            Sections = { new CueOutputMappingSection { SrcWidth = 0.5, DestWidth = 100 } },
        };

        var vm = CueVideoPlacementViewModel.FromModel(new CueVideoPlacement
        {
            VideoFx = mapping,
            VideoFxEnabled = true,
        });

        var model = vm.ToModel();
        Assert.True(model.VideoFxEnabled);
        Assert.NotNull(model.VideoFx);
        Assert.Equal(0.5, model.VideoFx!.Sections[0].SrcWidth);
        Assert.Equal(100, model.VideoFx.Sections[0].DestWidth);
    }
}
