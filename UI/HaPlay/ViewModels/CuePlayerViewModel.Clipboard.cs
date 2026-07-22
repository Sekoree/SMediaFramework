using System.Text.Json;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Models;

namespace HaPlay.ViewModels;

/// <summary>
/// Cue copy/paste. Cues travel as a version-stamped JSON envelope in plain clipboard text, so
/// they paste across cue lists, projects, and app instances. Copy works even in show mode
/// (non-destructive); paste sits behind the edit gate like every other tree mutation.
/// </summary>
public partial class CuePlayerViewModel
{
    [RelayCommand(CanExecute = nameof(CanCopySelectedCues))]
    private async Task CopySelectedCuesAsync()
    {
        var clipboard = TryGetMainWindow()?.Clipboard;
        if (clipboard is null)
            return;

        // Whole multi-selection, minus nodes a selected ancestor group already carries.
        var targets = EffectiveSelection().ToList();
        targets.RemoveAll(node => targets.Any(other =>
            !ReferenceEquals(other, node) && ContainsNode(other.Children, node)));
        if (targets.Count == 0)
            return;

        var document = new CueClipboardDocument
        {
            Cues = OrderInTreeOrder(targets).Select(static n => n.ToModel()).ToList(),
        };
        var json = JsonSerializer.Serialize(document, CueListJsonContext.Default.CueClipboardDocument);
        await clipboard.SetTextAsync(json);
        StatusMessage = targets.Count == 1
            ? $"Copied cue {CueDisplay(targets[0])}."
            : $"Copied {targets.Count} cues.";
    }

    private bool CanCopySelectedCues() => SelectedCueNode is not null;

    [RelayCommand(CanExecute = nameof(CanPasteCues))]
    private async Task PasteCuesAsync()
    {
        if (SelectedCueList is null)
            return;
        var clipboard = TryGetMainWindow()?.Clipboard;
        if (clipboard is null)
            return;

        string? text;
        try
        {
            text = await clipboard.TryGetTextAsync();
        }
        catch (Exception)
        {
            // Clipboard access is best-effort (another app may hold it); a paste that finds
            // nothing must never take the cue player down.
            return;
        }
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("\"Cues\"", StringComparison.Ordinal))
            return;

        CueClipboardDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(text, CueListJsonContext.Default.CueClipboardDocument);
        }
        catch (JsonException)
        {
            document = null;
        }
        if (document is not { Version: CueClipboardDocument.CurrentVersion } || document.Cues.Count == 0)
        {
            StatusMessage = "Clipboard has no cues.";
            return;
        }

        // Paste lands after the selected cue (same parent); no selection → end of list. Ids are
        // rotated like Duplicate so a paste never collides with the originals.
        IList<CueNodeViewModel> parent = SelectedCueList.Nodes;
        var insertAt = parent.Count;
        if (SelectedCueNode is { } anchor
            && FindParentCollection(SelectedCueList.Nodes, anchor) is IList<CueNodeViewModel> anchorParent)
        {
            parent = anchorParent;
            insertAt = anchorParent.IndexOf(anchor) + 1;
        }

        var pasted = new List<CueNodeViewModel>();
        foreach (var model in document.Cues)
        {
            var vm = CueNodeViewModel.FromModel(CloneCueNodeWithNewIds(model), ResolveOutputLine);
            parent.Insert(Math.Clamp(insertAt++, 0, parent.Count), vm);
            pasted.Add(vm);
        }

        UpdateSelection(pasted);
        RefreshCueTargetDisplays();
        GoCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        StatusMessage = pasted.Count == 1
            ? $"Pasted cue {CueDisplay(pasted[0])}."
            : $"Pasted {pasted.Count} cues.";
    }

    private bool CanPasteCues() => IsCueEditMode && SelectedCueList is not null;
}
