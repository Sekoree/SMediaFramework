using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HaPlay.ViewModels;

/// <summary>One row in the right-side Now Playing panel. Tracks the cue node plus its current
/// playback position / duration as reported by the engine; the row's cancel button calls back
/// into the host (engine) to stop just this cue without touching the others.</summary>
public sealed partial class ActiveCueViewModel : ObservableObject
{
    public ActiveCueViewModel(CueNodeViewModel node, Guid cueId, Action<Guid> cancelCallback)
    {
        Node = node;
        CueId = cueId;
        _cancelCallback = cancelCallback;
    }

    public CueNodeViewModel Node { get; }

    /// <summary>The engine's cue id (matches the model's <c>CueNode.Id</c>). Used to dispatch
    /// per-cue stop without aliasing on the cue's tree position.</summary>
    public Guid CueId { get; }

    private readonly Action<Guid> _cancelCallback;

    [ObservableProperty]
    private long _positionMs;

    [ObservableProperty]
    private long _durationMs;

    /// <summary>Display string: <c>mm:ss / mm:ss (-mm:ss)</c> (or <c>h:mm:ss</c> for clips ≥ 1 h);
    /// the parenthesized part is the remaining time, omitted when duration is unknown.</summary>
    public string PositionDisplay => FormatPositionDisplay(PositionMs, DurationMs);

    /// <summary>0–100. Clamped; returns 0 when duration is unknown (live sources etc.).</summary>
    public double ProgressPercent
    {
        get
        {
            if (DurationMs <= 0) return 0;
            return Math.Clamp(PositionMs * 100.0 / DurationMs, 0, 100);
        }
    }

    /// <summary>Operator aid (mirrors the media deck's low-time clock warning): true when a finite
    /// cue is within <see cref="LowTimeWarningMs"/> of its end - drives the row's red readout so an
    /// ending cue draws the eye before it goes silent. False for unknown-duration (live) cues.</summary>
    public bool IsNearEnd => DurationMs > 0 && DurationMs - PositionMs <= LowTimeWarningMs;

    private const long LowTimeWarningMs = 10_000;

    public string CueNumberDisplay => string.IsNullOrWhiteSpace(Node.Number)
        ? string.Empty
        : Node.Number;

    public string CueLabel => Node.Label;

    partial void OnPositionMsChanged(long value)
    {
        _ = value;
        OnPropertyChanged(nameof(PositionDisplay));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(IsNearEnd));
    }

    partial void OnDurationMsChanged(long value)
    {
        _ = value;
        OnPropertyChanged(nameof(PositionDisplay));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(IsNearEnd));
    }

    [RelayCommand]
    private void Cancel() => _cancelCallback(CueId);

    /// <summary>Shared "pos / dur (-remaining)" formatter (also used by the group aggregate row).
    /// Remaining is omitted when duration is unknown (live sources etc.).</summary>
    internal static string FormatPositionDisplay(long positionMs, long durationMs)
    {
        var pos = FormatMs(positionMs);
        if (durationMs <= 0)
            return $"{pos} / {Resources.Strings.Dash}";
        var dur = FormatMs(durationMs);
        var remaining = FormatMs(Math.Max(0, durationMs - positionMs));
        return $"{pos} / {dur} (-{remaining})";
    }

    private static string FormatMs(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
