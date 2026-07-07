using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace HaPlay.Themes;

/// <summary>Avalonia's built-in Simple theme (light/dark, flat) bundled with the package TreeDataGrid +
/// ColorPicker themes. See <see cref="ClassicThemeBundle"/> for why the includes live in XAML.</summary>
public class SimpleThemeBundle : Styles
{
    public SimpleThemeBundle() => AvaloniaXamlLoader.Load(this);
}
