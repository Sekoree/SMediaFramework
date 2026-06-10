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

public enum CueNodeKind
{
    Group,
    Media,
    Action,
    Comment,
}

public enum CueRowStatus
{
    Idle,
    Standby,
    Current,
}

public enum CueMidiCommandType
{
    NRPN,
    RPN,
    NoteOff,
    NoteOn,
    PolyphonicAftertouch,
    ControlChange,
    HighResolutionControlChange,
    ProgramChange,
    ChannelAftertouch,
    PitchBend,
    SysEx,
    MIDITimeCode,
    SongPosition,
    SongSelect,
    TuneRequest,
    TimingClock,
    Start,
    Continue,
    Stop,
    ActiveSensing,
    Reset,
}

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
    };

    public static CueCompositionViewModel FromModel(CueComposition model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Width = model.Width,
        Height = model.Height,
        FrameRateNum = model.FrameRateNum,
        FrameRateDen = model.FrameRateDen,
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
    };

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

public sealed partial class CueNodeViewModel : ObservableObject
{
    private const int DefaultNdiInputAudioChannels = 2;

    public CueNodeViewModel(CueNodeKind kind)
    {
        Kind = kind;
        Children.CollectionChanged += OnChildrenCollectionChanged;
    }

    public CueNodeKind Kind { get; }

    [ObservableProperty]
    private Guid _id = Guid.NewGuid();

    public ObservableCollection<CueNodeViewModel> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private string _number = string.Empty;

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private CueTriggerMode _triggerMode = CueTriggerMode.Manual;

    [ObservableProperty]
    private int _preWaitMs;

    [ObservableProperty]
    private string? _notes;

    [ObservableProperty]
    private string _sourceOrAction = string.Empty;

    /// <summary>Canonical media source for <see cref="CueNodeKind.Media"/> rows (files and live inputs).</summary>
    [ObservableProperty]
    private PlaylistItem? _mediaSourceItem;

    [ObservableProperty]
    private int _fadeInMs;

    [ObservableProperty]
    private int _fadeOutMs;

    [ObservableProperty]
    private int _durationMs;

    [ObservableProperty]
    private bool _sourceHasVideo;

    public bool IsTextCue => MediaSourceItem is TextPlaylistItem;

    public bool IsImageCue => MediaSourceItem is ImagePlaylistItem;

    private TextPlaylistItem? TextSource => MediaSourceItem as TextPlaylistItem;

    // Text-style edits replace the immutable TextPlaylistItem source; OnMediaSourceItemChanged re-raises
    // all text properties so the editor and the live (re-rendered) frame both follow.
    private void MutateText(Func<TextPlaylistItem, TextPlaylistItem> update)
    {
        if (MediaSourceItem is TextPlaylistItem t)
            MediaSourceItem = update(t);
    }

    partial void OnMediaSourceItemChanged(PlaylistItem? value)
    {
        ApplyLiveSourceDefaults(value);
        OnPropertyChanged(nameof(IsTextCue));
        OnPropertyChanged(nameof(IsImageCue));
        OnPropertyChanged(nameof(TextContent));
        OnPropertyChanged(nameof(TextFontFamily));
        OnPropertyChanged(nameof(TextFontSizePx));
        OnPropertyChanged(nameof(TextBold));
        OnPropertyChanged(nameof(TextItalic));
        OnPropertyChanged(nameof(TextColorHex));
        OnPropertyChanged(nameof(TextBackgroundHex));
        OnPropertyChanged(nameof(TextOutlineHex));
        OnPropertyChanged(nameof(TextOutlineWidthPx));
        OnPropertyChanged(nameof(TextHAlign));
        OnPropertyChanged(nameof(TextVAlign));
        OnPropertyChanged(nameof(TextWrapWidthFraction));
    }

    private void ApplyLiveSourceDefaults(PlaylistItem? source)
    {
        switch (source)
        {
            case PortAudioInputPlaylistItem pa:
                SourceHasAudio = true;
                SourceAudioChannels = Math.Max(1, pa.Channels);
                SourceHasVideo = false;
                SourceVideoIsAttachedPicture = false;
                SourceFrameRateNum = 0;
                SourceFrameRateDen = 0;
                SourceVideoWidth = 0;
                SourceVideoHeight = 0;
                break;
            case NDIInputPlaylistItem ndi:
                SourceHasAudio = !ndi.VideoOnly;
                SourceAudioChannels = ndi.VideoOnly
                    ? 0
                    : Math.Max(SourceAudioChannels, DefaultNdiInputAudioChannels);
                SourceHasVideo = !ndi.AudioOnly;
                SourceVideoIsAttachedPicture = false;
                if (ndi.AudioOnly)
                {
                    SourceFrameRateNum = 0;
                    SourceFrameRateDen = 0;
                    SourceVideoWidth = 0;
                    SourceVideoHeight = 0;
                }
                break;
        }
    }

    public string TextContent
    {
        get => TextSource?.Text ?? string.Empty;
        set { if (TextSource is { } t && t.Text != value) MutateText(_ => _ with { Text = value ?? string.Empty }); }
    }

    public string TextFontFamily
    {
        get => TextSource?.FontFamily ?? "Inter";
        set { if (TextSource is { } t && t.FontFamily != value && !string.IsNullOrWhiteSpace(value)) MutateText(_ => _ with { FontFamily = value }); }
    }

    public double TextFontSizePx
    {
        get => TextSource?.FontSizePx ?? 96;
        set { if (TextSource is { } t && Math.Abs(t.FontSizePx - value) > 0.001) MutateText(_ => _ with { FontSizePx = value }); }
    }

    public bool TextBold
    {
        get => TextSource?.Bold ?? false;
        set { if (TextSource is { } t && t.Bold != value) MutateText(_ => _ with { Bold = value }); }
    }

    public bool TextItalic
    {
        get => TextSource?.Italic ?? false;
        set { if (TextSource is { } t && t.Italic != value) MutateText(_ => _ with { Italic = value }); }
    }

    public string TextColorHex
    {
        get => ToHex(TextSource?.ColorArgb ?? 0xFFFFFFFF);
        set { if (TextSource is { } t) MutateText(_ => _ with { ColorArgb = ParseHex(value, t.ColorArgb) }); }
    }

    public string TextBackgroundHex
    {
        get => ToHex(TextSource?.BackgroundArgb ?? 0);
        set { if (TextSource is { } t) MutateText(_ => _ with { BackgroundArgb = ParseHex(value, t.BackgroundArgb) }); }
    }

    public string TextOutlineHex
    {
        get => ToHex(TextSource?.OutlineArgb ?? 0xFF000000);
        set { if (TextSource is { } t) MutateText(_ => _ with { OutlineArgb = ParseHex(value, t.OutlineArgb) }); }
    }

    public double TextOutlineWidthPx
    {
        get => TextSource?.OutlineWidthPx ?? 0;
        set { if (TextSource is { } t && Math.Abs(t.OutlineWidthPx - value) > 0.001) MutateText(_ => _ with { OutlineWidthPx = value }); }
    }

    public TextAlignH TextHAlign
    {
        get => TextSource?.HAlign ?? TextAlignH.Center;
        set { if (TextSource is { } t && t.HAlign != value) MutateText(_ => _ with { HAlign = value }); }
    }

    public TextAlignV TextVAlign
    {
        get => TextSource?.VAlign ?? TextAlignV.Middle;
        set { if (TextSource is { } t && t.VAlign != value) MutateText(_ => _ with { VAlign = value }); }
    }

    public double TextWrapWidthFraction
    {
        get => TextSource?.WrapWidthFraction ?? 0.9;
        set { if (TextSource is { } t && Math.Abs(t.WrapWidthFraction - value) > 0.001) MutateText(_ => _ with { WrapWidthFraction = Math.Clamp(value, 0, 1) }); }
    }

    private static string ToHex(uint argb) => $"#{argb:X8}";

    private static uint ParseHex(string? value, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var s = value.Trim().TrimStart('#');
        if (s.Length == 6) s = "FF" + s; // assume opaque when alpha omitted
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var argb)
            ? argb
            : fallback;
    }

    [ObservableProperty]
    private bool _sourceHasAudio;

    [ObservableProperty]
    private int _sourceAudioChannels;

    /// <summary>Persisted explicit audio track (container stream index); null = automatic.</summary>
    [ObservableProperty]
    private int? _audioTrackIndex;

    /// <summary>Content signature of the chosen track at pick time — guards against re-muxed files.</summary>
    [ObservableProperty]
    private string? _audioTrackSignature;

    /// <summary>Track picker entries: an "Automatic" row followed by the probed audio tracks.</summary>
    public ObservableCollection<CueAudioTrackChoice> AudioTrackChoices { get; } = new();

    [ObservableProperty]
    private CueAudioTrackChoice? _selectedAudioTrackChoice;

    /// <summary>The picker only shows when there is an actual choice (2+ real audio tracks).</summary>
    public bool HasMultipleAudioTracks => AudioTrackChoices.Count >= 3;

    partial void OnSelectedAudioTrackChoiceChanged(CueAudioTrackChoice? value)
    {
        AudioTrackIndex = value?.Index;
        AudioTrackSignature = value?.Signature;
    }

    /// <summary>
    /// Replaces the picker entries from a probe and re-resolves the persisted choice: same
    /// index+signature first, then signature alone (stream table shifted), else automatic.
    /// </summary>
    public void SetAudioTrackChoices(IReadOnlyList<S.Media.FFmpeg.MediaStreamInfo> tracks)
    {
        var persistedIndex = AudioTrackIndex;
        var persistedSignature = AudioTrackSignature;

        AudioTrackChoices.Clear();
        AudioTrackChoices.Add(CueAudioTrackChoice.Automatic);
        foreach (var track in tracks)
            AudioTrackChoices.Add(new CueAudioTrackChoice(track.Index, track.ContentSignature, track.ToDisplayString()));

        SelectedAudioTrackChoice =
            AudioTrackChoices.FirstOrDefault(c => c.Index == persistedIndex && c.Signature == persistedSignature)
            ?? AudioTrackChoices.FirstOrDefault(c => c.Signature is not null && c.Signature == persistedSignature)
            ?? AudioTrackChoices[0];
        OnPropertyChanged(nameof(HasMultipleAudioTracks));
    }

    [ObservableProperty]
    private bool _sourceVideoIsAttachedPicture;

    [ObservableProperty]
    private int _sourceFrameRateNum;

    [ObservableProperty]
    private int _sourceFrameRateDen;

    // Probed source video pixel dimensions (0 = unknown / no video). Used to size a new placement.
    [ObservableProperty]
    private int _sourceVideoWidth;

    [ObservableProperty]
    private int _sourceVideoHeight;

    [ObservableProperty]
    private CueRowStatus _rowStatus = CueRowStatus.Idle;

    /// <summary>True while this cue's media is held warm in the pre-roll cache (Phase 5.7.2).
    /// The status badge column draws a light outline when this is set and the row is idle.
    /// Kept in sync with <see cref="PreRollState"/> == <see cref="PreparedCueState.Ready"/>.</summary>
    [ObservableProperty]
    private bool _isPreRollWarm;

    /// <summary>Structured standby preparation state (idle/preparing/ready/failed), richer than the
    /// binary <see cref="IsPreRollWarm"/>. Drives the badge color and tooltip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreRollStateGlyph))]
    [NotifyPropertyChangedFor(nameof(PreRollStateTooltip))]
    [NotifyPropertyChangedFor(nameof(HasPreRollFailure))]
    private PreparedCueState _preRollState = PreparedCueState.Idle;

    /// <summary>Last standby preparation failure reason, when <see cref="PreRollState"/> is
    /// <see cref="PreparedCueState.Failed"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreRollStateTooltip))]
    private string? _preRollError;

    public bool HasPreRollFailure => PreRollState == PreparedCueState.Failed;

    /// <summary>Small glyph shown in the status column for the standby state.</summary>
    public string PreRollStateGlyph => PreRollState switch
    {
        PreparedCueState.Preparing => "…",
        PreparedCueState.Ready => "●",
        PreparedCueState.Stale => "◌",
        PreparedCueState.Failed => "!",
        _ => string.Empty,
    };

    public string? PreRollStateTooltip => PreRollState switch
    {
        PreparedCueState.Preparing => "Standby: preparing…",
        PreparedCueState.Ready => "Standby: ready (decoder open, seeked to start)",
        PreparedCueState.Stale => "Standby: stale (cue changed — re-preparing)",
        PreparedCueState.Failed => $"Standby failed: {PreRollError}",
        _ => null,
    };

    partial void OnPreRollStateChanged(PreparedCueState value) =>
        IsPreRollWarm = value == PreparedCueState.Ready;

    /// <summary>Color tag index 0..7 (Phase 5.8.1). 0 = no tag. The tree's first column shows
    /// a thin vertical strip filled with the palette color; the drawer's General tab lets the
    /// operator pick a swatch.</summary>
    [ObservableProperty]
    private int _colorTag;

    public string ColorTagBrush => CueColorTagPalette.BrushHex(ColorTag);

    partial void OnColorTagChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(ColorTagBrush));
    }

    public ObservableCollection<CueAudioRouteViewModel> AudioRoutes { get; } = new();

    public ObservableCollection<CueVideoPlacementViewModel> VideoPlacements { get; } = new();

    [ObservableProperty]
    private int _startOffsetMs;

    [ObservableProperty]
    private int _endOffsetMs;

    public TimeSpan? StartOffsetTime
    {
        get => TimeSpan.FromMilliseconds(Math.Max(0, StartOffsetMs));
        set => StartOffsetMs = ToOffsetMilliseconds(value);
    }

    public TimeSpan? EndOffsetTime
    {
        get => TimeSpan.FromMilliseconds(Math.Max(0, EndOffsetMs));
        set => EndOffsetMs = ToOffsetMilliseconds(value);
    }

    public string StartOffsetTimeText
    {
        get => FormatTimeCodeMs(StartOffsetMs);
        set
        {
            if (TryParseTimeCodeMilliseconds(value, out var ms))
                StartOffsetMs = ms;
        }
    }

    public string EndOffsetTimeText
    {
        get => FormatTimeCodeMs(EndOffsetMs);
        set
        {
            if (TryParseTimeCodeMilliseconds(value, out var ms))
                EndOffsetMs = ms;
        }
    }

    public string DurationTimeText
    {
        get => FormatTimeCodeMs(DurationMs);
        set
        {
            if (TryParseTimeCodeMilliseconds(value, out var ms))
                DurationMs = ms;
        }
    }

    [ObservableProperty]
    private bool _loop;

    [ObservableProperty]
    private CueEndBehavior _endBehavior = CueEndBehavior.Stop;

    /// <summary>Per-cue pre-roll opt-out. When set, the cue is excluded from standby warming so a
    /// large/expensive file doesn't hold an open decoder in the pre-roll window.</summary>
    [ObservableProperty]
    private bool _disablePreRoll;

    [ObservableProperty]
    private string _endpointIdText = string.Empty;

    [ObservableProperty]
    private bool _isEndpointBroken;

    [ObservableProperty]
    private string _extra = string.Empty;

    public CueGroupFireMode GroupFireMode
    {
        get => Enum.TryParse<CueGroupFireMode>(Extra, out var mode) ? mode : CueGroupFireMode.FirstCueOnly;
        set => Extra = value.ToString();
    }

    public CueActionKind ActionKind
    {
        get => Enum.TryParse<CueActionKind>(Extra, out var kind) ? kind : CueActionKind.OscOut;
        set => Extra = value.ToString();
    }

    public bool IsGroup => Kind == CueNodeKind.Group;

    public bool HasChildren => Children.Count > 0;

    public string KindLabel => Kind switch
    {
        CueNodeKind.Group => Strings.CueKindGroupLabel,
        CueNodeKind.Media => Strings.CueKindMediaLabel,
        CueNodeKind.Action => Strings.CueKindActionLabel,
        CueNodeKind.Comment => Strings.CueKindCommentLabel,
        _ => Strings.CueKindDefaultLabel,
    };

    public string DurationDisplay
    {
        get
        {
            if (Kind == CueNodeKind.Group)
                return BuildGroupDurationDisplay();
            if (Kind != CueNodeKind.Media || EffectiveDurationMs <= 0)
                return Strings.EmDash;
            return FormatDurationMs(EffectiveDurationMs);
        }
    }

    private string BuildGroupDurationDisplay()
    {
        long rollupMs;
        int itemCount;
        switch (GroupFireMode)
        {
            case CueGroupFireMode.FireAllSimultaneously:
                (rollupMs, itemCount) = AggregateChildrenDurations(static (sumMs, childMs) => Math.Max(sumMs, childMs));
                break;
            case CueGroupFireMode.FirstCueOnly:
                rollupMs = Children.FirstOrDefault(c => c.Kind != CueNodeKind.Comment)?.RolledDurationMs ?? 0;
                itemCount = Children.Count;
                break;
            case CueGroupFireMode.ArmedList:
            default:
                (rollupMs, itemCount) = AggregateChildrenDurations(static (sumMs, childMs) => sumMs + childMs);
                break;
        }

        if (rollupMs <= 0 && itemCount == 0)
            return Strings.EmDash;

        var time = rollupMs <= 0 ? Strings.EmDash : FormatDurationMs((int)Math.Min(int.MaxValue, rollupMs));
        return $"{time} · {itemCount}";
    }

    /// <summary>Walk children, accumulate via <paramref name="combine"/>, count items recursively.
    /// Children that are groups roll up first via <see cref="RolledDurationMs"/>.</summary>
    private (long Ms, int Count) AggregateChildrenDurations(Func<long, long, long> combine)
    {
        long ms = 0;
        var count = 0;
        foreach (var child in Children)
        {
            if (child.Kind == CueNodeKind.Comment) continue;
            ms = combine(ms, child.RolledDurationMs);
            count++;
        }
        return (ms, count);
    }

    /// <summary>Effective duration for roll-ups: groups recursively roll up via their own
    /// <see cref="BuildGroupDurationDisplay"/> rules; media cues return their probed
    /// <see cref="EffectiveDurationMs"/>; other kinds (Action / Comment) return 0.</summary>
    public long RolledDurationMs
    {
        get
        {
            switch (Kind)
            {
                case CueNodeKind.Media: return EffectiveDurationMs;
                case CueNodeKind.Group:
                {
                    switch (GroupFireMode)
                    {
                        case CueGroupFireMode.FireAllSimultaneously:
                            return AggregateChildrenDurations(static (sumMs, childMs) => Math.Max(sumMs, childMs)).Ms;
                        case CueGroupFireMode.FirstCueOnly:
                            return Children.FirstOrDefault(c => c.Kind != CueNodeKind.Comment)?.RolledDurationMs ?? 0;
                        default:
                            return AggregateChildrenDurations(static (sumMs, childMs) => sumMs + childMs).Ms;
                    }
                }
                default: return 0;
            }
        }
    }

    public int EffectiveDurationMs =>
        Kind == CueNodeKind.Media && DurationMs > 0
            ? Math.Max(0, DurationMs - Math.Max(0, StartOffsetMs) - Math.Max(0, EndOffsetMs))
            : 0;

    private static string FormatDurationMs(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private static string FormatTimeCodeMs(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    private static bool TryParseTimeCodeMilliseconds(string? value, out int ms)
    {
        ms = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            text = text[..^2].Trim();
        text = text.Replace(',', '.');

        if (!text.Contains(':'))
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plainMs))
                return false;
            ms = ClampMilliseconds(plainMs);
            return true;
        }

        var parts = text.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3)
            return false;

        var hours = 0;
        var minutesIndex = 0;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours) || hours < 0)
                return false;
            minutesIndex = 1;
        }

        if (!int.TryParse(parts[minutesIndex], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes < 0)
            return false;
        if (!double.TryParse(parts[minutesIndex + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
            return false;
        if (parts.Length == 3 && (minutes >= 60 || seconds >= 60))
            return false;

        var totalMs = (hours * 3_600_000d) + (minutes * 60_000d) + (seconds * 1_000d);
        ms = ClampMilliseconds(totalMs);
        return true;
    }

    private static int ClampMilliseconds(double ms)
    {
        if (double.IsNaN(ms) || double.IsInfinity(ms) || ms <= 0)
            return 0;
        return (int)Math.Min(int.MaxValue, Math.Round(ms, MidpointRounding.AwayFromZero));
    }

    partial void OnDurationMsChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(DurationDisplay));
        OnPropertyChanged(nameof(RolledDurationMs));
        OnPropertyChanged(nameof(EffectiveDurationMs));
        OnPropertyChanged(nameof(DurationTimeText));
    }

    partial void OnStartOffsetMsChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(DurationDisplay));
        OnPropertyChanged(nameof(RolledDurationMs));
        OnPropertyChanged(nameof(EffectiveDurationMs));
        OnPropertyChanged(nameof(StartOffsetTime));
        OnPropertyChanged(nameof(StartOffsetTimeText));
    }

    partial void OnEndOffsetMsChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(DurationDisplay));
        OnPropertyChanged(nameof(RolledDurationMs));
        OnPropertyChanged(nameof(EffectiveDurationMs));
        OnPropertyChanged(nameof(EndOffsetTime));
        OnPropertyChanged(nameof(EndOffsetTimeText));
    }

    private static int ToOffsetMilliseconds(TimeSpan? value)
    {
        if (value is null || value.Value <= TimeSpan.Zero)
            return 0;
        return (int)Math.Min(int.MaxValue, Math.Round(value.Value.TotalMilliseconds));
    }

    partial void OnExtraChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(GroupFireMode));
        OnPropertyChanged(nameof(ActionKind));
        // GroupFireMode determines the roll-up formula — refresh derived displays.
        if (Kind == CueNodeKind.Group)
        {
            OnPropertyChanged(nameof(DurationDisplay));
            OnPropertyChanged(nameof(RolledDurationMs));
        }
    }

    private void OnChildrenCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = sender;
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<CueNodeViewModel>())
                item.PropertyChanged -= OnChildPropertyChangedForRollup;
        }
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<CueNodeViewModel>())
                item.PropertyChanged += OnChildPropertyChangedForRollup;
        }
        OnPropertyChanged(nameof(HasChildren));
        if (Kind == CueNodeKind.Group)
        {
            OnPropertyChanged(nameof(DurationDisplay));
            OnPropertyChanged(nameof(RolledDurationMs));
        }
    }

    private void OnChildPropertyChangedForRollup(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Kind != CueNodeKind.Group) return;
        if (e.PropertyName is nameof(RolledDurationMs)
            or nameof(DurationMs)
            or nameof(StartOffsetMs)
            or nameof(EndOffsetMs)
            or nameof(EffectiveDurationMs)
            or nameof(GroupFireMode))
        {
            OnPropertyChanged(nameof(DurationDisplay));
            OnPropertyChanged(nameof(RolledDurationMs));
        }
    }

    public static CueNodeViewModel FromModel(CueNode node, Func<Guid, OutputLineViewModel?>? resolveLine = null)
    {
        switch (node)
        {
            case CueGroupNode g:
            {
                var vm = new CueNodeViewModel(CueNodeKind.Group)
                {
                    Id = g.Id,
                    Number = g.Number,
                    Label = g.Label,
                    TriggerMode = g.TriggerMode,
                    PreWaitMs = g.PreWaitMs,
                    Notes = g.Notes,
                    ColorTag = g.ColorTag,
                    Extra = g.FireMode.ToString(),
                };
                foreach (var c in g.Children)
                    vm.Children.Add(FromModel(c, resolveLine));
                return vm;
            }
            case MediaCueNode m:
            {
                var vm = new CueNodeViewModel(CueNodeKind.Media)
                {
                    Id = m.Id,
                    Number = m.Number,
                    Label = m.Label,
                    TriggerMode = m.TriggerMode,
                    PreWaitMs = m.PreWaitMs,
                    Notes = m.Notes,
                    ColorTag = m.ColorTag,
                    SourceOrAction = m.Source?.DisplayName ?? string.Empty,
                    FadeInMs = m.FadeInMs,
                    FadeOutMs = m.FadeOutMs,
                    DurationMs = m.DurationMs,
                    SourceHasVideo = m.HasVideo,
                    SourceHasAudio = m.HasAudio,
                    SourceAudioChannels = m.AudioChannels,
                    AudioTrackIndex = m.AudioTrackIndex,
                    AudioTrackSignature = m.AudioTrackSignature,
                    SourceVideoIsAttachedPicture = m.VideoIsAttachedPicture,
                    SourceFrameRateNum = m.SourceFrameRateNum,
                    SourceFrameRateDen = m.SourceFrameRateDen,
                    SourceVideoWidth = m.SourceVideoWidth,
                    SourceVideoHeight = m.SourceVideoHeight,
                    MediaSourceItem = m.Source,
                    StartOffsetMs = m.StartOffsetMs,
                    EndOffsetMs = m.EndOffsetMs,
                    Loop = m.Loop,
                    EndBehavior = m.EndBehavior,
                    DisablePreRoll = m.DisablePreRoll,
                };
                foreach (var route in m.AudioRoutes)
                    vm.AudioRoutes.Add(CueAudioRouteViewModel.FromModel(route, resolveLine));
                foreach (var placement in m.VideoPlacements)
                    vm.VideoPlacements.Add(CueVideoPlacementViewModel.FromModel(placement));
                return vm;
            }
            case ActionCueNode a:
                return new CueNodeViewModel(CueNodeKind.Action)
                {
                    Id = a.Id,
                    Number = a.Number,
                    Label = a.Label,
                    TriggerMode = a.TriggerMode,
                    PreWaitMs = a.PreWaitMs,
                    Notes = a.Notes,
                    ColorTag = a.ColorTag,
                    SourceOrAction = a.AddressOrMessage,
                    EndpointIdText = a.EndpointId?.ToString() ?? string.Empty,
                    Extra = a.ActionKind.ToString(),
                };
            case CommentCueNode c:
                return new CueNodeViewModel(CueNodeKind.Comment)
                {
                    Id = c.Id,
                    Number = c.Number,
                    Label = c.Label,
                    TriggerMode = c.TriggerMode,
                    PreWaitMs = c.PreWaitMs,
                    Notes = c.Notes,
                    ColorTag = c.ColorTag,
                    SourceOrAction = c.Text,
                };
            default:
                return new CueNodeViewModel(CueNodeKind.Comment) { Label = Strings.UnsupportedCueNodeLabel };
        }
    }

    public CueNode ToModel()
    {
        return Kind switch
        {
            CueNodeKind.Group => new CueGroupNode
            {
                Id = Id,
                Number = Number,
                Label = Label,
                TriggerMode = TriggerMode,
                PreWaitMs = PreWaitMs,
                Notes = Notes,
                ColorTag = ColorTag,
                FireMode = Enum.TryParse<CueGroupFireMode>(Extra, out var fm) ? fm : CueGroupFireMode.FirstCueOnly,
                Children = Children.Select(c => c.ToModel()).ToList(),
            },
            CueNodeKind.Media => new MediaCueNode
            {
                Id = Id,
                Number = Number,
                Label = Label,
                TriggerMode = TriggerMode,
                PreWaitMs = PreWaitMs,
                Notes = Notes,
                ColorTag = ColorTag,
                Source = MediaSourceItem
                           ?? (string.IsNullOrWhiteSpace(SourceOrAction)
                               ? null
                               : new FilePlaylistItem(SourceOrAction)),
                FadeInMs = Math.Max(0, FadeInMs),
                FadeOutMs = Math.Max(0, FadeOutMs),
                DurationMs = Math.Max(0, DurationMs),
                HasVideo = SourceHasVideo,
                HasAudio = SourceHasAudio,
                AudioChannels = Math.Max(0, SourceAudioChannels),
                AudioTrackIndex = AudioTrackIndex,
                AudioTrackSignature = AudioTrackSignature,
                VideoIsAttachedPicture = SourceVideoIsAttachedPicture,
                SourceFrameRateNum = Math.Max(0, SourceFrameRateNum),
                SourceFrameRateDen = Math.Max(0, SourceFrameRateDen),
                SourceVideoWidth = Math.Max(0, SourceVideoWidth),
                SourceVideoHeight = Math.Max(0, SourceVideoHeight),
                StartOffsetMs = Math.Max(0, StartOffsetMs),
                EndOffsetMs = Math.Max(0, EndOffsetMs),
                Loop = Loop,
                EndBehavior = EndBehavior,
                DisablePreRoll = DisablePreRoll,
                AudioRoutes = AudioRoutes.Select(r => r.ToModel()).ToList(),
                VideoPlacements = VideoPlacements.Select(p => p.ToModel()).ToList(),
            },
            CueNodeKind.Action => new ActionCueNode
            {
                Id = Id,
                Number = Number,
                Label = Label,
                TriggerMode = TriggerMode,
                PreWaitMs = PreWaitMs,
                Notes = Notes,
                ColorTag = ColorTag,
                AddressOrMessage = SourceOrAction,
                EndpointId = Guid.TryParse(EndpointIdText, out var endpointId) ? endpointId : null,
                ActionKind = Enum.TryParse<CueActionKind>(Extra, out var ak) ? ak : CueActionKind.OscOut,
            },
            _ => new CommentCueNode
            {
                Id = Id,
                Number = Number,
                Label = Label,
                TriggerMode = TriggerMode,
                PreWaitMs = PreWaitMs,
                Notes = Notes,
                ColorTag = ColorTag,
                Text = SourceOrAction,
            },
        };
    }
}

