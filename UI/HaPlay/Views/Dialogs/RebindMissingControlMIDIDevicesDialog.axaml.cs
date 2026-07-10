using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class RebindMissingControlMIDIDevicesDialog : Window
{
    public RebindMissingControlMIDIDevicesDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
        DialogStatePersister.Attach(this, nameof(RebindMissingControlMIDIDevicesDialog), MinWidth, MinHeight);
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
        if (DataContext is not RebindMissingControlMIDIDevicesDialogViewModel vm)
        {
            Close(null);
            return;
        }

        Close(vm.BuildSelections());
    }
}
