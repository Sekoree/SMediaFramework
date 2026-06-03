using System.Collections.ObjectModel;
using Avalonia;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlGraphWorkspaceViewModelTests
{
    [Fact]
    public void LoadGraphs_EditSelectedAndBuildSnapshot_PreservesGraph()
    {
        var graphId = Guid.NewGuid();
        var workspace = new ControlGraphWorkspaceViewModel(new ObservableCollection<ActionEndpoint>());
        workspace.LoadGraphs(
        [
            new ControlGraphConfig
            {
                Id = graphId,
                Name = "BCF Layer",
                IsEnabled = true,
                Nodes =
                [
                    new ControlNodeConfig
                    {
                        Kind = ControlNodeKind.ScriptTransform,
                        Settings = new ScriptTransformControlNodeSettings
                        {
                            Source = "return emit.scalar(event.value);",
                        },
                    },
                ],
            },
        ]);

        Assert.Single(workspace.Graphs);
        Assert.NotNull(workspace.SelectedGraph);
        workspace.SelectedGraph!.Name = "Edited Layer";
        workspace.SelectedGraph.IsEnabled = false;

        var snapshot = Assert.Single(workspace.BuildSnapshot());
        Assert.Equal(graphId, snapshot.Id);
        Assert.Equal("Edited Layer", snapshot.Name);
        Assert.False(snapshot.IsEnabled);
        Assert.Single(snapshot.Nodes);
    }

    [Fact]
    public void Editor_BuildSnapshot_PersistsViewportNodesConnectionsAndSettings()
    {
        var inputId = Guid.NewGuid();
        var outputId = Guid.NewGuid();
        var graph = new ControlGraphConfig
        {
            Id = Guid.NewGuid(),
            Name = "Editor Graph",
            ViewportX = 12,
            ViewportY = -8,
            Zoom = 1.5,
            Nodes =
            [
                new ControlNodeConfig
                {
                    Id = inputId,
                    Kind = ControlNodeKind.MidiInput,
                    X = 10,
                    Y = 20,
                    Settings = new MidiInputControlNodeSettings
                    {
                        Channel = 2,
                        Controller = 7,
                        HighResolution14Bit = true,
                    },
                },
                new ControlNodeConfig
                {
                    Id = outputId,
                    Kind = ControlNodeKind.OscOutput,
                    X = 200,
                    Y = 20,
                    Settings = new OscOutputControlNodeSettings
                    {
                        Host = "192.168.1.50",
                        Port = 10023,
                        Address = "/ch/01/mix/fader",
                    },
                },
            ],
            Connections =
            [
                new ControlConnectionConfig
                {
                    FromNodeId = inputId,
                    ToNodeId = outputId,
                },
            ],
        };
        var editor = new ControlGraphEditorViewModel(graph);

        editor.OffsetX = 24;
        editor.OffsetY = -16;
        editor.Zoom = 2;
        var midiNode = Assert.Single(editor.EditorNodes, n => n.Id == inputId);
        midiNode.Location = new Point(32, 64);
        midiNode.MidiController = 10;

        var snapshot = editor.BuildSnapshot();

        Assert.Equal(24, snapshot.ViewportX);
        Assert.Equal(-16, snapshot.ViewportY);
        Assert.Equal(2, snapshot.Zoom);
        var midiConfig = Assert.Single(snapshot.Nodes, n => n.Id == inputId);
        Assert.Equal(32, midiConfig.X);
        Assert.Equal(64, midiConfig.Y);
        var midiSettings = Assert.IsType<MidiInputControlNodeSettings>(midiConfig.Settings);
        Assert.Equal(10, midiSettings.Controller);
        Assert.Single(snapshot.Connections);
    }

    [Fact]
    public void Workspace_AddNodeFromPalette_AddsNodeAndMonitorEntry()
    {
        var workspace = new ControlGraphWorkspaceViewModel(new ObservableCollection<ActionEndpoint>());
        workspace.AddGraphCommand.Execute(null);
        var paletteItem = workspace.PaletteGroups
            .SelectMany(g => g.Items)
            .Single(i => i.Kind == ControlNodeKind.ScriptTransform);

        workspace.AddNodeCommand.Execute(paletteItem);

        var graph = Assert.Single(workspace.Graphs);
        Assert.Single(graph.Editor.EditorNodes);
        Assert.Contains(workspace.MonitorEntries, e => e.Kind == "Node");
        var snapshot = Assert.Single(workspace.BuildSnapshot());
        Assert.Single(snapshot.Nodes);
    }
}
