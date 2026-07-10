using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HaPlay.Views.Dialogs;

/// <summary>Shown on app close when the project has unsaved changes (or control scripts that only live in the
/// scratch cache). Supersedes the older scripts-only prompt. Closing via the window chrome returns null, which
/// the caller treats as <see cref="UnsavedChangesChoice.Cancel"/>.</summary>
public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
    }

    private void SaveClick(object? sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Save);
    private void DiscardClick(object? sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Discard);
    private void CancelClick(object? sender, RoutedEventArgs e) => Close(UnsavedChangesChoice.Cancel);
}

public enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel,
}
