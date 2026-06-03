using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using HaPlay.Resources;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>Result returned by the cue list settings dialog. The caller is responsible for
/// applying these onto the active <see cref="CueListEditorViewModel"/> + persisting them as
/// part of the project save.</summary>
public sealed record CueListSettingsDialogResult(
    int PreRollCount,
    int MaxPreparedDecoders,
    CueTriggerMode DefaultTriggerMode,
    bool AutoRenumberOnInsert);

/// <summary>VM for the cue-list settings dialog (Phase 5.8.2). Moves the inline pre-roll
/// spinner off the toolbar and surfaces per-list knobs: the standby-decoder cap, the default
/// trigger mode applied to new cues, and an auto-renumber toggle for insert/reorder.</summary>
public sealed partial class CueListSettingsDialogViewModel : ViewModelBase
{
    public CueListSettingsDialogViewModel(int preRollCount, int maxPreparedDecoders, CueTriggerMode defaultTriggerMode, bool autoRenumber)
    {
        _preRollCount = preRollCount;
        _maxPreparedDecoders = maxPreparedDecoders;
        _defaultTriggerMode = defaultTriggerMode;
        _autoRenumberOnInsert = autoRenumber;
    }

    [ObservableProperty]
    private int _preRollCount;

    [ObservableProperty]
    private int _maxPreparedDecoders;

    [ObservableProperty]
    private CueTriggerMode _defaultTriggerMode;

    [ObservableProperty]
    private bool _autoRenumberOnInsert;

    public CueTriggerMode[] TriggerModes { get; } = System.Enum.GetValues<CueTriggerMode>();

    public string DialogTitle => Strings.CueListSettingsDialogTitle;
}
