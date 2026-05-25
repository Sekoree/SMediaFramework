using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class RenameCueDialog : Window
{
    public RenameCueDialog()
    {
        InitializeComponent();
        // Focus the Label box on open — Number is usually fine; Label is what operators want to
        // edit most often.
        Opened += (_, _) => LabelBox.SelectAll();
        Opened += (_, _) => LabelBox.Focus();
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RenameCueDialogViewModel vm) return;
        Close(new RenameCueDialogResult(vm.Number ?? string.Empty, vm.Label ?? string.Empty));
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
