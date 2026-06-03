using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.ControlGraph;
using HaPlay.Models;
using NodifyM.Avalonia.ViewModelBase;

namespace HaPlay.ViewModels;

public partial class ControlGraphWorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly ObservableCollection<ActionEndpoint> _endpoints;

    public ControlGraphWorkspaceViewModel(ObservableCollection<ActionEndpoint> endpoints)
    {
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        PaletteGroups =
        [
            new ControlNodePaletteGroup("Inputs",
            [
                ControlNodePaletteItem.ForKind(ControlNodeKind.MidiInput, "MIDI Input"),
                ControlNodePaletteItem.ForKind(ControlNodeKind.OscInput, "OSC Input"),
            ]),
            new ControlNodePaletteGroup("Outputs",
            [
                ControlNodePaletteItem.ForKind(ControlNodeKind.OscOutput, "OSC Output"),
                ControlNodePaletteItem.ForKind(ControlNodeKind.MidiOutput, "MIDI Output"),
                ControlNodePaletteItem.ForKind(ControlNodeKind.X32ChannelFader, "X32 Channel Fader"),
            ]),
            new ControlNodePaletteGroup("Transforms",
            [
                ControlNodePaletteItem.ForKind(ControlNodeKind.MapRange, "Map Range"),
                ControlNodePaletteItem.ForKind(ControlNodeKind.Passthrough, "Passthrough"),
            ]),
            new ControlNodePaletteGroup("Scripts",
            [
                ControlNodePaletteItem.ForKind(ControlNodeKind.ScriptTransform, "Mond Script"),
            ]),
            new ControlNodePaletteGroup("State", []),
            new ControlNodePaletteGroup("Presets",
            [
                ControlNodePaletteItem.Preset("BCF2000 14-bit fader -> X32 ch 1", ControlNodeKind.X32ChannelFader),
                ControlNodePaletteItem.Preset("X-Touch Mini CC -> OSC", ControlNodeKind.OscOutput),
            ]),
        ];
    }

    public ObservableCollection<ControlGraphRowViewModel> Graphs { get; } = new();
    public IReadOnlyList<ControlNodePaletteGroup> PaletteGroups { get; }
    public ObservableCollection<ControlGraphMonitorEntryViewModel> MonitorEntries { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedGraphCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartSelectedGraphCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopSelectedGraphCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelectedGraph))]
    [NotifyPropertyChangedFor(nameof(SelectedEditor))]
    private ControlGraphRowViewModel? _selectedGraph;

    public ControlGraphEditorViewModel? SelectedEditor => SelectedGraph?.Editor;
    public IEnumerable<ControlGraphConfig> BuildSnapshot() =>
        Graphs.Select(g => g.BuildSnapshot()).ToList();

    public void LoadGraphs(IEnumerable<ControlGraphConfig> graphs)
    {
        foreach (var graph in Graphs)
            graph.Dispose();
        Graphs.Clear();

        foreach (var graph in graphs)
            Graphs.Add(new ControlGraphRowViewModel(graph, _endpoints));

        SelectedGraph = Graphs.FirstOrDefault();
    }

    [RelayCommand]
    private void AddGraph()
    {
        var graph = new ControlGraphRowViewModel(
            new ControlGraphConfig
            {
                Name = $"Control Graph {Graphs.Count + 1}",
                IsEnabled = true,
            },
            _endpoints);
        graph.MonitorRaised += OnGraphMonitorRaised;
        Graphs.Add(graph);
        SelectedGraph = graph;
        AddMonitor("Graph", $"Added {graph.Name}", postWhenOffUiThread: false);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedGraph))]
    private void RemoveSelectedGraph()
    {
        if (SelectedGraph is not { } graph)
            return;

        graph.Dispose();
        var index = Graphs.IndexOf(graph);
        Graphs.Remove(graph);
        AddMonitor("Graph", $"Removed {graph.Name}", postWhenOffUiThread: false);
        SelectedGraph = Graphs.Count == 0
            ? null
            : Graphs[Math.Clamp(index, 0, Graphs.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(CanStartSelectedGraph))]
    private async Task StartSelectedGraphAsync()
    {
        if (SelectedGraph is not { } graph)
            return;
        await graph.StartAsync();
        StartSelectedGraphCommand.NotifyCanExecuteChanged();
        StopSelectedGraphCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStopSelectedGraph))]
    private async Task StopSelectedGraphAsync()
    {
        if (SelectedGraph is not { } graph)
            return;
        await graph.StopAsync();
        StartSelectedGraphCommand.NotifyCanExecuteChanged();
        StopSelectedGraphCommand.NotifyCanExecuteChanged();
    }

    public bool HasSelectedGraph => SelectedGraph is not null;
    private bool HasSelectedGraphForCommand() => SelectedGraph is not null;
    private bool CanStartSelectedGraph() => SelectedGraph is { IsRunning: false };
    private bool CanStopSelectedGraph() => SelectedGraph is { IsRunning: true };

    [RelayCommand]
    private void AddNode(ControlNodePaletteItem? paletteItem)
    {
        if (SelectedGraph is null || paletteItem is null)
            return;

        var node = SelectedGraph.Editor.AddNode(paletteItem.CreateConfig());
        SelectedGraph.RefreshTopology();
        AddMonitor("Node", $"Added {node.Title} to {SelectedGraph.Name}", postWhenOffUiThread: false);
    }

    [RelayCommand]
    private void RemoveSelectedNode()
    {
        if (SelectedGraph?.Editor.SelectedEditorNode is not { } node)
            return;

        SelectedGraph.Editor.RemoveNode(node);
        SelectedGraph.RefreshTopology();
        AddMonitor("Node", $"Removed {node.Title} from {SelectedGraph.Name}", postWhenOffUiThread: false);
    }

    private void OnGraphMonitorRaised(object? sender, ControlGraphMonitorEntryViewModel entry) =>
        AddMonitor(entry.Kind, entry.Detail, postWhenOffUiThread: true);

    private void AddMonitor(string kind, string detail, bool postWhenOffUiThread)
    {
        var entry = new ControlGraphMonitorEntryViewModel(DateTimeOffset.Now, kind, detail);
        if (!postWhenOffUiThread || Dispatcher.UIThread.CheckAccess())
            AddMonitorOnUi(entry);
        else
            Dispatcher.UIThread.Post(() => AddMonitorOnUi(entry));
    }

    private void AddMonitorOnUi(ControlGraphMonitorEntryViewModel entry)
    {
        MonitorEntries.Insert(0, entry);
        while (MonitorEntries.Count > 200)
            MonitorEntries.RemoveAt(MonitorEntries.Count - 1);
    }

    public void Dispose()
    {
        foreach (var graph in Graphs)
        {
            graph.MonitorRaised -= OnGraphMonitorRaised;
            graph.Dispose();
        }
        Graphs.Clear();
        SelectedGraph = null;
    }
}

