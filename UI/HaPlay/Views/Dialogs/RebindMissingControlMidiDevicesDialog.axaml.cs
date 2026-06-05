using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class RebindMissingControlMidiDevicesDialog : Window
{
    public RebindMissingControlMidiDevicesDialog()
    {
        InitializeComponent();
        DialogStatePersister.Attach(this, nameof(RebindMissingControlMidiDevicesDialog), MinWidth, MinHeight);
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
        if (DataContext is not RebindMissingControlMidiDevicesDialogViewModel vm)
        {
            Close(null);
            return;
        }

        Close(vm.BuildSelections());
    }
}
