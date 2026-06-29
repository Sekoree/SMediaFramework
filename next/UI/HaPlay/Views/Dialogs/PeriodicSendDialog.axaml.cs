using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class PeriodicSendDialog : Window
{
    public PeriodicSendDialog()
    {
        InitializeComponent();
        DialogStatePersister.Attach(this, nameof(PeriodicSendDialog), MinWidth, MinHeight);
    }

    private void CancelClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(false);
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(DataContext is PeriodicSendDialogViewModel { IsValid: true });
    }
}
