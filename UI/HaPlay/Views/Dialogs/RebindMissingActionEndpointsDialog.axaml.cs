using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class RebindMissingActionEndpointsDialog : Window
{
    public RebindMissingActionEndpointsDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
        DialogStatePersister.Attach(this, nameof(RebindMissingActionEndpointsDialog), MinWidth, MinHeight);
    }

    private void SkipClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close(null);
    }

    private void OkClick(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is not RebindMissingActionEndpointsDialogViewModel vm)
        {
            Close(null);
            return;
        }

        Close(vm.BuildReplacementMap());
    }
}
