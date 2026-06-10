using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HaPlay.ViewModels;

/// <summary>
/// One visible toast (UI rewrite P1). Auto-dismisses after <see cref="MainViewModel"/>'s timeout
/// unless the operator pins it by clicking the body; ✕ always closes immediately.
/// </summary>
public sealed partial class ToastViewModel(ToastSeverity severity, string message, Action<ToastViewModel> close)
    : ObservableObject
{
    public ToastSeverity Severity { get; } = severity;
    public string Message { get; } = message;

    public bool IsInfo => Severity == ToastSeverity.Info;
    public bool IsWarning => Severity == ToastSeverity.Warning;
    public bool IsError => Severity == ToastSeverity.Error;

    /// <summary>Pinned toasts ignore the auto-dismiss deadline (operator wants to read/keep it).</summary>
    [ObservableProperty]
    private bool _isPinned;

    /// <summary>Auto-dismiss deadline; ignored once <see cref="IsPinned"/> is set.</summary>
    public long DeadlineTicks { get; init; }

    [RelayCommand]
    private void Close() => close(this);

    [RelayCommand]
    private void TogglePin() => IsPinned = !IsPinned;
}
