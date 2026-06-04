using System.Net;
using HaPlay.ControlGraph;
using HaPlay.Models;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlMonitorTests
{
    [Fact]
    public void ControlMonitorBuffer_DropsOldestRecordsPastLimit()
    {
        var buffer = new ControlMonitorBuffer(maxRecords: 2);

        buffer.Record(Record("/one"));
        buffer.Record(Record("/two"));
        buffer.Record(Record("/three"));

        Assert.Collection(
            buffer.Records,
            record => Assert.Equal("/two", record.Address),
            record => Assert.Equal("/three", record.Address));
    }

    [Fact]
    public void JsonLines_RoundTripsMonitorRecords()
    {
        var id = Guid.NewGuid();
        var record = new ControlMonitorRecord
        {
            DeviceInstanceId = id,
            Direction = ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.Osc,
            Result = ControlMonitorResult.Received,
            RemoteHost = "127.0.0.1",
            RemotePort = 10023,
            Address = "/ch/01/mix/fader",
            OscArguments = [ControlMonitorOscArgumentRecord.FromOscArgument(OSCArgument.Float32(0.75f))],
        };

        var jsonLines = ControlMonitorJsonLines.Write([record]);
        var restored = Assert.Single(ControlMonitorJsonLines.Read(jsonLines));

        Assert.Equal(id, restored.DeviceInstanceId);
        Assert.Equal(ControlMonitorDirection.Input, restored.Direction);
        Assert.Equal(ControlMonitorProtocol.Osc, restored.Protocol);
        Assert.Equal(ControlMonitorResult.Received, restored.Result);
        Assert.Equal("/ch/01/mix/fader", restored.Address);
        Assert.Equal(0.75, Assert.Single(restored.OscArguments).FloatValue!.Value, precision: 6);
    }

    [Fact]
    public async Task RuntimeSession_RecordsScriptInvocationAndOscOutput()
    {
        var monitor = new ControlMonitorBuffer(maxRecords: 10);
        var sender = new RecordingOscSender();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            Devices = [OscDevice(Guid.NewGuid(), "X32", "x32", "127.0.0.1", 10023)],
            Scripts =
            [
                new ControlScriptConfig
                {
                    Id = Guid.NewGuid(),
                    Name = "Manual send",
                    ScriptPath = "Scripts/send.mnd",
                    Triggers =
                    [
                        new ControlScriptTriggerConfig
                        {
                            Kind = ControlScriptTriggerKind.Manual,
                            FunctionName = "run",
                        },
                    ],
                },
            ],
        };
        var session = new ControlScriptRuntimeSession(
            config,
            new InMemoryControlScriptSourceProvider(new Dictionary<string, string>
            {
                ["Scripts/send.mnd"] =
                    """
                    export fun run(event, context) {
                        osc.send("x32", "/test", osc.float32(1));
                    }
                    """,
            }),
            sender,
            monitor: monitor);

        await session.DispatchManualAsync();

        Assert.Contains(monitor.Records, r => r.Protocol == ControlMonitorProtocol.Script && r.Result == ControlMonitorResult.Invoked);
        var output = Assert.Single(monitor.Records, r => r.Protocol == ControlMonitorProtocol.Osc && r.Direction == ControlMonitorDirection.Output);
        Assert.Equal("/test", output.Address);
        Assert.Equal("127.0.0.1", output.RemoteHost);
        Assert.Equal(10023, output.RemotePort);
        Assert.Equal(1, Assert.Single(output.OscArguments).FloatValue!.Value);
    }

    [Fact]
    public async Task OscListenerManager_RecordsInputAndDroppedOscMessages()
    {
        var monitor = new ControlMonitorBuffer(maxRecords: 10);
        var listenerId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var config = new ControlSystemConfig
        {
            IsArmed = true,
            OscListeners = [new ControlOscListenerConfig { Id = listenerId, LocalPort = 10020 }],
            Devices = [OscDevice(deviceId, "X32", "x32", "127.0.0.1", 10023, listenerId)],
            Scripts =
            [
                new ControlScriptConfig
                {
                    Id = Guid.NewGuid(),
                    ScriptPath = "Scripts/on-osc.mnd",
                    DeviceInstanceId = deviceId,
                    Scope = ControlScriptScope.Device,
                    Triggers =
                    [
                        new ControlScriptTriggerConfig
                        {
                            Kind = ControlScriptTriggerKind.OscMessage,
                            FunctionName = "onOsc",
                            DeviceInstanceId = deviceId,
                            OscAddressPattern = "/ch/*/mix/fader",
                        },
                    ],
                },
            ],
        };
        var session = new ControlScriptRuntimeSession(
            config,
            new InMemoryControlScriptSourceProvider(new Dictionary<string, string>
            {
                ["Scripts/on-osc.mnd"] =
                    """
                    export fun onOsc(event, context) {
                    }
                    """,
            }),
            new RecordingOscSender(),
            monitor: monitor);
        await using var manager = new ControlOscListenerManager(config, session, monitor);

        await manager.DispatchMessageAsync(
            listenerId,
            Context("127.0.0.1", 10023, "/ch/01/mix/fader", [OSCArgument.Float32(0.5f)]));
        await manager.DispatchMessageAsync(
            listenerId,
            Context("127.0.0.2", 10023, "/ch/01/mix/fader", [OSCArgument.Float32(0.5f)]));

        var input = Assert.Single(monitor.Records, r => r.Direction == ControlMonitorDirection.Input);
        Assert.Equal(listenerId, input.ListenerId);
        Assert.Equal(deviceId, input.DeviceInstanceId);
        Assert.Equal("127.0.0.1", input.RemoteHost);
        Assert.Equal(10023, input.RemotePort);
        Assert.Equal("/ch/01/mix/fader", input.Address);

        var dropped = Assert.Single(monitor.Records, r => r.Direction == ControlMonitorDirection.Dropped);
        Assert.Equal(ControlMonitorResult.Dropped, dropped.Result);
        Assert.Equal("No matching OSC device", dropped.Message);
        Assert.Equal("127.0.0.2", dropped.RemoteHost);
    }

    private static ControlMonitorRecord Record(string address) =>
        new()
        {
            Direction = ControlMonitorDirection.Input,
            Protocol = ControlMonitorProtocol.Osc,
            Result = ControlMonitorResult.Received,
            Address = address,
        };

    private static ControlDeviceInstanceConfig OscDevice(
        Guid id,
        string name,
        string alias,
        string host,
        int port,
        Guid? listenerId = null) =>
        new()
        {
            Id = id,
            Name = name,
            ProfileId = "x32",
            Protocol = ControlDeviceProtocol.Osc,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                OscHost = host,
                OscPort = port,
                OscListenerId = listenerId,
            },
        };

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

    private sealed class RecordingOscSender : IControlOscSender
    {
        public ValueTask SendAsync(
            string host,
            int port,
            string address,
            IReadOnlyList<OSCArgument> arguments,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
