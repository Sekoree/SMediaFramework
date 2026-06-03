using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class CueListSettingsDialog : Window
{
    public CueListSettingsDialog()
    {
        InitializeComponent();
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CueListSettingsDialogViewModel vm) return;
        Close(new CueListSettingsDialogResult(vm.PreRollCount, vm.MaxPreparedDecoders, vm.DefaultTriggerMode, vm.AutoRenumberOnInsert));
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
