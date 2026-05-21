using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class AddPortAudioInputDialog : Window
{
    public AddPortAudioInputDialog()
    {
        InitializeComponent();
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AddPortAudioInputDialogViewModel vm)
            return;
        var r = vm.TryCommit();
        if (r is null)
            return;
        Close(r);
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
