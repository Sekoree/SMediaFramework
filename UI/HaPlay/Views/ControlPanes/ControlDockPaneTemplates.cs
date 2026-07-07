using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using HaPlay.ViewModels.ControlDock;

namespace HaPlay.Views.ControlPanes;

/// <summary>Maps the Control workspace's dock-pane documents to their views. Registered in code (via
/// <see cref="App"/>) rather than as App.axaml <c>DataTemplate</c>s: Dock's recycling content presenter reuses
/// one content control across the panes and re-points its DataContext, so a compiled <c>{Binding Workspace}</c>
/// (typed to one document) reused the first pane's view and nulled its context on the others. A code template
/// that assigns the DataContext outright is immune.
/// <para>These are registered app-wide (so floated panes in their own windows still resolve), which means they
/// must match ONLY their exact document type. A plain <c>FuncDataTemplate&lt;T&gt;</c> also matches <c>null</c>,
/// which made the Surfaces view leak into unrelated null content presenters app-wide (empty combo boxes, the
/// I/O view with no output). The explicit <c>o is T</c> match excludes null.</para></summary>
public static class ControlDockPaneTemplates
{
    public static IDataTemplate[] Create() =>
    [
        Pane<ControlSurfacesDocument>(d => new ControlSurfacesView { DataContext = d.Workspace }),
        Pane<ControlScriptsDocument>(d => new ControlScriptsView { DataContext = d.Workspace }),
        Pane<ControlMonitorDocument>(d => new ControlMonitorView { DataContext = d.Workspace }),
        Pane<ControlToolboxDocument>(d => new ControlToolsView { DataContext = d.Workspace }),
    ];

    private static IDataTemplate Pane<T>(Func<T, Control> build) where T : class =>
        new FuncDataTemplate(
            o => o is T,                              // exact type only; null → false, so no app-wide null leak
            (o, _) => o is T t ? build(t) : null);
}
