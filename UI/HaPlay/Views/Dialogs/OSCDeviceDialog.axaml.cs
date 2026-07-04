using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class OSCDeviceDialog : Window
{
    public OSCDeviceDialog()
    {
        InitializeComponent();
        DialogStatePersister.Attach(this, nameof(OSCDeviceDialog), MinWidth, MinHeight);
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
        Close(DataContext is OSCDeviceDialogViewModel { IsValid: true });
    }
}
