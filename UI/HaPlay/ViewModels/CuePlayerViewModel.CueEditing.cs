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

public partial class CuePlayerViewModel
{
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

        // Unique cue numbers (#27): number-based references (jump targets) need unambiguous numbers,
        // so a rename that would duplicate an existing number anywhere in the list is rejected.
        if (!string.IsNullOrWhiteSpace(result.Number)
            && EnumerateAllCueNodes().Any(c => !ReferenceEquals(c, SelectedCueNode)
                && string.Equals(c.Number?.Trim(), result.Number.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = Strings.Format(nameof(Strings.CueNumberDuplicateFormat), result.Number.Trim());
            return;
        }

        var oldDisplay = CueDisplay(SelectedCueNode);
        SelectedCueNode.Number = result.Number;
        SelectedCueNode.Label = result.Label;
        RefreshCueTargetDisplays();
        OnPropertyChanged(nameof(SelectedCueDrawerTitle));
        StatusMessage = Strings.Format(nameof(Strings.RenamedCueStatusFormat), oldDisplay, CueDisplay(SelectedCueNode));
    }

    private bool CanRenameSelectedCue() => SelectedCueNode is not null;

    /// <summary>Open the cue list settings dialog for default trigger mode and auto-renumber.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenCueListSettings))]
    private async Task OpenCueListSettingsAsync()
    {
        if (SelectedCueList is null) return;
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var dialogVm = new Dialogs.CueListSettingsDialogViewModel(
            SelectedCueList.DefaultTriggerMode,
            SelectedCueList.AutoRenumberOnInsert);
        var dialog = new Views.Dialogs.CueListSettingsDialog { DataContext = dialogVm };
        var result = await dialog.ShowDialog<Dialogs.CueListSettingsDialogResult?>(owner);
        if (result is null) return;

        SelectedCueList.DefaultTriggerMode = result.DefaultTriggerMode;
        SelectedCueList.AutoRenumberOnInsert = result.AutoRenumberOnInsert;
        RebuildUpcomingCues();
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
    /// here. No-op at the top of the parent (operator's expected behaviour - they get to feel
    /// the boundary).</summary>
    [RelayCommand(CanExecute = nameof(CanMoveSelectedCue))]
    private void MoveSelectedCueUp() => MoveSelectedCue(-1);

    [RelayCommand(CanExecute = nameof(CanMoveSelectedCue))]
    private void MoveSelectedCueDown() => MoveSelectedCue(+1);

    private bool CanMoveSelectedCue() => IsCueEditMode && SelectedCueNode is not null && SelectedCueList is not null;

    private void MoveSelectedCue(int delta)
    {
        if (SelectedCueNode is null || SelectedCueList is null) return;

        // Applies to the whole multi-selection: each selected row shifts one slot within its own
        // parent. A contiguous block keeps its relative order and piles up at the boundary
        // instead of wrapping (the operator gets to feel the edge, same as single-select).
        var primary = SelectedCueNode;
        var entries = new List<(IList<CueNodeViewModel> Parent, int Index, CueNodeViewModel Node)>();
        foreach (var node in EffectiveSelection())
        {
            if (FindParentCollection(SelectedCueList.Nodes, node) is IList<CueNodeViewModel> parent)
                entries.Add((parent, parent.IndexOf(node), node));
        }
        if (entries.Count == 0) return;

        var moved = false;
        foreach (var group in entries.GroupBy(e => e.Parent))
        {
            var parent = group.Key;
            if (delta < 0)
            {
                var blockedTop = 0;
                foreach (var entry in group.OrderBy(e => e.Index))
                {
                    var idx = parent.IndexOf(entry.Node);
                    if (idx <= blockedTop)
                    {
                        blockedTop = idx + 1;
                        continue;
                    }
                    parent.RemoveAt(idx);
                    parent.Insert(idx - 1, entry.Node);
                    moved = true;
                }
            }
            else
            {
                var blockedBottom = parent.Count - 1;
                foreach (var entry in group.OrderByDescending(e => e.Index))
                {
                    var idx = parent.IndexOf(entry.Node);
                    if (idx >= blockedBottom)
                    {
                        blockedBottom = idx - 1;
                        continue;
                    }
                    parent.RemoveAt(idx);
                    parent.Insert(idx + 1, entry.Node);
                    moved = true;
                }
            }
        }

        if (!moved) return;
        SelectedCueNode = primary;
        MaybeRenumberAfterStructureChange();
        RefreshCueTargetDisplays();
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
        RefreshCueTargetDisplays();
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

    /// <summary>Deep-copy every selected cue with fresh ids, inserting each copy immediately after
    /// its original. Routes, placements, and group-children all clone. Bound to Ctrl+D. The clones
    /// become the new selection.</summary>
    [RelayCommand(CanExecute = nameof(CanDuplicateSelectedCue))]
    private void DuplicateSelectedCue()
    {
        if (SelectedCueNode is null || SelectedCueList is null) return;

        // Applies to the whole multi-selection; a node whose selected ancestor group is also in
        // the selection is skipped (cloning the group already clones it).
        var targets = EffectiveSelection().ToList();
        targets.RemoveAll(node => targets.Any(other =>
            !ReferenceEquals(other, node) && ContainsNode(other.Children, node)));

        var clones = new List<CueNodeViewModel>();
        foreach (var node in OrderInTreeOrder(targets))
        {
            if (FindParentCollection(SelectedCueList.Nodes, node) is not IList<CueNodeViewModel> parent)
                continue;

            // Deep-copy via the model layer. `ToModel()` projects through fresh `.Select(...).ToList()`
            // collections for routes / placements / children, so the snapshot doesn't share list
            // references with the original VM. `CloneCueNodeWithNewIds` then rotates ids (a `with` on
            // a record only does a shallow copy - we'd otherwise share AudioRoutes / VideoPlacements
            // lists between original and copy). `FromModel` rebuilds fresh VM collections from the
            // cloned snapshot, so no list reference is shared with the original cue.
            var snapshot = node.ToModel();
            var copy = CloneCueNodeWithNewIds(snapshot);
            var copyVm = CueNodeViewModel.FromModel(copy, ResolveOutputLine);
            parent.Insert(parent.IndexOf(node) + 1, copyVm);
            clones.Add(copyVm);
        }

        if (clones.Count == 0) return;
        UpdateSelection(clones);
        RefreshCueTargetDisplays();
        if (clones.Count > 1)
            StatusMessage = $"Duplicated {clones.Count} cues.";
    }

    private bool CanDuplicateSelectedCue() => SelectedCueNode is not null && SelectedCueList is not null;

    /// <summary>Phase 5.8.1 - clicking a color swatch sets the tag on every selected cue
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
    /// sub-numbering scheme - `1`, `1.1`, `1.2`, `2`, … - preserving the visible cue hierarchy.</summary>
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

        RefreshCueTargetDisplays();
        StatusMessage = Strings.Format(nameof(Strings.RenumberedStatusFormat), renumbered);
    }

    private bool CanRenumber() => SelectedCueList is not null && SelectedCueList.Nodes.Count > 0;

    /// <summary>One-click canonical numbering: root rows become 1, 2, 3… and each nested group receives
    /// hierarchical child numbers such as 2.1, 2.2 and 2.3. Stable-ID cue links remain unchanged.</summary>
    [RelayCommand(CanExecute = nameof(CanRenumber))]
    private void ReorganizeCueList()
    {
        if (SelectedCueList is null)
            return;
        var renumbered = RenumberSubtree(SelectedCueList.Nodes, start: 1, step: 1, recurseIntoGroups: true);
        RefreshCueTargetDisplays();
        StatusMessage = Strings.Format(nameof(Strings.ReorganizedCueListStatusFormat), renumbered);
    }

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
        if (SelectedActionCue is not { } actionCue || SelectedActionEndpoint is null)
            return;
        actionCue.EndpointIdText = SelectedActionEndpoint.Id.ToString();
        OnPropertyChanged(nameof(SelectedActionEndpointSummary));
    }

    private bool CanAssignSelectedActionEndpoint() =>
        SelectedActionCue is not null && SelectedActionEndpoint is not null;

    [RelayCommand(CanExecute = nameof(CanEditActionCue))]
    private async Task EditActionCueAsync()
    {
        if (SelectedActionCue is not { } cue)
            return;

        var owner = TryGetMainWindow();
        if (owner is null)
            return;

        var dialogVm = new Dialogs.ActionCueBuilderDialogViewModel();
        var actionKind = Enum.TryParse<CueActionKind>(cue.Extra, out var parsed)
            ? parsed
            : CueActionKind.OSCOut;
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

    private bool CanEditActionCue() => SelectedActionCue is not null;
}
