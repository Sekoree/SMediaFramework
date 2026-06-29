using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using HaPlay.Resources;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>Result returned by the cue list settings dialog. The caller is responsible for
/// applying these onto the active <see cref="CueListEditorViewModel"/> + persisting them as
/// part of the project save.</summary>
public sealed record CueListSettingsDialogResult(
    CueTriggerMode DefaultTriggerMode,
    bool AutoRenumberOnInsert);

/// <summary>VM for the cue-list settings dialog.</summary>
public sealed partial class CueListSettingsDialogViewModel : ViewModelBase
{
    public CueListSettingsDialogViewModel(CueTriggerMode defaultTriggerMode, bool autoRenumber)
    {
        _defaultTriggerMode = defaultTriggerMode;
        _autoRenumberOnInsert = autoRenumber;
    }

    [ObservableProperty]
    private CueTriggerMode _defaultTriggerMode;

    [ObservableProperty]
    private bool _autoRenumberOnInsert;

    public CueTriggerMode[] TriggerModes { get; } = System.Enum.GetValues<CueTriggerMode>();

    public string DialogTitle => Strings.CueListSettingsDialogTitle;
}
