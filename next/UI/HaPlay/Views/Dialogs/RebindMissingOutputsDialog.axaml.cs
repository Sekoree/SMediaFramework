using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class RebindMissingOutputsDialog : Window
{
    public RebindMissingOutputsDialog()
    {
        InitializeComponent();
        DialogStatePersister.Attach(this, nameof(RebindMissingOutputsDialog), MinWidth, MinHeight);
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
        if (DataContext is not RebindMissingOutputsDialogViewModel vm)
        {
            Close(null);
            return;
        }

        Close(vm.BuildReplacementMap());
    }
}
