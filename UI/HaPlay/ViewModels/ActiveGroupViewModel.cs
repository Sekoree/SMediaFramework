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

    /// <summary>Collapsed by default - the aggregate row is the at-a-glance surface; expanding
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

    /// <summary>Longest child duration - the timeline the aggregate progress runs on.</summary>
    public long LongestDurationMs { get; private set; }

    private void OnChildProgressChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ActiveCueViewModel.PositionMs) or nameof(ActiveCueViewModel.DurationMs))
            RecomputeAggregate();
    }

    /// <summary>Chain members that will fire automatically after the currently-playing one, each
    /// with a ticking time-until-start. Rendered under the active children when expanded - this is
    /// the per-group replacement for the panel-wide "Upcoming" section.</summary>
    public ObservableCollection<UpcomingChainItemViewModel> UpcomingItems { get; } = new();

    private void RecomputeAggregate()
    {
        // Chain-aware timeline (#29): a sequential group (song1 → song2 → …) runs on the SUMMED chain
        // duration, with the position projected as "durations of the chain items already passed + the
        // active item's position". Falls back to the longest-child view (and plain count-up when no
        // durations are known) - exactly the requested semantics. One chain walk (same semantics as
        // CueNodeViewModel.SequentialChainDurationMs) feeds both the aggregate and the upcoming rows.
        var chain = new List<(CueNodeViewModel Node, long StartMs)>();
        if (GroupNode.GroupFireMode == CueGroupFireMode.FirstCueOnly)
        {
            long offset = 0;
            var started = false;
            foreach (var node in GroupNode.Children)
            {
                if (node.Kind == CueNodeKind.Comment)
                    continue;
                if (started && node.TriggerMode is not (CueTriggerMode.AutoFollow or CueTriggerMode.AutoContinue))
                    break; // a Manual child needs its own GO - the automatic chain ends here
                started = true;
                chain.Add((node, offset));
                offset += node.ChainContributionMs;
            }
        }

        var chainTotal = GroupNode.RolledDurationMs;
        var lastActiveIndex = -1;
        var allActiveOnChain = true;
        long projected = 0;
        foreach (var active in Children)
        {
            var index = chain.FindIndex(c => c.Node.Id == active.CueId);
            if (index < 0)
            {
                // Active child outside the automatic chain (simultaneous/manual fire): the chain
                // timeline doesn't describe this group's playback - use the longest-child view.
                allActiveOnChain = false;
                continue;
            }

            lastActiveIndex = Math.Max(lastActiveIndex, index);
            projected = Math.Max(projected, chain[index].StartMs + active.PositionMs);
        }

        if (chainTotal > 0 && lastActiveIndex >= 0 && allActiveOnChain)
        {
            LongestDurationMs = chainTotal;
            ProgressPercent = Math.Clamp(projected * 100.0 / chainTotal, 0, 100);
            PositionDisplay = ActiveCueViewModel.FormatPositionDisplay(projected, chainTotal);
            IsNearEnd = chainTotal - projected <= 10_000;
        }
        else
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

        // Upcoming rows: everything after the last active chain member that isn't itself playing.
        // ETA only when the chain timeline is known; -1 renders as a dash.
        var desired = new List<(CueNodeViewModel Node, long EtaMs)>();
        if (lastActiveIndex >= 0)
        {
            for (var i = lastActiveIndex + 1; i < chain.Count; i++)
            {
                var (node, startMs) = chain[i];
                if (Children.Any(c => c.CueId == node.Id))
                    continue;
                desired.Add((node, chainTotal > 0 ? Math.Max(0, startMs - projected) : -1));
            }
        }

        ReconcileUpcoming(desired);
    }

    /// <summary>In-place update of <see cref="UpcomingItems"/>: same node sequence just refreshes the
    /// countdowns (progress ticks arrive several times a second - no row churn), a changed sequence
    /// rebuilds the list.</summary>
    private void ReconcileUpcoming(List<(CueNodeViewModel Node, long EtaMs)> desired)
    {
        var sameShape = UpcomingItems.Count == desired.Count;
        if (sameShape)
        {
            for (var i = 0; i < desired.Count; i++)
            {
                if (!ReferenceEquals(UpcomingItems[i].Node, desired[i].Node))
                {
                    sameShape = false;
                    break;
                }
            }
        }

        if (!sameShape)
        {
            UpcomingItems.Clear();
            foreach (var (node, _) in desired)
                UpcomingItems.Add(new UpcomingChainItemViewModel(node));
        }

        for (var i = 0; i < desired.Count; i++)
            UpcomingItems[i].EtaMs = desired[i].EtaMs;
    }

    [RelayCommand]
    private void CancelAll()
    {
        foreach (var child in Children.ToArray())
            child.CancelCommand.Execute(null);
    }
}
