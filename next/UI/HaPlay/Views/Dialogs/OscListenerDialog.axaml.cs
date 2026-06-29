using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class OscListenerDialog : Window
{
    public OscListenerDialog()
    {
        InitializeComponent();
        DialogStatePersister.Attach(this, nameof(OscListenerDialog), MinWidth, MinHeight);
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
        Close(DataContext is OscListenerDialogViewModel { IsValid: true });
    }
}
