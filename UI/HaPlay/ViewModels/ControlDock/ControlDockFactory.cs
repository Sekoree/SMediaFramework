using System;
using System.Collections.Generic;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace HaPlay.ViewModels.ControlDock;

// One dock document per Control workspace tab. Documents (not tools) so the tab strip sits at the TOP in every
// base theme's dock theme — the tool tab strip is docked bottom by convention. Each carries a reference to the
// shared ControlWorkspaceViewModel; the matching view (a DataTemplate in ControlWorkspaceView) binds to it.
public sealed class ControlSurfacesDocument : Document { public ControlWorkspaceViewModel Workspace { get; init; } = null!; }
public sealed class ControlScriptsDocument : Document { public ControlWorkspaceViewModel Workspace { get; init; } = null!; }
public sealed class ControlMonitorDocument : Document { public ControlWorkspaceViewModel Workspace { get; init; } = null!; }
public sealed class ControlToolboxDocument : Document { public ControlWorkspaceViewModel Workspace { get; init; } = null!; }

/// <summary>Builds the Control workspace's docking layout: the four panes (Surfaces / Scripts / Monitor /
/// Tools) start as top-tabbed documents in one dock — same as the old TabControl — but can be dragged out to
/// split the pane or float into their own window (watch the Monitor while editing Scripts). A closed or floated
/// pane comes back via the workspace's "Reset layout" command, which rebuilds this from scratch.</summary>
public sealed class ControlDockFactory : Factory
{
    private readonly ControlWorkspaceViewModel _workspace;

    public ControlDockFactory(ControlWorkspaceViewModel workspace) => _workspace = workspace;

    public override IRootDock CreateLayout()
    {
        var surfaces = new ControlSurfacesDocument { Id = "Surfaces", Title = "Surfaces", Workspace = _workspace };
        var scripts = new ControlScriptsDocument { Id = "Scripts", Title = "Scripts", Workspace = _workspace };
        var monitor = new ControlMonitorDocument { Id = "Monitor", Title = "Monitor", Workspace = _workspace };
        var toolbox = new ControlToolboxDocument { Id = "Tools", Title = "Tools", Workspace = _workspace };

        var documents = new DocumentDock
        {
            Id = "ControlDocuments",
            Title = "ControlDocuments",
            CanCreateDocument = false, // no "+" new-tab button — the four panes are fixed
            VisibleDockables = CreateList<IDockable>(surfaces, scripts, monitor, toolbox),
            ActiveDockable = surfaces,
        };

        var root = CreateRootDock();
        root.Id = "ControlRoot";
        root.Title = "ControlRoot";
        root.VisibleDockables = CreateList<IDockable>(documents);
        root.ActiveDockable = documents;
        root.DefaultDockable = documents;
        return root;
    }

    public override void InitLayout(IDockable layout)
    {
        // Without a host-window locator a pane dragged out of the dock has no window to float into and simply
        // vanishes — wire it up so drag-out opens a proper floating window instead.
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow(),
        };
        base.InitLayout(layout);
    }
}
