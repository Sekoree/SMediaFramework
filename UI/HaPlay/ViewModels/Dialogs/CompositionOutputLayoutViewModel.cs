using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>
/// One physical output's region over the composition canvas in the multi-output layout editor. The Src
/// rectangle is the normalized [0,1] slice of the canvas this output displays full-frame; where two items
/// overlap is a blend zone, and canvas not covered by any item is a gap. Mirrors the draggable-rect shape
/// of <see cref="CueVideoPlacementViewModel"/>.
/// </summary>
public sealed partial class OutputLayoutItemViewModel : ObservableObject
{
    public OutputLayoutItemViewModel(Guid outputLineId, string displayName, int colorIndex)
    {
        OutputLineId = outputLineId;
        DisplayName = displayName;
        ColorIndex = colorIndex;
    }

    public Guid OutputLineId { get; }

    public string DisplayName { get; }

    /// <summary>Stable palette index the canvas maps to a colour (keeps this VM Avalonia-free and testable).</summary>
    public int ColorIndex { get; }

    [ObservableProperty] private double _srcX;
    [ObservableProperty] private double _srcY;
    [ObservableProperty] private double _srcWidth = 1.0;
    [ObservableProperty] private double _srcHeight = 1.0;

    /// <summary>Sets the canvas slice, clamped inside [0,1] with a sane minimum. Overlaps and gaps between
    /// items are intentionally allowed (a video wall may blend or leave bezels).</summary>
    public void SetSrcRect(double x, double y, double width, double height)
    {
        width = Math.Clamp(width, 0.02, 1.0);
        height = Math.Clamp(height, 0.02, 1.0);
        SrcX = Math.Clamp(x, 0.0, 1.0 - width);
        SrcY = Math.Clamp(y, 0.0, 1.0 - height);
        SrcWidth = width;
        SrcHeight = height;
    }
}

/// <summary>
/// Editor model for arranging a composition's bound physical outputs over its canvas — the multi-output /
/// video-wall layout. Each <see cref="OutputLayoutItemViewModel"/> is one output's canvas slice; the canvas
/// is drawn aspect-correct. Round-trips to each binding's <see cref="CueOutputMapping"/>: a single section
/// whose normalized source slice is the item's rectangle, displayed across the full output (the output
/// raster is sized to the slice's native pixels — the video-wall tile model). See
/// <c>Doc/HaPlay-Output-Mapping-Plan.md</c>.
/// </summary>
public sealed partial class CompositionOutputLayoutViewModel : ViewModelBase
{
    public CompositionOutputLayoutViewModel(int canvasWidth, int canvasHeight)
    {
        CanvasWidth = Math.Max(1, canvasWidth);
        CanvasHeight = Math.Max(1, canvasHeight);
    }

    public int CanvasWidth { get; }

    public int CanvasHeight { get; }

    public double AspectRatio => (double)CanvasWidth / CanvasHeight;

    public ObservableCollection<OutputLayoutItemViewModel> Items { get; } = new();

    [ObservableProperty] private OutputLayoutItemViewModel? _selectedItem;

    /// <summary>Builds the editor from a composition's output bindings, reading each binding's current source
    /// slice (full canvas when it has no mapping yet).</summary>
    public static CompositionOutputLayoutViewModel Build(
        int canvasWidth,
        int canvasHeight,
        IEnumerable<(Guid OutputLineId, string DisplayName, CueOutputMapping? Mapping)> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var vm = new CompositionOutputLayoutViewModel(canvasWidth, canvasHeight);
        var index = 0;
        foreach (var (lineId, name, mapping) in outputs)
        {
            var item = new OutputLayoutItemViewModel(lineId, name, index++);
            var section = mapping?.Sections.FirstOrDefault();
            if (section is not null)
                item.SetSrcRect(section.SrcX, section.SrcY, section.SrcWidth, section.SrcHeight);
            else
                item.SetSrcRect(0, 0, 1, 1);
            vm.Items.Add(item);
        }

        vm.SelectedItem = vm.Items.FirstOrDefault();
        return vm;
    }

    /// <summary>Builds the <see cref="CueOutputMapping"/> for one item: a single section showing its canvas
    /// slice across the full output, with the output raster sized to the slice's native pixels.</summary>
    public CueOutputMapping ToMapping(OutputLayoutItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var outW = Math.Max(1, (int)Math.Round(item.SrcWidth * CanvasWidth));
        var outH = Math.Max(1, (int)Math.Round(item.SrcHeight * CanvasHeight));
        return new CueOutputMapping
        {
            OutputWidth = outW,
            OutputHeight = outH,
            Sections =
            {
                new CueOutputMappingSection
                {
                    Enabled = true,
                    SrcX = item.SrcX,
                    SrcY = item.SrcY,
                    SrcWidth = item.SrcWidth,
                    SrcHeight = item.SrcHeight,
                    DestX = 0,
                    DestY = 0,
                    DestWidth = outW,
                    DestHeight = outH,
                },
            },
        };
    }
}