public sealed partial class CueListEditorViewModel : ObservableObject
{
    public CueListEditorViewModel(string name)
    {
        Name = name;
    }

    [ObservableProperty]
    private string _name;

    partial void OnNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            Name = Strings.CueListFileNameFallback;
    }

    [ObservableProperty]
    private string? _path;

    public ObservableCollection<CueCompositionViewModel> Compositions { get; } = new();

    public ObservableCollection<CueVideoOutputBindingViewModel> VideoOutputs { get; } = new();

    public ObservableCollection<CueNodeViewModel> Nodes { get; } = new();

    public CueList ToModel() => new()
    {
        Name = Name,
        PreRollCount = PreRollCount,
        MaxPreparedDecoders = MaxPreparedDecoders,
        DefaultTriggerMode = DefaultTriggerMode,
        AutoRenumberOnInsert = AutoRenumberOnInsert,
        Compositions = Compositions.Select(c => c.ToModel()).ToList(),
        VideoOutputs = VideoOutputs.Select(o => o.ToModel()).ToList(),
        Nodes = Nodes.Select(n => n.ToModel()).ToList(),
    };

    [ObservableProperty]
    private int _preRollCount;

    [ObservableProperty]
    private int _maxPreparedDecoders;

    [ObservableProperty]
    private CueTriggerMode _defaultTriggerMode = CueTriggerMode.Manual;

    [ObservableProperty]
    private bool _autoRenumberOnInsert;

    public static CueListEditorViewModel FromModel(
        CueList list,
        string? path = null,
        Func<Guid, OutputLineViewModel?>? resolveLine = null)
    {
        var vm = new CueListEditorViewModel(list.Name)
        {
            Path = path,
            PreRollCount = Math.Max(0, list.PreRollCount),
            MaxPreparedDecoders = Math.Max(0, list.MaxPreparedDecoders),
            DefaultTriggerMode = list.DefaultTriggerMode,
            AutoRenumberOnInsert = list.AutoRenumberOnInsert,
        };
        foreach (var c in list.Compositions)
            vm.Compositions.Add(CueCompositionViewModel.FromModel(c));
        foreach (var o in list.VideoOutputs)
            vm.VideoOutputs.Add(CueVideoOutputBindingViewModel.FromModel(o, resolveLine));
        foreach (var node in list.Nodes)
            vm.Nodes.Add(CueNodeViewModel.FromModel(node, resolveLine));
        return vm;
    }
}

