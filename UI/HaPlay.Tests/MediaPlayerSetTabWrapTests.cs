using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaPlay.Models;
using HaPlay.ViewModels;
using HaPlay.Views;
using Xunit;

namespace HaPlay.Tests;

/// <summary>The playlist Set tab strip wraps onto extra lines when the tabs outgrow the toolbar row
/// (WrapPanel items panel) - it must never fall back to the old horizontal scroller that hid Sets
/// off-screen. Lays out the real MediaPlayerView so the header-only TabControl template, the shared
/// toolbar DockPanel row, and the theme's TabItem containers are all in play.</summary>
public sealed class MediaPlayerSetTabWrapTests
{
    private static void RunUi(Action body) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(MediaPlayerSetTabWrapTests).Assembly)
            .Dispatch(body, CancellationToken.None)
            .GetAwaiter().GetResult();

    [Fact]
    public void SetTabs_WrapOntoExtraLines_InsteadOfOverflowing()
    {
        RunUi(() =>
        {
            var app = Application.Current!;
            // Theme FIRST: ApplyBaseTheme swaps app.Styles[0], so tokens added before it get clobbered.
            AppearanceController.ApplyBaseTheme(AppBaseTheme.Classic);
            AppearanceController.ApplyTheme(AppThemeMode.Light);
            var tokens = new StyleInclude(new Uri("avares://HaPlay/")) { Source = new Uri("avares://HaPlay/Styles/Tokens.axaml") };
            app.Styles.Add(tokens);
            Window? window = null;
            try
            {

                var vm = new MediaPlayerViewModel(new OutputManagementViewModel(), "P1");
                for (var i = 0; i < 9; i++)
                    vm.AddPlaylistTabCommand.Execute(null);
                Assert.Equal(10, vm.PlaylistTabs.Count);

                const double width = 700;
                var view = new MediaPlayerView { DataContext = vm };
                window = new Window { Width = width, Height = 800, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.Measure(new Size(width, 800));
                window.Arrange(new Rect(0, 0, width, 800));
                Dispatcher.UIThread.RunJobs();

                var tabs = view.GetVisualDescendants().OfType<TabItem>().ToList();
                Assert.Equal(10, tabs.Count);

                // Ten ~80px tabs can't fit one 700px row that also holds the toolbar buttons, so the
                // WrapPanel must have broken them onto at least two distinct lines.
                var lines = tabs
                    .Select(t => Math.Round(t.TranslatePoint(new Point(0, 0), view)!.Value.Y))
                    .Distinct()
                    .Count();
                Assert.True(lines >= 2, $"expected the Set tabs to wrap onto multiple lines, got {lines}");

                // And no tab may poke past the view's right edge (the old strip scrolled it away).
                foreach (var tab in tabs)
                {
                    var right = tab.TranslatePoint(new Point(tab.Bounds.Width, 0), view)!.Value.X;
                    Assert.True(right <= width + 0.5, $"a Set tab overflows the view: right edge at {right}");
                }
            }
            finally
            {
                window?.Close();
                app.Styles.Remove(tokens);
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Classic);
                AppearanceController.ApplyTheme(AppThemeMode.Light);
            }
        });
    }
}
