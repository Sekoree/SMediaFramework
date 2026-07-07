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

public sealed partial class CueNodeViewModel : ObservableObject
{
    private const int DefaultNDIInputAudioChannels = 2;

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
        OnPropertyChanged(nameof(FontFamilyOptions));
        OnPropertyChanged(nameof(TextBounds));
        OnPropertyChanged(nameof(TextFontSizePx));
        OnPropertyChanged(nameof(TextBold));
        OnPropertyChanged(nameof(TextItalic));
        OnPropertyChanged(nameof(TextColorHex));
        OnPropertyChanged(nameof(TextBackgroundHex));
        OnPropertyChanged(nameof(TextOutlineHex));
        OnPropertyChanged(nameof(TextColor));
        OnPropertyChanged(nameof(TextBackgroundColor));
        OnPropertyChanged(nameof(TextOutlineColor));
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
                    : Math.Max(SourceAudioChannels, DefaultNDIInputAudioChannels);
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
            // An MMD scene is a pure video source (30 fps BGRA at the scene's render size, no audio
            // leg) — without these flags the drawer never offers the Video tab, so the cue could not
            // be placed on a composition at all.
            case MMDPlaylistItem mmd:
                SourceHasVideo = true;
                SourceHasAudio = false;
                SourceAudioChannels = 0;
                SourceVideoIsAttachedPicture = false;
                SourceFrameRateNum = 30;
                SourceFrameRateDen = 1;
                SourceVideoWidth = mmd.RenderWidth;
                SourceVideoHeight = mmd.RenderHeight;
                break;
            // A prepared YouTube item plays from the local cache like a file. Conservative defaults
            // here (stereo audio, video unless deliberately audio-only) so the Audio/Video tabs show
            // immediately; the add path refines them by probing the cached asset when it exists.
            case YouTubePlaylistItem yt:
                SourceHasVideo = !yt.AudioOnly;
                SourceHasAudio = true;
                SourceAudioChannels = Math.Max(SourceAudioChannels, 2);
                SourceVideoIsAttachedPicture = false;
                if (yt.AudioOnly)
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

    /// <summary>Font families for the dropdown: the installed system fonts plus this cue's current family pinned at
    /// the top (so the embedded "Inter" default — which isn't an OS system font — still shows and stays selected).</summary>
    public IReadOnlyList<string> FontFamilyOptions => FontCatalog.WithCurrent(TextFontFamily);

    /// <summary>The tight bounding box of this text cue's rendered text, as fractions (0..1) of its canvas — for
    /// the placement editor to outline the actual text extent inside the placed frame. Null for a non-text cue.</summary>
    public Avalonia.Rect? TextBounds =>
        TextSource is { } t && S.Media.Source.Text.TextFrameRenderer.MeasureNormalizedBounds(HaPlay.Playback.TextSourceSpecMapper.ToSpec(t)) is { } b
            ? new Avalonia.Rect(b.X, b.Y, b.W, b.H)
            : null;

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

    // Avalonia.Media.Color views over the ARGB fields for the ColorPicker controls (kept in sync with the *Hex
    // strings, which stay for scripting/round-trip). A no-op guard avoids a set→render loop when the picker
    // re-emits the same colour.
    public Avalonia.Media.Color TextColor
    {
        get => ToColor(TextSource?.ColorArgb ?? 0xFFFFFFFF);
        set { if (TextSource is { } t && FromColor(value) != t.ColorArgb) MutateText(_ => _ with { ColorArgb = FromColor(value) }); }
    }

    public Avalonia.Media.Color TextBackgroundColor
    {
        get => ToColor(TextSource?.BackgroundArgb ?? 0);
        set { if (TextSource is { } t && FromColor(value) != t.BackgroundArgb) MutateText(_ => _ with { BackgroundArgb = FromColor(value) }); }
    }

