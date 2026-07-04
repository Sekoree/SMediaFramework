using S.Control;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlPeriodicOSCSendManagerTests
{
    [Fact]
    public async Task TickAsync_SendsDuePeriodicOSCCommandsForMultipleDevices()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var sender = new RecordingOSCSender();
        var manager = new ControlPeriodicOSCSendManager(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices =
                [
                    OSCDevice(firstId, "X32 Main", "x32-main", "192.168.2.76", 10023, Periodic("/xremote", intervalMs: 8000)),
                    OSCDevice(secondId, "X32 Monitor", "x32-mon", "192.168.2.77", 10023, Periodic("/xremote", intervalMs: 8000)),
                ],
            },
            sender);
        var now = DateTimeOffset.Parse("2026-06-04T10:00:00Z");

        var first = await manager.TickAsync(now);
        var tooSoon = await manager.TickAsync(now.AddMilliseconds(7999));
        var second = await manager.TickAsync(now.AddMilliseconds(8000));

        Assert.All(first, result => Assert.True(result.Succeeded));
        Assert.Empty(tooSoon);
        Assert.All(second, result => Assert.True(result.Succeeded));
        Assert.Collection(
            sender.Sent,
            sent =>
            {
                Assert.Equal("192.168.2.76", sent.Host);
                Assert.Equal("/xremote", sent.Address);
            },
            sent =>
            {
                Assert.Equal("192.168.2.77", sent.Host);
                Assert.Equal("/xremote", sent.Address);
            },
            sent =>
            {
                Assert.Equal("192.168.2.76", sent.Host);
                Assert.Equal("/xremote", sent.Address);
            },
            sent =>
            {
                Assert.Equal("192.168.2.77", sent.Host);
                Assert.Equal("/xremote", sent.Address);
            });
    }

    [Fact]
    public async Task TickAsync_DoesNotSendWhenControlSystemIsDisarmed()
    {
        var sender = new RecordingOSCSender();
        var manager = new ControlPeriodicOSCSendManager(
            new ControlSystemConfig
            {
                IsArmed = false,
                Devices = [OSCDevice(Guid.NewGuid(), "X32", "x32", "192.168.2.76", 10023, Periodic("/xremote"))],
            },
            sender);

        var results = await manager.TickAsync(DateTimeOffset.UtcNow);

        Assert.Empty(results);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task TickAsync_ConvertsArgumentsAndRecordsMonitorOutput()
    {
        var deviceId = Guid.NewGuid();
        var monitor = new ControlMonitorBuffer(maxRecords: 10);
        var sender = new RecordingOSCSender();
        var manager = new ControlPeriodicOSCSendManager(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices =
                [
                    OSCDevice(
                        deviceId,
                        "X32",
                        "x32",
                        "192.168.2.76",
                        10023,
                        Periodic(
                            "/subscribe",
                            intervalMs: 8000,
                            [
                                new ControlOSCArgumentConfig { Kind = ControlOSCArgumentKind.String, StringValue = "/ch/01/mix/fader" },
                                new ControlOSCArgumentConfig { Kind = ControlOSCArgumentKind.Int32, IntegerValue = 50 },
                                new ControlOSCArgumentConfig { Kind = ControlOSCArgumentKind.True },
                            ])),
                ],
            },
            sender,
            monitor);

        var results = await manager.TickAsync(DateTimeOffset.UtcNow);

        Assert.True(Assert.Single(results).Succeeded);
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("/subscribe", sent.Address);
        Assert.Equal("/ch/01/mix/fader", sent.Arguments[0].AsString());
        Assert.Equal(50, sent.Arguments[1].AsInt32());
        Assert.Equal(OSCArgumentType.True, sent.Arguments[2].Type);

        var output = Assert.Single(monitor.Records);
        Assert.Equal(ControlMonitorDirection.Output, output.Direction);
        Assert.Equal(ControlMonitorProtocol.OSC, output.Protocol);
        Assert.Equal(deviceId, output.DeviceInstanceId);
        Assert.Equal("/subscribe", output.Address);
        Assert.Equal("192.168.2.76", output.RemoteHost);
        Assert.Equal(10023, output.RemotePort);
        Assert.Equal(3, output.OSCArguments.Count);
    }

    [Fact]
    public async Task TickAsync_ReturnsFailureAndMonitorErrorForMissingBinding()
    {
        var monitor = new ControlMonitorBuffer(maxRecords: 10);
        var sender = new RecordingOSCSender();
        var manager = new ControlPeriodicOSCSendManager(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices =
                [
                    new ControlDeviceInstanceConfig
                    {
                        Id = Guid.NewGuid(),
                        Name = "X32",
                        Protocol = ControlDeviceProtocol.OSC,
                        IsEnabled = true,
                        Binding = new ControlDeviceBindingConfig { Alias = "x32" },
                        PeriodicOSCSends = [Periodic("/xremote")],
                    },
                ],
            },
            sender,
            monitor);

        var result = Assert.Single(await manager.TickAsync(DateTimeOffset.UtcNow));

        Assert.False(result.Succeeded);
        Assert.Contains("does not have a host and port binding", result.ErrorMessage);
        Assert.Empty(sender.Sent);
        var error = Assert.Single(monitor.Records);
        Assert.Equal(ControlMonitorDirection.Error, error.Direction);
        Assert.Equal(ControlMonitorResult.Failed, error.Result);
        Assert.Equal("/xremote", error.Address);
    }

    [Fact]
    public async Task TickAsync_RetriesFailedAttemptsEveryTwoSeconds()
    {
        var sender = new FailingOSCSender();
        var manager = new ControlPeriodicOSCSendManager(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OSCDevice(Guid.NewGuid(), "X32", "x32", "192.168.2.76", 10023, Periodic("/xremote", intervalMs: 8000))],
            },
            sender);
        var now = DateTimeOffset.Parse("2026-06-04T10:00:00Z");

        var first = Assert.Single(await manager.TickAsync(now));
        var tooSoon = await manager.TickAsync(now.AddMilliseconds(500));
        var second = Assert.Single(await manager.TickAsync(now.AddSeconds(2)));

        Assert.False(first.Succeeded);
        Assert.Empty(tooSoon);
        Assert.False(second.Succeeded);
        Assert.Equal(2, sender.Attempts);
    }

    private static ControlDeviceInstanceConfig OSCDevice(
        Guid id,
        string name,
        string alias,
        string host,
        int port,
        ControlPeriodicOSCSendConfig periodic) =>
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
            },
            PeriodicOSCSends = [periodic],
        };

    private static ControlPeriodicOSCSendConfig Periodic(
        string address,
        int intervalMs = 8000,
        List<ControlOSCArgumentConfig>? arguments = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = address,
            Address = address,
            IntervalMs = intervalMs,
            Arguments = arguments ?? [],
        };

    private sealed class RecordingOSCSender : IControlOSCSender
    {
        public List<SentOSCMessage> Sent { get; } = new();

        public ValueTask SendAsync(
            string host,
            int port,
            string address,
            IReadOnlyList<OSCArgument> arguments,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentOSCMessage(host, port, address, arguments.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SentOSCMessage(
        string Host,
        int Port,
        string Address,
        IReadOnlyList<OSCArgument> Arguments);

    private sealed class FailingOSCSender : IControlOSCSender
    {
        public int Attempts { get; private set; }

        public ValueTask SendAsync(
            string host,
            int port,
            string address,
            IReadOnlyList<OSCArgument> arguments,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("network unavailable");
        }
    }
}
