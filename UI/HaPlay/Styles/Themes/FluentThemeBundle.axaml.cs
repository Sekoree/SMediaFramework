using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace HaPlay.Themes;

/// <summary>Avalonia's built-in Fluent theme (light/dark, density-aware) bundled with the package
/// TreeDataGrid + ColorPicker themes. See <see cref="ClassicThemeBundle"/> for why the includes live in
/// XAML.</summary>
public class FluentThemeBundle : Styles
{
    public FluentThemeBundle() => AvaloniaXamlLoader.Load(this);
}
