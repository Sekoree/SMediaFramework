using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

public sealed partial class CueCompositionViewModel : ObservableObject
{
    [ObservableProperty]
    private Guid _id = Guid.NewGuid();

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private int _width = 1920;

    [ObservableProperty]
    private int _height = 1080;

    [ObservableProperty]
    private int _frameRateNum = 60;

    [ObservableProperty]
    private int _frameRateDen = 1;

    [ObservableProperty]
    private CueOutputMapping? _videoFx;

    [ObservableProperty]
    private bool _videoFxEnabled;

    public string Summary =>
        $"{Width}×{Height} @ {(FrameRateDen > 0 ? FrameRateNum / (double)FrameRateDen : 0):0.##}fps";

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? Summary
        : $"{Name} ({Summary})";

    partial void OnNameChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnWidthChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnHeightChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnFrameRateNumChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(DisplayName));
        CompositionFrameRateChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnFrameRateDenChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(DisplayName));
        CompositionFrameRateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when canvas frame rate edits should re-evaluate source/canvas warnings.</summary>
    internal event EventHandler? CompositionFrameRateChanged;

    public CueComposition ToModel() => new()
    {
        Id = Id,
        Name = Name,
        Width = Width,
        Height = Height,
        FrameRateNum = FrameRateNum,
        FrameRateDen = FrameRateDen,
        VideoFx = VideoFx,
        VideoFxEnabled = VideoFxEnabled,
    };

    public static CueCompositionViewModel FromModel(CueComposition model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Width = model.Width,
        Height = model.Height,
        FrameRateNum = model.FrameRateNum,
        FrameRateDen = model.FrameRateDen,
        VideoFx = model.VideoFx,
        VideoFxEnabled = model.VideoFxEnabled,
    };
}

/// <summary>One swatch in the drawer's color-tag picker (Phase 5.8.1). Plain DTO — the
/// button command lives on <see cref="CuePlayerViewModel"/>; this VM just supplies the
/// fill/border colors and the tag index.</summary>
public sealed class CueColorSwatchViewModel
{
    public CueColorSwatchViewModel(int index)
    {
        Index = index;
        FillBrush = CueColorTagPalette.BrushHex(index);
        Name = CueColorTagPalette.Name(index);
        BorderBrush = index == 0 ? "#888888" : "#22000000";
    }

    public int Index { get; }
    public string FillBrush { get; }
    public string Name { get; }
    public string BorderBrush { get; }
}

public sealed partial class CueVideoOutputBindingViewModel : ObservableObject
{
    private Func<Guid, OutputLineViewModel?>? _resolveLine;

    [ObservableProperty]
    private Guid _id = Guid.NewGuid();

    [ObservableProperty]
    private Guid _outputLineId;

    [ObservableProperty]
    private Guid _compositionId;

    /// <summary>Resolved reference to the line so the row can show its health dot/tooltip.
    /// Kept in sync by <see cref="CuePlayerViewModel"/>.</summary>
    [ObservableProperty]
    private OutputLineViewModel? _lineRef;

    partial void OnOutputLineIdChanged(Guid value) => LineRef = _resolveLine?.Invoke(value);

    /// <summary>Output mapping (warp sections) for this binding — edited by the mapping editor
    /// dialog, persisted with the cue list. Null = no mapping stage. Geometry is retained while
    /// <see cref="MappingEnabled"/> is false so disabling then re-enabling restores the slice.</summary>
    [ObservableProperty]
    private CueOutputMapping? _mapping;

    /// <summary>Whether <see cref="Mapping"/> is active (see the model field). Default true.</summary>
    [ObservableProperty]
    private bool _mappingEnabled = true;

    internal void SetLineResolver(Func<Guid, OutputLineViewModel?> resolveLine)
    {
        _resolveLine = resolveLine;
        LineRef = resolveLine(OutputLineId);
    }

    public CueVideoOutputBinding ToModel() => new()
    {
        Id = Id,
        OutputLineId = OutputLineId,
        CompositionId = CompositionId,
        Mapping = Mapping,
        MappingEnabled = MappingEnabled,
    };

