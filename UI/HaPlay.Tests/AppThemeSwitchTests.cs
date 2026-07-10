using System.Linq;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using HaPlay.Models;
using HaPlay.Themes;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Base-theme composition (§8.6): <see cref="AppearanceController.ApplyBaseTheme"/> - the routine the
/// app runs at startup - puts the selected theme's bundle in <c>Styles[0]</c>, pins Light for the light-only
/// Classic theme, honours Light/Dark for the variant-aware themes, and routes density to Fluent only. (In the
/// app this only runs at launch: a live control-theme swap isn't reliable in Avalonia, so the UI defers a base
/// change to restart - see <see cref="MainViewModel.AppearanceChangePending"/>.) Each test restores Classic +
/// Light in a finally so it doesn't leak into the other view tests (the headless app is shared per-assembly).</summary>
public sealed class AppThemeSwitchTests
{
    private static void Dispatch(Action body) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(AppThemeSwitchTests).Assembly)
            .Dispatch(body, CancellationToken.None);

    [Fact]
    public void Classic_pins_Light_even_when_Dark_is_requested()
    {
        Dispatch(static () =>
        {
            try
            {
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Classic);
                AppearanceController.ApplyTheme(AppThemeMode.Dark);
                var app = Application.Current!;
                Assert.IsType<ClassicThemeBundle>(app.Styles[0]);
                Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
            }
            finally
            {
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Classic);
                AppearanceController.ApplyTheme(AppThemeMode.Light);
            }
        });
    }

    [Fact]
    public void Fluent_swaps_the_bundle_and_honours_the_Dark_variant()
    {
        Dispatch(static () =>
        {
            try
            {
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Fluent);
                AppearanceController.ApplyTheme(AppThemeMode.Dark);
                var app = Application.Current!;
                Assert.IsType<FluentThemeBundle>(app.Styles[0]);
                Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            }
            finally
            {
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Classic);
                AppearanceController.ApplyTheme(AppThemeMode.Light);
            }
        });
    }

    [Fact]
    public void Simple_swaps_the_bundle_and_is_variant_aware()
    {
        Dispatch(static () =>
        {
            try
            {
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Simple);
                AppearanceController.ApplyTheme(AppThemeMode.Light);
                var app = Application.Current!;
                Assert.IsType<SimpleThemeBundle>(app.Styles[0]);
                Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
            }
            finally
            {
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Classic);
                AppearanceController.ApplyTheme(AppThemeMode.Light);
            }
        });
    }

    [Fact]
    public void Selecting_a_new_base_theme_is_deferred_to_restart_not_swapped_live()
    {
        Dispatch(static () =>
        {
            var vm = new MainViewModel();
            var original = vm.BaseTheme;
            try
            {
                var startupBundle = Application.Current!.Styles[0].GetType();
                var target = vm.BaseTheme == AppBaseTheme.Fluent ? AppBaseTheme.Simple : AppBaseTheme.Fluent;

                vm.BaseTheme = target;

                Assert.True(vm.AppearanceChangePending);
                // The running control theme must be untouched - no live Styles[0] swap (that path crashed).
                Assert.Equal(startupBundle, Application.Current!.Styles[0].GetType());
            }
            finally
            {
                vm.BaseTheme = original; // restore the persisted value (net no change to app-settings.json)
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Classic);
                AppearanceController.ApplyTheme(AppThemeMode.Light);
            }
            Assert.False(vm.AppearanceChangePending);
        });
    }

    [Fact]
    public void Changing_variant_or_density_surfaces_a_dismissable_restart_prompt_without_applying_live()
    {
        Dispatch(static () =>
        {
            var vm = new MainViewModel();
            var origTheme = vm.Theme;
            var origDensity = vm.Density;
            try
            {
                Assert.False(vm.ShowAppearanceRestartPrompt);
                var variantBefore = Application.Current!.RequestedThemeVariant;

                // Change the light/dark variant → pending + prompt, but the running variant is untouched.
                vm.Theme = vm.Theme == AppThemeMode.Dark ? AppThemeMode.Light : AppThemeMode.Dark;
                Assert.True(vm.AppearanceChangePending);
                Assert.True(vm.ShowAppearanceRestartPrompt);
                Assert.Equal(variantBefore, Application.Current!.RequestedThemeVariant);

                // "Later" hides the prompt but the change stays pending (applies on next launch).
                vm.DismissAppearanceRestartCommand.Execute(null);
                Assert.False(vm.ShowAppearanceRestartPrompt);
                Assert.True(vm.AppearanceChangePending);

                // A further change re-surfaces the prompt.
                vm.Density = vm.Density == AppDensityMode.Normal ? AppDensityMode.Compact : AppDensityMode.Normal;
                Assert.True(vm.ShowAppearanceRestartPrompt);
            }
            finally
            {
                vm.Theme = origTheme;
                vm.Density = origDensity;
            }
        });
    }

    [Fact]
    public void Density_only_takes_effect_under_Fluent()
    {
        Dispatch(static () =>
        {
            try
            {
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Fluent);
                AppearanceController.ApplyDensity(AppDensityMode.Normal);
                var fluent = ((Styles)Application.Current!.Styles[0]).OfType<FluentTheme>().First();
                Assert.Equal(DensityStyle.Normal, fluent.DensityStyle);

                AppearanceController.ApplyDensity(AppDensityMode.Compact);
                Assert.Equal(DensityStyle.Compact, fluent.DensityStyle);

                // Under a non-Fluent base theme, ApplyDensity is a no-op (nothing to assert but that it
                // doesn't throw / touch the previous Fluent instance).
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Simple);
                AppearanceController.ApplyDensity(AppDensityMode.Normal);
                Assert.IsType<SimpleThemeBundle>(Application.Current!.Styles[0]);
            }
            finally
            {
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Classic);
                AppearanceController.ApplyTheme(AppThemeMode.Light);
            }
        });
    }
}
