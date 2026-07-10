using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.Models;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class AddPortAudioOutputDialog : Window
{
    public AddPortAudioOutputDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
        DialogStatePersister.Attach(this, nameof(AddPortAudioOutputDialog), MinWidth, MinHeight);
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddPortAudioOutputDialogViewModel vm)
            return;
        var r = vm.TryCommit();
        if (r is null)
            return;
        Close(r);
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
