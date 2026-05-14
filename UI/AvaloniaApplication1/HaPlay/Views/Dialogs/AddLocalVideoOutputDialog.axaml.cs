using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.Models;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class AddLocalVideoOutputDialog : Window
{
    public AddLocalVideoOutputDialog()
    {
        InitializeComponent();
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddLocalVideoOutputDialogViewModel vm)
            return;
        var r = vm.TryCommit();
        if (r is null)
            return;
        Close(r);
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
