using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace HaPlay.App;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);
}
