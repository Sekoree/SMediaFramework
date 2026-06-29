using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class SubtitleSelectionDialog : Window
{
    public SubtitleSelectionDialog()
    {
        InitializeComponent();
        DialogStatePersister.Attach(this, nameof(SubtitleSelectionDialog), MinWidth, MinHeight);
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SubtitleSelectionDialogViewModel vm)
            return;
        Close(vm.BuildSelections());
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
