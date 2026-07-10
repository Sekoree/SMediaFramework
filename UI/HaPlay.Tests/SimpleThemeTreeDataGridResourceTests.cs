using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Regression for the Simple-theme Cue-player crash. The TreeDataGrid package ships only a *Fluent*
/// theme, whose brushes resolve four Fluent-palette colours (SystemListLowColor / SystemBaseHighColor /
/// SystemBaseMediumLowColor / SystemAccentColor) via <c>StaticResource</c>. FluentTheme defines them, SimpleTheme
/// does not - so under Simple, building any of those brushes threw <see cref="KeyNotFoundException"/>
/// ("Static resource 'SystemListLowColor' not found") the instant the Cue list's <c>TreeDataGrid</c> was laid
/// out (a StaticResource on a SolidColorBrush has no runtime fallback - it throws rather than defers).
/// <see cref="Themes.SimpleThemeBundle"/> now supplies those colours at bundle scope; these tests pin it.
/// <para>NB: the session <c>Dispatch</c> is awaited (<c>GetAwaiter().GetResult()</c>) so an exception on the UI
/// thread - a crash, or a failed <c>Assert</c> - actually fails the test. Fire-and-forget dispatch swallows it.</para></summary>
public sealed class SimpleThemeTreeDataGridResourceTests
{
    // Awaited on purpose - see the class remarks. Without it, a KeyNotFoundException on the dispatched UI thread
    // lands on a discarded Task and the test passes regardless.
    private static void RunUi(Action body) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(SimpleThemeTreeDataGridResourceTests).Assembly)
            .Dispatch(body, CancellationToken.None)
            .GetAwaiter().GetResult();

    // Each TreeDataGrid brush that the Fluent TreeDataGrid theme derives from a Fluent-only System*Color; if a
    // colour is missing, building the brush throws - so resolving the brush exercises the crash path end to end.
    private static readonly (string Brush, string Color)[] BrushToColor =
    {
        ("TreeDataGridGridLinesBrush", "SystemListLowColor"),
        ("TreeDataGridHeaderForegroundPointerOverBrush", "SystemBaseHighColor"),
        ("TreeDataGridHeaderBackgroundPressedBrush", "SystemBaseMediumLowColor"),
        ("TreeDataGridSelectedCellBackgroundBrush", "SystemAccentColor"),
    };

    /// <summary>The exact production scenario: a real <c>TreeDataGrid</c> laid out under the Simple theme. This
    /// threw <c>KeyNotFoundException: Static resource 'SystemListLowColor' not found</c> before the fix.</summary>
    [Fact]
    public void TreeDataGrid_lays_out_under_the_Simple_theme_without_crashing()
    {
        RunUi(() =>
        {
            try
            {
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Simple);
                AppearanceController.ApplyTheme(AppThemeMode.Light);

                var items = new System.Collections.ObjectModel.ObservableCollection<string> { "a", "b" };
                var source = new FlatTreeDataGridSource<string>(items)
                {
                    Columns = { new TextColumn<string, string>("Name", x => x) },
                };
                var grid = new TreeDataGrid { Source = source };
                var window = new Window { Width = 400, Height = 300, Content = grid };
                try
                {
                    window.Show();
                    Dispatcher.UIThread.RunJobs();
                    grid.Measure(new Size(400, 300));
                    grid.Arrange(new Rect(0, 0, 400, 300)); // template + brushes realised here - the crash point
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Classic);
                AppearanceController.ApplyTheme(AppThemeMode.Light);
            }
        });
    }

    [Theory]
    [InlineData(AppThemeMode.Light)]
    [InlineData(AppThemeMode.Dark)] // Simple is variant-aware; the bundle's Dark palette must resolve too.
    public void Fluent_TreeDataGrid_brushes_and_their_System_colours_resolve_under_Simple(AppThemeMode variant)
    {
        RunUi(() =>
        {
            try
            {
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Simple);
                AppearanceController.ApplyTheme(variant);

                var app = Application.Current!;
                var themeVariant = app.RequestedThemeVariant;

                foreach (var (brush, color) in BrushToColor)
                {
                    Assert.True(
                        app.TryGetResource(color, themeVariant, out var c) && c is Color,
                        $"Simple theme must define '{color}' ({variant}) for the Fluent TreeDataGrid theme.");

                    // Resolving the brush builds the same deferred content that crashed the Cue player.
                    Assert.True(
                        app.TryGetResource(brush, themeVariant, out var b) && b is IBrush,
                        $"'{brush}' failed to build under the Simple theme ({variant}).");
                }
            }
            finally
            {
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Classic);
                AppearanceController.ApplyTheme(AppThemeMode.Light);
            }
        });
    }
}
