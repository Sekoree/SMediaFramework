using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Resources;

namespace HaPlay.ViewModels.Dialogs;

public enum RenumberScope
{
    All,
    RootLevelOnly,
    SelectionOnly,
}

/// <summary>VM for the "Renumber…" dialog. Operators rarely live without a numbering scheme -
/// this lets them apply one in bulk: pick a start, a step, and what to renumber. Selection-only
/// mode is essential after drag-reorder.</summary>
public sealed partial class RenumberSelectionDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    private double _start = 1.0;

    [ObservableProperty]
    private double _step = 1.0;

    [ObservableProperty]
    private RenumberScope _scope = RenumberScope.All;

    public string DialogTitle => Strings.RenumberSelectionDialogTitle;

    public bool ScopeAll
    {
        get => Scope == RenumberScope.All;
        set { if (value) Scope = RenumberScope.All; }
    }

    public bool ScopeRoot
    {
        get => Scope == RenumberScope.RootLevelOnly;
        set { if (value) Scope = RenumberScope.RootLevelOnly; }
    }

    public bool ScopeSelection
    {
        get => Scope == RenumberScope.SelectionOnly;
        set { if (value) Scope = RenumberScope.SelectionOnly; }
    }

    partial void OnScopeChanged(RenumberScope value)
    {
        _ = value;
        OnPropertyChanged(nameof(ScopeAll));
        OnPropertyChanged(nameof(ScopeRoot));
        OnPropertyChanged(nameof(ScopeSelection));
    }
}

public sealed record RenumberSelectionDialogResult(double Start, double Step, RenumberScope Scope);
