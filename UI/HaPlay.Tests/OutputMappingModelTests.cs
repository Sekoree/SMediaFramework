using System.Text.Json;
using HaPlay.Models;
using HaPlay.Playback;
using HaPlay.ViewModels;
using HaPlay.ViewModels.Dialogs;
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
        Assert.Equal(0, s.MeshColumns);
        Assert.Null(s.MeshPoints);
    }

    [Fact]
    public void CueListJson_RoundTripsMeshWarp()
    {
        var list = new CueList
        {
            VideoOutputs =
            {
                new CueVideoOutputBinding
                {
                    Mapping = new CueOutputMapping
                    {
                        Sections =
                        {
                            new CueOutputMappingSection
                            {
                                Name = "Curved screen",
                                DestWidth = 800,
                                DestHeight = 600,
                                MeshColumns = 3,
                                MeshRows = 2,
                                MeshPoints = CueOutputMappingSection.IdentityMeshPoints(3, 2),
                            },
                        },
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(list);
        var loaded = JsonSerializer.Deserialize<CueList>(json);

        var section = Assert.Single(Assert.Single(loaded!.VideoOutputs).Mapping!.Sections);
        Assert.Equal((3, 2), (section.MeshColumns, section.MeshRows));
        Assert.NotNull(section.MeshPoints);
        Assert.Equal(6, section.MeshPoints!.Count);
        Assert.Equal(0.5, section.MeshPoints[1].X, precision: 6);
        Assert.Equal(1.0, section.MeshPoints[5].Y, precision: 6);
    }

    [Fact]
    public void ToMappingSpec_ConvertsMeshPoints()
    {
        var spec = CueCompositionRuntime.ToMappingSpec(new CueOutputMapping
        {
            Sections =
            {
                new CueOutputMappingSection
                {
                    MeshColumns = 2,
                    MeshRows = 2,
                    MeshPoints = [new(0, 0), new(1, 0), new(0, 1), new(1.25, 0.75)],
                },
            },
        });

        var s = Assert.Single(spec!.Sections);
        Assert.Equal((2, 2), (s.MeshColumns, s.MeshRows));
        Assert.NotNull(s.MeshPoints);
        Assert.Equal(1.25, s.MeshPoints![3].X);
        Assert.Equal(0.75, s.MeshPoints[3].Y);
    }

    [Fact]
    public void IdentityMeshPoints_AreTheUniformGrid()
    {
        var points = CueOutputMappingSection.IdentityMeshPoints(3, 3);
        Assert.Equal(9, points.Count);
        Assert.Equal(new CuePoint(0, 0), points[0]);
        Assert.Equal(new CuePoint(0.5, 0), points[1]);
        Assert.Equal(new CuePoint(0.5, 0.5), points[4]);
        Assert.Equal(new CuePoint(1, 1), points[8]);
    }

    [Fact]
    public void EditorViewModel_DisabledSeed_AppliesLayoutSliceWhenEnabled()
    {
        CueOutputMapping? applied = null;
        var seed = new CueOutputMapping
        {
            OutputWidth = 960,
            OutputHeight = 1080,
            Sections =
            {
                new CueOutputMappingSection
                {
                    Name = "Right tile",
                    SrcX = 0.5,
                    SrcY = 0,
                    SrcWidth = 0.5,
                    SrcHeight = 1,
                    DestWidth = 960,
                    DestHeight = 1080,
                },
            },
        };

        var appliedEnabled = false;
        var vm = new MappingEditorViewModel(
            "Out",
            1920,
            1080,
            initial: null,
            apply: (m, enabled) => { applied = m; appliedEnabled = enabled; },
            disabledSeed: seed,
            initialEnabled: false);

        Assert.False(vm.MappingEnabled);
        // Geometry is retained even while disabled (so re-enabling restores it); it's seeded from the layout.
        Assert.Equal((960, 1080), (vm.OutputWidth, vm.OutputHeight));
        Assert.Equal(0.5, vm.ToMapping().Sections[0].SrcX, precision: 6);
        Assert.Equal(0.5, vm.SelectedSection!.SrcX, precision: 6);

        vm.MappingEnabled = true;

        Assert.True(appliedEnabled);
        var section = Assert.Single(applied!.Sections);
        Assert.Equal((960, 1080), (applied.OutputWidth, applied.OutputHeight));
        Assert.Equal(0.5, section.SrcX, precision: 6);
        Assert.Equal(0.5, section.SrcWidth, precision: 6);
        Assert.Equal(960, section.DestWidth, precision: 6);
    }

    [Fact]
    public void EditorViewModel_EnablingMesh_SeedsIdentityGridAndApplies()
    {
        CueOutputMapping? applied = null;
        var vm = new MappingEditorViewModel(
            "Out", 1920, 1080,
            CueOutputMapping.Identity(),
            (m, _) => applied = m,
            initialEnabled: true);
        var section = vm.SelectedSection!;

        section.MeshEnabled = true;

        Assert.Equal(16, section.MeshPoints.Count); // default 4×4
        var model = Assert.Single(applied!.Sections);
        Assert.Equal((4, 4), (model.MeshColumns, model.MeshRows));
        Assert.Equal(16, model.MeshPoints!.Count);

        // Disabling drops the mesh from the persisted model but keeps the grid in the VM.
        section.MeshEnabled = false;
        model = Assert.Single(applied!.Sections);
        Assert.Equal(0, model.MeshColumns);
        Assert.Null(model.MeshPoints);
        Assert.Equal(16, section.MeshPoints.Count);
    }

    [Fact]
    public void EditorViewModel_MeshRoundTripsAndPointEditsApply()
    {
        CueOutputMapping? applied = null;
        var initial = new CueOutputMapping
        {
            Sections =
            {
                new CueOutputMappingSection
                {
                    MeshColumns = 2,
                    MeshRows = 2,
                    MeshPoints = [new(0, 0), new(1, 0), new(0, 1), new(1.1, 1.1)],
                },
            },
        };
        var vm = new MappingEditorViewModel("Out", 1920, 1080, initial, (m, _) => applied = m, initialEnabled: true);
        var section = vm.SelectedSection!;

        Assert.True(section.MeshEnabled);
        Assert.Equal((2, 2), (section.MeshColumns, section.MeshRows));
        Assert.Equal(1.1, section.MeshPoints[3].X, precision: 6);

        section.SetMeshPoint(3, 1.3, 0.9);
        var model = Assert.Single(applied!.Sections);
        Assert.Equal(1.3, model.MeshPoints![3].X, precision: 6);
        Assert.Equal(0.9, model.MeshPoints[3].Y, precision: 6);

        // Grid resize restarts from identity.
        section.MeshColumns = 3;
        model = Assert.Single(applied!.Sections);
        Assert.Equal(6, model.MeshPoints!.Count);
        Assert.Equal(0.5, model.MeshPoints[1].X, precision: 6);
    }

    [Fact]
    public void EditorViewModel_DisableThenEnable_PreservesGeometryAndTogglesActive()
    {
        CueOutputMapping? applied = null;
        var appliedEnabled = true;
        var initial = new CueOutputMapping
        {
            OutputWidth = 1920,
            OutputHeight = 1080,
            Sections =
            {
                new CueOutputMappingSection
                {
                    SrcX = 0, SrcY = 0.5, SrcWidth = 1, SrcHeight = 0.5, DestWidth = 1920, DestHeight = 1080,
                },
            },
        };
        var vm = new MappingEditorViewModel(
            "Out", 1920, 2160, initial,
            (m, enabled) => { applied = m; appliedEnabled = enabled; },
            initialEnabled: true);

        Assert.True(vm.MappingEnabled);

        // Disable: the geometry is still handed to the caller (so it can be retained), enabled = false.
        vm.MappingEnabled = false;
        Assert.False(appliedEnabled);
        Assert.Equal(0.5, Assert.Single(applied!.Sections).SrcY, precision: 6);

        // Re-enable: the same bottom-half slice comes back active (not a reset to full canvas).
        vm.MappingEnabled = true;
        Assert.True(appliedEnabled);
        Assert.Equal(0.5, Assert.Single(applied!.Sections).SrcY, precision: 6);
    }

    [Fact]
    public void Binding_MappingEnabled_DefaultsTrueAndRoundTrips()
    {
        Assert.True(new CueVideoOutputBinding().MappingEnabled);

        var vm = new CueVideoOutputBindingViewModel
        {
            Mapping = CueOutputMapping.Identity(),
            MappingEnabled = false,
        };
        var roundTripped = CueVideoOutputBindingViewModel.FromModel(vm.ToModel());
        Assert.False(roundTripped.MappingEnabled);
        Assert.NotNull(roundTripped.Mapping); // geometry retained while disabled
    }
}
