using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HaPlay.Views.Dialogs;

public partial class PlayerSettingsDialog : Window
{
    public PlayerSettingsDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
