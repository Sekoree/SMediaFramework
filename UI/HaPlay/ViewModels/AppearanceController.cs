using Avalonia;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using HaPlay.Models;

namespace HaPlay.ViewModels;

/// <summary>
/// Phase E (§8.6) — applies the persisted <see cref="AppThemeMode"/> / <see cref="AppDensityMode"/> to
/// the live <see cref="Application"/>. Kept as static so the App startup path and any later VM
/// property-change can call the same code without resolving services.
/// </summary>
internal static class AppearanceController
{
    public static void ApplyTheme(AppThemeMode mode)
    {
        var app = Application.Current;
        if (app is null) return;
        app.RequestedThemeVariant = mode switch
        {
            AppThemeMode.Light => ThemeVariant.Light,
            AppThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public static void ApplyDensity(AppDensityMode mode)
    {
        var app = Application.Current;
        if (app is null) return;
        // The FluentTheme is the first Styles entry by convention (see App.axaml). Finding it by type
        // is safer than indexing in case the Styles list ever grows or reorders.
        foreach (var style in app.Styles)
        {
            if (style is FluentTheme ft)
            {
                ft.DensityStyle = mode == AppDensityMode.Normal ? DensityStyle.Normal : DensityStyle.Compact;
                return;
            }
        }
    }
}
