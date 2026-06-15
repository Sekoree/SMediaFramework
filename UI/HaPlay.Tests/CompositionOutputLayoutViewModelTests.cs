using HaPlay.Models;
using HaPlay.ViewModels.Dialogs;
using Xunit;

namespace HaPlay.Tests;

public sealed class CompositionOutputLayoutViewModelTests
{
    private static readonly Guid Left = Guid.NewGuid();
    private static readonly Guid Right = Guid.NewGuid();

    [Fact]
    public void Build_reads_source_slices_and_defaults_unmapped_outputs_to_reported_size()
    {
        var leftMapping = new CueOutputMapping
        {
            Sections = { new CueOutputMappingSection { SrcX = 0, SrcY = 0, SrcWidth = 0.5, SrcHeight = 1.0 } },
        };

        var vm = CompositionOutputLayoutViewModel.Build(1920, 1080, new[]
        {
            (Left, "Left wall", (int?)960, (int?)1080, (CueOutputMapping?)leftMapping),
            (Right, "Right wall", (int?)960, (int?)1080, (CueOutputMapping?)null),
        });

        Assert.Equal(2, vm.Items.Count);
        var left = vm.Items[0];
        Assert.Equal(0.0, left.SrcX);
        Assert.Equal(0.5, left.SrcWidth);

        var right = vm.Items[1];
        Assert.Equal(0.5, right.SrcX);          // no mapping yet → next native-sized tile
        Assert.Equal(0.5, right.SrcWidth);
        Assert.Equal(1.0, right.SrcHeight);
        Assert.Equal(960, right.OutputWidth);
        Assert.Equal(1080, right.OutputHeight);

        Assert.Same(left, vm.SelectedItem);     // first item selected by default
    }

    [Fact]
    public void ToMapping_emits_a_full_output_section_sized_to_the_reported_output()
    {
        var vm = CompositionOutputLayoutViewModel.Build(1920, 1080, new[]
        {
            (Right, "Right half", (int?)1920, (int?)1080, (CueOutputMapping?)null),
        });
        var item = vm.Items[0];
        item.SetSrcRect(0.25, 0.0, 0.5, 1.0);   // middle half of the canvas

        var mapping = vm.ToMapping(item);

        Assert.Equal(1920, mapping.OutputWidth); // physical output stays 1080p even when the slice changes
        Assert.Equal(1080, mapping.OutputHeight);
        var section = Assert.Single(mapping.Sections);
        Assert.Equal(0.25, section.SrcX);
        Assert.Equal(0.5, section.SrcWidth);
        Assert.Equal(0, section.DestX);
        Assert.Equal(1920, section.DestWidth);   // slice shown across the full output raster
        Assert.Equal(1080, section.DestHeight);
        Assert.True(section.Enabled);
    }

    [Fact]
    public void Build_then_ToMapping_preserves_the_slice()
    {
        var original = new CueOutputMapping
        {
            Sections = { new CueOutputMappingSection { SrcX = 0.25, SrcY = 0.1, SrcWidth = 0.5, SrcHeight = 0.4 } },
        };

        var vm = CompositionOutputLayoutViewModel.Build(1920, 1080, new[]
        {
            (Left, "Tile", (int?)1280, (int?)720, (CueOutputMapping?)original),
        });
        var round = vm.ToMapping(vm.Items[0]).Sections[0];

        Assert.Equal(0.25, round.SrcX, 6);
        Assert.Equal(0.1, round.SrcY, 6);
        Assert.Equal(0.5, round.SrcWidth, 6);
        Assert.Equal(0.4, round.SrcHeight, 6);
    }

    [Fact]
    public void Overlaps_and_gaps_are_allowed_between_items()
    {
        var vm = CompositionOutputLayoutViewModel.Build(1000, 1000, new[]
        {
            (Left, "A", (int?)400, (int?)1000, (CueOutputMapping?)null),
            (Right, "B", (int?)400, (int?)1000, (CueOutputMapping?)null),
        });

        // A covers the left 60%, B the right 60% → they overlap in the middle 20% and there is no gap.
        vm.Items[0].SetSrcRect(0.0, 0.0, 0.6, 1.0);
        vm.Items[1].SetSrcRect(0.4, 0.0, 0.6, 1.0);
        Assert.True(vm.Items[0].SrcX + vm.Items[0].SrcWidth > vm.Items[1].SrcX, "items may overlap");

        // Shrink both to leave an uncovered gap in the middle — also allowed (no exception, values kept).
        vm.Items[0].SetSrcRect(0.0, 0.0, 0.4, 1.0);
        vm.Items[1].SetSrcRect(0.6, 0.0, 0.4, 1.0);
        Assert.Equal(0.4, vm.Items[0].SrcWidth, 6);
        Assert.Equal(0.6, vm.Items[1].SrcX, 6);
    }

    [Fact]
    public void Pixel_controls_update_normalized_rect_and_aspect_lock_can_be_disabled()
    {
        var vm = CompositionOutputLayoutViewModel.Build(3840, 1080, new[]
        {
            (Left, "Left", (int?)1920, (int?)1080, (CueOutputMapping?)null),
        });

        var item = vm.Items[0];
        Assert.Equal(1920, item.PixelWidth, 6);
        Assert.Equal(1080, item.PixelHeight, 6);

        item.PixelX = 960;
        item.PixelWidth = 960; // aspect locked to 16:9, height follows

        Assert.Equal(0.25, item.SrcX, 6);
        Assert.Equal(0.25, item.SrcWidth, 6);
        Assert.Equal(540, item.PixelHeight, 6);

        item.AspectLocked = false;
        item.PixelHeight = 800;

        Assert.Equal(800, item.PixelHeight, 6);
        Assert.Equal(960, item.PixelWidth, 6);
    }
}