public partial class ControlGraphRowViewModel : ViewModelBase, IDisposable
{
    private readonly ObservableCollection<ActionEndpoint> _endpoints;
    private ControlGraphSession? _session;

    public ControlGraphRowViewModel(ControlGraphConfig config, ObservableCollection<ActionEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(config);
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _name = config.Name;
        _isEnabled = config.IsEnabled;
        Editor = new ControlGraphEditorViewModel(config);
        Editor.GraphChanged += (_, _) => RefreshTopology();
        Editor.MonitorRaised += (_, entry) => MonitorRaised?.Invoke(this, entry);
        HealthState = ControlSessionState.Stopped.ToString();
        HealthDetail = "Stopped";
    }

    public event EventHandler<ControlGraphMonitorEntryViewModel>? MonitorRaised;

    public Guid Id => Editor.Id;
    public ControlGraphEditorViewModel Editor { get; }
    public int NodeCount => Editor.EditorNodes.Count;
    public int ConnectionCount => Editor.EditorConnections.Count;
    public string Summary => $"{NodeCount} node(s), {ConnectionCount} connection(s)";

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _healthState;

    [ObservableProperty]
    private string _healthDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastError))]
    private string? _lastError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScriptDiagnostics))]
    private string? _scriptDiagnostics;

    public bool IsRunning => _session?.IsRunning == true;
    public bool HasScriptDiagnostics => !string.IsNullOrWhiteSpace(ScriptDiagnostics);
    public bool HasLastError => !string.IsNullOrWhiteSpace(LastError);

    public ControlGraphConfig BuildSnapshot() =>
        Editor.BuildSnapshot() with
        {
            Name = Name,
            IsEnabled = IsEnabled,
        };

    public void RefreshTopology()
    {
        OnPropertyChanged(nameof(NodeCount));
        OnPropertyChanged(nameof(ConnectionCount));
        OnPropertyChanged(nameof(Summary));
    }

    public async Task StartAsync()
    {
        if (IsRunning)
            return;

        LastError = null;
        ScriptDiagnostics = null;
        _session?.Dispose();
        _session = new ControlGraphSession(BuildSnapshot(), _endpoints);

        try
        {
            await _session.StartAsync();
            if (_session.Runtime is not null)
                _session.Runtime.EventDispatched += OnRuntimeEventDispatched;
            RefreshHealth();
            MonitorRaised?.Invoke(this, new ControlGraphMonitorEntryViewModel(DateTimeOffset.Now, "Runtime", $"Started {Name}"));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            RefreshHealth();
            MonitorRaised?.Invoke(this, new ControlGraphMonitorEntryViewModel(DateTimeOffset.Now, "Runtime", $"Failed to start {Name}: {ex.Message}"));
        }

        OnPropertyChanged(nameof(IsRunning));
    }

    public async Task StopAsync()
    {
        if (_session is null)
            return;

        CaptureScriptDiagnostics();
        if (_session.Runtime is not null)
            _session.Runtime.EventDispatched -= OnRuntimeEventDispatched;
        await _session.StopAsync();
        RefreshHealth();
        MonitorRaised?.Invoke(this, new ControlGraphMonitorEntryViewModel(DateTimeOffset.Now, "Runtime", $"Stopped {Name}"));
        OnPropertyChanged(nameof(IsRunning));
    }

    private void OnRuntimeEventDispatched(object? sender, ControlEvent evt)
    {
        var detail = evt switch
        {
            MidiControlEvent midi => $"MIDI ch {midi.Channel} cc {midi.Controller} = {midi.Value}",
            OscControlEvent osc => $"OSC {osc.Address} ({osc.Arguments.Count} arg(s))",
            ScalarControlEvent scalar => $"Scalar {scalar.Value:0.###}",
            TextControlEvent text => $"Text {text.Value}",
            BlobControlEvent blob => $"Blob {blob.Value.Length} byte(s)",
            _ => evt.GetType().Name,
        };
        MonitorRaised?.Invoke(this, new ControlGraphMonitorEntryViewModel(DateTimeOffset.Now, "Event", detail));
    }

    private void RefreshHealth()
    {
        var health = _session?.Health ?? ControlSessionHealth.Stopped();
        HealthState = health.State.ToString();
        HealthDetail = string.IsNullOrWhiteSpace(health.Detail) ? health.State.ToString() : health.Detail;
    }

    private void CaptureScriptDiagnostics()
    {
        var diagnostics = _session?.Runtime?.ScriptDiagnostics;
        if (diagnostics is null || diagnostics.Count == 0)
            return;

        ScriptDiagnostics = string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Stage}: {d.Message}"));
        foreach (var diagnostic in diagnostics)
            MonitorRaised?.Invoke(this, new ControlGraphMonitorEntryViewModel(DateTimeOffset.Now, "Script", $"{diagnostic.Stage}: {diagnostic.Message}"));
    }

    public void Dispose()
    {
        CaptureScriptDiagnostics();
        if (_session?.Runtime is not null)
            _session.Runtime.EventDispatched -= OnRuntimeEventDispatched;
        _session?.Dispose();
        _session = null;
        Editor.GraphChanged -= (_, _) => RefreshTopology();
        RefreshHealth();
        OnPropertyChanged(nameof(IsRunning));
    }
}

