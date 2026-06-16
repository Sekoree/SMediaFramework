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
    private bool _normalizingSourceRect;

    public OutputLayoutItemViewModel(
        Guid outputLineId,
        string displayName,
        int colorIndex,
        int canvasWidth,
        int canvasHeight,
        int? outputWidth,
        int? outputHeight)
    {
        OutputLineId = outputLineId;
        DisplayName = displayName;
        ColorIndex = colorIndex;
        CanvasWidth = Math.Max(1, canvasWidth);
        CanvasHeight = Math.Max(1, canvasHeight);
        OutputWidth = Math.Max(1, outputWidth ?? CanvasWidth);
        OutputHeight = Math.Max(1, outputHeight ?? CanvasHeight);
    }

    /// <summary>The output raster this region is sent at — editable, so an output can render its canvas
    /// slice at a chosen resolution (e.g. a 1920×1080 slice of a 1920×2160 stacked canvas) instead of
    /// inheriting the canvas size. Stored back into the binding's <see cref="CueOutputMapping.OutputWidth"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputSummary))]
    [NotifyPropertyChangedFor(nameof(OutputAspectRatio))]
    private int _outputWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputSummary))]
    [NotifyPropertyChangedFor(nameof(OutputAspectRatio))]
    private int _outputHeight;

    public Guid OutputLineId { get; }

    public string DisplayName { get; }

    /// <summary>Stable palette index the canvas maps to a colour (keeps this VM Avalonia-free and testable).</summary>
    public int ColorIndex { get; }

    public int CanvasWidth { get; }

    public int CanvasHeight { get; }

    public string OutputSummary => $"{OutputWidth}×{OutputHeight}";

    public double OutputAspectRatio => OutputHeight > 0 ? OutputWidth / (double)OutputHeight : 1.0;

    [ObservableProperty] private double _srcX;
    [ObservableProperty] private double _srcY;
    [ObservableProperty] private double _srcWidth = 1.0;
    [ObservableProperty] private double _srcHeight = 1.0;

    [ObservableProperty] private bool _aspectLocked = true;

    public double PixelX
    {
        get => SrcX * CanvasWidth;
        set => SetPixelRect(value, PixelY, PixelWidth, PixelHeight);
    }

    public double PixelY
    {
        get => SrcY * CanvasHeight;
        set => SetPixelRect(PixelX, value, PixelWidth, PixelHeight);
    }

    public double PixelWidth
    {
        get => SrcWidth * CanvasWidth;
        set
        {
            var width = Math.Clamp(value, 1.0, CanvasWidth);
            var height = AspectLocked ? width / OutputAspectRatio : PixelHeight;
            SetPixelRect(PixelX, PixelY, width, height);
        }
    }

    public double PixelHeight
    {
        get => SrcHeight * CanvasHeight;
        set
        {
            var height = Math.Clamp(value, 1.0, CanvasHeight);
            var width = AspectLocked ? height * OutputAspectRatio : PixelWidth;
            SetPixelRect(PixelX, PixelY, width, height);
        }
    }

    /// <summary>Sets the canvas slice, clamped inside [0,1] with a sane minimum. Overlaps and gaps between
    /// items are intentionally allowed (a video wall may blend or leave bezels).</summary>
    public void SetSrcRect(double x, double y, double width, double height)
    {
        width = Math.Clamp(width, 1.0 / CanvasWidth, 1.0);
        height = Math.Clamp(height, 1.0 / CanvasHeight, 1.0);
        _normalizingSourceRect = true;
        try
        {
            SrcX = Math.Clamp(x, 0.0, 1.0 - width);
            SrcY = Math.Clamp(y, 0.0, 1.0 - height);
            SrcWidth = width;
            SrcHeight = height;
        }
        finally
        {
            _normalizingSourceRect = false;
        }
        NotifyPixelRectChanged();
    }

    private void SetPixelRect(double x, double y, double width, double height) =>
        SetSrcRect(x / CanvasWidth, y / CanvasHeight, width / CanvasWidth, height / CanvasHeight);

    partial void OnSrcXChanged(double value) => NormalizeSourceRectAndNotifyPixels();
    partial void OnSrcYChanged(double value) => NormalizeSourceRectAndNotifyPixels();
    partial void OnSrcWidthChanged(double value) => NormalizeSourceRectAndNotifyPixels();
    partial void OnSrcHeightChanged(double value) => NormalizeSourceRectAndNotifyPixels();

    private void NormalizeSourceRectAndNotifyPixels()
    {
        if (!_normalizingSourceRect)
        {
            var width = Math.Clamp(SrcWidth, 1.0 / CanvasWidth, 1.0);
            var height = Math.Clamp(SrcHeight, 1.0 / CanvasHeight, 1.0);
            var x = Math.Clamp(SrcX, 0.0, 1.0 - width);
            var y = Math.Clamp(SrcY, 0.0, 1.0 - height);
            if (!NearlyEqual(SrcX, x)
                || !NearlyEqual(SrcY, y)
                || !NearlyEqual(SrcWidth, width)
                || !NearlyEqual(SrcHeight, height))
            {
                SetSrcRect(x, y, width, height);
                return;
            }
        }

        NotifyPixelRectChanged();
    }

    private void NotifyPixelRectChanged()
    {
        OnPropertyChanged(nameof(PixelX));
        OnPropertyChanged(nameof(PixelY));
        OnPropertyChanged(nameof(PixelWidth));
        OnPropertyChanged(nameof(PixelHeight));
    }

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) < 0.000001;
}

