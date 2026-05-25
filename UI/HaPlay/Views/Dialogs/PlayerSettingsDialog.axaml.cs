using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HaPlay.Views.Dialogs;

public partial class PlayerSettingsDialog : Window
{
    public PlayerSettingsDialog()
    {
        InitializeComponent();
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
