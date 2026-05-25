using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HaPlay.Views.Dialogs;

public partial class RoutingDialog : Window
{
    public RoutingDialog()
    {
        InitializeComponent();
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
