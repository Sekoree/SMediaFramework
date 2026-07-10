using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Regression for the script-editor crash under a non-Fluent base theme. AvaloniaEdit ships only a
/// *Fluent* theme (merged app-wide in App.axaml for the script editor's <c>TextEditor</c>); its resources
/// resolve Fluent-palette keys the Simple and Classic base themes don't define. Two aliases use StaticResource
/// and so throw <see cref="KeyNotFoundException"/> the moment they're built - the reported crash was
/// "Static resource 'ControlContentThemeFontSize' not found" during <c>SearchPanel.Install</c>:
/// <list type="bullet">
///   <item><c>SearchPanelFontSize = {StaticResource ControlContentThemeFontSize}</c> (built on template apply)</item>
///   <item><c>CompletionToolTip* = {StaticResource ToolTip{Background,Foreground,BorderBrush,BorderThemeThickness}}</c></item>
/// </list>
/// Three more (SystemChromeMediumColor / SystemBaseLowColor / SystemAccentColor) are DynamicResource - soft, but
/// supplied too so the search panel and selection are themed. <c>FluentCompatResources</c> (merged into the
/// Simple and Classic bundles) provides them; this pins that every required key resolves. Before the fix the
/// keys are absent, so these assertions fail.
/// <para>RunUi is awaited so a failed <c>Assert</c> on the UI thread actually fails the test (a fire-and-forget
/// dispatch swallows it).</para></summary>
public sealed class ScriptEditorThemeResourceTests
{
    private static void RunUi(Action body) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(ScriptEditorThemeResourceTests).Assembly)
            .Dispatch(body, CancellationToken.None)
            .GetAwaiter().GetResult();

    [Theory]
    // Simple is variant-aware (Light + Dark); Classic pins Light. Fluent is the baseline (FluentTheme already
    // defines these) - it passes with or without the fix; Simple/Classic are the cases the fix addresses.
    [InlineData(AppBaseTheme.Simple, AppThemeMode.Light)]
    [InlineData(AppBaseTheme.Simple, AppThemeMode.Dark)]
    [InlineData(AppBaseTheme.Classic, AppThemeMode.Light)]
    [InlineData(AppBaseTheme.Fluent, AppThemeMode.Light)]
    public void AvaloniaEdit_Fluent_theme_keys_resolve(AppBaseTheme baseTheme, AppThemeMode mode)
    {
        RunUi(() =>
        {
            try
            {
                AppearanceController.ApplyBaseTheme(baseTheme);
                AppearanceController.ApplyTheme(mode);

                var app = Application.Current!;
                var v = app.RequestedThemeVariant;

                void Require<T>(string key)
                {
                    var found = app.TryGetResource(key, v, out var value);
                    Assert.True(found && value is T,
                        $"'{key}' must resolve to {typeof(T).Name} under {baseTheme}/{mode}, got " +
                        (found ? value?.GetType().Name : "MISSING"));
                }

                // StaticResource-aliased - these hard-crash the editor when missing.
                Require<double>("ControlContentThemeFontSize");
                Require<IBrush>("ToolTipBackground");
                Require<IBrush>("ToolTipForeground");
                Require<IBrush>("ToolTipBorderBrush");
                Require<Thickness>("ToolTipBorderThemeThickness");

                // DynamicResource - soft, but required for a themed search panel / visible selection.
                Require<Color>("SystemChromeMediumColor");
                Require<Color>("SystemBaseLowColor");
                Require<Color>("SystemAccentColor");
            }
            finally
            {
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Classic);
                AppearanceController.ApplyTheme(AppThemeMode.Light);
            }
        });
    }
}