    public Avalonia.Media.Color TextOutlineColor
    {
        get => ToColor(TextSource?.OutlineArgb ?? 0xFF000000);
        set { if (TextSource is { } t && FromColor(value) != t.OutlineArgb) MutateText(_ => _ with { OutlineArgb = FromColor(value) }); }
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

    private static Avalonia.Media.Color ToColor(uint argb) =>
        Avalonia.Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static uint FromColor(Avalonia.Media.Color c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

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
    public void SetAudioTrackChoices(IReadOnlyList<S.Media.Decode.FFmpeg.MediaStreamInfo> tracks)
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

    /// <summary>Persisted subtitle selections from the loaded cue. Used to restore the picker's checked state
    /// and to preserve sidecar selections / style overrides not editable in the embedded-track picker.</summary>
    public IReadOnlyList<HaPlay.Models.CueSubtitleSelection> PersistedSubtitles { get; init; } = [];

    /// <summary>Embedded subtitle tracks for this cue's source, each with its own none/one/many toggle.</summary>
    public ObservableCollection<CueSubtitleTrackChoice> SubtitleTrackChoices { get; } = new();

    /// <summary>The subtitle tab shows only when the source carries at least one embedded subtitle track.</summary>
    public bool HasSubtitleTracks => SubtitleTrackChoices.Count > 0;

    /// <summary>Cue-level subtitle font override applied to all selected tracks (libass fallback family).
    /// Empty keeps each document's own font.</summary>
    [ObservableProperty]
    private string? _subtitleFontFamily;

    /// <summary>Cue-level subtitle size multiplier (1.0 = document default). Null keeps the document sizing.</summary>
    [ObservableProperty]
    private double? _subtitleFontScale;

    /// <summary>NumericUpDown-friendly (decimal?) view of <see cref="SubtitleFontScale"/>.</summary>
    public decimal? SubtitleFontScaleValue
    {
        get => SubtitleFontScale is { } s ? (decimal)s : null;
        set => SubtitleFontScale = value is null ? null : (double)value;
    }

    partial void OnSubtitleFontScaleChanged(double? value) => OnPropertyChanged(nameof(SubtitleFontScaleValue));

    /// <summary>Alignment options for the subtitle picker (ASS numpad). First entry keeps the document default.</summary>
    public IReadOnlyList<SubtitleAlignmentChoice> SubtitleAlignmentChoices { get; } =
    [
        new(null, "Default"),
        new(1, "Bottom-left"), new(2, "Bottom-center"), new(3, "Bottom-right"),
        new(4, "Middle-left"), new(5, "Middle-center"), new(6, "Middle-right"),
        new(7, "Top-left"), new(8, "Top-center"), new(9, "Top-right"),
    ];

    /// <summary>Cue-level alignment override applied to all selected text tracks.</summary>
    [ObservableProperty]
    private SubtitleAlignmentChoice? _selectedSubtitleAlignment;

    /// <summary>Replaces the subtitle picker entries from a probe, restoring the checked state and the cue-level
    /// font overrides from the persisted selections (matched by stream index).</summary>
    public void SetSubtitleTrackChoices(IReadOnlyList<S.Media.Decode.FFmpeg.MediaStreamInfo> tracks)
    {
        var selectedIndices = PersistedSubtitles
            .Where(s => s.IsEmbedded)
            .Select(s => s.StreamIndex!.Value)
            .ToHashSet();

        SubtitleTrackChoices.Clear();
        foreach (var t in tracks)
            SubtitleTrackChoices.Add(new CueSubtitleTrackChoice(t.Index, t.ToDisplayString(), selectedIndices.Contains(t.Index)));

        // Restore the cue-level style overrides from whichever persisted selection carried them.
        var styled = PersistedSubtitles.FirstOrDefault(
            s => s.FontFamily is not null || s.FontScale is not null || s.Alignment is not null);
        SubtitleFontFamily = styled?.FontFamily;
        SubtitleFontScale = styled?.FontScale;
        SelectedSubtitleAlignment =
            SubtitleAlignmentChoices.FirstOrDefault(a => a.Value == styled?.Alignment) ?? SubtitleAlignmentChoices[0];

        OnPropertyChanged(nameof(HasSubtitleTracks));
    }

    /// <summary>Builds the model's subtitle selection list from the checked embedded tracks, preserving any
    /// persisted style overrides for those tracks plus any sidecar selections (not shown in this picker).
    /// Returns the persisted list unchanged when the picker hasn't been populated (no probe yet) so a save
    /// from a never-opened cue can't drop selections.</summary>
    public IReadOnlyList<HaPlay.Models.CueSubtitleSelection> BuildSubtitleSelections()
    {
        if (SubtitleTrackChoices.Count == 0)
            return PersistedSubtitles;

        var family = string.IsNullOrWhiteSpace(SubtitleFontFamily) ? null : SubtitleFontFamily.Trim();
        var result = new List<HaPlay.Models.CueSubtitleSelection>();
        foreach (var choice in SubtitleTrackChoices.Where(c => c.IsSelected))
        {
            result.Add(new HaPlay.Models.CueSubtitleSelection
            {
                StreamIndex = choice.StreamIndex,
                Label = choice.Label,
                FontFamily = family,
                FontScale = SubtitleFontScale,
                Alignment = SelectedSubtitleAlignment?.Value,
            });
        }

        // Sidecar selections aren't represented in the embedded-track picker — round-trip them untouched.
        result.AddRange(PersistedSubtitles.Where(s => !s.IsEmbedded));
        return result;
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
        get => Enum.TryParse<CueActionKind>(Extra, out var kind) ? kind : CueActionKind.OSCOut;
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
                    PersistedSubtitles = m.Subtitles,
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
                Subtitles = BuildSubtitleSelections(),
                VideoIsAttachedPicture = SourceVideoIsAttachedPicture,
                SourceFrameRateNum = Math.Max(0, SourceFrameRateNum),
                SourceFrameRateDen = Math.Max(0, SourceFrameRateDen),
                SourceVideoWidth = Math.Max(0, SourceVideoWidth),
                SourceVideoHeight = Math.Max(0, SourceVideoHeight),
                StartOffsetMs = Math.Max(0, StartOffsetMs),
                EndOffsetMs = Math.Max(0, EndOffsetMs),
                Loop = Loop,
                EndBehavior = EndBehavior,
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
                ActionKind = Enum.TryParse<CueActionKind>(Extra, out var ak) ? ak : CueActionKind.OSCOut,
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
