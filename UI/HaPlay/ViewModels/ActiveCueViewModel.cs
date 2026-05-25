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

    /// <summary>Display string: <c>mm:ss / mm:ss</c> (or <c>h:mm:ss</c> for clips ≥ 1 h).</summary>
    public string PositionDisplay
    {
        get
        {
            var pos = FormatMs(PositionMs);
            var dur = DurationMs > 0 ? FormatMs(DurationMs) : Resources.Strings.EmDash;
            return $"{pos} / {dur}";
        }
    }

    /// <summary>0–100. Clamped; returns 0 when duration is unknown (live sources etc.).</summary>
    public double ProgressPercent
    {
        get
        {
            if (DurationMs <= 0) return 0;
            return Math.Clamp(PositionMs * 100.0 / DurationMs, 0, 100);
        }
    }

    public string CueNumberDisplay => string.IsNullOrWhiteSpace(Node.Number)
        ? string.Empty
        : Node.Number;

    public string CueLabel => Node.Label;

    partial void OnPositionMsChanged(long value)
    {
        _ = value;
        OnPropertyChanged(nameof(PositionDisplay));
        OnPropertyChanged(nameof(ProgressPercent));
    }

    partial void OnDurationMsChanged(long value)
    {
        _ = value;
        OnPropertyChanged(nameof(PositionDisplay));
        OnPropertyChanged(nameof(ProgressPercent));
    }

    [RelayCommand]
    private void Cancel() => _cancelCallback(CueId);

    private static string FormatMs(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
