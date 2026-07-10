using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.Models;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class AddNDIOutputDialog : Window
{
    public AddNDIOutputDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
        DialogStatePersister.Attach(this, nameof(AddNDIOutputDialog), MinWidth, MinHeight);
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddNDIOutputDialogViewModel vm)
            return;
        var r = vm.TryCommit();
        if (r is null)
            return;
        Close(r);
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
