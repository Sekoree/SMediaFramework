using CommunityToolkit.Mvvm.ComponentModel;

namespace HaPlay.ViewModels;

/// <summary>
/// One not-yet-playing member of a group's automatic chain in the Now Playing panel: shows the
/// cue's number/label plus a ticking "in m:ss" countdown to its automatic start (#29). ETA -1
/// means the chain timeline is unknown (some prior duration missing) - renders as a dash.
/// </summary>
public sealed partial class UpcomingChainItemViewModel : ObservableObject
{
    public UpcomingChainItemViewModel(CueNodeViewModel node) => Node = node;

    public CueNodeViewModel Node { get; }

    /// <summary>Milliseconds until this cue fires automatically; -1 when unknown.</summary>
    [ObservableProperty]
    private long _etaMs = -1;

    public string CountdownDisplay => EtaMs < 0
        ? Resources.Strings.Dash
        : Resources.Strings.Format(nameof(Resources.Strings.NowPlayingStartsInFormat),
            ActiveCueViewModel.FormatMs(EtaMs));

    partial void OnEtaMsChanged(long value)
    {
        _ = value;
        OnPropertyChanged(nameof(CountdownDisplay));
    }
}