public partial class ControlGraphEditorViewModel : NodifyEditorViewModelBase
{
    private readonly Guid _id;

    public ControlGraphEditorViewModel(ControlGraphConfig config)
    {
        _id = config.Id;
        _name = config.Name;
        _offsetX = config.ViewportX;
        _offsetY = config.ViewportY;
        _zoom = config.Zoom <= 0 ? 1.0 : config.Zoom;

        foreach (var node in config.Nodes)
            AddNodeInternal(new ControlGraphNodeViewModel(node));

        foreach (var connection in config.Connections)
            AddConnectionInternal(connection);

        SelectedNodes.CollectionChanged += OnSelectedNodesChanged;
    }

    public event EventHandler? GraphChanged;
    public event EventHandler<ControlGraphMonitorEntryViewModel>? MonitorRaised;

    public Guid Id => _id;
    public ObservableCollection<ControlGraphNodeViewModel> EditorNodes { get; } = new();
    public ObservableCollection<ControlGraphConnectionViewModel> EditorConnections { get; } = new();

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private double _offsetX;

    [ObservableProperty]
    private double _offsetY;

    [ObservableProperty]
    private double _zoom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedNode))]
    private ControlGraphNodeViewModel? _selectedEditorNode;

    public bool HasSelectedNode => SelectedEditorNode is not null;

    public ControlGraphNodeViewModel AddNode(ControlNodeConfig config)
    {
        var node = new ControlGraphNodeViewModel(config);
        AddNodeInternal(node);
        SelectedEditorNode = node;
        GraphChanged?.Invoke(this, EventArgs.Empty);
        return node;
    }

    public void RemoveNode(ControlGraphNodeViewModel node)
    {
        var connections = EditorConnections
            .Where(c => c.SourceNode == node || c.TargetNode == node)
            .ToList();
        foreach (var connection in connections)
        {
            EditorConnections.Remove(connection);
            Connections.Remove(connection);
        }

        EditorNodes.Remove(node);
        Nodes.Remove(node);
        if (ReferenceEquals(SelectedEditorNode, node))
            SelectedEditorNode = EditorNodes.FirstOrDefault();
        GraphChanged?.Invoke(this, EventArgs.Empty);
    }

    public override void Connect(ConnectorViewModelBase source, ConnectorViewModelBase target)
    {
        if (source is not ControlGraphConnectorViewModel sourceConnector
            || target is not ControlGraphConnectorViewModel targetConnector)
            return;

        if (sourceConnector.Flow == ConnectorViewModelBase.ConnectorFlow.Input)
            (sourceConnector, targetConnector) = (targetConnector, sourceConnector);
        if (sourceConnector.Flow != ConnectorViewModelBase.ConnectorFlow.Output
            || targetConnector.Flow != ConnectorViewModelBase.ConnectorFlow.Input
            || sourceConnector.Node == targetConnector.Node)
        {
            return;
        }

        var config = new ControlConnectionConfig
        {
            FromNodeId = sourceConnector.Node.Id,
            FromPortId = sourceConnector.PortId,
            ToNodeId = targetConnector.Node.Id,
            ToPortId = targetConnector.PortId,
        };
        var connection = new ControlGraphConnectionViewModel(this, config, sourceConnector, targetConnector);
        EditorConnections.Add(connection);
        Connections.Add(connection);
        sourceConnector.IsConnected = true;
        targetConnector.IsConnected = true;
        GraphChanged?.Invoke(this, EventArgs.Empty);
        MonitorRaised?.Invoke(this, new ControlGraphMonitorEntryViewModel(DateTimeOffset.Now, "Connection", $"{sourceConnector.Node.Title} -> {targetConnector.Node.Title}"));
    }

    public ControlGraphConfig BuildSnapshot() => new()
    {
        Id = Id,
        Name = Name,
        IsEnabled = true,
        ViewportX = OffsetX,
        ViewportY = OffsetY,
        Zoom = Zoom <= 0 ? 1.0 : Zoom,
        Nodes = EditorNodes.Select(n => n.BuildConfig()).ToList(),
        Connections = EditorConnections.Select(c => c.BuildConfig()).ToList(),
    };

    partial void OnOffsetXChanged(double value) => GraphChanged?.Invoke(this, EventArgs.Empty);
    partial void OnOffsetYChanged(double value) => GraphChanged?.Invoke(this, EventArgs.Empty);
    partial void OnZoomChanged(double value) => GraphChanged?.Invoke(this, EventArgs.Empty);

    private void AddNodeInternal(ControlGraphNodeViewModel node)
    {
        EditorNodes.Add(node);
        Nodes.Add(node);
    }

    private void AddConnectionInternal(ControlConnectionConfig connection)
    {
        var sourceNode = EditorNodes.FirstOrDefault(n => n.Id == connection.FromNodeId);
        var targetNode = EditorNodes.FirstOrDefault(n => n.Id == connection.ToNodeId);
        var source = sourceNode?.Outputs.FirstOrDefault(p => p.PortId == connection.FromPortId);
        var target = targetNode?.Inputs.FirstOrDefault(p => p.PortId == connection.ToPortId);
        if (sourceNode is null || targetNode is null || source is null || target is null)
            return;

        var vm = new ControlGraphConnectionViewModel(this, connection, source, target);
        EditorConnections.Add(vm);
        Connections.Add(vm);
        source.IsConnected = true;
        target.IsConnected = true;
    }

    private void OnSelectedNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SelectedEditorNode = SelectedNodes.OfType<ControlGraphNodeViewModel>().LastOrDefault();
    }
}

