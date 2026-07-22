using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HaPlay.ViewModels;

/// <summary>
/// Ctrl+F cue search. Deliberately jump-to-match navigation, not a filter: during a show the
/// operator must keep seeing the full list (standby/transport context), so matches are selected,
/// expanded into view and scrolled to instead of hiding non-matches. Enter cycles forward,
/// Shift+Enter backward, Esc clears.
/// </summary>
public partial class CuePlayerViewModel
{
    /// <summary>Raised when search wants the view to select + scroll a row into view. Index-path
    /// resolution lives in the view because it owns the TreeDataGrid source.</summary>
    public event Action<CueNodeViewModel>? CueSearchNavigationRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCueSearchText))]
    private string _cueSearchText = string.Empty;

    private int _cueSearchMatchIndex = -1;

    public bool HasCueSearchText => !string.IsNullOrWhiteSpace(CueSearchText);

    [ObservableProperty]
    private string? _cueSearchStatus;

    partial void OnCueSearchTextChanged(string value)
    {
        _ = value;
        _cueSearchMatchIndex = -1;
        NavigateCueSearch(step: 1, fromScratch: true);
    }

    [RelayCommand]
    private void FindNextCueMatch() => NavigateCueSearch(step: 1, fromScratch: false);

    [RelayCommand]
    private void FindPreviousCueMatch() => NavigateCueSearch(step: -1, fromScratch: false);

    [RelayCommand]
    private void ClearCueSearch()
    {
        CueSearchText = string.Empty;
        CueSearchStatus = null;
    }

    private void NavigateCueSearch(int step, bool fromScratch)
    {
        if (!HasCueSearchText)
        {
            _cueSearchMatchIndex = -1;
            CueSearchStatus = null;
            return;
        }

        var needle = CueSearchText.Trim();
        var matches = EnumerateAllCueNodes().Where(n => MatchesCueSearch(n, needle)).ToList();
        if (matches.Count == 0)
        {
            _cueSearchMatchIndex = -1;
            CueSearchStatus = "No matches";
            return;
        }

        _cueSearchMatchIndex = fromScratch || _cueSearchMatchIndex < 0
            ? 0
            : (((_cueSearchMatchIndex + step) % matches.Count) + matches.Count) % matches.Count;
        var match = matches[_cueSearchMatchIndex];
        CueSearchStatus = $"{_cueSearchMatchIndex + 1} of {matches.Count}";

        ExpandAncestors(match);
        UpdateSelection([match]);
        CueSearchNavigationRequested?.Invoke(match);
    }

    private static bool MatchesCueSearch(CueNodeViewModel node, string needle) =>
        (node.Label?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
        || (node.Number?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);

    private void ExpandAncestors(CueNodeViewModel node)
    {
        if (SelectedCueList is null)
            return;
        var path = new List<CueNodeViewModel>();
        if (FindPathTo(SelectedCueList.Nodes, node, path))
        {
            foreach (var ancestor in path)
                ancestor.IsExpanded = true;
        }
    }

    /// <summary>Collects the ancestor chain (root → parent) of <paramref name="target"/> into
    /// <paramref name="path"/>; false when the node isn't in this subtree.</summary>
    private static bool FindPathTo(
        IEnumerable<CueNodeViewModel> nodes, CueNodeViewModel target, List<CueNodeViewModel> path)
    {
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node, target))
                return true;
            path.Add(node);
            if (FindPathTo(node.Children, target, path))
                return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }
}
