using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace HaPlay.ViewModels;

/// <summary>
/// One script-editor window's view-model: PINS a single script row (one script per window - the old
/// shared window followed the workspace list selection, which silently switched the open editor).
/// The heavy editing machinery (text buffer, save, learn, diagnostics) still lives on
/// <see cref="ControlWorkspaceViewModel"/> keyed to its selected row, so this shim keeps the
/// workspace selection on the pinned row while the window is in use (the view re-asserts it on
/// window activation) and forwards the selection-scoped members under their existing names -
/// the editor XAML binds to the same properties it always did.
/// </summary>
public sealed partial class ScriptEditorWindowViewModel : ObservableObject
{
    private string _textCache;

    public ScriptEditorWindowViewModel(ControlWorkspaceViewModel workspace, ControlScriptRowViewModel row)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        SelectedScriptRow = row ?? throw new ArgumentNullException(nameof(row));
        PinSelection();
        _textCache = workspace.SelectedScriptText;
        workspace.PropertyChanged += OnWorkspacePropertyChanged;
    }

    public ControlWorkspaceViewModel Workspace { get; }

    /// <summary>The pinned row - named like the workspace property so the editor XAML is unchanged.</summary>
    public ControlScriptRowViewModel SelectedScriptRow { get; }

    public bool HasSelectedScript => true;

    /// <summary>Drives this window's "unsaved changes" bar. Gated on the pinned row actually being the
    /// workspace selection so a backgrounded editor never shows another script's dirty state (it re-pins on
    /// activation, which restores the correct value).</summary>
    public bool IsSelectedScriptDirty => IsPinnedSelected && Workspace.IsSelectedScriptDirty;

    /// <summary>Re-selects the pinned script in the workspace (called on window activation and before
    /// edits) so the selection-scoped machinery - buffer, save, learn, diagnostics - targets it.</summary>
    public void PinSelection()
    {
        if (!ReferenceEquals(Workspace.SelectedScriptRow, SelectedScriptRow))
            Workspace.SelectedScriptRow = SelectedScriptRow;
    }

    private bool IsPinnedSelected => ReferenceEquals(Workspace.SelectedScriptRow, SelectedScriptRow);

    /// <summary>The pinned script's edit buffer. While another script is selected in the workspace the
    /// last-known text is served from a cache so this window never displays the other script; edits
    /// re-assert the pinned selection first.</summary>
    public string SelectedScriptText
    {
        get => IsPinnedSelected ? Workspace.SelectedScriptText : _textCache;
        set
        {
            PinSelection();
            Workspace.SelectedScriptText = value;
        }
    }

    // Selection-scoped pass-throughs (valid whenever the pinned row is selected - always the case
    // while the user interacts with this window).
    public IReadOnlyList<S.Control.ControlScriptScope> ScriptScopeOptions => Workspace.ScriptScopeOptions;
    public IReadOnlyList<S.Control.ControlScriptFailureMode> ScriptFailureModeOptions => Workspace.ScriptFailureModeOptions;
    public string ScriptEditorStatus => Workspace.ScriptEditorStatus;
    public string LearnButtonText => Workspace.LearnButtonText;
    public bool HasLearnCandidate => Workspace.HasLearnCandidate;
    public ControlLearnCandidateViewModel? LearnCandidate => Workspace.LearnCandidate;
    public ObservableCollection<ControlScriptDiagnosticRowViewModel> ScriptDiagnostics => Workspace.ScriptDiagnostics;
    public string ExportedFunctionsSummary => Workspace.ExportedFunctionsSummary;

    public System.Windows.Input.ICommand ToggleLearnCommand => Workspace.ToggleLearnCommand;
    public System.Windows.Input.ICommand ConfirmLearnCommand => Workspace.ConfirmLearnCommand;
    public System.Windows.Input.ICommand CancelLearnCommand => Workspace.CancelLearnCommand;
    public System.Windows.Input.ICommand SaveSelectedScriptCommand => Workspace.SaveSelectedScriptCommand;
    public System.Windows.Input.ICommand DiscardSelectedScriptChangesCommand => Workspace.DiscardSelectedScriptChangesCommand;

    public void Detach() => Workspace.PropertyChanged -= OnWorkspacePropertyChanged;

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ControlWorkspaceViewModel.SelectedScriptText):
                if (IsPinnedSelected)
                {
                    _textCache = Workspace.SelectedScriptText;
                    OnPropertyChanged(nameof(SelectedScriptText));
                }
                break;
            case nameof(ControlWorkspaceViewModel.IsSelectedScriptDirty):
                OnPropertyChanged(nameof(IsSelectedScriptDirty));
                break;
            case nameof(ControlWorkspaceViewModel.ScriptEditorStatus):
            case nameof(ControlWorkspaceViewModel.LearnButtonText):
            case nameof(ControlWorkspaceViewModel.HasLearnCandidate):
            case nameof(ControlWorkspaceViewModel.LearnCandidate):
            case nameof(ControlWorkspaceViewModel.ExportedFunctionsSummary):
                OnPropertyChanged(e.PropertyName);
                break;
        }
    }
}