    public static CueVideoOutputBindingViewModel FromModel(
        CueVideoOutputBinding model,
        Func<Guid, OutputLineViewModel?>? resolveLine = null)
    {
        var vm = new CueVideoOutputBindingViewModel
        {
            Id = model.Id,
            OutputLineId = model.OutputLineId,
            CompositionId = model.CompositionId,
            Mapping = model.Mapping,
            MappingEnabled = model.MappingEnabled,
        };
        if (resolveLine is not null)
            vm.SetLineResolver(resolveLine);
        return vm;
    }
}

public sealed partial class CueAudioRouteViewModel : ObservableObject
{
    private Func<Guid, OutputLineViewModel?>? _resolveLine;

    [ObservableProperty]
    private int _sourceChannel;

    [ObservableProperty]
    private Guid _outputLineId;

    [ObservableProperty]
    private int _outputChannel = 1;

    [ObservableProperty]
    private double _gainDb;

    [ObservableProperty]
    private bool _muted;

    /// <summary>Resolved reference to the line so the row can show its health dot/tooltip.
    /// Kept in sync by <see cref="CuePlayerViewModel"/>.</summary>
    [ObservableProperty]
    private OutputLineViewModel? _lineRef;

    partial void OnOutputLineIdChanged(Guid value) => LineRef = _resolveLine?.Invoke(value);

    internal void SetLineResolver(Func<Guid, OutputLineViewModel?> resolveLine)
    {
        _resolveLine = resolveLine;
        LineRef = resolveLine(OutputLineId);
    }

    public CueAudioRoute ToModel() => new()
    {
        SourceChannel = SourceChannel,
        OutputLineId = OutputLineId,
        OutputChannel = OutputChannel,
        GainDb = GainDb,
        Muted = Muted,
    };

    public static CueAudioRouteViewModel FromModel(
        CueAudioRoute model,
        Func<Guid, OutputLineViewModel?>? resolveLine = null)
    {
        var vm = new CueAudioRouteViewModel
        {
            SourceChannel = model.SourceChannel,
            OutputLineId = model.OutputLineId,
            OutputChannel = model.OutputChannel,
            GainDb = model.GainDb,
            Muted = model.Muted,
        };
        if (resolveLine is not null)
            vm.SetLineResolver(resolveLine);
        return vm;
    }
}

public sealed partial class CueVideoPlacementViewModel : ObservableObject
{
    private bool _normalizingDestinationRect;

    [ObservableProperty]
    private Guid _compositionId;

    [ObservableProperty]
    private int _layerIndex;

    [ObservableProperty]
    private CueLayerPosition _position = CueLayerPosition.Cover;

    [ObservableProperty]
    private double _opacity = 1.0;

    // Destination rectangle on the composition canvas, normalized [0,1]. Defaults to the full canvas.
    [ObservableProperty]
    private double _destX;

    [ObservableProperty]
    private double _destY;

    [ObservableProperty]
    private double _destWidth = 1.0;

    [ObservableProperty]
    private double _destHeight = 1.0;

    // Per-edge source crop insets, normalized [0,1). Default 0 = no trim.
    [ObservableProperty]
    private double _cropLeft;

    [ObservableProperty]
    private double _cropTop;

    [ObservableProperty]
    private double _cropRight;

    [ObservableProperty]
    private double _cropBottom;

    /// <summary>Clockwise rotation (degrees) of this layer about its destination-rect centre.</summary>
    [ObservableProperty]
    private double _rotationDegrees;

    [ObservableProperty]
    private CueOutputMapping? _videoFx;

    [ObservableProperty]
    private bool _videoFxEnabled;

    /// <summary>Sets the destination rectangle, clamped to the canvas with a sane minimum size.</summary>
    public void SetDestRect(double x, double y, double width, double height)
    {
        width = Math.Clamp(width, 0.02, 1.0);
        height = Math.Clamp(height, 0.02, 1.0);
        _normalizingDestinationRect = true;
        try
        {
            DestX = Math.Clamp(x, 0.0, 1.0 - width);
            DestY = Math.Clamp(y, 0.0, 1.0 - height);
            DestWidth = width;
            DestHeight = height;
        }
        finally
        {
            _normalizingDestinationRect = false;
        }
    }

    partial void OnDestXChanged(double value) => NormalizeDestinationRect();
    partial void OnDestYChanged(double value) => NormalizeDestinationRect();
    partial void OnDestWidthChanged(double value) => NormalizeDestinationRect();
    partial void OnDestHeightChanged(double value) => NormalizeDestinationRect();