public partial class ControlGraphNodeViewModel : ViewModelBase, INodePosition
{
    private readonly Guid _id;
    private readonly ControlNodeKind _kind;

    public ControlGraphNodeViewModel(ControlNodeConfig config)
    {
        _id = config.Id;
        _kind = config.Kind;
        _title = string.IsNullOrWhiteSpace(config.DisplayName) ? DisplayNameForKind(config.Kind) : config.DisplayName;
        _location = new Point(config.X, config.Y);
        LoadSettings(config.Settings);
        Inputs.Add(new ControlGraphConnectorViewModel(this, "in", "In", ControlPortType.Any, ConnectorViewModelBase.ConnectorFlow.Input));
        Outputs.Add(new ControlGraphConnectorViewModel(this, "out", "Out", OutputTypeForKind(config.Kind), ConnectorViewModelBase.ConnectorFlow.Output));
    }

    public Guid Id => _id;
    public ControlNodeKind Kind => _kind;
    public string KindLabel => Kind.ToString();
    public ObservableCollection<ControlGraphConnectorViewModel> Inputs { get; } = new();
    public ObservableCollection<ControlGraphConnectorViewModel> Outputs { get; } = new();

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private Point _location;

    [ObservableProperty]
    private int _midiChannel = 1;

    [ObservableProperty]
    private int _midiController;

