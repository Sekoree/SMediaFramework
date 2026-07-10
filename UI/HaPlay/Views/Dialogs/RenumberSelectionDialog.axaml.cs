using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class RenumberSelectionDialog : Window
{
    public RenumberSelectionDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RenumberSelectionDialogViewModel vm) return;
        Close(new RenumberSelectionDialogResult(vm.Start, vm.Step, vm.Scope));
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