    private void NormalizeDestinationRect()
    {
        if (_normalizingDestinationRect)
            return;

        var width = Math.Clamp(DestWidth, 0.02, 1.0);
        var height = Math.Clamp(DestHeight, 0.02, 1.0);
        var x = Math.Clamp(DestX, 0.0, 1.0 - width);
        var y = Math.Clamp(DestY, 0.0, 1.0 - height);

        if (NearlyEqual(DestX, x)
            && NearlyEqual(DestY, y)
            && NearlyEqual(DestWidth, width)
            && NearlyEqual(DestHeight, height))
        {
            return;
        }

        _normalizingDestinationRect = true;
        try
        {
            DestWidth = width;
            DestHeight = height;
            DestX = x;
            DestY = y;
        }
        finally
        {
            _normalizingDestinationRect = false;
        }
    }

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) < 0.000001;

    public CueVideoPlacement ToModel() => new()
    {
        CompositionId = CompositionId,
        LayerIndex = LayerIndex,
        Position = Position,
        Opacity = Math.Clamp(Opacity, 0.0, 1.0),
        DestX = Math.Clamp(DestX, 0.0, 1.0),
        DestY = Math.Clamp(DestY, 0.0, 1.0),
        DestWidth = Math.Clamp(DestWidth, 0.0, 1.0),
        DestHeight = Math.Clamp(DestHeight, 0.0, 1.0),
        CropLeft = Math.Clamp(CropLeft, 0.0, 0.99),
        CropTop = Math.Clamp(CropTop, 0.0, 0.99),
        CropRight = Math.Clamp(CropRight, 0.0, 0.99),
        CropBottom = Math.Clamp(CropBottom, 0.0, 0.99),
        RotationDegrees = NormalizeRotation(RotationDegrees),
        VideoFx = VideoFx,
        VideoFxEnabled = VideoFxEnabled,
    };

    /// <summary>Wraps rotation into (-180, 180] so the editor and serialized value stay tidy.</summary>
    private static double NormalizeRotation(double degrees)
    {
        var wrapped = degrees % 360.0;
        if (wrapped > 180.0) wrapped -= 360.0;
        else if (wrapped <= -180.0) wrapped += 360.0;
        return wrapped;
    }

    public static CueVideoPlacementViewModel FromModel(CueVideoPlacement model)
    {
        var vm = new CueVideoPlacementViewModel
        {
            CompositionId = model.CompositionId,
            LayerIndex = model.LayerIndex,
            Position = model.Position,
            Opacity = model.Opacity,
            CropLeft = model.CropLeft,
            CropTop = model.CropTop,
            CropRight = model.CropRight,
            CropBottom = model.CropBottom,
            RotationDegrees = model.RotationDegrees,
            VideoFx = model.VideoFx,
            VideoFxEnabled = model.VideoFxEnabled,
        };
        vm.SetDestRect(
            model.DestX,
            model.DestY,
            model.DestWidth <= 0 ? 1.0 : model.DestWidth,
            model.DestHeight <= 0 ? 1.0 : model.DestHeight);
        return vm;
    }
}

/// <summary>One audio-track picker entry. <see cref="Index"/> null = automatic election.</summary>
public sealed record CueAudioTrackChoice(int? Index, string? Signature, string Label)
{
    public static readonly CueAudioTrackChoice Automatic = new(null, null, Strings.AudioTrackAutomaticLabel);

    public override string ToString() => Label;
}

/// <summary>One embedded subtitle-track entry in the cue subtitle picker. Subtitles are none/one/many, so each
/// entry carries its own <see cref="IsSelected"/> toggle rather than a single shared selection.</summary>
public sealed partial class CueSubtitleTrackChoice : ObservableObject
{
    public CueSubtitleTrackChoice(int streamIndex, string label, bool isSelected)
    {
        StreamIndex = streamIndex;
        Label = label;
        _isSelected = isSelected;
    }

    /// <summary>Embedded container stream index.</summary>
    public int StreamIndex { get; }

    /// <summary>Operator-facing label (codec / language / title).</summary>
    public string Label { get; }

    [ObservableProperty]
    private bool _isSelected;

    public override string ToString() => Label;
}

/// <summary>One subtitle alignment option (ASS numpad 1–9; <c>null</c> = keep the document's alignment).</summary>
public sealed record SubtitleAlignmentChoice(int? Value, string Label)
{
    public override string ToString() => Label;
}