    [ObservableProperty]
    private bool _highResolution14Bit;

    [ObservableProperty]
    private int _oscLocalPort = 9000;

    [ObservableProperty]
    private string _oscAddressPattern = "/ch/01/mix/fader";

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private int _port = 10023;

    [ObservableProperty]
    private string _oscAddress = "/ch/01/mix/fader";

    [ObservableProperty]
    private int _x32Channel = 1;

    [ObservableProperty]
    private double _mapInputMin;

    [ObservableProperty]
    private double _mapInputMax = 127;

    [ObservableProperty]
    private double _mapOutputMin;

    [ObservableProperty]
    private double _mapOutputMax = 1;

    [ObservableProperty]
    private bool _mapClamp = true;

    [ObservableProperty]
    private string _scriptSource = "return emit.scalar(event.value);";

    [ObservableProperty]
    private int _scriptInstructionLimit = 100_000;

    public bool IsMidiInput => Kind == ControlNodeKind.MidiInput;
    public bool IsMidiOutput => Kind == ControlNodeKind.MidiOutput;
    public bool IsOscInput => Kind == ControlNodeKind.OscInput;
    public bool IsOscOutput => Kind == ControlNodeKind.OscOutput;
    public bool IsX32ChannelFader => Kind == ControlNodeKind.X32ChannelFader;
    public bool IsMapRange => Kind == ControlNodeKind.MapRange;
    public bool IsScriptTransform => Kind == ControlNodeKind.ScriptTransform;