/// <summary>
/// Editor model for arranging a composition's bound physical outputs over its canvas — the multi-output /
/// video-wall layout. Each <see cref="OutputLayoutItemViewModel"/> is one output's canvas slice; the canvas
/// is drawn aspect-correct. Round-trips to each binding's <see cref="CueOutputMapping"/>: a single section
/// whose normalized source slice is the item's rectangle, displayed across the full reported output raster.
/// See
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
    /// slice. Unmapped outputs default to their reported output raster size and are laid out left-to-right.</summary>
    public static CompositionOutputLayoutViewModel Build(
        int canvasWidth,
        int canvasHeight,
        IEnumerable<(Guid OutputLineId, string DisplayName, CueOutputMapping? Mapping)> outputs) =>
        Build(
            canvasWidth,
            canvasHeight,
            outputs.Select(o => (o.OutputLineId, o.DisplayName, (int?)null, (int?)null, o.Mapping)));

    public static CompositionOutputLayoutViewModel Build(
        int canvasWidth,
        int canvasHeight,
        IEnumerable<(Guid OutputLineId, string DisplayName, int? OutputWidth, int? OutputHeight, CueOutputMapping? Mapping)> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var vm = new CompositionOutputLayoutViewModel(canvasWidth, canvasHeight);
        var index = 0;
        var nextX = 0.0;
        var nextY = 0.0;
        var rowHeight = 0.0;
        foreach (var (lineId, name, outputWidth, outputHeight, mapping) in outputs)
        {
            // Round-trip: a previously-saved mapping resolution wins, then the output's reported raster
            // (NDI lock / window size), then the canvas size (in the ctor). So reopening the editor shows
            // the resolution you set last time instead of resetting to the lock/canvas default.
            var item = new OutputLayoutItemViewModel(
                lineId,
                name,
                index++,
                vm.CanvasWidth,
                vm.CanvasHeight,
                mapping?.OutputWidth ?? outputWidth,
                mapping?.OutputHeight ?? outputHeight);
            var section = mapping?.Sections.FirstOrDefault();
            if (section is not null)
            {
                item.SetSrcRect(section.SrcX, section.SrcY, section.SrcWidth, section.SrcHeight);
                nextX = Math.Max(nextX, item.SrcX + item.SrcWidth);
                rowHeight = Math.Max(rowHeight, item.SrcHeight);
                if (nextX >= 1.0 - 0.000001)
                {
                    nextX = 0.0;
                    nextY += rowHeight;
                    rowHeight = 0.0;
                }
            }
            else
            {
                var w = Math.Clamp(item.OutputWidth / (double)vm.CanvasWidth, 1.0 / vm.CanvasWidth, 1.0);
                var h = Math.Clamp(item.OutputHeight / (double)vm.CanvasHeight, 1.0 / vm.CanvasHeight, 1.0);
                if (nextX > 0 && nextX + w > 1.0 + 0.000001)
                {
                    nextX = 0.0;
                    nextY += rowHeight;
                    rowHeight = 0.0;
                }

                if (nextY + h > 1.0 + 0.000001)
                    nextY = 0.0;

                item.SetSrcRect(nextX, nextY, w, h);
                nextX = Math.Min(1.0, nextX + w);
                rowHeight = Math.Max(rowHeight, h);
            }
            vm.Items.Add(item);
        }

        vm.SelectedItem = vm.Items.FirstOrDefault();
        return vm;
    }

    /// <summary>Builds the <see cref="CueOutputMapping"/> for one item: a single section showing its canvas
    /// slice across the full physical output raster.</summary>
    public CueOutputMapping ToMapping(OutputLayoutItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var outW = Math.Max(1, item.OutputWidth);
        var outH = Math.Max(1, item.OutputHeight);
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
