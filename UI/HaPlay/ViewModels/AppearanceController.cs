using System.Linq;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using HaPlay.Models;
using HaPlay.Themes;

namespace HaPlay.ViewModels;

/// <summary>
/// Applies the persisted appearance choices to the live <see cref="Application"/>: the
/// <see cref="AppBaseTheme"/> base theme (§8.6), the <see cref="AppThemeMode"/> light/dark variant, and the
/// <see cref="AppDensityMode"/> Fluent density. The base theme lives in <c>Styles[0]</c> as a swappable bundle
/// (control theme + its TreeDataGrid + ColorPicker themes); switching rebuilds that slot in place so the
/// running app re-skins without a restart. Static so App startup and any later VM change call the same code
/// without resolving services.
/// </summary>
internal static class AppearanceController
{
    // Held so ApplyDensity can reach the live FluentTheme - only Fluent exposes DensityStyle. Null whenever a
    // non-Fluent base theme is active.
    private static FluentTheme? _activeFluentTheme;
    private static AppBaseTheme _activeBase = AppBaseTheme.Classic;

    /// <summary>Swap <c>Styles[0]</c> for the chosen base theme's bundle. Classic keeps the in-repo
    /// Windows-Classic skin + its bespoke TreeDataGrid/ColorPicker themes; Simple/Fluent bring their own
    /// built-in companion themes.</summary>
    public static void ApplyBaseTheme(AppBaseTheme mode)
    {
        var app = Application.Current;
        if (app is null) return;

        // Each bundle is a compiled x:Class Styles type (base theme + its TreeDataGrid + ColorPicker themes),
        // so the StyleIncludes inside are resolved at compile time and stay trim/AOT-safe.
        Styles bundle = mode switch
        {
            AppBaseTheme.Fluent => new FluentThemeBundle(),
            AppBaseTheme.Simple => new SimpleThemeBundle(),
            _ => new ClassicThemeBundle(),
        };
        // Density only applies to Fluent; grab the live instance from the active bundle (null otherwise).
        _activeFluentTheme = bundle.OfType<FluentTheme>().FirstOrDefault();

        _activeBase = mode;
        if (app.Styles.Count == 0)
            app.Styles.Add(bundle);
        else
            app.Styles[0] = bundle;
    }

    public static void ApplyTheme(AppThemeMode mode)
    {
        var app = Application.Current;
        if (app is null) return;
        // Classic is a light-only theme (Dark yields a half-dark window with white-on-white islands); pin
        // Light regardless of the saved variant. Simple/Fluent are variant-aware and honour the choice.
        if (_activeBase == AppBaseTheme.Classic)
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
            return;
        }
        app.RequestedThemeVariant = mode switch
        {
            AppThemeMode.Light => ThemeVariant.Light,
            AppThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    public static void ApplyDensity(AppDensityMode mode)
    {
        // Only Fluent exposes a density axis; Classic/Simple ignore it.
        if (_activeFluentTheme is { } ft)
            ft.DensityStyle = mode == AppDensityMode.Normal ? DensityStyle.Normal : DensityStyle.Compact;
    }
}
