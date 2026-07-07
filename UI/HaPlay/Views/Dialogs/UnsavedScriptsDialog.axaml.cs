using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HaPlay.Views.Dialogs;

/// <summary>Shown on app close when the operator authored scripts before ever saving the project (they live
/// only in the scratch cache — see <see cref="ViewModels.ControlWorkspaceViewModel.HasUnsavedScratchScripts"/>).
/// Closing via the window chrome returns null, which the caller treats as <see cref="UnsavedScriptsChoice.Cancel"/>.</summary>
public partial class UnsavedScriptsDialog : Window
{
    public UnsavedScriptsDialog() => InitializeComponent();

    private void SaveClick(object? sender, RoutedEventArgs e) => Close(UnsavedScriptsChoice.Save);
    private void DiscardClick(object? sender, RoutedEventArgs e) => Close(UnsavedScriptsChoice.Discard);
    private void CancelClick(object? sender, RoutedEventArgs e) => Close(UnsavedScriptsChoice.Cancel);
}

public enum UnsavedScriptsChoice
{
    Save,
    Discard,
    Cancel,
}
