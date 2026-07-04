using System.Net;
using System.Net.Sockets;
using S.Control;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlOSCListenerManagerTests
{
    [Fact]
    public async Task DispatchMessageAsync_RoutesIncomingOSCToMatchingDeviceAndScript()
    {
        var listenerId = Guid.NewGuid();
        var x32Id = Guid.NewGuid();
        var sender = new RecordingOSCSender();
        var config = ConfigWithListener(
            listenerId,
            devices: [OSCDevice(x32Id, "X32", "x32", "127.0.0.1", 10023, listenerId)],
            scripts:
            [
                OSCScript(x32Id, "Scripts/on-osc.mnd", "onOSC", "/ch/*/mix/fader"),
            ]);
        var session = CreateRuntimeSession(config, sender);
        await using var manager = new ControlOSCListenerManager(config, session);

        var results = await manager.DispatchMessageAsync(
            listenerId,
            Context("127.0.0.1", 10023, "/ch/01/mix/fader", [OSCArgument.Float32(0.6f)]));

        var route = Assert.Single(results);
        Assert.Equal(x32Id, route.DeviceInstanceId);
        Assert.True(Assert.Single(route.ScriptResult.Invocations).Succeeded);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("127.0.0.1", sent.Host);
        Assert.Equal(10023, sent.Port);
        Assert.Equal("/seen", sent.Address);
        Assert.Equal(0.6f, Assert.Single(sent.Arguments).AsFloat32());
    }

    [Fact]
    public async Task DispatchMessageAsync_CapturesRawOSCBytesInMonitor()
    {
        var listenerId = Guid.NewGuid();
        var x32Id = Guid.NewGuid();
        var sender = new RecordingOSCSender();
        var config = ConfigWithListener(
            listenerId,
            devices: [OSCDevice(x32Id, "X32", "x32", "127.0.0.1", 10023, listenerId)],
            scripts: []);
        var session = CreateRuntimeSession(config, sender);
        var monitor = new ControlMonitorBuffer(maxRecords: 20);
        await using var manager = new ControlOSCListenerManager(config, session, monitor);

        await manager.DispatchMessageAsync(
            listenerId,
            Context("127.0.0.1", 10023, "/ch/01/mix/fader", [OSCArgument.Float32(0.6f)]));

        var input = Assert.Single(
            monitor.Records,
            r => r.Direction == ControlMonitorDirection.Input && r.Protocol == ControlMonitorProtocol.OSC);
        Assert.NotNull(input.RawBytes);
        Assert.NotEmpty(input.RawBytes!);
        // OSC wire format leads with the address as a null-padded string, so the bytes contain it verbatim.
        Assert.Contains("/ch/01/mix/fader", System.Text.Encoding.ASCII.GetString(input.RawBytes!));
    }

    [Fact]
    public async Task DispatchMessageAsync_RoutesOnlyToMatchingDeviceOnSharedListener()
    {
        var listenerId = Guid.NewGuid();
        var x32Id = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var sender = new RecordingOSCSender();
        var config = ConfigWithListener(
            listenerId,
            devices:
            [
                OSCDevice(x32Id, "X32 A", "x32-a", "127.0.0.1", 10023, listenerId),
                OSCDevice(secondId, "X32 B", "x32-b", "127.0.0.2", 10023, listenerId),
            ],
            scripts:
            [
                OSCScript(x32Id, "Scripts/a.mnd", "onOSC", "/ch/*/mix/fader"),
                OSCScript(secondId, "Scripts/b.mnd", "onOSC", "/ch/*/mix/fader"),
            ]);
        var session = CreateRuntimeSession(config, sender);
        await using var manager = new ControlOSCListenerManager(config, session);

        var results = await manager.DispatchMessageAsync(
            listenerId,
            Context("127.0.0.2", 10023, "/ch/02/mix/fader", [OSCArgument.Float32(0.5f)]));

        var route = Assert.Single(results);
        Assert.Equal(secondId, route.DeviceInstanceId);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("127.0.0.2", sent.Host);
        Assert.Equal("/seen", sent.Address);
    }

    [Fact]
    public async Task DispatchMessageAsync_UsesFirstEnabledListenerForDeviceWithoutListenerBinding()
    {
        var listenerId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var sender = new RecordingOSCSender();
        var config = ConfigWithListener(
            listenerId,
            devices: [OSCDevice(deviceId, "X32", "x32", "127.0.0.1", 10023, listenerId: null)],
            scripts: [OSCScript(deviceId, "Scripts/on-osc.mnd", "onOSC", "/ch/*/mix/fader")]);
        var session = CreateRuntimeSession(config, sender);
        await using var manager = new ControlOSCListenerManager(config, session);

        var results = await manager.DispatchMessageAsync(
            listenerId,
            Context("127.0.0.1", 33445, "/ch/01/mix/fader", [OSCArgument.Float32(0.7f)]));

        Assert.Equal(deviceId, Assert.Single(results).DeviceInstanceId);
        Assert.Equal("/seen", Assert.Single(sender.Sent).Address);
    }

    [Fact]
    public async Task DispatchDeviceMessageAsync_RoutesClientReplyWithNoListenerConfigured()
    {
        var deviceId = Guid.NewGuid();
        var sender = new RecordingOSCSender();
        var device = OSCDevice(deviceId, "X32", "x32", "127.0.0.1", 10023, listenerId: null);
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            OSCListeners = [], // X32 replies arrive on the client socket; no app listener needed
            Devices = [device],
            Scripts = [OSCScript(deviceId, "Scripts/on-osc.mnd", "onOSC", "/ch/*/mix/fader")],
        };
        var monitor = new ControlMonitorBuffer(maxRecords: 20);
        var session = CreateRuntimeSession(config, sender, monitor);
        await using var manager = new ControlOSCListenerManager(config, session, monitor);

        var result = await manager.DispatchDeviceMessageAsync(
            device,
            Context("127.0.0.1", 10023, "/ch/01/mix/fader", [OSCArgument.Float32(0.42f)]));

        Assert.Equal(deviceId, result.DeviceInstanceId);
        Assert.True(Assert.Single(result.ScriptResult.Invocations).Succeeded);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("/seen", sent.Address);
        Assert.Equal(0.42f, Assert.Single(sent.Arguments).AsFloat32());
        Assert.Contains(
            monitor.Records,
            record => record.Direction == ControlMonitorDirection.Input
                      && record.Protocol == ControlMonitorProtocol.OSC
                      && record.Result == ControlMonitorResult.Received
                      && record.DeviceInstanceId == deviceId
                      && record.Address == "/ch/01/mix/fader");
    }

    [Fact]
    public async Task StartAsync_ReceivesUdpOSCAndRoutesThroughRuntimeSession()
    {
        var listenPort = GetFreeUdpPort();
        var listenerId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var sender = new RecordingOSCSender();
        var monitor = new ControlMonitorBuffer(maxRecords: 20);
        var config = ConfigWithListener(
            listenerId,
            localPort: listenPort,
            devices: [OSCDevice(deviceId, "X32", "x32", "127.0.0.1", 10023, listenerId)],
            scripts: [OSCScript(deviceId, "Scripts/on-osc.mnd", "onOSC", "/ch/*/mix/fader")]);
        var session = CreateRuntimeSession(config, sender, monitor);
        await using var manager = new ControlOSCListenerManager(config, session, monitor);
        await manager.StartAsync();
        await using var client = await OSCClient.CreateAsync("127.0.0.1", listenPort);

        await client.SendMessageAsync("/ch/01/mix/fader", [OSCArgument.Float32(0.75f)]);

        var sent = await sender.NextSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("/seen", sent.Address);
        Assert.Equal(0.75f, Assert.Single(sent.Arguments).AsFloat32());
        Assert.Equal(ControlSessionState.Running, manager.ListenerHealth[listenerId].State);
        Assert.Contains(
            monitor.Records,
            record => record.Direction == ControlMonitorDirection.Input
                      && record.Protocol == ControlMonitorProtocol.OSC
                      && record.Result == ControlMonitorResult.Received
                      && record.ListenerId == listenerId
                      && record.DeviceInstanceId == deviceId
                      && record.RemoteHost == "127.0.0.1"
                      && record.RemotePort > 0
                      && record.Address == "/ch/01/mix/fader"
                      && record.OSCArguments.Count == 1);
    }

    [Fact]
    public async Task StartAsync_RejectsDuplicateEnabledListenerPorts()
    {
        var port = GetFreeUdpPort();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            OSCListeners =
            [
                new ControlOSCListenerConfig { Id = Guid.NewGuid(), Name = "A", LocalPort = port },
                new ControlOSCListenerConfig { Id = Guid.NewGuid(), Name = "B", LocalPort = port },
            ],
        };
        var session = CreateRuntimeSession(config, new RecordingOSCSender());
        await using var manager = new ControlOSCListenerManager(config, session);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync());

        Assert.Contains($"local port {port}", ex.Message);
    }

    private static ControlSystemConfig ConfigWithListener(
        Guid listenerId,
        IReadOnlyList<ControlDeviceInstanceConfig> devices,
        IReadOnlyList<ControlScriptConfig> scripts,
        int localPort = 10020) =>
        new()
        {
            IsArmed = true,
            OSCListeners =
            [
                new ControlOSCListenerConfig
                {
                    Id = listenerId,
                    Name = "Main",
                    LocalPort = localPort,
                },
            ],
            Devices = devices.ToList(),
            Scripts = scripts.ToList(),
        };

    private static ControlDeviceInstanceConfig OSCDevice(
        Guid id,
        string name,
        string alias,
        string host,
        int port,
        Guid? listenerId) =>
        new()
        {
            Id = id,
            Name = name,
            ProfileId = "x32",
            Protocol = ControlDeviceProtocol.OSC,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                OSCHost = host,
                OSCPort = port,
                OSCListenerId = listenerId,
            },
        };

    private static ControlScriptConfig OSCScript(
        Guid deviceId,
        string scriptPath,
        string functionName,
        string addressPattern) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "OSC script",
            ScriptPath = scriptPath,
            DeviceInstanceId = deviceId,
            Scope = ControlScriptScope.Device,
            Triggers =
            [
                new ControlScriptTriggerConfig
                {
                    Kind = ControlScriptTriggerKind.OSCMessage,
                    FunctionName = functionName,
                    DeviceInstanceId = deviceId,
                    OSCAddressPattern = addressPattern,
                },
            ],
            Imports = [],
        };

    private static ControlScriptRuntimeSession CreateRuntimeSession(
        ControlSystemConfig config,
        RecordingOSCSender sender,
        IControlMonitorSink? monitor = null)
    {
        var sources = config.Scripts.ToDictionary(
            s => s.ScriptPath,
            s =>
            {
                var alias = config.Devices.First(d => d.Id == s.Triggers[0].DeviceInstanceId).Binding.Alias;
                return
                $$"""
                export fun {{s.Triggers[0].FunctionName}}(event, context) {
                    var current = osc.cacheFloat("{{alias}}", event.osc.address, 0.0);
                    osc.send("{{alias}}", "/seen", osc.float32(current));
                }
                """;
            });
        return new ControlScriptRuntimeSession(config, new InMemoryControlScriptSourceProvider(sources), sender, monitor: monitor);
    }

    private static OSCMessageContext Context(
        string host,
        int port,
        string address,
        IReadOnlyList<OSCArgument> arguments) =>
        new(
            new OSCMessage(address, arguments),
            new IPEndPoint(IPAddress.Parse(host), port),
            BundleTimeTag: null,
            DateTimeOffset.UtcNow);

    private static int GetFreeUdpPort()
    {
        using var udp = new UdpClient(0);
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private sealed class RecordingOSCSender : IControlOSCSender
    {
        public List<SentOSCMessage> Sent { get; } = new();

        public TaskCompletionSource<SentOSCMessage> NextSent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask SendAsync(
            string host,
            int port,
            string address,
            IReadOnlyList<OSCArgument> arguments,
            CancellationToken cancellationToken = default)
        {
            var sent = new SentOSCMessage(host, port, address, arguments.ToArray());
            Sent.Add(sent);
            NextSent.TrySetResult(sent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SentOSCMessage(
        string Host,
        int Port,
        string Address,
        IReadOnlyList<OSCArgument> Arguments);
}
