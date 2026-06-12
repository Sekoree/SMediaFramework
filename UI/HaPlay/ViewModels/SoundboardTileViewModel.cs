using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;

namespace HaPlay.ViewModels;

/// <summary>
/// One cell on a soundboard grid. Holds the persisted tile settings plus the transient playback
/// state the grid renders (progress, remaining time, fade countdown). Tap handling lives on
/// <see cref="SoundboardWorkspaceViewModel.TapTileAsync"/>.
/// </summary>
public sealed partial class SoundboardTileViewModel : ObservableObject
{
    public SoundboardTileViewModel(int row, int column)
    {
        Row = row;
        Column = column;
    }

    /// <summary>Stable id — also the engine's sound key while the tile plays.</summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    public int Row { get; }

    public int Column { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBound), nameof(DisplayName), nameof(FileNameDisplay), nameof(TimeLabel), nameof(ShowsDropHint))]
    private string? _filePath;

    /// <summary>Optional alias shown on the tile instead of the filename; null/blank = filename.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string? _label;

    /// <summary>Target output line; <see cref="Guid.Empty"/> = board default at play time.</summary>
    [ObservableProperty]
    private Guid _outputLineId;

    /// <summary>Linear volume 0..1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumePercent))]
    private double _volume = 1.0;

    /// <summary>0 = tap-again stops instantly; otherwise tap-again ramps out over this time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFadeOut))]
    private int _fadeOutMs;

    [ObservableProperty]
    private bool _loop;

    /// <summary>Clip length (probe-cached; refined from engine progress while playing).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeLabel), nameof(ProgressPercent))]
    private long _durationMs;

    // ----- Transient playback state (engine events) ---------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeLabel), nameof(ProgressPercent))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeLabel), nameof(ProgressPercent))]
    private long _positionMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FadeProgressPercent))]
    private bool _isFading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FadeProgressPercent))]
    private long _fadeRemainingMs;

    /// <summary>Mirrors the workspace edit mode (propagated by the owning board) so the grid can
    /// show unbound tiles as drop targets without cross-template ancestor bindings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsDropHint))]
    private bool _isEditing;

    public bool IsBound => !string.IsNullOrWhiteSpace(FilePath);

    /// <summary>The "+" placeholder: unbound tiles surface only in edit mode; outside it they stay
    /// blank but keep their grid cell so bound tiles never shift.</summary>
    public bool ShowsDropHint => !IsBound && IsEditing;

    public bool HasFadeOut => FadeOutMs > 0;

    /// <summary>What the grid shows on the tile: the alias when set, else the filename without
    /// extension.</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(Label)
        ? Label
        : IsBound ? Path.GetFileNameWithoutExtension(FilePath!) : string.Empty;

    /// <summary>Actual filename (with extension) for the editor's file row — stays truthful even
    /// when an alias hides it on the grid.</summary>
    public string FileNameDisplay => IsBound ? Path.GetFileName(FilePath!) : string.Empty;

    /// <summary>Volume for the editor slider (0–100).</summary>
    public double VolumePercent
    {
        get => Math.Round(Volume * 100.0);
        set => Volume = Math.Clamp(value, 0, 100) / 100.0;
    }

    /// <summary>Idle: the clip's length. Playing: the remaining time (counting down).</summary>
    public string TimeLabel
    {
        get
        {
            if (!IsBound)
                return string.Empty;
            if (IsPlaying && DurationMs > 0)
                return "-" + FormatMs(Math.Max(0, DurationMs - PositionMs));
            return DurationMs > 0 ? FormatMs(DurationMs) : Resources.Strings.EmDash;
        }
    }

    public double ProgressPercent
    {
        get
        {
            if (!IsPlaying || DurationMs <= 0)
                return 0;
            return Math.Clamp(PositionMs * 100.0 / DurationMs, 0, 100);
        }
    }

    /// <summary>Fade countdown bar: 100 at fade start, ticking to 0.</summary>
    public double FadeProgressPercent
    {
        get
        {
            if (!IsFading || FadeOutMs <= 0)
                return 0;
            return Math.Clamp(FadeRemainingMs * 100.0 / FadeOutMs, 0, 100);
        }
    }

    public void ResetPlaybackState()
    {
        IsPlaying = false;
        PositionMs = 0;
        IsFading = false;
        FadeRemainingMs = 0;
    }

    public SoundboardTileConfig ToConfig() => new()
    {
        Id = Id,
        Row = Row,
        Column = Column,
        FilePath = FilePath,
        Label = string.IsNullOrWhiteSpace(Label) ? null : Label,
        OutputLineId = OutputLineId,
        Volume = Volume,
        FadeOutMs = FadeOutMs,
        Loop = Loop,
        DurationMs = DurationMs > 0 ? (int)Math.Min(int.MaxValue, DurationMs) : null,
    };

    public static SoundboardTileViewModel FromConfig(SoundboardTileConfig config) =>
        new(Math.Max(0, config.Row), Math.Max(0, config.Column))
        {
            Id = config.Id,
            FilePath = config.FilePath,
            Label = config.Label,
            OutputLineId = config.OutputLineId,
            Volume = Math.Clamp(config.Volume, 0, 1),
            FadeOutMs = Math.Max(0, config.FadeOutMs),
            Loop = config.Loop,
            DurationMs = Math.Max(0, config.DurationMs ?? 0),
        };

    internal static string FormatMs(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
