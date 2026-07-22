using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HaPlay.ViewModels;

/// <summary>
/// One visible toast (UI rewrite P1). Auto-dismisses after <see cref="MainViewModel"/>'s timeout
/// unless the operator pins it by clicking the body; ✕ always closes immediately. A toast may
/// carry a one-shot action button (e.g. "Undo" after removing cues) - running the action closes
/// the toast so it can't fire twice.
/// </summary>
public sealed partial class ToastViewModel(
    ToastSeverity severity,
    string message,
    Action<ToastViewModel> close,
    string? actionLabel = null,
    Action? action = null)
    : ObservableObject
{
    private readonly Action? _action = action;

    public ToastSeverity Severity { get; } = severity;
    public string Message { get; } = message;

    public bool IsInfo => Severity == ToastSeverity.Info;
    public bool IsWarning => Severity == ToastSeverity.Warning;
    public bool IsError => Severity == ToastSeverity.Error;

    public string? ActionLabel { get; } = actionLabel;

    public bool HasAction => ActionLabel is not null && _action is not null;

    /// <summary>Pinned toasts ignore the auto-dismiss deadline (operator wants to read/keep it).</summary>
    [ObservableProperty]
    private bool _isPinned;

    /// <summary>Auto-dismiss deadline; ignored once <see cref="IsPinned"/> is set. Settable so a
    /// repeated identical message refreshes the existing toast instead of stacking a duplicate.</summary>
    public long DeadlineTicks { get; set; }

    [RelayCommand]
    private void Close() => close(this);

    [RelayCommand]
    private void TogglePin() => IsPinned = !IsPinned;

    [RelayCommand]
    private void RunAction()
    {
        _action?.Invoke();
        close(this);
    }
}
