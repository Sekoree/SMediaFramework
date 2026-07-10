using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Core;
using HaPlay.Models;
using HaPlay.ViewModels;
using HaPlay.ViewModels.ControlDock;
using HaPlay.Views;
using HaPlay.Views.ControlPanes;
using Xunit;

namespace HaPlay.Tests;

/// <summary>The Control workspace hosts its four panes in a Dock.Avalonia layout (ControlDockFactory), with a
/// Dock theme wired into every base-theme bundle. These tests lay the real view out under each theme (catching
/// missing-resource crashes - the docking chrome pulls in a fresh set of theme keys), and verify each pane
/// resolves its OWN view when the active tab changes (Dock's recycling content presenter reused one view until
/// the pane templates moved to app scope - see App.axaml).</summary>
public sealed class ControlDockSmokeTests
{
    private static void RunUi(Action body) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(ControlDockSmokeTests).Assembly)
            .Dispatch(body, System.Threading.CancellationToken.None)
            .GetAwaiter().GetResult();

    // App registers these at Application scope in code (App.RegisterDockPaneTemplates); the empty headless
    // TestApp has none, so add the SAME templates (ControlDockPaneTemplates.Create) for a test's duration.
    private static IDisposable RegisterPaneTemplates(Application app)
    {
        var templates = ControlDockPaneTemplates.Create();
        foreach (var t in templates)
            app.DataTemplates.Add(t);
        return new Disposer(() => { foreach (var t in templates) app.DataTemplates.Remove(t); });
    }

    private sealed class Disposer(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    [Theory]
    [InlineData(AppBaseTheme.Classic, AppThemeMode.Light)]
    [InlineData(AppBaseTheme.Simple, AppThemeMode.Light)]
    [InlineData(AppBaseTheme.Simple, AppThemeMode.Dark)]
    [InlineData(AppBaseTheme.Fluent, AppThemeMode.Light)]
    public void ControlWorkspace_LaysOut_UnderEachTheme_WithDockLayout(AppBaseTheme baseTheme, AppThemeMode mode)
    {
        RunUi(() =>
        {
            var app = Application.Current!;
            var tokens = new StyleInclude(new Uri("avares://HaPlay/")) { Source = new Uri("avares://HaPlay/Styles/Tokens.axaml") };
            app.Styles.Add(tokens);
            var templates = RegisterPaneTemplates(app);
            Window? window = null;
            try
            {
                AppearanceController.ApplyBaseTheme(baseTheme);
                AppearanceController.ApplyTheme(mode);

                var vm = new ControlWorkspaceViewModel();
                var view = new ControlWorkspaceView { DataContext = vm };
                window = new Window { Width = 1000, Height = 700, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.Measure(new Size(1000, 700));
                window.Arrange(new Rect(0, 0, 1000, 700));
                Dispatcher.UIThread.RunJobs();

                // The default active pane is Surfaces; its view must have materialised inside the dock.
                Assert.Contains(window.GetVisualDescendants(), v => v is ControlSurfacesView);
            }
            finally
            {
                window?.Close();
                templates.Dispose();
                app.Styles.Remove(tokens);
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Classic);
                AppearanceController.ApplyTheme(AppThemeMode.Light);
            }
        });
    }

    [Fact]
    public void EachActivePane_RendersItsOwnView_NotAlwaysSurfaces()
    {
        RunUi(() =>
        {
            var app = Application.Current!;
            var tokens = new StyleInclude(new Uri("avares://HaPlay/")) { Source = new Uri("avares://HaPlay/Styles/Tokens.axaml") };
            app.Styles.Add(tokens);
            var templates = RegisterPaneTemplates(app);
            Window? window = null;
            try
            {
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Fluent);
                AppearanceController.ApplyTheme(AppThemeMode.Light);

                var vm = new ControlWorkspaceViewModel();
                var view = new ControlWorkspaceView { DataContext = vm };
                window = new Window { Width = 1000, Height = 700, Content = view };
                window.Show();

                var pane = (IDock)vm.DockLayout.VisibleDockables![0];

                void ActivateAndLayout(string id)
                {
                    pane.ActiveDockable = pane.VisibleDockables!.First(d => d.Id == id);
                    Dispatcher.UIThread.RunJobs();
                    window!.Measure(new Size(1000, 700));
                    window.Arrange(new Rect(0, 0, 1000, 700));
                    Dispatcher.UIThread.RunJobs();
                }

                bool Present<T>() where T : Control => window!.GetVisualDescendants().OfType<T>().Any();

                ActivateAndLayout("Scripts");
                Assert.True(Present<ControlScriptsView>(), "Scripts pane should render the Scripts view");
                Assert.False(Present<ControlSurfacesView>(), "Scripts pane must not still show Surfaces");

                ActivateAndLayout("Monitor");
                Assert.True(Present<ControlMonitorView>(), "Monitor pane should render the Monitor view");

                ActivateAndLayout("Tools");
                Assert.True(Present<ControlToolsView>(), "Tools pane should render the Tools view");

                ActivateAndLayout("Surfaces");
                Assert.True(Present<ControlSurfacesView>(), "Surfaces pane should render the Surfaces view");
            }
            finally
            {
                window?.Close();
                templates.Dispose();
                app.Styles.Remove(tokens);
                AppearanceController.ApplyBaseTheme(AppBaseTheme.Classic);
                AppearanceController.ApplyTheme(AppThemeMode.Light);
            }
        });
    }

    [Fact]
    public void PaneTemplates_MatchOnlyTheirDocument_NotNullNorOtherTypes()
    {
        // These are registered app-wide (so floated panes resolve too), so they must claim ONLY their exact
        // document type. A plain FuncDataTemplate<T> also matches null, which leaked the Surfaces view into
        // unrelated null content presenters app-wide (empty combo boxes, the I/O view with no output) and NRE'd
        // when a still-loaded dock re-measured on a workspace switch. The explicit match must exclude null.
        RunUi(() =>
        {
            var templates = ControlDockPaneTemplates.Create().Cast<FuncDataTemplate>().ToArray();
            var surfaces = templates[0];
            Assert.False(surfaces.Match(null), "must not match null content");
            Assert.False(surfaces.Match(new object()), "must not match unrelated objects");
            Assert.True(surfaces.Match(new ControlSurfacesDocument()), "must match its own document type");

            // Building null/unmatched data yields nothing rather than throwing.
            foreach (var template in templates)
                Assert.Null(template.Build(null));
        });
    }

    [Fact]
    public void ResetDockLayout_RestoresAllFourPanes_AfterOneIsClosed()
    {
        RunUi(() =>
        {
            var vm = new ControlWorkspaceViewModel();
            var pane = (IDock)vm.DockLayout.VisibleDockables![0]; // the DocumentDock holding the four panes
            Assert.Equal(4, pane.VisibleDockables!.Count);
            var before = vm.DockLayout;

            // Simulate the operator closing a pane.
            pane.VisibleDockables!.RemoveAt(0);
            Assert.Equal(3, pane.VisibleDockables!.Count);

            // Reset layout rebuilds a fresh layout with all four panes back.
            vm.ResetDockLayoutCommand.Execute(null);
            Assert.NotSame(before, vm.DockLayout);
            var restored = (IDock)vm.DockLayout.VisibleDockables![0];
            Assert.Equal(4, restored.VisibleDockables!.Count);
        });
    }
}
