using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace HaPlay.Themes;

/// <summary>The Windows-Classic base theme bundled with its bespoke TreeDataGrid + ColorPicker themes.
/// Composed in XAML (so the StyleIncludes stay compile-time, trim/AOT-safe); AppearanceController
/// constructs it to swap <c>Application.Styles[0]</c> at runtime.</summary>
public class ClassicThemeBundle : Styles
{
    public ClassicThemeBundle() => AvaloniaXamlLoader.Load(this);
}
