using Avalonia.Controls;

namespace HaPlay.Views.Dialogs;

public partial class TargetConfigurationDialog : Window
{
    public TargetConfigurationDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
        DialogStatePersister.Attach(this, nameof(TargetConfigurationDialog), MinWidth, MinHeight);
    }

    private void CloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close();
    }
}
