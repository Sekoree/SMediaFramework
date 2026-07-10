using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HaPlay.ViewModels;

/// <summary>
/// UI rewrite P4 (plan §3.2): one Now Playing row aggregating every active cue that lives under
/// the same group node. Shows group-level progress (position/duration of the longest child
/// timeline), expands to the per-cue rows, and its ✕ cancels all children.
/// </summary>
public sealed partial class ActiveGroupViewModel : ObservableObject
{
    public ActiveGroupViewModel(CueNodeViewModel groupNode)
    {
        GroupNode = groupNode;
        Children.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null)
                foreach (var removed in e.OldItems.OfType<ActiveCueViewModel>())
                    removed.PropertyChanged -= OnChildProgressChanged;
            if (e.NewItems is not null)
                foreach (var added in e.NewItems.OfType<ActiveCueViewModel>())
                    added.PropertyChanged += OnChildProgressChanged;
            RecomputeAggregate();
        };
    }

    public CueNodeViewModel GroupNode { get; }

    public Guid GroupId => GroupNode.Id;

    public ObservableCollection<ActiveCueViewModel> Children { get; } = new();

    /// <summary>Collapsed by default — the aggregate row is the at-a-glance surface; expanding
    /// reveals the per-cue rows (the pre-P4 flat list).</summary>
    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _positionDisplay = "";

    /// <summary>Low-time warning for the aggregate: the longest child timeline is within 10 s of
    /// its end (same threshold as <see cref="ActiveCueViewModel.IsNearEnd"/>).</summary>
    [ObservableProperty]
    private bool _isNearEnd;

    /// <summary>Longest child duration — the timeline the aggregate progress runs on.</summary>
    public long LongestDurationMs { get; private set; }

    private void OnChildProgressChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ActiveCueViewModel.PositionMs) or nameof(ActiveCueViewModel.DurationMs))
            RecomputeAggregate();
    }

    private void RecomputeAggregate()
    {
        long maxPos = 0, maxDur = 0;
        foreach (var child in Children)
        {
            maxPos = Math.Max(maxPos, child.PositionMs);
            maxDur = Math.Max(maxDur, child.DurationMs);
        }

        LongestDurationMs = maxDur;
        ProgressPercent = maxDur > 0 ? Math.Clamp(maxPos * 100.0 / maxDur, 0, 100) : 0;
        PositionDisplay = ActiveCueViewModel.FormatPositionDisplay(maxPos, maxDur);
        IsNearEnd = maxDur > 0 && maxDur - maxPos <= 10_000;
    }

    [RelayCommand]
    private void CancelAll()
    {
        foreach (var child in Children.ToArray())
            child.CancelCommand.Execute(null);
    }
}