    public ControlNodeConfig BuildConfig() => new()
    {
        Id = Id,
        DisplayName = Title,
        Kind = Kind,
        X = Location.X,
        Y = Location.Y,
        Settings = BuildSettings(),
    };

    private ControlNodeSettings BuildSettings() =>
        Kind switch
        {
            ControlNodeKind.MidiInput => new MidiInputControlNodeSettings
            {
                Channel = MidiChannel,
                Controller = MidiController,
                HighResolution14Bit = HighResolution14Bit,
            },
            ControlNodeKind.OscInput => new OscInputControlNodeSettings
            {
                LocalPort = OscLocalPort,
                AddressPattern = OscAddressPattern,
            },
            ControlNodeKind.MapRange => new MapRangeControlNodeSettings
            {
                InputMin = MapInputMin,
                InputMax = MapInputMax,
                OutputMin = MapOutputMin,
                OutputMax = MapOutputMax,
                Clamp = MapClamp,
            },
            ControlNodeKind.OscOutput => new OscOutputControlNodeSettings
            {
                Host = Host,
                Port = Port,
                Address = OscAddress,
            },
            ControlNodeKind.MidiOutput => new MidiOutputControlNodeSettings
            {
                Channel = MidiChannel,
                Controller = MidiController,
                HighResolution14Bit = HighResolution14Bit,
            },
            ControlNodeKind.X32ChannelFader => new X32ChannelFaderControlNodeSettings
            {
                Host = Host,
                Port = Port,
                Channel = X32Channel,
            },
            ControlNodeKind.ScriptTransform => new ScriptTransformControlNodeSettings
            {
                Source = ScriptSource,
                InstructionLimit = ScriptInstructionLimit,
            },
            _ => new PassthroughControlNodeSettings(),
        };

    private void LoadSettings(ControlNodeSettings settings)
    {
        switch (settings)
        {
            case MidiInputControlNodeSettings midi:
                MidiChannel = midi.Channel;
                MidiController = midi.Controller;
                HighResolution14Bit = midi.HighResolution14Bit;
                break;
            case MidiOutputControlNodeSettings midi:
                MidiChannel = midi.Channel;
                MidiController = midi.Controller;
                HighResolution14Bit = midi.HighResolution14Bit;
                break;
            case OscInputControlNodeSettings osc:
                OscLocalPort = osc.LocalPort;
                OscAddressPattern = osc.AddressPattern;
                break;
            case OscOutputControlNodeSettings osc:
                Host = osc.Host;
                Port = osc.Port;
                OscAddress = osc.Address;
                break;
            case X32ChannelFaderControlNodeSettings x32:
                Host = x32.Host;
                Port = x32.Port;
                X32Channel = x32.Channel;
                break;
            case MapRangeControlNodeSettings map:
                MapInputMin = map.InputMin;
                MapInputMax = map.InputMax;
                MapOutputMin = map.OutputMin;
                MapOutputMax = map.OutputMax;
                MapClamp = map.Clamp;
                break;
            case ScriptTransformControlNodeSettings script:
                ScriptSource = script.Source;
                ScriptInstructionLimit = script.InstructionLimit;
                break;
        }
    }

    private static ControlPortType OutputTypeForKind(ControlNodeKind kind) =>
        kind switch
        {
            ControlNodeKind.MidiInput => ControlPortType.Midi,
            ControlNodeKind.OscInput => ControlPortType.Osc,
            ControlNodeKind.MapRange => ControlPortType.Scalar,
            _ => ControlPortType.Any,
        };

