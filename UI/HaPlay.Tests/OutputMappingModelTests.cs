using System.Text.Json;
using HaPlay.Models;
using HaPlay.Playback;
using Xunit;

namespace HaPlay.Tests;

public sealed class OutputMappingModelTests
{
    [Fact]
    public void CueListJson_RoundTripsBindingMapping()
    {
        var list = new CueList
        {
            VideoOutputs =
            {
                new CueVideoOutputBinding
                {
                    OutputLineId = Guid.NewGuid(),
                    CompositionId = Guid.NewGuid(),
                    Mapping = new CueOutputMapping
                    {
                        OutputWidth = 2560,
                        OutputHeight = 800,
                        Sections =
                        {
                            new CueOutputMappingSection
                            {
                                Name = "Panel 2",
                                SrcX = 1.0 / 3,
                                SrcWidth = 1.0 / 3,
                                DestX = 660,
                                RotationDegrees = 1.5,
                                Opacity = 0.9,
                                Brightness = 0.8,
                            },
                        },
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(list);
        var loaded = JsonSerializer.Deserialize<CueList>(json);

        var mapping = Assert.Single(loaded!.VideoOutputs).Mapping;
        Assert.NotNull(mapping);
        Assert.Equal((2560, 800), (mapping.OutputWidth, mapping.OutputHeight));
        var section = Assert.Single(mapping.Sections);
        Assert.Equal("Panel 2", section.Name);
        Assert.Equal(660, section.DestX);
        Assert.Equal(1.5, section.RotationDegrees);
        Assert.Equal(0.8, section.Brightness);
    }

    [Fact]
    public void CueListJson_WithoutMapping_LoadsAsNull()
    {
        // Pre-mapping project files have no Mapping property — must load unchanged.
        var json = """{"VideoOutputs":[{"OutputLineId":"11111111-1111-1111-1111-111111111111","CompositionId":"22222222-2222-2222-2222-222222222222"}]}""";
        var loaded = JsonSerializer.Deserialize<CueList>(json);
        Assert.Null(Assert.Single(loaded!.VideoOutputs).Mapping);
    }

    [Fact]
    public void ToMappingSpec_ConvertsFieldsAndPreservesNull()
    {
        Assert.Null(CueCompositionRuntime.ToMappingSpec(null));

        var spec = CueCompositionRuntime.ToMappingSpec(new CueOutputMapping
        {
            OutputWidth = 1024,
            Sections =
            {
                new CueOutputMappingSection
                {
                    Enabled = false,
                    SrcX = 0.25,
                    SrcWidth = 0.5,
                    DestX = 10,
                    DestWidth = 200,
                    RotationDegrees = -2,
                    Opacity = 0.5,
                    Brightness = 0.75,
                },
            },
        });

        Assert.NotNull(spec);
        Assert.Equal(1024, spec.OutputWidth);
        Assert.Null(spec.OutputHeight);
        var s = Assert.Single(spec.Sections);
        Assert.False(s.Enabled);
        Assert.Equal(0.25, s.SrcX);
        Assert.Equal(0.5, s.SrcWidth);
        Assert.Equal(200, s.DestWidth);
        Assert.Equal(-2, s.RotationDegrees);
        Assert.Equal(0.75, s.Brightness);
    }
}