public sealed record PreviewAudioDeviceOption(int? DeviceIndex, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public enum CueNodeDropPlacement
{
    Before,
    Inside,
    After,
}

public partial class CuePlayerViewModel : ViewModelBase
{
    private CancellationTokenSource? _transportRunCts;

    /// <summary>
    /// Host-provided media execution callback. When null, media cues only update transport state.
    /// </summary>
    public Func<MediaCueNode, CancellationToken, Task<string?>>? MediaCueExecutor { get; set; }

    /// <summary>
    /// Host-provided coordinated group execution callback. Opens all cues in parallel, then starts
    /// them in sync. When null, falls back to dispatching each cue independently.
    /// </summary>
    public Func<IReadOnlyList<MediaCueNode>, CancellationToken, Task<string?>>? MediaCueGroupExecutor { get; set; }

    /// <summary>
    /// Host-provided action execution callback. When null, action cues only update transport state.
    /// </summary>
    public Func<ActionCueNode, CancellationToken, Task<string?>>? ActionCueExecutor { get; set; }

    /// <summary>Host-provided stop callback — Stop / Panic forwards to this so the playback
    /// engine can tear down its session. Optional; null in tests.</summary>
    public Func<Task>? StopPlaybackCallback { get; set; }

    /// <summary>Host-provided pause callback — Pause/Resume forwards to this so the playback
    /// engine freezes active media instead of only deferring pending cue delays.</summary>
    public Func<bool, Task>? SetPlaybackPausedCallback { get; set; }

    /// <summary>Host-provided preview callbacks (Phase 5.5). Null in tests.</summary>
    public Func<MediaCueNode, CancellationToken, Task<string?>>? PreviewCueCallback { get; set; }
    public Func<Task>? StopPreviewCallback { get; set; }
    public Func<Guid, TimeSpan, Task>? SeekCueCallback { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewingSelectedCue))]
    [NotifyPropertyChangedFor(nameof(IsCueScrubberVisible))]
    [NotifyPropertyChangedFor(nameof(PreviewButtonLabel))]
    [NotifyCanExecuteChangedFor(nameof(TogglePreviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(SeekActiveCueFromScrubberCommand))]
    private Guid? _previewingCueId;

    public bool IsPreviewing => PreviewingCueId is not null;

    public bool IsPreviewingSelectedCue =>
        PreviewingCueId is { } id && SelectedCueNode?.Id == id;

    public string PreviewButtonLabel =>
        IsPreviewingSelectedCue ? Strings.StopPreviewCueButton : Strings.PreviewCueButton;

    public ObservableCollection<PreviewAudioDeviceOption> PreviewAudioDevices { get; } = new();

    [ObservableProperty]
    private PreviewAudioDeviceOption? _selectedPreviewAudioDevice;

    partial void OnSelectedPreviewAudioDeviceChanged(PreviewAudioDeviceOption? value) =>
        OnPropertyChanged(nameof(PreviewAudioDeviceIndex));

    public int? PreviewAudioDeviceIndex => SelectedPreviewAudioDevice?.DeviceIndex;

    public void RefreshPreviewAudioDevices()
    {
        PreviewAudioDevices.Clear();
        PreviewAudioDevices.Add(new PreviewAudioDeviceOption(null, Strings.Format(nameof(Strings.DefaultDeviceLabel))));
        foreach (var dev in S.Media.PortAudio.PortAudioDeviceCatalog.EnumerateOutputDevices())
            PreviewAudioDevices.Add(new PreviewAudioDeviceOption(dev.GlobalDeviceIndex, dev.Name));
        SelectedPreviewAudioDevice ??= PreviewAudioDevices.FirstOrDefault();
    }

    private float[]? _selectedCueWaveform;
    private int _selectedCueWaveformRevision;
    private CancellationTokenSource? _waveformCts;

    public float[]? SelectedCueWaveform
    {
        get => _selectedCueWaveform;
        private set { _selectedCueWaveform = value; OnPropertyChanged(); }
    }

    public int SelectedCueWaveformRevision
    {
        get => _selectedCueWaveformRevision;
        private set { _selectedCueWaveformRevision = value; OnPropertyChanged(); }
    }

    public bool HasSelectedCueWaveform =>
        HasSelectedMediaCueWithAudio && SelectedCueWaveform is { Length: > 0 };

    private void ExtractCueWaveform(CueNodeViewModel? cue)
    {
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _waveformCts = null;

        if (cue is not { Kind: CueNodeKind.Media } || !cue.SourceHasAudio)
        {
            SelectedCueWaveform = null;
            SelectedCueWaveformRevision++;
            OnPropertyChanged(nameof(HasSelectedCueWaveform));
            return;
        }

        var source = cue.MediaSourceItem;
        var path = source is FilePlaylistItem f ? f.Path : null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SelectedCueWaveform = null;
            SelectedCueWaveformRevision++;
            OnPropertyChanged(nameof(HasSelectedCueWaveform));
            return;
        }

        _waveformCts = new CancellationTokenSource();
        var ct = _waveformCts.Token;
        _ = Task.Run(async () =>
        {
            var peaks = await Playback.WaveformExtractor.ExtractAsync(path, ct);
            if (!ct.IsCancellationRequested)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    SelectedCueWaveform = peaks;
                    SelectedCueWaveformRevision++;
                    OnPropertyChanged(nameof(HasSelectedCueWaveform));
                });
            }
        }, ct);
    }

    /// <summary>Visible when the selected cue is active in the Now Playing panel (Phase 5.5.2).</summary>
    public bool IsCueScrubberVisible =>
        SelectedCueNode is not null
        && (ActiveCues.Any(a => a.CueId == SelectedCueNode.Id) || IsPreviewingSelectedCue);

    [ObservableProperty]
    private double _cueScrubberValue;

    public CuePlayerViewModel()
    {
        var initial = new CueListEditorViewModel(Strings.DefaultCueListName);
        CueLists.Add(initial);
        SelectedCueList = initial;
    }

    /// <summary>Wire the cue player to the shared output registry. Audio routes and video output
    /// bindings pick lines from this list directly — no per-cue-list device config.</summary>
    public void SetAvailableOutputs(ObservableCollection<OutputLineViewModel> outputs)
    {
        AvailableOutputs = outputs;
        outputs.CollectionChanged += (_, _) => RefreshAvailableOutputBuckets();
        RefreshAvailableOutputBuckets();
    }

    private void RefreshAvailableOutputBuckets()
    {
        AvailableAudioOutputs.Clear();
        AvailableVideoOutputs.Clear();
        foreach (var line in AvailableOutputs)
        {
            if (line.Definition is Models.PortAudioOutputDefinition)
            {
                AvailableAudioOutputs.Add(line);
            }
            else if (line.Definition is Models.LocalVideoOutputDefinition)
            {
                AvailableVideoOutputs.Add(line);
            }
            else if (line.Definition is Models.NDIOutputDefinition ndi)
            {
                if (ndi.StreamMode != NDIOutputStreamMode.VideoOnly)
                    AvailableAudioOutputs.Add(line);
                if (ndi.StreamMode != NDIOutputStreamMode.AudioOnly)
                    AvailableVideoOutputs.Add(line);
            }
        }
        ResolveAllBindingLineRefs();
    }

    private OutputLineViewModel? ResolveOutputLine(Guid lineId) =>
        AvailableOutputs.FirstOrDefault(l => l.Definition.Id == lineId);

    /// <summary>Walks every loaded cue list and refreshes the resolved <c>LineRef</c> on each
    /// audio route + video output binding. Called when the available output set changes (lines
    /// added/removed/swapped) so the row dots and tooltips stay accurate.</summary>
    private void ResolveAllBindingLineRefs()
    {
        foreach (var list in CueLists)
        {
            foreach (var binding in list.VideoOutputs)
                binding.SetLineResolver(ResolveOutputLine);
            ResolveLineRefsInNodes(list.Nodes);
        }
    }

    private void ResolveLineRefsInNodes(IEnumerable<CueNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            foreach (var route in node.AudioRoutes)
                route.SetLineResolver(ResolveOutputLine);
            ResolveLineRefsInNodes(node.Children);
        }
    }

    public ObservableCollection<CueListEditorViewModel> CueLists { get; } = new();

    public IReadOnlyList<CueEndBehavior> CueEndBehaviors { get; } = Enum.GetValues<CueEndBehavior>();
    public IReadOnlyList<CueTriggerMode> CueTriggerModes { get; } = Enum.GetValues<CueTriggerMode>();
    public IReadOnlyList<CueGroupFireMode> GroupFireModes { get; } = Enum.GetValues<CueGroupFireMode>();
    public IReadOnlyList<CueLayerPosition> LayerPositions { get; } = Enum.GetValues<CueLayerPosition>();

    public IReadOnlyList<TextAlignH> TextHAlignOptions { get; } = Enum.GetValues<TextAlignH>();

    public IReadOnlyList<TextAlignV> TextVAlignOptions { get; } = Enum.GetValues<TextAlignV>();

    [ObservableProperty]
    private CueListEditorViewModel? _selectedCueList;

    [ObservableProperty]
    private CueNodeViewModel? _selectedCueNode;

    /// <summary>All cue nodes the operator currently has highlighted in the tree (multi-select).
    /// The drawer still shows fields from the singular <see cref="SelectedCueNode"/>, but
    /// "+ Route" / "+ Placement" fan their action out across every media cue in this list — so
    /// the operator can stage a route on 11 audio cues in one click.</summary>
    private readonly List<CueNodeViewModel> _selectedCueNodes = new();

    public IReadOnlyList<CueNodeViewModel> SelectedCueNodes => _selectedCueNodes;

    /// <summary>Called by <c>CuePlayerView</c>'s row-selection changed handler with the live set
    /// of selected nodes. Keeps the singular <see cref="SelectedCueNode"/> as the primary
    /// (first in the list) so all the existing drawer bindings keep working.</summary>
    public void UpdateSelection(IReadOnlyList<CueNodeViewModel> selected)
    {
        _selectedCueNodes.Clear();
        _selectedCueNodes.AddRange(selected);
        SelectedCueNode = _selectedCueNodes.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCueCount));
        OnPropertyChanged(nameof(IsMultiSelected));
    }

    [ObservableProperty]
    private CueCompositionViewModel? _selectedComposition;

    [ObservableProperty]
    private CueVideoOutputBindingViewModel? _selectedVideoOutput;

    [ObservableProperty]
    private CueAudioRouteViewModel? _selectedAudioRoute;

    [ObservableProperty]
    private CueVideoPlacementViewModel? _selectedVideoPlacement;

    [ObservableProperty]
    private ActionEndpoint? _selectedActionEndpoint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransportState))]
    private CueNodeViewModel? _standbyCueNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransportState))]
    private CueNodeViewModel? _currentCueNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransportState))]
    private bool _isTransportPaused;

    [ObservableProperty]
    private string? _statusMessage;

    private static readonly TimeSpan StatusMessageAutoClearDelay = TimeSpan.FromSeconds(5);
    private CancellationTokenSource? _statusMessageClearCts;

    [ObservableProperty]
    private bool _isCueEditMode = true;

    public ObservableCollection<CueNodeViewModel> VisibleNodes =>
        SelectedCueList?.Nodes ?? _emptyNodes;

    private readonly ObservableCollection<CueNodeViewModel> _emptyNodes = new();
    private readonly ObservableCollection<CueCompositionViewModel> _emptyCompositions = new();
    private readonly ObservableCollection<CueVideoOutputBindingViewModel> _emptyVideoOutputs = new();
    private readonly ObservableCollection<CueAudioRouteViewModel> _emptyAudioRoutes = new();
    private readonly ObservableCollection<CueVideoPlacementViewModel> _emptyVideoPlacements = new();
    public ObservableCollection<ActionEndpoint> ActionEndpoints { get; } = new();

    /// <summary>Bag of output lines the operator has created in the shared
    /// <c>OutputManagementView</c>. <see cref="MainViewModel"/> populates this via
    /// <see cref="SetAvailableOutputs"/>. Updates are live — adding/removing in OutputManagement
    /// flows through to the cue player's dropdowns immediately.</summary>
    public ObservableCollection<OutputLineViewModel> AvailableOutputs { get; private set; } = new();

    public ObservableCollection<OutputLineViewModel> AvailableAudioOutputs { get; } = new();
    public ObservableCollection<OutputLineViewModel> AvailableVideoOutputs { get; } = new();

    public ObservableCollection<CueCompositionViewModel> VisibleCompositions =>
        SelectedCueList?.Compositions ?? _emptyCompositions;

    public ObservableCollection<CueVideoOutputBindingViewModel> VisibleVideoOutputs =>
        SelectedCueList?.VideoOutputs ?? _emptyVideoOutputs;

    public ObservableCollection<CueAudioRouteViewModel> VisibleAudioRoutes =>
        SelectedCueNode is { Kind: CueNodeKind.Media } node ? node.AudioRoutes : _emptyAudioRoutes;

    public ObservableCollection<CueVideoPlacementViewModel> VisibleVideoPlacements =>
        SelectedCueNode is { Kind: CueNodeKind.Media } node ? node.VideoPlacements : _emptyVideoPlacements;

    /// <summary>Aspect ratio (w/h) of the composition the placement editor canvas should mirror.</summary>
    public double PlacementCanvasAspect
    {
        get
        {
            var comp = SelectedVideoPlacement is { } p
                ? SelectedCueList?.Compositions.FirstOrDefault(c => c.Id == p.CompositionId)
                : null;
            comp ??= SelectedComposition ?? SelectedCueList?.Compositions.FirstOrDefault();
            return comp is { Width: > 0, Height: > 0 } ? (double)comp.Width / comp.Height : 16.0 / 9.0;
        }
    }

    public bool HasSelectedMediaCue => SelectedCueNode?.Kind == CueNodeKind.Media;
    public bool HasSelectedTextCue => SelectedCueNode is { Kind: CueNodeKind.Media } media && media.IsTextCue;

    /// <summary>Image/text cues have no inherent length, so the operator sets the hold duration directly.</summary>
    public bool HasSelectedStaticCue =>
        SelectedCueNode is { Kind: CueNodeKind.Media } media && (media.IsImageCue || media.IsTextCue);
    public bool HasSelectedActionCue => SelectedCueNode?.Kind == CueNodeKind.Action;
    public bool HasSelectedCommentCue => SelectedCueNode?.Kind == CueNodeKind.Comment;
    public bool HasSelectedGroupCue => SelectedCueNode?.Kind == CueNodeKind.Group;
    public bool HasSelectedCue => SelectedCueNode is not null;

    /// <summary>Video tab visibility: media cue AND the source actually has a video stream
    /// (decodable — covers regular video files and audio files with attached picture cover art).</summary>
    public bool HasSelectedMediaCueWithVideo =>
        SelectedCueNode is { Kind: CueNodeKind.Media } media && media.SourceHasVideo;

    /// <summary>Audio tab visibility: media cue AND (the probe found audio OR the cue already
    /// has routes wired). The "has routes" branch keeps the tab editable for pre-Phase-5.1 cues
    /// that never went through the audio-stream probe but already have routes saved on disk.</summary>
    public bool HasSelectedMediaCueWithAudio =>
        SelectedCueNode is { Kind: CueNodeKind.Media } media
        && (media.SourceHasAudio || media.AudioRoutes.Count > 0);

    /// <summary>Operator hint banner — true when the only "video" the source offers is an
    /// attached picture (e.g. MP3 album art). The Video tab still works (the still frame can be
    /// placed into a composition for a now-playing slate) but it's worth flagging.</summary>
    public bool HasSelectedMediaCueWithAttachedPictureOnly =>
        SelectedCueNode is { Kind: CueNodeKind.Media } media && media.SourceVideoIsAttachedPicture;

    /// <summary>Non-null when the selected media cue's probed frame rate doesn't divide evenly
    /// into at least one wired composition's canvas rate (Phase 5.9.2).</summary>
    public string? VideoFrameRateMismatchWarning => BuildVideoFrameRateMismatchWarning();

    public bool HasVideoFrameRateMismatchWarning =>
        !string.IsNullOrWhiteSpace(VideoFrameRateMismatchWarning);

    /// <summary>How many cues the operator currently has highlighted in the tree. The drawer
    /// shows a banner above the routes/placements lists when this is > 1 so the operator knows
    /// that "+ Route" / "+ Placement" applies to all of them, not just the primary.</summary>
    public int SelectedCueCount => _selectedCueNodes.Count;

    /// <summary>True iff <see cref="SelectedCueCount"/> > 1. Bound as the banner visibility flag —
    /// Avalonia's <c>ObjectConverters</c> doesn't ship a <c>GreaterThan</c>, so we expose a
    /// dedicated boolean rather than wire a per-view converter.</summary>
    public bool IsMultiSelected => _selectedCueNodes.Count > 1;

    public string SelectedCueDrawerTitle => SelectedCueNode is null
        ? Strings.SelectACueDrawerHint
        : string.IsNullOrWhiteSpace(SelectedCueNode.Number)
            ? $"{SelectedCueNode.Label} — {SelectedCueNode.KindLabel}"
            : $"{SelectedCueNode.Number} {SelectedCueNode.Label} — {SelectedCueNode.KindLabel}";
    public IReadOnlyList<CueActionKind> ActionKinds { get; } = Enum.GetValues<CueActionKind>();

    public string SelectedActionEndpointSummary
    {
        get
        {
            if (SelectedCueNode?.Kind != CueNodeKind.Action)
                return string.Empty;

            if (!Guid.TryParse(SelectedCueNode.EndpointIdText, out var endpointId))
                return Strings.NoActionTargetSelected;

            return SelectedActionEndpoint is null
                ? Strings.Format(nameof(Strings.ActionTargetMissingFormat), endpointId)
                : Strings.Format(
                    nameof(Strings.SelectedActionTargetFormat),
                    SelectedActionEndpoint.Name,
                    SelectedActionEndpoint.KindLabel,
                    SelectedActionEndpoint.Summary);
        }
    }

    public string TransportState =>
        CurrentCueNode is null
            ? Strings.Format(
                nameof(Strings.CueTransportStandbyFormat),
                StandbyCueNode is null ? Strings.NoneInParensLabel : CueDisplay(StandbyCueNode))
            : Strings.Format(
                nameof(Strings.CueTransportRunningFormat),
                IsTransportPaused ? Strings.CueTransportPausedLabel : Strings.CueTransportRunningLabel,
                CueDisplay(CurrentCueNode))
              + (StandbyCueNode is null
                  ? string.Empty
                  : Strings.Format(nameof(Strings.CueTransportNextFormat), CueDisplay(StandbyCueNode)));

    partial void OnSelectedCueListChanged(CueListEditorViewModel? value)
    {
        CancelTransportRun();
        OnPropertyChanged(nameof(VisibleNodes));
        OnPropertyChanged(nameof(VisibleCompositions));
        OnPropertyChanged(nameof(VisibleVideoOutputs));
        SelectedComposition = value?.Compositions.FirstOrDefault();
        SelectedVideoOutput = value?.VideoOutputs.FirstOrDefault();
        _selectedCueNodes.Clear();
        OnPropertyChanged(nameof(SelectedCueCount));
        OnPropertyChanged(nameof(IsMultiSelected));
        SelectedCueNode = null;
        SelectedAudioRoute = null;
        SelectedVideoPlacement = null;
        CurrentCueNode = null;
        StandbyCueNode = null;
        IsTransportPaused = false;
        RemoveCueListCommand.NotifyCanExecuteChanged();
        OpenCueOutputSetupCommand.NotifyCanExecuteChanged();
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StandbySelectedCommand.NotifyCanExecuteChanged();
        ResubscribeCompositionFpsWatch(value);
    }

    private CueListEditorViewModel? _watchedCueListForFps;

    private void ResubscribeCompositionFpsWatch(CueListEditorViewModel? value)
    {
        if (_watchedCueListForFps is not null)
        {
            foreach (var comp in _watchedCueListForFps.Compositions)
                comp.CompositionFrameRateChanged -= OnCompositionFrameRateChanged;
        }

        _watchedCueListForFps = value;
        if (value is null)
            return;

        foreach (var comp in value.Compositions)
            comp.CompositionFrameRateChanged += OnCompositionFrameRateChanged;
        RefreshVideoFrameRateMismatchWarning();
    }

    private void OnCompositionFrameRateChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        RefreshVideoFrameRateMismatchWarning();
    }

    private CueNodeViewModel? _watchedSelectedCueForProbe;

    private void OnSelectedCueProbeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CueNodeViewModel.MediaSourceItem)
            or nameof(CueNodeViewModel.SourceHasVideo)
            or nameof(CueNodeViewModel.SourceHasAudio)
            or nameof(CueNodeViewModel.SourceAudioChannels)
            or nameof(CueNodeViewModel.SourceVideoIsAttachedPicture)
            or nameof(CueNodeViewModel.SourceFrameRateNum)
            or nameof(CueNodeViewModel.SourceFrameRateDen))
        {
            OnPropertyChanged(nameof(HasSelectedMediaCueWithVideo));
            OnPropertyChanged(nameof(HasSelectedTextCue));
            OnPropertyChanged(nameof(HasSelectedStaticCue));
            OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
            OnPropertyChanged(nameof(HasSelectedMediaCueWithAttachedPictureOnly));
            OnPropertyChanged(nameof(IsPreviewingSelectedCue));
            OnPropertyChanged(nameof(PreviewButtonLabel));
            OnPropertyChanged(nameof(IsCueScrubberVisible));
            RefreshVideoFrameRateMismatchWarning();
            SyncCueScrubberFromActiveSelection();
            TogglePreviewCommand.NotifyCanExecuteChanged();
            SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(CueNodeViewModel.SourceHasAudio))
                ExtractCueWaveform(_watchedSelectedCueForProbe);
        }
    }

    private CueNodeViewModel? _preRollWatchedCue;

    /// <summary>Tracks the selected media cue so that in-place edits to its transport offsets and to
    /// its audio routes / video placements re-warm standby pre-roll. The add/remove route commands
    /// already call <see cref="SuggestPreRollRefresh"/>; this covers the property edits that don't.</summary>
    private void WatchSelectedCueForPreRoll(CueNodeViewModel? value)
    {
        var next = value is { Kind: CueNodeKind.Media } ? value : null;
        if (ReferenceEquals(_preRollWatchedCue, next))
            return;

        if (_preRollWatchedCue is not null)
        {
            _preRollWatchedCue.PropertyChanged -= OnWatchedCuePreRollPropertyChanged;
            _preRollWatchedCue.AudioRoutes.CollectionChanged -= OnWatchedCueRouteCollectionChanged;
            _preRollWatchedCue.VideoPlacements.CollectionChanged -= OnWatchedCuePlacementCollectionChanged;
            foreach (var route in _preRollWatchedCue.AudioRoutes)
                route.PropertyChanged -= OnWatchedRouteOrPlacementPropertyChanged;
            foreach (var placement in _preRollWatchedCue.VideoPlacements)
                placement.PropertyChanged -= OnWatchedRouteOrPlacementPropertyChanged;
        }

        _preRollWatchedCue = next;

        if (_preRollWatchedCue is not null)
        {
            _preRollWatchedCue.PropertyChanged += OnWatchedCuePreRollPropertyChanged;
            _preRollWatchedCue.AudioRoutes.CollectionChanged += OnWatchedCueRouteCollectionChanged;
            _preRollWatchedCue.VideoPlacements.CollectionChanged += OnWatchedCuePlacementCollectionChanged;
            foreach (var route in _preRollWatchedCue.AudioRoutes)
                route.PropertyChanged += OnWatchedRouteOrPlacementPropertyChanged;
            foreach (var placement in _preRollWatchedCue.VideoPlacements)
                placement.PropertyChanged += OnWatchedRouteOrPlacementPropertyChanged;
        }
    }

    private void OnWatchedCuePreRollPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CueNodeViewModel.StartOffsetMs)
            or nameof(CueNodeViewModel.EndOffsetMs)
            or nameof(CueNodeViewModel.Loop)
            or nameof(CueNodeViewModel.EndBehavior)
            or nameof(CueNodeViewModel.DisablePreRoll)
            or nameof(CueNodeViewModel.DurationMs)        // image/text duration drives the hold window
            or nameof(CueNodeViewModel.MediaSourceItem)   // text restyle replaces the source -> re-render
            or nameof(CueNodeViewModel.AudioTrackIndex))  // track change is part of the prepared-cue key
            OnWatchedCueEdited();
    }

    private void OnWatchedCueRouteCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebindItemSubscriptions(e);
        PushActiveAudioRoutesUpdate();
        // Add/Remove route commands already suggest a refresh, but a programmatic edit might not.
        OnWatchedCueEdited();
    }

    private void OnWatchedCuePlacementCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebindItemSubscriptions(e);
        OnWatchedCueEdited();
    }

    private void RebindItemSubscriptions(NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var item in e.OldItems.OfType<ObservableObject>())
                item.PropertyChanged -= OnWatchedRouteOrPlacementPropertyChanged;
        if (e.NewItems is not null)
            foreach (var item in e.NewItems.OfType<ObservableObject>())
                item.PropertyChanged += OnWatchedRouteOrPlacementPropertyChanged;
    }

    private void OnWatchedRouteOrPlacementPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // LineRef is a resolved UI reference, not part of the cue's cache key — ignore it so a mere
        // output-line resolution doesn't churn pre-roll.
        if (e.PropertyName is nameof(CueAudioRouteViewModel.SourceChannel)
            or nameof(CueAudioRouteViewModel.OutputLineId)
            or nameof(CueAudioRouteViewModel.OutputChannel)
            or nameof(CueAudioRouteViewModel.GainDb)
            or nameof(CueAudioRouteViewModel.Muted))
        {
            PushActiveAudioRoutesUpdate();
            OnWatchedCueEdited();
            return;
        }

        if (sender is CueVideoPlacementViewModel placement
            && IsVideoPlacementProperty(e.PropertyName))
        {
            if (IsLiveEditableVideoPlacementProperty(e.PropertyName))
                PushActiveVideoPlacementUpdate(placement);
            RefreshVideoFrameRateMismatchWarning();
        }
    }

    private static bool IsVideoPlacementProperty(string? propertyName) =>
        propertyName is nameof(CueVideoPlacementViewModel.CompositionId)
            or nameof(CueVideoPlacementViewModel.LayerIndex)
            or nameof(CueVideoPlacementViewModel.Position)
            or nameof(CueVideoPlacementViewModel.Opacity)
            or nameof(CueVideoPlacementViewModel.DestX)
            or nameof(CueVideoPlacementViewModel.DestY)
            or nameof(CueVideoPlacementViewModel.DestWidth)
            or nameof(CueVideoPlacementViewModel.DestHeight)
            or nameof(CueVideoPlacementViewModel.CropLeft)
            or nameof(CueVideoPlacementViewModel.CropTop)
            or nameof(CueVideoPlacementViewModel.CropRight)
            or nameof(CueVideoPlacementViewModel.CropBottom);

    private static bool IsLiveEditableVideoPlacementProperty(string? propertyName) =>
        propertyName is nameof(CueVideoPlacementViewModel.LayerIndex)
            or nameof(CueVideoPlacementViewModel.Position)
            or nameof(CueVideoPlacementViewModel.Opacity)
            or nameof(CueVideoPlacementViewModel.DestX)
            or nameof(CueVideoPlacementViewModel.DestY)
            or nameof(CueVideoPlacementViewModel.DestWidth)
            or nameof(CueVideoPlacementViewModel.DestHeight)
            or nameof(CueVideoPlacementViewModel.CropLeft)
            or nameof(CueVideoPlacementViewModel.CropTop)
            or nameof(CueVideoPlacementViewModel.CropRight)
            or nameof(CueVideoPlacementViewModel.CropBottom);

    private void PushActiveVideoPlacementUpdate(CueVideoPlacementViewModel placement)
    {
        if (_preRollWatchedCue is not { } cue
            || UpdateActiveCueVideoPlacementCallback is not { } callback
            || !_activeCueIds.Contains(cue.Id))
            return;

        var index = cue.VideoPlacements.IndexOf(placement);
        if (index < 0)
            return;

        _ = callback(cue.Id, index, placement.ToModel());
    }

    private void PushActiveAudioRoutesUpdate()
    {
        if (_preRollWatchedCue is not { } cue
            || UpdateActiveCueAudioRoutesCallback is not { } callback
            || !_activeCueIds.Contains(cue.Id))
            return;

        var routes = cue.AudioRoutes.Select(route => route.ToModel()).ToArray();
        _ = callback(cue.Id, routes);
    }

    /// <summary>An edit-relevant change to the watched (selected) cue: immediately flag its warm
    /// standby <see cref="PreparedCueState.Stale"/> so the badge reflects the drift, then request a
    /// debounced pre-roll refresh that re-prepares it.</summary>
    private void OnWatchedCueEdited()
    {
        if (_preRollWatchedCue is { } cue)
            CueStandbyInvalidated?.Invoke(this, cue.Id);
        SuggestPreRollRefresh();
    }

    /// <summary>Raised with a cue id when an in-place edit drifts that cue's warm standby out of date.
    /// The host marks the engine's prepared entry stale; the following refresh re-prepares it.</summary>
    public event EventHandler<Guid>? CueStandbyInvalidated;

    private void RefreshVideoFrameRateMismatchWarning()
    {
        OnPropertyChanged(nameof(VideoFrameRateMismatchWarning));
        OnPropertyChanged(nameof(HasVideoFrameRateMismatchWarning));
    }

    private string? BuildVideoFrameRateMismatchWarning()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Media } node || !node.SourceHasVideo)
            return null;
        if (!CueFrameRatePolicy.IsKnown(node.SourceFrameRateNum, node.SourceFrameRateDen))
            return null;
        if (SelectedCueList is null)
            return null;

        foreach (var placement in node.VideoPlacements)
        {
            var comp = SelectedCueList.Compositions.FirstOrDefault(c => c.Id == placement.CompositionId);
            if (comp is null)
                continue;
            if (!CueFrameRatePolicy.RatesMismatch(
                    node.SourceFrameRateNum, node.SourceFrameRateDen,
                    comp.FrameRateNum, comp.FrameRateDen))
                continue;

            var srcFps = FormatProbeFps(node.SourceFrameRateNum, node.SourceFrameRateDen);
            var canvasFps = FormatProbeFps(comp.FrameRateNum, comp.FrameRateDen);
            return Strings.Format(
                nameof(Strings.VideoFrameRateMismatchWarningFormat),
                srcFps,
                canvasFps,
                comp.DisplayName);
        }

        return null;
    }

    private static string FormatProbeFps(int num, int den)
    {
        if (den <= 0)
            return "?";
        var fps = num / (double)den;
        return fps >= 100 ? fps.ToString("0.#") : fps.ToString("0.###");
    }

    partial void OnSelectedCueNodeChanged(CueNodeViewModel? value)
    {
        // The selected cue's probe fields can land AFTER selection (when the operator picks a
        // file via "Browse media…"; the probe is async). Re-subscribe so the Video tab visibility
        // re-evaluates when the probe finishes.
        if (_watchedSelectedCueForProbe is not null)
            _watchedSelectedCueForProbe.PropertyChanged -= OnSelectedCueProbeChanged;
        _watchedSelectedCueForProbe = value;
        if (_watchedSelectedCueForProbe is not null)
            _watchedSelectedCueForProbe.PropertyChanged += OnSelectedCueProbeChanged;

        // In-place edits to the selected cue's routes/placements/offsets don't go through the
        // add/remove commands (those already suggest a refresh), so watch the node directly to keep
        // its standby pre-roll warm after gain/channel/opacity/offset tweaks. Debounced downstream.
        WatchSelectedCueForPreRoll(value);

        // Cues loaded from disk have no probed track list yet — fill the audio-track picker lazily
        // on first selection (stream-table probe only, no decoder build).
        if (value is { Kind: CueNodeKind.Media })
            _ = EnsureAudioTrackChoicesAsync(value);

        SelectedAudioRoute = value is { Kind: CueNodeKind.Media } media
            ? media.AudioRoutes.FirstOrDefault()
            : null;
        SelectedVideoPlacement = value is { Kind: CueNodeKind.Media } media2
            ? media2.VideoPlacements.FirstOrDefault()
            : null;
        RemoveNodeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(VisibleAudioRoutes));
        OnPropertyChanged(nameof(VisibleVideoPlacements));
        OnPropertyChanged(nameof(HasSelectedMediaCue));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithVideo));
        OnPropertyChanged(nameof(HasSelectedTextCue));
        OnPropertyChanged(nameof(HasSelectedStaticCue));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAttachedPictureOnly));
        OnPropertyChanged(nameof(HasSelectedActionCue));
        OnPropertyChanged(nameof(HasSelectedCommentCue));
        OnPropertyChanged(nameof(HasSelectedGroupCue));
        OnPropertyChanged(nameof(HasSelectedCue));
        OnPropertyChanged(nameof(SelectedCueDrawerTitle));
        OnPropertyChanged(nameof(SelectedActionEndpointSummary));
        AddAudioRouteCommand.NotifyCanExecuteChanged();
        RemoveAudioRouteCommand.NotifyCanExecuteChanged();
        ApplyCueDownmixPresetCommand.NotifyCanExecuteChanged();
        AddVideoPlacementCommand.NotifyCanExecuteChanged();
        RemoveVideoPlacementCommand.NotifyCanExecuteChanged();
        StandbySelectedCommand.NotifyCanExecuteChanged();
        BrowseMediaSourceCommand.NotifyCanExecuteChanged();
        AssignSelectedActionEndpointCommand.NotifyCanExecuteChanged();
        EditActionCueCommand.NotifyCanExecuteChanged();
        TogglePreviewCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsPreviewingSelectedCue));
        OnPropertyChanged(nameof(PreviewButtonLabel));
        OnPropertyChanged(nameof(IsCueScrubberVisible));
        SyncCueScrubberFromActiveSelection();
        SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
        RefreshVideoFrameRateMismatchWarning();
        ExtractCueWaveform(value);

        if (value?.Kind == CueNodeKind.Action && Guid.TryParse(value.EndpointIdText, out var endpointId))
            SelectedActionEndpoint = ActionEndpoints.FirstOrDefault(e => e.Id == endpointId);
        else
            SelectedActionEndpoint = null;
    }

    partial void OnSelectedAudioRouteChanged(CueAudioRouteViewModel? value)
    {
        _ = value;
        RemoveAudioRouteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedVideoPlacementChanged(CueVideoPlacementViewModel? value)
    {
        _ = value;
        RemoveVideoPlacementCommand.NotifyCanExecuteChanged();
        ApplyPlacementLayoutCommand.NotifyCanExecuteChanged();
        ApplyCropPresetCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PlacementCanvasAspect));
        RefreshVideoFrameRateMismatchWarning();
    }

    partial void OnSelectedActionEndpointChanged(ActionEndpoint? value)
    {
        _ = value;
        OnPropertyChanged(nameof(SelectedActionEndpointSummary));
        AssignSelectedActionEndpointCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsCueEditModeChanged(bool value)
    {
        _ = value;
        MoveSelectedCueUpCommand.NotifyCanExecuteChanged();
        MoveSelectedCueDownCommand.NotifyCanExecuteChanged();
    }

    partial void OnStandbyCueNodeChanged(CueNodeViewModel? value)
    {
        _ = value;
        RefreshRowStatuses();
        RebuildUpcomingCues();
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        if (!_suppressStandbyPreRollRefresh)
            SuggestPreRollRefresh();
    }

    /// <summary>Host subscribes to warm the selected player's pre-roll cache (§5.7).</summary>
    public event EventHandler? PreRollRefreshSuggested;

    private void SuggestPreRollRefresh() => PreRollRefreshSuggested?.Invoke(this, EventArgs.Empty);

    private bool _suppressStandbyPreRollRefresh;

    /// <summary>The fireable cue order starting at standby (or list start) — the window each
    /// pre-roll query pulls its next-N targets from. Callers apply a per-source-type filter and
    /// cap the count of *matched* targets themselves, so a non-matching cue (e.g. an NDI cue while
    /// scanning for files) is skipped without consuming the budget.</summary>
    private IEnumerable<CueNodeViewModel> EnumeratePreRollWindow()
    {
        if (SelectedCueList is null)
            yield break;

        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0)
            yield break;

        var startIdx = 0;
        if (StandbyCueNode is not null)
        {
            var resolved = ResolveFireableCue(StandbyCueNode) ?? StandbyCueNode;
            var idx = ordered.FindIndex(c => ReferenceEquals(c, resolved));
            if (idx >= 0)
                startIdx = idx;
        }

        for (var i = startIdx; i < ordered.Count; i++)
            yield return ordered[i];
    }

    /// <summary>Next file media cues from standby for the cue engine's own opened/routed cache.</summary>
    public IReadOnlyList<MediaCueNode> GetPreparedMediaCueTargets(int maxCount)
    {
        var effectiveMax = ResolvePreRollTargetLimit(maxCount);

        var targets = new List<MediaCueNode>();
        foreach (var cue in EnumeratePreRollWindow())
        {
            if (targets.Count >= effectiveMax)
                break;
            if (cue.Kind != CueNodeKind.Media
                || cue.DisablePreRoll // per-cue resource opt-out
                || cue.MediaSourceItem is not FilePlaylistItem
                || cue.ToModel() is not MediaCueNode media)
                continue;
            targets.Add(media);
        }

        return targets;
    }

    /// <summary>NDI media cues in the pre-roll window (§6.11).</summary>
    public IReadOnlyList<(Guid CueId, NDIInputPlaylistItem Item)> GetNdiPreConnectTargets(int maxCount)
    {
        var effectiveMax = ResolvePreRollTargetLimit(maxCount);

        var targets = new List<(Guid, NDIInputPlaylistItem)>();
        foreach (var cue in EnumeratePreRollWindow())
        {
            if (targets.Count >= effectiveMax)
                break;
            if (cue.Kind != CueNodeKind.Media
                || cue.DisablePreRoll // per-cue resource opt-out
                || cue.MediaSourceItem is not NDIInputPlaylistItem ndi
                || !ndi.SupportsPreRoll())
                continue;
            targets.Add((cue.Id, ndi));
        }

        return targets;
    }

    private static int ResolvePreRollTargetLimit(int maxCount) =>
        maxCount <= 0 ? int.MaxValue : maxCount;

    partial void OnCurrentCueNodeChanged(CueNodeViewModel? value)
    {
        _ = value;
        RefreshRowStatuses();
        PauseCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Set of cue ids the playback engine reports as currently active. Maintained via
    /// <see cref="OnCueStarted"/> / <see cref="OnCueEnded"/> from the host (MainViewModel wires
    /// these to the engine's events). Used by <see cref="RefreshRowStatuses"/> so every active
    /// cue lights up — the singular <see cref="CurrentCueNode"/> only tracks the last-started
    /// one for AutoFollow / transport-state purposes.</summary>
    private readonly HashSet<Guid> _activeCueIds = new();

    /// <summary>Rows visible in the right-side Now Playing panel. Maintained by
    /// <see cref="OnCueStarted"/> / <see cref="OnCueEnded"/>; their progress fields update via
    /// <see cref="OnCueProgress"/>.</summary>
    public ObservableCollection<ActiveCueViewModel> ActiveCues { get; } = new();

    /// <summary>Cues that *will* fire once the operator presses Go from the current Standby
    /// position — used by the Now Playing panel's Upcoming section.</summary>
    public ObservableCollection<CueNodeViewModel> UpcomingCues { get; } = new();

    /// <summary>Host-provided per-cue stop callback (engine.StopCueAsync). The Now Playing
    /// panel's per-row ✕ button forwards through this; null in tests.</summary>
    public Func<Guid, Task>? CancelCueCallback { get; set; }

    /// <summary>Host callback for mutating a placement's already-running compositor slot while the
    /// selected cue is active. No-op in tests or when the cue is not playing.</summary>
    public Func<Guid, int, CueVideoPlacement, Task>? UpdateActiveCueVideoPlacementCallback { get; set; }

    /// <summary>Host callback for reconciling the selected cue's running audio routes after route
    /// row edits. No-op in tests or when the cue is not playing.</summary>
    public Func<Guid, IReadOnlyList<CueAudioRoute>, Task>? UpdateActiveCueAudioRoutesCallback { get; set; }

    /// <summary>Engine callback — cue began playing. Marks its row Current and pushes a new
    /// <see cref="ActiveCueViewModel"/> into <see cref="ActiveCues"/>.</summary>
    public void OnCueStarted(Guid cueId)
    {
        _activeCueIds.Add(cueId);
        RefreshRowStatuses();

        var node = FindNodeById(cueId);
        if (node is not null && !ActiveCues.Any(a => a.CueId == cueId))
        {
            var entry = new ActiveCueViewModel(node, cueId, id => _ = (CancelCueCallback?.Invoke(id) ?? Task.CompletedTask))
            {
                DurationMs = Math.Max(0, node.EffectiveDurationMs),
            };
            ActiveCues.Add(entry);
        }
        RebuildUpcomingCues();
        OnPropertyChanged(nameof(IsCueScrubberVisible));
        SyncCueScrubberFromActiveSelection();
        SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Engine callback — preview stopped. Clears preview state on the VM.</summary>
    public void OnPreviewEnded(Guid cueId)
    {
        _ = cueId;
        if (PreviewingCueId is null) return;
        PreviewingCueId = null;
        StatusMessage = Strings.PreviewStoppedStatus;
    }

    /// <summary>Engine callback — cue stopped (natural end, Stop, or Panic). Clears Current
    /// status and removes the matching <see cref="ActiveCueViewModel"/>.</summary>
    public void OnCueEnded(Guid cueId)
    {
        _activeCueIds.Remove(cueId);
        RefreshRowStatuses();

        for (var i = ActiveCues.Count - 1; i >= 0; i--)
            if (ActiveCues[i].CueId == cueId)
                ActiveCues.RemoveAt(i);
        RebuildUpcomingCues();
        OnPropertyChanged(nameof(IsCueScrubberVisible));
        SyncCueScrubberFromActiveSelection();
        SeekActiveCueFromScrubberCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Engine callback — progress sample for one active cue. Updates the row's
    /// position so the progress bar and "mm:ss / mm:ss" display advance.</summary>
    public void OnCueProgress(CuePlaybackProgress p)
    {
        foreach (var a in ActiveCues)
        {
            if (a.CueId != p.CueId) continue;
            a.PositionMs = (long)p.Position.TotalMilliseconds;
            if (p.Duration > TimeSpan.Zero)
                a.DurationMs = (long)p.Duration.TotalMilliseconds;
            break;
        }

        if (SelectedCueNode?.Id == p.CueId && p.Duration > TimeSpan.Zero)
            CueScrubberValue = p.Position.TotalMilliseconds * 1000.0 / p.Duration.TotalMilliseconds;
    }

    private void SyncCueScrubberFromActiveSelection()
    {
        if (SelectedCueNode is null)
            return;
        var active = ActiveCues.FirstOrDefault(a => a.CueId == SelectedCueNode.Id);
        var durationMs = active?.DurationMs ?? SelectedCueNode.EffectiveDurationMs;
        if (durationMs <= 0)
            return;
        var positionMs = active?.PositionMs ?? 0;
        CueScrubberValue = positionMs * 1000.0 / durationMs;
    }

    [RelayCommand(CanExecute = nameof(CanTogglePreview))]
    private async Task TogglePreviewAsync()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Media } node)
            return;

        if (IsPreviewingSelectedCue)
        {
            if (StopPreviewCallback is not null)
                await StopPreviewCallback();
            PreviewingCueId = null;
            StatusMessage = Strings.PreviewStoppedStatus;
            return;
        }

        if (PreviewCueCallback is null)
        {
            StatusMessage = Strings.CueMediaExecutionNotConfigured;
            return;
        }

        if (node.ToModel() is not MediaCueNode media)
        {
            StatusMessage = Strings.CueInvalidMediaCue;
            return;
        }

        using var cts = new CancellationTokenSource();
        var err = await PreviewCueCallback(media, cts.Token);
        if (!string.IsNullOrWhiteSpace(err))
        {
            StatusMessage = err;
            return;
        }

        PreviewingCueId = node.Id;
        StatusMessage = Strings.Format(nameof(Strings.PreviewingCueStatusFormat), CueDisplay(node));
    }

    private bool CanTogglePreview() =>
        SelectedCueNode is { Kind: CueNodeKind.Media };

    [RelayCommand(CanExecute = nameof(CanSeekActiveCueFromScrubber))]
    private async Task SeekActiveCueFromScrubberAsync()
    {
        if (SelectedCueNode is null || SeekCueCallback is null)
            return;

        var active = ActiveCues.FirstOrDefault(a => a.CueId == SelectedCueNode.Id);
        var durationMs = active?.DurationMs ?? SelectedCueNode.EffectiveDurationMs;
        if (durationMs <= 0)
            return;

        var position = TimeSpan.FromMilliseconds(CueScrubberValue * durationMs / 1000.0);
        await SeekCueCallback(SelectedCueNode.Id, position);
    }

    private bool CanSeekActiveCueFromScrubber() => IsCueScrubberVisible;

    private CueNodeViewModel? FindNodeById(Guid id)
    {
        foreach (var node in EnumerateAllCueNodes())
            if (node.Id == id)
                return node;
        return null;
    }

    /// <summary>Host callback — pre-roll cache membership changed. Snapshot lists the cue ids
    /// that are currently warmed. Walks every loaded cue node and sets <c>IsPreRollWarm</c>
    /// accordingly so the status badge column can render the warming indicator (Phase 5.7.2).
    /// <para>This method does not marshal threads on its own; the host wiring (MainViewModel)
    /// hops onto the UI dispatcher before invoking, because the underlying
    /// <see cref="CuePreRollCache.EntriesChanged"/> can fire from any thread.</para>
    /// </summary>
    public void OnPreRollCacheChanged(IReadOnlyCollection<Guid> warmCueIds)
    {
        var warm = warmCueIds as HashSet<Guid> ?? new HashSet<Guid>(warmCueIds);
        foreach (var node in EnumerateAllCueNodes())
        {
            var shouldBeWarm = warm.Contains(node.Id);
            if (node.IsPreRollWarm != shouldBeWarm)
                node.IsPreRollWarm = shouldBeWarm;
        }
    }

    /// <summary>Host callback — richer per-cue standby preparation states changed (Idle/Preparing/
    /// Ready/Failed). Cues absent from the snapshot are Idle. Drives the status badge + tooltip and,
    /// via <see cref="CueNodeViewModel.PreRollState"/>, keeps <c>IsPreRollWarm</c> in sync.</summary>
    public void OnPreparedCueStatesChanged(IReadOnlyList<Playback.CuePreparationStatus> states)
    {
        var byId = states.ToDictionary(s => s.CueId);
        foreach (var node in EnumerateAllCueNodes())
        {
            if (byId.TryGetValue(node.Id, out var status))
            {
                node.PreRollState = status.State;
                node.PreRollError = status.Error;
            }
            else
            {
                node.PreRollState = PreparedCueState.Idle;
                node.PreRollError = null;
            }
        }
    }

    private void RebuildUpcomingCues()
    {
        UpcomingCues.Clear();
        if (SelectedCueList is null) return;
        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0) return;

        var anchor = StandbyCueNode ?? ordered.FirstOrDefault();
        if (anchor is null) return;
        var startIdx = ordered.FindIndex(c => ReferenceEquals(c, ResolveFireableCue(anchor) ?? anchor));
        if (startIdx < 0) return;

        // Show up to 8 cues ahead — enough context for a chain without crowding the panel.
        for (var i = startIdx; i < ordered.Count && UpcomingCues.Count < 8; i++)
        {
            var c = ordered[i];
            // Don't list already-active cues as upcoming — they're in the Active section.
            if (_activeCueIds.Contains(c.Id)) continue;
            UpcomingCues.Add(c);
        }
    }

    private void RefreshRowStatuses()
    {
        foreach (var node in EnumerateAllCueNodes())
        {
            var status = _activeCueIds.Contains(node.Id)
                ? CueRowStatus.Current
                : ReferenceEquals(node, StandbyCueNode)
                    ? CueRowStatus.Standby
                    : CueRowStatus.Idle;
            if (node.RowStatus != status)
                node.RowStatus = status;
        }
    }

    partial void OnIsTransportPausedChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(TransportState));
    }

    partial void OnStatusMessageChanged(string? value)
    {
        _statusMessageClearCts?.Cancel();
        _statusMessageClearCts?.Dispose();
        _statusMessageClearCts = null;

        if (string.IsNullOrWhiteSpace(value))
            return;

        var cts = new CancellationTokenSource();
        _statusMessageClearCts = cts;
        _ = ClearStatusMessageLaterAsync(value, cts.Token);
    }

    private async Task ClearStatusMessageLaterAsync(string message, CancellationToken token)
    {
        try
        {
            await Task.Delay(StatusMessageAutoClearDelay, token).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested && string.Equals(StatusMessage, message, StringComparison.Ordinal))
                    StatusMessage = null;
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private ICollection<CueNodeViewModel>? SelectedParentCollection()
    {
        if (SelectedCueList is null)
            return null;
        if (SelectedCueNode is null)
            return SelectedCueList.Nodes;
        if (SelectedCueNode.IsGroup)
            return SelectedCueNode.Children;
        return FindParentCollection(SelectedCueList.Nodes, SelectedCueNode) ?? SelectedCueList.Nodes;
    }

    private static ICollection<CueNodeViewModel>? FindParentCollection(
        ICollection<CueNodeViewModel> nodes,
        CueNodeViewModel target)
    {
        if (nodes.Contains(target))
            return nodes;
        foreach (var n in nodes)
        {
            var c = FindParentCollection(n.Children, target);
            if (c is not null) return c;
        }
        return null;
    }

    private static bool RemoveNodeRecursive(ICollection<CueNodeViewModel> nodes, CueNodeViewModel target)
    {
        if (nodes.Remove(target))
            return true;
        foreach (var n in nodes)
            if (RemoveNodeRecursive(n.Children, target))
                return true;
        return false;
    }

    private bool IsInCurrentCueTree(CueNodeViewModel node) =>
        SelectedCueList is not null && ContainsNode(SelectedCueList.Nodes, node);

    private void PruneSelectionToCurrentTree()
    {
        var removed = _selectedCueNodes.RemoveAll(n => !IsInCurrentCueTree(n));
        if (removed == 0 && (SelectedCueNode is null || IsInCurrentCueTree(SelectedCueNode)))
            return;

        SelectedCueNode = _selectedCueNodes.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCueCount));
        OnPropertyChanged(nameof(IsMultiSelected));
    }

    private void ReconcileTransportAfterTreeMutation(int removedFireableIndex)
    {
        var ordered = EnumerateFireableCueOrder().ToList();

        if (CurrentCueNode is not null && !IsInCurrentCueTree(CurrentCueNode))
        {
            CurrentCueNode = null;
            IsTransportPaused = false;
        }

        if (StandbyCueNode is not null && !IsInCurrentCueTree(StandbyCueNode))
        {
            StandbyCueNode = ordered.Count == 0
                ? null
                : ordered[Math.Clamp(removedFireableIndex < 0 ? 0 : removedFireableIndex, 0, ordered.Count - 1)];
        }
        else
        {
            RefreshRowStatuses();
            RebuildUpcomingCues();
        }

        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AddCueList()
    {
        var list = new CueListEditorViewModel(Strings.Format(nameof(Strings.CueListNameFormat), CueLists.Count + 1));
        CueLists.Add(list);
        SelectedCueList = list;
        StatusMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveCueList))]
    private void RemoveCueList()
    {
        if (SelectedCueList is null || CueLists.Count <= 1)
            return;
        var idx = CueLists.IndexOf(SelectedCueList);
        CueLists.RemoveAt(idx);
        SelectedCueList = CueLists[Math.Clamp(idx - 1, 0, CueLists.Count - 1)];
        SelectedCueNode = null;
    }

    private bool CanRemoveCueList() => SelectedCueList is not null && CueLists.Count > 1;

    [RelayCommand]
    private void AddComposition()
    {
        if (SelectedCueList is null) return;
        var comp = new CueCompositionViewModel
        {
            Name = Strings.Format(nameof(Strings.CueOutputDefaultVideoNameFormat),
                SelectedCueList.Compositions.Count + 1),
        };
        SelectedCueList.Compositions.Add(comp);
        comp.CompositionFrameRateChanged += OnCompositionFrameRateChanged;
        SelectedComposition = comp;
        RefreshVideoFrameRateMismatchWarning();
        SuggestPreRollRefresh();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveComposition))]
    private void RemoveComposition()
    {
        if (SelectedCueList is null || SelectedComposition is null) return;
        var removedId = SelectedComposition.Id;
        if (!SelectedCueList.Compositions.Remove(SelectedComposition)) return;
        foreach (var media in EnumerateMediaNodes(SelectedCueList.Nodes))
            for (var i = media.VideoPlacements.Count - 1; i >= 0; i--)
                if (media.VideoPlacements[i].CompositionId == removedId)
                    media.VideoPlacements.RemoveAt(i);
        for (var i = SelectedCueList.VideoOutputs.Count - 1; i >= 0; i--)
            if (SelectedCueList.VideoOutputs[i].CompositionId == removedId)
                SelectedCueList.VideoOutputs.RemoveAt(i);
        SelectedComposition = SelectedCueList.Compositions.FirstOrDefault();
        SuggestPreRollRefresh();
    }

    private bool CanRemoveComposition() => SelectedComposition is not null;

    [RelayCommand]
    private void AddVideoOutput()
    {
        if (SelectedCueList is null) return;
        var binding = new CueVideoOutputBindingViewModel
        {
            OutputLineId = AvailableVideoOutputs.FirstOrDefault()?.Definition.Id ?? Guid.Empty,
            CompositionId = SelectedCueList.Compositions.FirstOrDefault()?.Id ?? Guid.Empty,
        };
        binding.SetLineResolver(ResolveOutputLine);
        SelectedCueList.VideoOutputs.Add(binding);
        SelectedVideoOutput = binding;
        SuggestPreRollRefresh();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveVideoOutput))]
    private void RemoveVideoOutput()
    {
        if (SelectedCueList is null || SelectedVideoOutput is null) return;
        if (!SelectedCueList.VideoOutputs.Remove(SelectedVideoOutput)) return;
        SelectedVideoOutput = SelectedCueList.VideoOutputs.FirstOrDefault();
        SuggestPreRollRefresh();
    }

    private bool CanRemoveVideoOutput() => SelectedVideoOutput is not null;

    partial void OnSelectedCompositionChanged(CueCompositionViewModel? value)
    {
        _ = value;
        RemoveCompositionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PlacementCanvasAspect));
    }

    partial void OnSelectedVideoOutputChanged(CueVideoOutputBindingViewModel? value)
    {
        _ = value;
        RemoveVideoOutputCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddAudioRoute))]
    private void AddAudioRoute()
    {
        var targets = MediaCuesInSelection();
        if (targets.Count == 0) return;
        var firstOutput = AvailableAudioOutputs.FirstOrDefault();
        var channelCount = GetAudioOutputChannelCount(firstOutput);

        CueAudioRouteViewModel? lastOnPrimary = null;
        foreach (var media in targets)
        {
            var route = new CueAudioRouteViewModel
            {
                SourceChannel = media.AudioRoutes.Count,
                OutputLineId = firstOutput?.Definition.Id ?? Guid.Empty,
                OutputChannel = 1 + (media.AudioRoutes.Count % Math.Max(1, channelCount)),
            };
            route.SetLineResolver(ResolveOutputLine);
            media.AudioRoutes.Add(route);
            if (ReferenceEquals(media, SelectedCueNode))
                lastOnPrimary = route;
        }
        if (lastOnPrimary is not null)
            SelectedAudioRoute = lastOnPrimary;
        OnPropertyChanged(nameof(VisibleAudioRoutes));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
        SuggestPreRollRefresh();
    }

    private static int GetAudioOutputChannelCount(OutputLineViewModel? line) =>
        line?.Definition switch
        {
            Models.PortAudioOutputDefinition pa => Math.Max(1, pa.ChannelCount),
            Models.NDIOutputDefinition nd when nd.StreamMode != NDIOutputStreamMode.VideoOnly =>
                Math.Max(1, nd.AudioChannelCount),
            _ => 2,
        };

    private bool CanAddAudioRoute() => SelectedCueNode is { Kind: CueNodeKind.Media };

    [RelayCommand(CanExecute = nameof(CanRemoveAudioRoute))]
    private void RemoveAudioRoute()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Media } media || SelectedAudioRoute is null) return;
        if (media.AudioRoutes.Remove(SelectedAudioRoute))
        {
            SelectedAudioRoute = media.AudioRoutes.FirstOrDefault();
            OnPropertyChanged(nameof(VisibleAudioRoutes));
            OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
            SuggestPreRollRefresh();
        }
    }

    private bool CanRemoveAudioRoute() =>
        SelectedCueNode is { Kind: CueNodeKind.Media } && SelectedAudioRoute is not null;

    /// <summary>Quick-apply a multichannel downmix preset to the selected cue's audio routes for one
    /// output line (the selected route's line, else the first available audio output). Replaces that
    /// line's routes; other lines are untouched. Shares <see cref="AudioDownmixPresets"/> with the
    /// media player's audio matrix.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyCueDownmix))]
    private void ApplyCueDownmixPreset(AudioDownmixPreset preset)
    {
        var targets = MediaCuesInSelection();
        if (targets.Count == 0)
            return;

        var line = SelectedAudioRoute?.OutputLineId is { } selId && selId != Guid.Empty
            ? AvailableAudioOutputs.FirstOrDefault(l => l.Definition.Id == selId) ?? AvailableAudioOutputs.FirstOrDefault()
            : AvailableAudioOutputs.FirstOrDefault();
        if (line is null)
        {
            StatusMessage = Strings.DownmixNoOutputStatus;
            return;
        }

        var outChannels = GetAudioOutputChannelCount(line);
        var lineId = line.Definition.Id;
        var applied = 0;
        string? firstNotApplicable = null;

        foreach (var media in targets)
        {
            var srcChannels = Math.Max(1, media.SourceAudioChannels);
            if (!AudioDownmixPresets.IsApplicable(preset, srcChannels, outChannels))
            {
                firstNotApplicable ??= Strings.Format(nameof(Strings.DownmixNotApplicableStatusFormat),
                    AudioDownmixPresets.DisplayName(preset), srcChannels, outChannels);
                continue;
            }

            for (var i = media.AudioRoutes.Count - 1; i >= 0; i--)
                if (media.AudioRoutes[i].OutputLineId == lineId)
                    media.AudioRoutes.RemoveAt(i);

            CueAudioRouteViewModel? first = null;
            foreach (var contrib in AudioDownmixPresets.Contributions(preset, srcChannels, outChannels))
            {
                var route = new CueAudioRouteViewModel
                {
                    SourceChannel = contrib.InputChannel,
                    OutputLineId = lineId,
                    OutputChannel = contrib.OutputChannel + 1, // cue routes are 1-based
                    GainDb = contrib.GainDb,
                };
                route.SetLineResolver(ResolveOutputLine);
                media.AudioRoutes.Add(route);
                first ??= route;
            }

            if (ReferenceEquals(media, SelectedCueNode) && first is not null)
                SelectedAudioRoute = first;
            applied++;
        }

        if (applied == 0)
        {
            StatusMessage = firstNotApplicable;
            return;
        }

        OnPropertyChanged(nameof(VisibleAudioRoutes));
        OnPropertyChanged(nameof(HasSelectedMediaCueWithAudio));
        StatusMessage = Strings.Format(nameof(Strings.DownmixAppliedStatusFormat),
            AudioDownmixPresets.DisplayName(preset),
            applied == 1 ? line.Definition.DisplayName : $"{applied} cues on {line.Definition.DisplayName}");
        SuggestPreRollRefresh();
    }

    private bool CanApplyCueDownmix() =>
        SelectedCueNode is { Kind: CueNodeKind.Media } && AvailableAudioOutputs.Count > 0;

    [RelayCommand(CanExecute = nameof(CanAddVideoPlacement))]
    private void AddVideoPlacement()
    {
        if (SelectedCueList is null) return;
        var targets = MediaCuesInSelection();
        if (targets.Count == 0) return;
        var firstComp = SelectedCueList.Compositions.FirstOrDefault();

        CueVideoPlacementViewModel? lastOnPrimary = null;
        foreach (var media in targets)
        {
            // Default the box to the source's size (actual size, scaled down to fit the canvas),
            // centered — so a new layer lands at the video's aspect instead of stretched full-frame.
            var (fx, fy, fw, fh) = SourceFitRect(
                media.SourceVideoWidth, media.SourceVideoHeight, firstComp?.Width ?? 0, firstComp?.Height ?? 0);
            var placement = new CueVideoPlacementViewModel
            {
                CompositionId = firstComp?.Id ?? Guid.Empty,
                LayerIndex = media.VideoPlacements.Count,
            };
            placement.SetDestRect(fx, fy, fw, fh);
            media.VideoPlacements.Add(placement);
            if (ReferenceEquals(media, SelectedCueNode))
                lastOnPrimary = placement;
        }
        if (lastOnPrimary is not null)
            SelectedVideoPlacement = lastOnPrimary;
        OnPropertyChanged(nameof(VisibleVideoPlacements));
        RefreshVideoFrameRateMismatchWarning();
        SuggestPreRollRefresh();
    }

    private bool CanAddVideoPlacement() => SelectedCueNode is { Kind: CueNodeKind.Media };

    /// <summary>Media cues in the current multi-selection. Falls back to the singular
    /// <see cref="SelectedCueNode"/> when only one row is selected (the common case).</summary>
    private List<CueNodeViewModel> MediaCuesInSelection()
    {
        if (_selectedCueNodes.Count > 1)
            return _selectedCueNodes.Where(n => n.Kind == CueNodeKind.Media).ToList();
        return SelectedCueNode is { Kind: CueNodeKind.Media } single
            ? new List<CueNodeViewModel> { single }
            : new List<CueNodeViewModel>();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveVideoPlacement))]
    private void RemoveVideoPlacement()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Media } media || SelectedVideoPlacement is null) return;
        if (media.VideoPlacements.Remove(SelectedVideoPlacement))
        {
            SelectedVideoPlacement = media.VideoPlacements.FirstOrDefault();
            OnPropertyChanged(nameof(VisibleVideoPlacements));
            SuggestPreRollRefresh();
        }
    }

    private bool CanRemoveVideoPlacement() =>
        SelectedCueNode is { Kind: CueNodeKind.Media } && SelectedVideoPlacement is not null;

    /// <summary>Quick destination-rect layouts for the selected placement (full / halves / quadrants).</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelectedPlacement))]
    private void ApplyPlacementLayout(string? preset)
    {
        if (SelectedVideoPlacement is not { } p) return;
        switch (preset)
        {
            case "fit":
            {
                var comp = SelectedCueList?.Compositions.FirstOrDefault(c => c.Id == p.CompositionId)
                    ?? SelectedCueList?.Compositions.FirstOrDefault();
                var node = SelectedCueNode;
                var (fx, fy, fw, fh) = SourceFitRect(
                    node?.SourceVideoWidth ?? 0, node?.SourceVideoHeight ?? 0, comp?.Width ?? 0, comp?.Height ?? 0);
                p.SetDestRect(fx, fy, fw, fh);
                break;
            }
            case "full": p.SetDestRect(0, 0, 1, 1); break;
            case "left": p.SetDestRect(0, 0, 0.5, 1); break;
            case "right": p.SetDestRect(0.5, 0, 0.5, 1); break;
            case "top": p.SetDestRect(0, 0, 1, 0.5); break;
            case "bottom": p.SetDestRect(0, 0.5, 1, 0.5); break;
            case "tl": p.SetDestRect(0, 0, 0.5, 0.5); break;
            case "tr": p.SetDestRect(0.5, 0, 0.5, 0.5); break;
            case "bl": p.SetDestRect(0, 0.5, 0.5, 0.5); break;
            case "br": p.SetDestRect(0.5, 0.5, 0.5, 0.5); break;
            default: return;
        }
        SuggestPreRollRefresh();
    }

    /// <summary>Normalized destination rect that places a <paramref name="srcW"/>×<paramref name="srcH"/>
    /// source on a <paramref name="canvasW"/>×<paramref name="canvasH"/> canvas at its own size, centered —
    /// scaled down (aspect preserved) only when the source is larger than the canvas, never scaled up.
    /// Falls back to the full frame when any dimension is unknown.</summary>
    internal static (double X, double Y, double W, double H) SourceFitRect(int srcW, int srcH, int canvasW, int canvasH)
    {
        if (srcW <= 0 || srcH <= 0 || canvasW <= 0 || canvasH <= 0)
            return (0.0, 0.0, 1.0, 1.0);

        var scale = Math.Min(1.0, Math.Min((double)canvasW / srcW, (double)canvasH / srcH));
        var w = Math.Clamp(srcW * scale / canvasW, 0.02, 1.0);
        var h = Math.Clamp(srcH * scale / canvasH, 0.02, 1.0);
        var x = Math.Clamp((1.0 - w) / 2.0, 0.0, 1.0 - w);
        var y = Math.Clamp((1.0 - h) / 2.0, 0.0, 1.0 - h);
        return (x, y, w, h);
    }

    /// <summary>Quick source-crop presets for the selected placement.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelectedPlacement))]
    private void ApplyCropPreset(string? preset)
    {
        if (SelectedVideoPlacement is not { } p) return;
        switch (preset)
        {
            case "none": p.CropLeft = p.CropTop = p.CropRight = p.CropBottom = 0; break;
            case "centerH": p.CropTop = p.CropBottom = 0; p.CropLeft = p.CropRight = 0.25; break; // centre 50% wide
            case "centerV": p.CropLeft = p.CropRight = 0; p.CropTop = p.CropBottom = 0.25; break; // centre 50% tall
            case "center": p.CropLeft = p.CropTop = p.CropRight = p.CropBottom = 0.25; break;      // centre 50% box
            default: return;
        }
        SuggestPreRollRefresh();
    }

    private bool CanEditSelectedPlacement() => SelectedVideoPlacement is not null;

    [RelayCommand]
    private void AddGroup()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;
        var row = new CueNodeViewModel(CueNodeKind.Group)
        {
            Number = NextNumber(parent),
            Label = Strings.CueNodeDefaultGroupLabel,
            Extra = CueGroupFireMode.FirstCueOnly.ToString(),
        };
        parent.Add(row);
        FinalizeAddedCue(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
    }

    /// <summary>Phase 5.8.2 — central hook for "just-added" cues. Stamps the cue list's
    /// configured default trigger mode and (if the per-list flag is set) re-runs the renumber
    /// pass so numbering stays sequential.</summary>
    private void FinalizeAddedCue(CueNodeViewModel node)
    {
        if (SelectedCueList is null) return;
        node.TriggerMode = SelectedCueList.DefaultTriggerMode;
        if (SelectedCueList.AutoRenumberOnInsert)
            RenumberFlat(SelectedCueList.Nodes, start: 1, step: 1);
    }

    [RelayCommand]
    private async Task AddMediaCueAsync()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;

        // Always seed at least one empty cue (matches the prior "+ Media → row, then pick" UX
        // and the test contract). Multi-select fills it with the first file plus N-1 follow-ups.
        var firstRow = new CueNodeViewModel(CueNodeKind.Media)
        {
            Number = NextNumber(parent),
            Label = Strings.CueNodeDefaultMediaLabel,
        };
        parent.Add(firstRow);
        FinalizeAddedCue(firstRow);
        SelectedCueNode = firstRow;

        var picked = await PickMediaFilePathsAsync(allowMultiple: true);
        if (picked.Count == 0)
        {
            // Picker cancelled — leave the empty cue so the operator can still drag a file onto
            // it (and so the existing tests' assumptions hold).
            GoCommand.NotifyCanExecuteChanged();
            BackCommand.NotifyCanExecuteChanged();
            StatusMessage = null;
            return;
        }

        var firstPath = picked[0];
        firstRow.MediaSourceItem = new FilePlaylistItem(firstPath);
        firstRow.SourceOrAction = firstPath;
        firstRow.Label = Path.GetFileNameWithoutExtension(firstPath);
        await ProbeAndAssignDurationAsync(firstRow, firstPath);

        CueNodeViewModel lastAdded = firstRow;
        for (var i = 1; i < picked.Count; i++)
        {
            var path = picked[i];
            var row = new CueNodeViewModel(CueNodeKind.Media)
            {
                Number = NextNumber(parent),
                Label = Path.GetFileNameWithoutExtension(path),
                MediaSourceItem = new FilePlaylistItem(path),
                SourceOrAction = path,
            };
            parent.Add(row);
            FinalizeAddedCue(row);
            lastAdded = row;
            await ProbeAndAssignDurationAsync(row, path);
        }

        SelectedCueNode = lastAdded;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = picked.Count > 1
            ? Strings.Format(nameof(Strings.CueAddedFromDropStatusFormat), picked.Count)
            : null;
    }

    [RelayCommand]
    private async Task AddNdiInputCueAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var dialogVm = new Dialogs.AddNDIInputDialogViewModel();
        await dialogVm.StartDiscoveryAsync();
        var dialog = new Views.Dialogs.AddNDIInputDialog { DataContext = dialogVm };
        try
        {
            var result = await dialog.ShowDialog<NDIInputPlaylistItem?>(owner);
            if (result is null) return;
            AddLiveInputCue(result, result.DisplayName);
        }
        finally
        {
            dialogVm.StopDiscovery();
        }
    }

    [RelayCommand]
    private async Task AddPortAudioInputCueAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var dialogVm = new Dialogs.AddPortAudioInputDialogViewModel();
        dialogVm.ReloadHostApis();
        var dialog = new Views.Dialogs.AddPortAudioInputDialog { DataContext = dialogVm };

        var result = await dialog.ShowDialog<PortAudioInputPlaylistItem?>(owner);
        if (result is null) return;
        AddLiveInputCue(result, result.DeviceName);
    }

    private void AddLiveInputCue(PlaylistItem source, string label)
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;
        var row = new CueNodeViewModel(CueNodeKind.Media)
        {
            Number = NextNumber(parent),
            Label = string.IsNullOrWhiteSpace(label) ? source.DisplayName : label,
            MediaSourceItem = source,
            SourceOrAction = source.DisplayName,
        };
        parent.Add(row);
        FinalizeAddedCue(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
    }

    private const int StaticCueDefaultDurationMs = 5000;

    /// <summary>Adds a still-image cue (held for the cue's custom duration). Default 5 s, editable in the drawer.</summary>
    [RelayCommand]
    private async Task AddImageCueAsync()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;
        var path = await PickImageFilePathAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        var (imgW, imgH) = await Task.Run(() =>
            FallbackImageLoader.TryGetImageSize(path, out var w, out var h) ? (w, h) : (0, 0));

        var row = new CueNodeViewModel(CueNodeKind.Media)
        {
            Number = NextNumber(parent),
            Label = Path.GetFileNameWithoutExtension(path),
            MediaSourceItem = new ImagePlaylistItem(path),
            SourceOrAction = path,
            SourceHasVideo = true,
            SourceVideoWidth = imgW,
            SourceVideoHeight = imgH,
            DurationMs = StaticCueDefaultDurationMs,
        };
        parent.Add(row);
        FinalizeAddedCue(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
    }

    /// <summary>Adds an editable text/title cue (rendered, held for the cue's custom duration). Default 5 s.</summary>
    [RelayCommand]
    private void AddTextCue()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;

        var text = new TextPlaylistItem { Text = Strings.CueNodeDefaultTextLabel };
        var row = new CueNodeViewModel(CueNodeKind.Media)
        {
            Number = NextNumber(parent),
            Label = text.DisplayName,
            MediaSourceItem = text,
            SourceOrAction = text.DisplayName,
            SourceHasVideo = true,
            SourceVideoWidth = text.CanvasWidth,
            SourceVideoHeight = text.CanvasHeight,
            DurationMs = StaticCueDefaultDurationMs,
        };
        parent.Add(row);
        FinalizeAddedCue(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
    }

    private static async Task<string?> PickImageFilePathAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return null;
        var opts = new FilePickerOpenOptions
        {
            Title = Strings.PickImageFileDialogTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.ImageFileTypeLabel) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.tiff"] },
                new FilePickerFileType(Strings.AllFilesFileTypeLabel) { Patterns = ["*"] },
            ],
        };
        var picked = await owner.StorageProvider.OpenFilePickerAsync(opts);
        return picked.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
    }

    [RelayCommand(CanExecute = nameof(CanBrowseMediaSource))]
    private async Task BrowseMediaSourceAsync()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Media } mediaCue)
            return;
        var path = await PickMediaFilePathAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            mediaCue.MediaSourceItem = new FilePlaylistItem(path);
            mediaCue.SourceOrAction = path;
            mediaCue.Label = Path.GetFileNameWithoutExtension(path);
            await ProbeAndAssignDurationAsync(mediaCue, path);
        }
    }

    /// <summary>Open the file once, probe duration + audio/video stream info + audio channel
    /// count, and write the lot onto the cue VM. The drawer's Audio + Video tab visibility and
    /// hints depend on these — landing them right away (before <c>StatusMessage</c> resets)
    /// keeps the UI accurate even for the cancel-leaves-empty-cue case.</summary>
    private static async Task ProbeAndAssignDurationAsync(CueNodeViewModel row, string path)
    {
        var probe = await CueMediaProbe.TryProbeAsync(path).ConfigureAwait(false);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (probe is null)
            {
                row.DurationMs = 0;
                row.SourceHasVideo = false;
                row.SourceHasAudio = false;
                row.SourceAudioChannels = 0;
                row.SourceVideoIsAttachedPicture = false;
                row.SourceFrameRateNum = 0;
                row.SourceFrameRateDen = 0;
                row.SourceVideoWidth = 0;
                row.SourceVideoHeight = 0;
                row.SetAudioTrackChoices([]);
                return;
            }

            row.DurationMs = probe.Value.DurationMs ?? 0;
            row.SourceHasVideo = probe.Value.HasVideo;
            row.SourceHasAudio = probe.Value.HasAudio;
            row.SourceAudioChannels = probe.Value.AudioChannels;
            row.SourceVideoIsAttachedPicture = probe.Value.VideoIsAttachedPicture;
            row.SourceFrameRateNum = probe.Value.SourceFrameRateNum;
            row.SourceFrameRateDen = probe.Value.SourceFrameRateDen;
            row.SourceVideoWidth = probe.Value.SourceVideoWidth;
            row.SourceVideoHeight = probe.Value.SourceVideoHeight;
            row.SetAudioTrackChoices(probe.Value.AudioTracks);
        });
    }

    private bool CanBrowseMediaSource() => SelectedCueNode?.Kind == CueNodeKind.Media;

    /// <summary>Fills the audio-track picker for cues that were loaded from disk (no probe yet).
    /// Stream-table probe only — cheap enough to run on first selection.</summary>
    private static async Task EnsureAudioTrackChoicesAsync(CueNodeViewModel node)
    {
        if (node.AudioTrackChoices.Count > 0)
            return;
        if (node.MediaSourceItem is not FilePlaylistItem file)
            return;

        var tracks = await CueMediaProbe.TryProbeAudioTracksAsync(file.Path).ConfigureAwait(false);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            // A Browse-media probe may have filled the list while we were probing; keep its result.
            if (node.AudioTrackChoices.Count == 0)
                node.SetAudioTrackChoices(tracks);
        });
    }

    private static async Task<string?> PickMediaFilePathAsync()
    {
        var paths = await PickMediaFilePathsAsync(allowMultiple: false);
        return paths.FirstOrDefault();
    }

    private static async Task<IReadOnlyList<string>> PickMediaFilePathsAsync(bool allowMultiple)
    {
        var owner = TryGetMainWindow();
        if (owner is null) return [];
        var opts = new FilePickerOpenOptions
        {
            Title = Strings.PickMediaFileDialogTitle,
            AllowMultiple = allowMultiple,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.MediaFileTypeLabel) { Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.mp3", "*.wav", "*.flac", "*.m4a"] },
                new FilePickerFileType(Strings.AllFilesFileTypeLabel) { Patterns = ["*"] },
            ],
        };
        var picked = await owner.StorageProvider.OpenFilePickerAsync(opts);
        return picked
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList()!;
    }

    [RelayCommand]
    private void AddActionCue()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;
        var row = new CueNodeViewModel(CueNodeKind.Action)
        {
            Number = NextNumber(parent),
            Label = Strings.CueNodeDefaultActionLabel,
            Extra = CueActionKind.OscOut.ToString(),
        };
        if (SelectedActionEndpoint is not null)
            row.EndpointIdText = SelectedActionEndpoint.Id.ToString();
        parent.Add(row);
        FinalizeAddedCue(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
    }

    [RelayCommand]
    private void AddCommentCue()
    {
        var parent = SelectedParentCollection();
        if (parent is null) return;
        var row = new CueNodeViewModel(CueNodeKind.Comment)
        {
            Number = NextNumber(parent),
            Label = Strings.CueNodeDefaultCommentLabel,
            SourceOrAction = Strings.CueNodeDefaultNotesText,
        };
        parent.Add(row);
        SelectedCueNode = row;
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveNode))]
    private void RemoveNode()
    {
        if (SelectedCueList is null || SelectedCueNode is null)
            return;
        var orderedBefore = EnumerateFireableCueOrder().ToList();
        var removedFireable = ResolveFireableCue(SelectedCueNode) ?? SelectedCueNode;
        var removedFireableIndex = orderedBefore.FindIndex(c => ReferenceEquals(c, removedFireable));
        if (!RemoveNodeRecursive(SelectedCueList.Nodes, SelectedCueNode))
            return;
        PruneSelectionToCurrentTree();
        ReconcileTransportAfterTreeMutation(removedFireableIndex);
    }

    private bool CanRemoveNode() => SelectedCueList is not null && SelectedCueNode is not null;

    /// <summary>Opens the rename popup for the currently selected cue. F2 triggers this from the
    /// tree's key bindings (Phase 5.6 wires F2); the right-click menu / drawer's "Rename…"
    /// affordance can also invoke it. Cancel discards changes; OK / Enter commits Number + Label.</summary>
    [RelayCommand(CanExecute = nameof(CanRenameSelectedCue))]
    private async Task RenameSelectedCueAsync()
    {
        if (SelectedCueNode is null) return;
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var dialogVm = Dialogs.RenameCueDialogViewModel.For(SelectedCueNode);
        var dialog = new Views.Dialogs.RenameCueDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<Dialogs.RenameCueDialogResult?>(owner);
        if (result is null) return;

        var oldDisplay = CueDisplay(SelectedCueNode);
        SelectedCueNode.Number = result.Number;
        SelectedCueNode.Label = result.Label;
        StatusMessage = Strings.Format(nameof(Strings.RenamedCueStatusFormat), oldDisplay, CueDisplay(SelectedCueNode));
    }

    private bool CanRenameSelectedCue() => SelectedCueNode is not null;

    /// <summary>Phase 5.8.2 — open the cue list settings dialog (pre-roll, default trigger
    /// mode, auto-renumber). Replaces the inline pre-roll spinner that used to live on the
    /// toolbar; gear icon on the toolbar opens this.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenCueListSettings))]
    private async Task OpenCueListSettingsAsync()
    {
        if (SelectedCueList is null) return;
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var dialogVm = new Dialogs.CueListSettingsDialogViewModel(
            SelectedCueList.PreRollCount,
            SelectedCueList.MaxPreparedDecoders,
            SelectedCueList.DefaultTriggerMode,
            SelectedCueList.AutoRenumberOnInsert);
        var dialog = new Views.Dialogs.CueListSettingsDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<Dialogs.CueListSettingsDialogResult?>(owner);
        if (result is null) return;

        SelectedCueList.PreRollCount = Math.Max(0, result.PreRollCount);
        SelectedCueList.MaxPreparedDecoders = Math.Max(0, result.MaxPreparedDecoders);
        SelectedCueList.DefaultTriggerMode = result.DefaultTriggerMode;
        SelectedCueList.AutoRenumberOnInsert = result.AutoRenumberOnInsert;
        StatusMessage = Strings.CueListSettingsAppliedStatus;
        SuggestPreRollRefresh();
    }

    private bool CanOpenCueListSettings() => SelectedCueList is not null;

    [RelayCommand(CanExecute = nameof(CanOpenCueOutputSetup))]
    private async Task OpenCueOutputSetupAsync()
    {
        if (SelectedCueList is null) return;
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var dialog = new Views.Dialogs.CueOutputSetupDialog { DataContext = this };
        await dialog.ShowDialog(owner);
    }

    private bool CanOpenCueOutputSetup() => SelectedCueList is not null;

    /// <summary>Move the selected cue up one slot within its parent collection. Ctrl+↑ binds
    /// here. No-op at the top of the parent (operator's expected behaviour — they get to feel
    /// the boundary).</summary>
    [RelayCommand(CanExecute = nameof(CanMoveSelectedCue))]
    private void MoveSelectedCueUp() => MoveSelectedCue(-1);

    [RelayCommand(CanExecute = nameof(CanMoveSelectedCue))]
    private void MoveSelectedCueDown() => MoveSelectedCue(+1);

    private bool CanMoveSelectedCue() => IsCueEditMode && SelectedCueNode is not null && SelectedCueList is not null;

    private void MoveSelectedCue(int delta)
    {
        if (SelectedCueNode is null || SelectedCueList is null) return;
        if (FindParentCollection(SelectedCueList.Nodes, SelectedCueNode) is not IList<CueNodeViewModel> parent)
            return;
        var idx = parent.IndexOf(SelectedCueNode);
        var next = idx + delta;
        if (next < 0 || next >= parent.Count) return;
        var node = SelectedCueNode;
        parent.RemoveAt(idx);
        parent.Insert(next, node);
        SelectedCueNode = node;
        MaybeRenumberAfterStructureChange();
        SuggestPreRollRefresh();
    }

    /// <summary>Moves a cue anywhere in the active cue-list tree. Used by drag/drop in the view.
    /// Dropping <see cref="CueNodeDropPlacement.Inside"/> onto a group appends to that group;
    /// dropping onto a non-group falls back to after the target.</summary>
    public bool MoveCueNode(CueNodeViewModel node, CueNodeViewModel? target, CueNodeDropPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!IsCueEditMode || SelectedCueList is null)
            return false;
        if (target is not null && ReferenceEquals(node, target))
            return false;
        if (target is not null && ContainsNode(node.Children, target))
            return false;

        if (FindParentCollection(SelectedCueList.Nodes, node) is not IList<CueNodeViewModel> sourceParent)
            return false;

        IList<CueNodeViewModel> destinationParent;
        var destinationIndex = 0;
        if (target is null)
        {
            destinationParent = SelectedCueList.Nodes;
            destinationIndex = destinationParent.Count;
        }
        else if (placement == CueNodeDropPlacement.Inside && target.IsGroup)
        {
            destinationParent = target.Children;
            destinationIndex = destinationParent.Count;
            target.IsExpanded = true;
        }
        else
        {
            destinationParent = FindParentCollection(SelectedCueList.Nodes, target) as IList<CueNodeViewModel>
                ?? SelectedCueList.Nodes;
            destinationIndex = destinationParent.IndexOf(target);
            if (destinationIndex < 0)
                return false;
            if (placement != CueNodeDropPlacement.Before)
                destinationIndex++;
        }

        var sourceIndex = sourceParent.IndexOf(node);
        if (sourceIndex < 0)
            return false;
        if (ReferenceEquals(sourceParent, destinationParent) && destinationIndex > sourceIndex)
            destinationIndex--;
        if (ReferenceEquals(sourceParent, destinationParent) && destinationIndex == sourceIndex)
            return false;

        sourceParent.RemoveAt(sourceIndex);
        destinationIndex = Math.Clamp(destinationIndex, 0, destinationParent.Count);
        destinationParent.Insert(destinationIndex, node);
        SelectedCueNode = node;
        MaybeRenumberAfterStructureChange();
        SuggestPreRollRefresh();
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = target is null
            ? "Moved cue to root level."
            : placement == CueNodeDropPlacement.Inside && target.IsGroup
                ? $"Moved cue into '{target.Label}'."
                : $"Moved cue near '{target.Label}'.";
        return true;
    }

    private void MaybeRenumberAfterStructureChange()
    {
        if (SelectedCueList?.AutoRenumberOnInsert == true)
            RenumberSubtree(SelectedCueList.Nodes, start: 1, step: 1, recurseIntoGroups: true);
    }

    private static bool ContainsNode(IEnumerable<CueNodeViewModel> nodes, CueNodeViewModel target)
    {
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node, target) || ContainsNode(node.Children, target))
                return true;
        }
        return false;
    }

    /// <summary>Deep-copy the selected cue with a fresh id and insert immediately after the
    /// original. Routes, placements, and group-children all clone. Bound to Ctrl+D.</summary>
    [RelayCommand(CanExecute = nameof(CanDuplicateSelectedCue))]
    private void DuplicateSelectedCue()
    {
        if (SelectedCueNode is null || SelectedCueList is null) return;
        if (FindParentCollection(SelectedCueList.Nodes, SelectedCueNode) is not IList<CueNodeViewModel> parent)
            return;

        // Deep-copy via the model layer. `ToModel()` projects through fresh `.Select(...).ToList()`
        // collections for routes / placements / children, so the snapshot doesn't share list
        // references with the original VM. `CloneCueNodeWithNewIds` then rotates ids (a `with` on
        // a record only does a shallow copy — we'd otherwise share AudioRoutes / VideoPlacements
        // lists between original and copy). `FromModel` rebuilds fresh VM collections from the
        // cloned snapshot, so no list reference is shared with the original cue.
        var snapshot = SelectedCueNode.ToModel();
        var copy = CloneCueNodeWithNewIds(snapshot);
        var copyVm = CueNodeViewModel.FromModel(copy, ResolveOutputLine);

        var idx = parent.IndexOf(SelectedCueNode);
        parent.Insert(idx + 1, copyVm);
        SelectedCueNode = copyVm;
    }

    private bool CanDuplicateSelectedCue() => SelectedCueNode is not null && SelectedCueList is not null;

    /// <summary>Phase 5.8.1 — clicking a color swatch sets the tag on every selected cue
    /// (so multi-select tagging works out of the box). Tag 0 clears.</summary>
    [RelayCommand(CanExecute = nameof(CanSetSelectedCueColorTag))]
    private void SetSelectedCueColorTag(int tag)
    {
        var clamped = Math.Clamp(tag, 0, CueColorTagPalette.MaxIndex);
        var targets = SelectedCueNodes.Count > 0
            ? SelectedCueNodes
            : (SelectedCueNode is null ? Array.Empty<CueNodeViewModel>() : new[] { SelectedCueNode });
        foreach (var node in targets)
            node.ColorTag = clamped;
    }

    private bool CanSetSelectedCueColorTag() => SelectedCueNode is not null;

    /// <summary>Swatch row bound by the drawer's General tab. Index 0 is "no tag" (transparent
    /// fill, slightly thicker border so it's clickable). Indexes 1..7 match
    /// <see cref="CueColorTagPalette"/>.</summary>
    public IReadOnlyList<CueColorSwatchViewModel> ColorTagSwatches { get; } =
        Enumerable.Range(0, CueColorTagPalette.MaxIndex + 1)
            .Select(i => new CueColorSwatchViewModel(i))
            .ToList();

    private static CueNode CloneCueNodeWithNewIds(CueNode src) => src switch
    {
        CueGroupNode g => g with
        {
            Id = Guid.NewGuid(),
            Children = g.Children.Select(CloneCueNodeWithNewIds).ToList(),
        },
        MediaCueNode m => m with { Id = Guid.NewGuid() },
        ActionCueNode a => a with { Id = Guid.NewGuid() },
        CommentCueNode c => c with { Id = Guid.NewGuid() },
        _ => src,
    };

    /// <summary>Bulk renumber. Walks the chosen scope (all / root only / current selection) in
    /// tree order, assigning <c>start</c>, <c>start+step</c>, … Nested groups recurse with a
    /// sub-numbering scheme — `1`, `1.1`, `1.2`, `2`, … — preserving the visible cue hierarchy.</summary>
    [RelayCommand(CanExecute = nameof(CanRenumber))]
    private async Task RenumberAsync()
    {
        if (SelectedCueList is null) return;
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var dialogVm = new Dialogs.RenumberSelectionDialogViewModel();
        if (_selectedCueNodes.Count <= 1)
            dialogVm.Scope = Dialogs.RenumberScope.All;
        var dialog = new Views.Dialogs.RenumberSelectionDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<Dialogs.RenumberSelectionDialogResult?>(owner);
        if (result is null) return;

        var renumbered = 0;
        switch (result.Scope)
        {
            case Dialogs.RenumberScope.All:
                renumbered = RenumberSubtree(SelectedCueList.Nodes, result.Start, result.Step, recurseIntoGroups: true);
                break;
            case Dialogs.RenumberScope.RootLevelOnly:
                renumbered = RenumberSubtree(SelectedCueList.Nodes, result.Start, result.Step, recurseIntoGroups: false);
                break;
            case Dialogs.RenumberScope.SelectionOnly:
                renumbered = RenumberFlat(_selectedCueNodes, result.Start, result.Step);
                break;
        }

        StatusMessage = Strings.Format(nameof(Strings.RenumberedStatusFormat), renumbered);
    }

    private bool CanRenumber() => SelectedCueList is not null && SelectedCueList.Nodes.Count > 0;

    /// <summary>Renumbers the rows in <paramref name="nodes"/> in tree order. When
    /// <paramref name="recurseIntoGroups"/> is true, group children get sub-numbers
    /// (parent="1" → children "1.1", "1.2", ...).</summary>
    private static int RenumberSubtree(IReadOnlyList<CueNodeViewModel> nodes, double start, double step, bool recurseIntoGroups)
    {
        var count = 0;
        var n = start;
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            node.Number = FormatCueNumber(n);
            count++;
            if (recurseIntoGroups && node.Kind == CueNodeKind.Group && node.Children.Count > 0)
                count += RenumberSubtreePrefixed(node.Children, node.Number, 1.0, 1.0);
            n += step;
        }
        return count;
    }

    private static int RenumberSubtreePrefixed(IReadOnlyList<CueNodeViewModel> children, string prefix, double start, double step)
    {
        var count = 0;
        var n = start;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            child.Number = $"{prefix}.{FormatCueNumber(n)}";
            count++;
            if (child.Kind == CueNodeKind.Group && child.Children.Count > 0)
                count += RenumberSubtreePrefixed(child.Children, child.Number, 1.0, 1.0);
            n += step;
        }
        return count;
    }

    private static int RenumberFlat(IReadOnlyList<CueNodeViewModel> nodes, double start, double step)
    {
        var count = 0;
        var n = start;
        foreach (var node in nodes)
        {
            node.Number = FormatCueNumber(n);
            count++;
            n += step;
        }
        return count;
    }

    private static string FormatCueNumber(double n) =>
        // Drop trailing zero for whole numbers (`1` not `1.0`); keep up to 2 decimals otherwise.
        n == Math.Truncate(n)
            ? ((long)n).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : n.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    [RelayCommand(CanExecute = nameof(CanAssignSelectedActionEndpoint))]
    private void AssignSelectedActionEndpoint()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Action } actionCue || SelectedActionEndpoint is null)
            return;
        actionCue.EndpointIdText = SelectedActionEndpoint.Id.ToString();
        OnPropertyChanged(nameof(SelectedActionEndpointSummary));
    }

    private bool CanAssignSelectedActionEndpoint() =>
        SelectedCueNode?.Kind == CueNodeKind.Action && SelectedActionEndpoint is not null;

    [RelayCommand(CanExecute = nameof(CanEditActionCue))]
    private async Task EditActionCueAsync()
    {
        if (SelectedCueNode is not { Kind: CueNodeKind.Action } cue)
            return;

        var owner = TryGetMainWindow();
        if (owner is null)
            return;

        var dialogVm = new Dialogs.ActionCueBuilderDialogViewModel();
        var actionKind = Enum.TryParse<CueActionKind>(cue.Extra, out var parsed)
            ? parsed
            : CueActionKind.OscOut;
        Guid? endpointId = Guid.TryParse(cue.EndpointIdText, out var id) ? id : null;
        dialogVm.Load(cue.Label, actionKind, cue.SourceOrAction, endpointId, ActionEndpoints);

        var dialog = new Views.Dialogs.ActionCueBuilderDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<Views.Dialogs.ActionCueBuilderResult?>(owner);
        if (result is null)
            return;

        if (result.EndpointId is { } endpoint)
            cue.EndpointIdText = endpoint.ToString();
        else
            cue.EndpointIdText = string.Empty;
        cue.Extra = result.ActionKind.ToString();
        cue.SourceOrAction = result.CommandText;
        SelectedActionEndpoint = result.EndpointId is { } resultEndpointId
            ? ActionEndpoints.FirstOrDefault(e => e.Id == resultEndpointId)
            : null;
        OnPropertyChanged(nameof(SelectedActionEndpointSummary));
        StatusMessage = Strings.Format(nameof(Strings.UpdatedActionCueStatusFormat), CueDisplay(cue));
    }

    private bool CanEditActionCue() => SelectedCueNode?.Kind == CueNodeKind.Action;

    private static string NextNumber(ICollection<CueNodeViewModel> siblings) => (siblings.Count + 1).ToString();

    [RelayCommand(CanExecute = nameof(CanStandbySelected))]
    private void StandbySelected()
    {
        if (SelectedCueNode is null)
            return;
        if (SelectedCueNode.Kind == CueNodeKind.Group && ResolveFireableCue(SelectedCueNode) is null)
            return;
        StandbyCueNode = SelectedCueNode;
        StatusMessage = Strings.Format(nameof(Strings.CueStandbyStatusFormat), CueDisplay(SelectedCueNode));
    }

    private bool CanStandbySelected() =>
        SelectedCueNode is { Kind: CueNodeKind.Group } group
            ? ResolveFireableCue(group) is not null
            : SelectedCueNode is not null;

    [RelayCommand(CanExecute = nameof(CanGo))]
    private async Task Go()
    {
        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0)
            return;

        if (CurrentCueNode is not null && IsTransportPaused)
        {
            IsTransportPaused = false;
            _ = SetPlaybackPausedCallback?.Invoke(false);
            StatusMessage = Strings.Format(nameof(Strings.CueResumedStatusFormat), CueDisplay(CurrentCueNode));
            return;
        }

        // Resolution order: explicit Standby (operator pressed the Standby button) → currently
        // selected cue (the operator's cursor — natural intent when they pressed Go directly) →
        // first cue in the list. Without the SelectedCueNode tier, pressing Go after clicking
        // anywhere in the tree fires cue 1, which is surprising.
        var fire = StandbyCueNode
                   ?? (SelectedCueNode is not null && ordered.Contains(ResolveFireableCue(SelectedCueNode)!)
                       ? SelectedCueNode
                       : ordered.FirstOrDefault());
        if (fire is null)
            return;

        CancelTransportRun();
        var plan = BuildTriggerPlan(fire);
        if (plan.Count == 0)
            return;

        var resolvedFire = ResolveFireableCue(fire) ?? fire;
        var nextStandby = NextCueAfter(resolvedFire, ordered);
        CurrentCueNode = plan[0].Cue;
        IsTransportPaused = false;
        _suppressStandbyPreRollRefresh = true;
        try
        {
            StandbyCueNode = nextStandby;
        }
        finally
        {
            _suppressStandbyPreRollRefresh = false;
        }
        SelectedCueNode = plan[0].Cue;
        StatusMessage = Strings.Format(
            nameof(Strings.CueGoStatusFormat),
            CueDisplay(fire),
            plan.Count,
            plan.Count == 1 ? string.Empty : Strings.PluralSuffixS);

        _transportRunCts = new CancellationTokenSource();
        try
        {
            await RunTriggerPlanAsync(plan, _transportRunCts.Token);
            SuggestPreRollRefresh();
        }
        catch (OperationCanceledException)
        {
            // Stop/Panic/next GO cancelled the prior run.
        }
    }

    private bool CanGo() => EnumerateFireableCueOrder().Any();

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        if (CurrentCueNode is null)
            return;
        IsTransportPaused = !IsTransportPaused;
        _ = SetPlaybackPausedCallback?.Invoke(IsTransportPaused);
        StatusMessage = IsTransportPaused
            ? Strings.Format(nameof(Strings.CuePausedStatusFormat), CueDisplay(CurrentCueNode))
            : Strings.Format(nameof(Strings.CueResumedStatusFormat), CueDisplay(CurrentCueNode));
    }

    private bool CanPause() => CurrentCueNode is not null;

    [RelayCommand]
    private void Stop()
    {
        CancelTransportRun();
        _ = StopPlaybackCallback?.Invoke();
        if (CurrentCueNode is null && StandbyCueNode is null && !IsTransportPaused)
            return;
        CurrentCueNode = null;
        IsTransportPaused = false;
        StatusMessage = Strings.CueStoppedStatus;
    }

    [RelayCommand]
    private void Panic()
    {
        CancelTransportRun();
        _ = StopPlaybackCallback?.Invoke();
        CurrentCueNode = null;
        StandbyCueNode = null;
        IsTransportPaused = false;
        StatusMessage = Strings.CuePanicStatus;
    }

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back()
    {
        var ordered = EnumerateFireableCueOrder().ToList();
        if (ordered.Count == 0)
            return;
        var anchor = StandbyCueNode ?? CurrentCueNode ?? ordered.First();
        var resolvedAnchor = ResolveFireableCue(anchor) ?? anchor;
        var idx = ordered.IndexOf(resolvedAnchor);
        if (idx < 0)
            return;
        var prev = idx > 0 ? ordered[idx - 1] : ordered[0];
        StandbyCueNode = prev;
        StatusMessage = Strings.Format(nameof(Strings.CueStandbyStatusFormat), CueDisplay(prev));
    }

    private bool CanBack() => EnumerateFireableCueOrder().Any();

    private CueNodeViewModel? ResolveFireableCue(CueNodeViewModel? node)
    {
        if (node is null)
            return null;
        if (node.Kind != CueNodeKind.Group)
            return node;
        return EnumerateFireableCueOrder(node.Children).FirstOrDefault();
    }

    private IEnumerable<CueNodeViewModel> EnumerateFireableCueOrder() =>
        SelectedCueList is null ? [] : EnumerateFireableCueOrder(SelectedCueList.Nodes);

    private static IEnumerable<CueNodeViewModel> EnumerateFireableCueOrder(IEnumerable<CueNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == CueNodeKind.Group)
            {
                foreach (var child in EnumerateFireableCueOrder(node.Children))
                    yield return child;
                continue;
            }
            yield return node;
        }
    }

    private static CueNodeViewModel? NextCueAfter(CueNodeViewModel current, IReadOnlyList<CueNodeViewModel> ordered)
    {
        var idx = -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!ReferenceEquals(ordered[i], current))
                continue;
            idx = i;
            break;
        }
        if (idx < 0 || idx + 1 >= ordered.Count)
            return null;
        return ordered[idx + 1];
    }

    private static string CueDisplay(CueNodeViewModel cue) =>
        string.IsNullOrWhiteSpace(cue.Number)
            ? cue.Label
            : $"{cue.Number} {cue.Label}".Trim();

    private static CueGroupFireMode ParseGroupFireMode(CueNodeViewModel group) =>
        Enum.TryParse<CueGroupFireMode>(group.Extra, out var mode)
            ? mode
            : CueGroupFireMode.FirstCueOnly;

    private List<(CueNodeViewModel Cue, int DelayMs)> BuildTriggerPlan(CueNodeViewModel target)
    {
        var plan = new List<(CueNodeViewModel Cue, int DelayMs)>();
        if (target.Kind != CueNodeKind.Group)
        {
            plan.Add((target, Math.Max(0, target.PreWaitMs)));
            AppendAutoContinueCues(plan, target);
            return plan;
        }

        var mode = ParseGroupFireMode(target);
        var children = target.Children.ToList();
        var groupPreWait = Math.Max(0, target.PreWaitMs);
        if (children.Count == 0)
            return plan;

        if (mode == CueGroupFireMode.FireAllSimultaneously)
        {
            foreach (var cue in EnumerateFireableCueOrder(children))
                plan.Add((cue, checked(groupPreWait + Math.Max(0, cue.PreWaitMs))));
            plan.Sort(static (a, b) => a.DelayMs.CompareTo(b.DelayMs));
            return plan;
        }

        var first = EnumerateFireableCueOrder(children).FirstOrDefault();
        if (first is not null)
        {
            plan.Add((first, checked(groupPreWait + Math.Max(0, first.PreWaitMs))));
            AppendAutoContinueCues(plan, first);
        }
        return plan;
    }

    private void AppendAutoContinueCues(List<(CueNodeViewModel Cue, int DelayMs)> plan, CueNodeViewModel anchor)
    {
        var ordered = EnumerateFireableCueOrder().ToList();
        var idx = ordered.FindIndex(c => ReferenceEquals(c, anchor));
        if (idx < 0)
            return;

        for (var i = idx + 1; i < ordered.Count; i++)
        {
            var next = ordered[i];
            if (next.TriggerMode != CueTriggerMode.AutoContinue)
                break;
            if (plan.Any(p => ReferenceEquals(p.Cue, next)))
                continue;
            plan.Add((next, Math.Max(0, next.PreWaitMs)));
        }
    }

    /// <summary>Called when the active player finishes a file naturally during cue-driven playback.</summary>
    public async Task OnMediaCueNaturallyEndedAsync()
    {
        if (CurrentCueNode is not { Kind: CueNodeKind.Media })
            return;

        var ordered = EnumerateFireableCueOrder().ToList();
        var idx = ordered.FindIndex(c => ReferenceEquals(c, CurrentCueNode));
        if (idx < 0 || idx + 1 >= ordered.Count)
            return;

        var next = ordered[idx + 1];
        if (next.TriggerMode != CueTriggerMode.AutoFollow)
            return;

        StandbyCueNode = next;
        SelectedCueNode = next;
        StatusMessage = Strings.Format(nameof(Strings.CueAutoFollowStatusFormat), CueDisplay(next));
        await Go();
    }

    public void RefreshBrokenEndpointFlags()
    {
        var ids = ActionEndpoints.Select(e => e.Id).ToHashSet();
        var broken = 0;
        foreach (var node in EnumerateAllCueNodes())
        {
            if (node.Kind != CueNodeKind.Action)
            {
                node.IsEndpointBroken = false;
                continue;
            }

            node.IsEndpointBroken = Guid.TryParse(node.EndpointIdText, out var endpointId)
                                    && !ids.Contains(endpointId);
            if (node.IsEndpointBroken)
                broken++;
        }

        if (broken > 0)
            StatusMessage = Strings.Format(nameof(Strings.CueBrokenEndpointCountStatusFormat), broken);
    }

    /// <summary>Distinct missing endpoint IDs referenced by action cues.</summary>
    public IReadOnlyList<(Guid MissingId, int CueCount, CueActionKind Kind)> GetBrokenEndpointGroups()
    {
        var liveIds = ActionEndpoints.Select(e => e.Id).ToHashSet();
        var groups = new Dictionary<Guid, (int Count, CueActionKind Kind)>();
        foreach (var node in EnumerateAllCueNodes())
        {
            if (node.Kind != CueNodeKind.Action)
                continue;
            if (!Guid.TryParse(node.EndpointIdText, out var missingId) || liveIds.Contains(missingId))
                continue;
            var kind = Enum.TryParse<CueActionKind>(node.Extra, out var k) ? k : CueActionKind.OscOut;
            if (groups.TryGetValue(missingId, out var g))
                groups[missingId] = (g.Count + 1, g.Kind);
            else
                groups[missingId] = (1, kind);
        }

        return groups.Select(kv => (kv.Key, kv.Value.Count, kv.Value.Kind)).ToList();
    }

    public void RemapActionEndpoints(IReadOnlyDictionary<Guid, Guid> missingToReplacement)
    {
        if (missingToReplacement.Count == 0)
            return;

        foreach (var node in EnumerateAllCueNodes())
        {
            if (node.Kind != CueNodeKind.Action)
                continue;
            if (!Guid.TryParse(node.EndpointIdText, out var missingId))
                continue;
            if (!missingToReplacement.TryGetValue(missingId, out var replacement))
                continue;
            node.EndpointIdText = replacement.ToString();
        }

        RefreshBrokenEndpointFlags();
    }

    public IReadOnlyList<(Guid CueId, PortAudioInputPlaylistItem Item)> GetPortAudioPreConnectTargets(int maxCount)
    {
        var effectiveMax = ResolvePreRollTargetLimit(maxCount);

        var targets = new List<(Guid, PortAudioInputPlaylistItem)>();
        foreach (var cue in EnumeratePreRollWindow())
        {
            if (targets.Count >= effectiveMax)
                break;
            if (cue.Kind != CueNodeKind.Media
                || cue.DisablePreRoll // per-cue resource opt-out
                || cue.MediaSourceItem is not PortAudioInputPlaylistItem pa
                || !pa.SupportsPreRoll())
                continue;
            targets.Add((cue.Id, pa));
        }

        return targets;
    }

    public void AddMediaFilesFromDrop(IEnumerable<string> paths)
    {
        if (SelectedCueList is null)
            return;

        var parent = SelectedParentCollection() ?? SelectedCueList.Nodes;
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;
            var row = new CueNodeViewModel(CueNodeKind.Media)
            {
                Number = NextNumber(parent),
                Label = Path.GetFileNameWithoutExtension(path),
                MediaSourceItem = new FilePlaylistItem(path),
                SourceOrAction = path,
            };
            parent.Add(row);
            _ = ProbeAndAssignDurationAsync(row, path);
            added++;
        }

        if (added > 0)
            StatusMessage = Strings.Format(nameof(Strings.CueAddedFromDropStatusFormat), added);
    }

    private IEnumerable<CueNodeViewModel> EnumerateAllCueNodes()
    {
        if (SelectedCueList is null)
            yield break;
        foreach (var node in EnumerateAllCueNodes(SelectedCueList.Nodes))
            yield return node;
    }

    private static IEnumerable<CueNodeViewModel> EnumerateAllCueNodes(IEnumerable<CueNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateAllCueNodes(node.Children))
                yield return child;
        }
    }

    private async Task RunTriggerPlanAsync(IReadOnlyList<(CueNodeViewModel Cue, int DelayMs)> plan, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;

        // Group steps that share the same delay for coordinated start.
        var groups = plan.GroupBy(s => s.DelayMs).OrderBy(g => g.Key).ToList();
        foreach (var group in groups)
        {
            await WaitUntilDelayAsync(startedAt, group.Key, ct);
            ct.ThrowIfCancellationRequested();

            var steps = group.ToList();
            foreach (var step in steps)
            {
                CurrentCueNode = step.Cue;
                SelectedCueNode = step.Cue;
            }

            if (steps.Count > 1 && MediaCueGroupExecutor is not null)
            {
                // Coordinated group: open all decoders in parallel, start in sync.
                DispatchCueGroupExecution(steps.Select(s => s.Cue).ToList(), ct);
            }
            else
            {
                foreach (var step in steps)
                    DispatchCueExecution(step.Cue, ct);
            }
        }
    }

    private void DispatchCueExecution(CueNodeViewModel cue, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var exec = await ExecuteCueAsync(cue, ct).ConfigureAwait(false);
                await SetStatusMessageOnUiAsync(string.IsNullOrWhiteSpace(exec)
                    ? Strings.Format(nameof(Strings.CueTriggeredStatusFormat), CueDisplay(cue))
                    : Strings.Format(nameof(Strings.CueTriggeredWithDetailStatusFormat), CueDisplay(cue), exec));
            }
            catch (OperationCanceledException) { /* Stop / Panic cancelled the dispatched cue. */ }
            catch (Exception ex)
            {
                await SetStatusMessageOnUiAsync(Strings.Format(nameof(Strings.CueTriggeredWithDetailStatusFormat),
                    CueDisplay(cue), ex.Message));
            }
        }, ct);
    }

    private void DispatchCueGroupExecution(IReadOnlyList<CueNodeViewModel> cues, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var mediaCues = cues
                    .Where(c => c.Kind == CueNodeKind.Media)
                    .Select(c => c.ToModel())
                    .OfType<MediaCueNode>()
                    .ToList();

                if (mediaCues.Count > 0 && MediaCueGroupExecutor is not null)
                {
                    var result = await MediaCueGroupExecutor(mediaCues, ct).ConfigureAwait(false);
                    await SetStatusMessageOnUiAsync(string.IsNullOrWhiteSpace(result)
                        ? Strings.Format(nameof(Strings.CueTriggeredStatusFormat), $"{mediaCues.Count} cues")
                        : result);
                }

                // Non-media cues in the group still dispatch individually.
                foreach (var cue in cues.Where(c => c.Kind != CueNodeKind.Media))
                {
                    try
                    {
                        var exec = await ExecuteCueAsync(cue, ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(exec))
                            await SetStatusMessageOnUiAsync(Strings.Format(nameof(Strings.CueTriggeredWithDetailStatusFormat), CueDisplay(cue), exec));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await SetStatusMessageOnUiAsync(Strings.Format(nameof(Strings.CueTriggeredWithDetailStatusFormat), CueDisplay(cue), ex.Message));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await SetStatusMessageOnUiAsync(ex.Message);
            }
        }, ct);
    }

    private async Task SetStatusMessageOnUiAsync(string? message)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            StatusMessage = message;
            return;
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => StatusMessage = message);
    }

    private async Task<string?> ExecuteCueAsync(CueNodeViewModel cue, CancellationToken ct)
    {
        switch (cue.Kind)
        {
            case CueNodeKind.Media:
                if (MediaCueExecutor is null)
                    return Strings.CueMediaExecutionNotConfigured;
                return cue.ToModel() is MediaCueNode media
                    ? await MediaCueExecutor(media, ct)
                    : Strings.CueInvalidMediaCue;
            case CueNodeKind.Action:
                if (ActionCueExecutor is null)
                    return Strings.CueActionExecutionNotConfigured;
                return cue.ToModel() is ActionCueNode action
                    ? await ActionCueExecutor(action, ct)
                    : Strings.CueInvalidActionCue;
            case CueNodeKind.Comment:
                return Strings.CueCommentResult;
            case CueNodeKind.Group:
            default:
                return null;
        }
    }

    private async Task WaitUntilDelayAsync(DateTime startedAtUtc, int delayMs, CancellationToken ct)
    {
        if (delayMs <= 0)
            return;

        while (true)
        {
                ct.ThrowIfCancellationRequested();
                while (IsTransportPaused)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(40, ct);
                    startedAtUtc = startedAtUtc.AddMilliseconds(40);
                }

            var due = startedAtUtc.AddMilliseconds(delayMs);
            var remaining = due - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return;

            var slice = remaining > TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : remaining;
            await Task.Delay(slice, ct);
        }
    }

    private void CancelTransportRun()
    {
        try { _transportRunCts?.Cancel(); } catch { /* best effort */ }
        try { _transportRunCts?.Dispose(); } catch { /* best effort */ }
        _transportRunCts = null;
    }

    private static IEnumerable<CueNodeViewModel> EnumerateMediaNodes(IEnumerable<CueNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Kind == CueNodeKind.Media)
                yield return node;
            foreach (var child in EnumerateMediaNodes(node.Children))
                yield return child;
        }
    }

    public void SetActionEndpoints(IEnumerable<ActionEndpoint> endpoints)
    {
        ActionEndpoints.Clear();
        foreach (var endpoint in endpoints.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            ActionEndpoints.Add(endpoint);
        if (SelectedCueNode?.Kind == CueNodeKind.Action && Guid.TryParse(SelectedCueNode.EndpointIdText, out var endpointId))
            SelectedActionEndpoint = ActionEndpoints.FirstOrDefault(e => e.Id == endpointId);
        RefreshBrokenEndpointFlags();
    }

    private string? _cueListsCollectionPath;

    public string? CueListsCollectionPath => _cueListsCollectionPath;

    public string? DisplayedCueFilePath => _cueListsCollectionPath ?? SelectedCueList?.Path;

    public List<CueList> BuildCueListsSnapshot() => CueLists.Select(c => c.ToModel()).ToList();

    public void ApplyCueLists(IReadOnlyList<CueList> lists, string? collectionPath = null)
    {
        _cueListsCollectionPath = collectionPath;
        OnPropertyChanged(nameof(CueListsCollectionPath));
        OnPropertyChanged(nameof(DisplayedCueFilePath));
        CueLists.Clear();
        foreach (var list in lists)
            CueLists.Add(CueListEditorViewModel.FromModel(list, resolveLine: ResolveOutputLine));
        if (CueLists.Count == 0)
            CueLists.Add(new CueListEditorViewModel(Strings.DefaultCueListName));
        SelectedCueList = CueLists[0];
        _selectedCueNodes.Clear();
        OnPropertyChanged(nameof(SelectedCueCount));
        OnPropertyChanged(nameof(IsMultiSelected));
        SelectedCueNode = null;
        SelectedAudioRoute = null;
        SelectedVideoPlacement = null;
        CurrentCueNode = null;
        StandbyCueNode = null;
        IsTransportPaused = false;
    }

    private void ClearCueListsCollectionPath()
    {
        if (_cueListsCollectionPath is null)
            return;

        _cueListsCollectionPath = null;
        OnPropertyChanged(nameof(CueListsCollectionPath));
        OnPropertyChanged(nameof(DisplayedCueFilePath));
    }

    [RelayCommand]
    private async Task LoadCueListAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var opts = new FilePickerOpenOptions
        {
            Title = Strings.OpenCueListDialogTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.HaPlayCueListFileTypeLabel) { Patterns = ["*." + CueListIO.FileExtension] },
                new FilePickerFileType(Strings.JsonFileTypeLabel) { Patterns = ["*.json"] },
                new FilePickerFileType(Strings.AllFilesFileTypeLabel) { Patterns = ["*"] },
            ],
        };

        var picks = await owner.StorageProvider.OpenFilePickerAsync(opts);
        var picked = picks.FirstOrDefault();
        if (picked is null) return;
        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var list = await CueListIO.LoadAsync(path);
            var vm = CueListEditorViewModel.FromModel(list, path, ResolveOutputLine);
            ClearCueListsCollectionPath();
            CueLists.Add(vm);
            SelectedCueList = vm;
            SelectedCueNode = null;
            StatusMessage = Strings.Format(nameof(Strings.LoadedCueListStatusFormat), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format(nameof(Strings.CueListLoadFailedStatusFormat), ex.Message);
        }
    }

    [RelayCommand]
    private Task SaveCueListAsync() =>
        SelectedCueList is { Path: { } path } ? SaveCueListToPathAsync(path) : SaveCueListAsAsync();

    [RelayCommand]
    private async Task SaveCueListAsAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null || SelectedCueList is null) return;

        var opts = new FilePickerSaveOptions
        {
            Title = Strings.SaveCueListDialogTitle,
            DefaultExtension = CueListIO.FileExtension,
            SuggestedFileName = string.IsNullOrWhiteSpace(SelectedCueList.Path)
                ? Strings.Format(nameof(Strings.CueListDefaultFileNameFormat), SanitizeFileName(SelectedCueList.Name), CueListIO.FileExtension)
                : Path.GetFileName(SelectedCueList.Path),
            FileTypeChoices =
            [
                new FilePickerFileType(Strings.HaPlayCueListFileTypeLabel) { Patterns = ["*." + CueListIO.FileExtension] },
            ],
        };
        var picked = await owner.StorageProvider.SaveFilePickerAsync(opts);
        if (picked is null) return;
        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        await SaveCueListToPathAsync(path);
    }

    private async Task SaveCueListToPathAsync(string path)
    {
        if (SelectedCueList is null)
            return;
        try
        {
            await CueListIO.SaveAsync(SelectedCueList.ToModel(), path);
            SelectedCueList.Path = path;
            OnPropertyChanged(nameof(DisplayedCueFilePath));
            StatusMessage = Strings.Format(nameof(Strings.SavedCueListStatusFormat), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format(nameof(Strings.CueListSaveFailedStatusFormat), ex.Message);
        }
    }

    [RelayCommand]
    private async Task LoadAllCueListsAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null)
            return;

        var opts = new FilePickerOpenOptions
        {
            Title = Strings.OpenAllCueListsDialogTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.HaPlayCueListsFileTypeLabel)
                {
                    Patterns = ["*." + CueListsIO.FileExtension],
                },
                new FilePickerFileType(Strings.JsonFileTypeLabel) { Patterns = ["*.json"] },
                new FilePickerFileType(Strings.AllFilesFileTypeLabel) { Patterns = ["*"] },
            ],
        };

        var picks = await owner.StorageProvider.OpenFilePickerAsync(opts);
        var picked = picks.FirstOrDefault();
        if (picked is null)
            return;

        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            var lists = await CueListsIO.LoadAsync(path);
            ApplyCueLists(lists, path);
            StatusMessage = Strings.Format(nameof(Strings.LoadedAllCueListsStatusFormat), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format(nameof(Strings.AllCueListsLoadFailedStatusFormat), ex.Message);
        }
    }

    [RelayCommand]
    private Task SaveAllCueListsAsync() =>
        !string.IsNullOrEmpty(_cueListsCollectionPath)
            ? SaveAllCueListsToPathAsync(_cueListsCollectionPath)
            : SaveAllCueListsAsAsync();

    [RelayCommand]
    private async Task SaveAllCueListsAsAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null)
            return;

        var opts = new FilePickerSaveOptions
        {
            Title = Strings.SaveAllCueListsDialogTitle,
            DefaultExtension = CueListsIO.FileExtension,
            SuggestedFileName = string.IsNullOrEmpty(_cueListsCollectionPath)
                ? Strings.Format(
                    nameof(Strings.CueListsCollectionDefaultFileNameFormat),
                    Strings.CueListsCollectionFileNameFallback,
                    CueListsIO.FileExtension)
                : Path.GetFileName(_cueListsCollectionPath),
            FileTypeChoices =
            [
                new FilePickerFileType(Strings.HaPlayCueListsFileTypeLabel)
                {
                    Patterns = ["*." + CueListsIO.FileExtension],
                },
            ],
        };

        var picked = await owner.StorageProvider.SaveFilePickerAsync(opts);
        if (picked is null)
            return;

        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        await SaveAllCueListsToPathAsync(path);
    }

    private async Task SaveAllCueListsToPathAsync(string path)
    {
        try
        {
            await CueListsIO.SaveAsync(BuildCueListsSnapshot(), path, "HaPlay");
            _cueListsCollectionPath = path;
            OnPropertyChanged(nameof(CueListsCollectionPath));
            OnPropertyChanged(nameof(DisplayedCueFilePath));
            StatusMessage = Strings.Format(nameof(Strings.SavedAllCueListsStatusFormat), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format(nameof(Strings.AllCueListsSaveFailedStatusFormat), ex.Message);
        }
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Strings.CueListFileNameFallback;
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static Window? TryGetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk)
            return desk.MainWindow;
        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime single
            && single.MainView is Window w)
            return w;
        return null;
    }
}