    private static string DisplayNameForKind(ControlNodeKind kind) =>
        kind switch
        {
            ControlNodeKind.MidiInput => "MIDI Input",
            ControlNodeKind.OscInput => "OSC Input",
            ControlNodeKind.MapRange => "Map Range",
            ControlNodeKind.OscOutput => "OSC Output",
            ControlNodeKind.MidiOutput => "MIDI Output",
            ControlNodeKind.X32ChannelFader => "X32 Channel Fader",
            ControlNodeKind.ScriptTransform => "Mond Script",
            _ => "Passthrough",
        };
}

public sealed class ControlGraphConnectorViewModel : ConnectorViewModelBase
{
    public ControlGraphConnectorViewModel(
        ControlGraphNodeViewModel node,
        string portId,
        string title,
        ControlPortType portType,
        ConnectorFlow flow)
    {
        Node = node;
        PortId = portId;
        PortType = portType;
        Title = title;
        Flow = flow;
    }

    public ControlGraphNodeViewModel Node { get; }
    public string PortId { get; }
    public ControlPortType PortType { get; }
}

public sealed class ControlGraphConnectionViewModel : ConnectionViewModelBase
{
    private readonly ControlConnectionConfig _config;

    public ControlGraphConnectionViewModel(
        NodifyEditorViewModelBase editor,
        ControlConnectionConfig config,
        ControlGraphConnectorViewModel source,
        ControlGraphConnectorViewModel target)
        : base(editor, source, target, $"{source.PortId} -> {target.PortId}")
    {
        _config = config;
        SourceNode = source.Node;
        TargetNode = target.Node;
    }

    public ControlGraphNodeViewModel SourceNode { get; }
    public ControlGraphNodeViewModel TargetNode { get; }

    public ControlConnectionConfig BuildConfig() => _config with
    {
        FromNodeId = SourceNode.Id,
        FromPortId = ((ControlGraphConnectorViewModel)Source).PortId,
        ToNodeId = TargetNode.Id,
        ToPortId = ((ControlGraphConnectorViewModel)Target).PortId,
    };
}

public sealed record ControlNodePaletteGroup(string Name, IReadOnlyList<ControlNodePaletteItem> Items);

public sealed record ControlNodePaletteItem(string Label, ControlNodeKind Kind, bool IsPreset)
{
    public static ControlNodePaletteItem ForKind(ControlNodeKind kind, string label) => new(label, kind, IsPreset: false);
    public static ControlNodePaletteItem Preset(string label, ControlNodeKind kind) => new(label, kind, IsPreset: true);

    public ControlNodeConfig CreateConfig()
    {
        var settings = Kind switch
        {
            ControlNodeKind.MidiInput => new MidiInputControlNodeSettings(),
            ControlNodeKind.OscInput => new OscInputControlNodeSettings(),
            ControlNodeKind.MapRange => new MapRangeControlNodeSettings(),
            ControlNodeKind.OscOutput => new OscOutputControlNodeSettings(),
            ControlNodeKind.MidiOutput => new MidiOutputControlNodeSettings(),
            ControlNodeKind.X32ChannelFader => new X32ChannelFaderControlNodeSettings(),
            ControlNodeKind.ScriptTransform => new ScriptTransformControlNodeSettings(),
            _ => (ControlNodeSettings)new PassthroughControlNodeSettings(),
        };
        if (IsPreset && Label.Contains("BCF2000", StringComparison.OrdinalIgnoreCase))
        {
            settings = new X32ChannelFaderControlNodeSettings { Channel = 1 };
        }
        return new ControlNodeConfig
        {
            DisplayName = Label,
            Kind = Kind,
            X = 80,
            Y = 80,
            Settings = settings,
        };
    }
}

public sealed record ControlGraphMonitorEntryViewModel(DateTimeOffset Timestamp, string Kind, string Detail)
{
    public string TimestampDisplay => Timestamp.ToString("HH:mm:ss");
}
