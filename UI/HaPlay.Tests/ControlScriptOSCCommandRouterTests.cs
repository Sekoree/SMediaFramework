using S.Control;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlScriptOSCCommandRouterTests
{
    [Fact]
    public async Task SendAllAsync_RoutesMessagesToMultipleOSCDevicesByAlias()
    {
        var x32Id = Guid.NewGuid();
        var lightsId = Guid.NewGuid();
        var sender = new RecordingOSCSender();
        var router = new ControlScriptOSCCommandRouter(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices =
                [
                    OSCDevice(x32Id, "X32", "x32", "192.168.2.76", 10023),
                    OSCDevice(lightsId, "Lights", "lights", "192.168.2.80", 9000),
                ],
            },
            sender);

        var results = await router.SendAllAsync(
        [
            new ControlScriptOSCMessage("x32", "/ch/01/mix/fader", [ControlScriptOSCArgument.Float32(0.75)]),
            new ControlScriptOSCMessage("lights", "/scene", [ControlScriptOSCArgument.Int32(4)]),
        ]);

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Collection(
            sender.Sent,
            sent =>
            {
                Assert.Equal("192.168.2.76", sent.Host);
                Assert.Equal(10023, sent.Port);
                Assert.Equal("/ch/01/mix/fader", sent.Address);
                Assert.Equal(0.75f, Assert.Single(sent.Arguments).AsFloat32());
            },
            sent =>
            {
                Assert.Equal("192.168.2.80", sent.Host);
                Assert.Equal(9000, sent.Port);
                Assert.Equal("/scene", sent.Address);
                Assert.Equal(4, Assert.Single(sent.Arguments).AsInt32());
            });
    }

    [Fact]
    public async Task SendAsync_DoesNotRouteToDisabledOSCDevice()
    {
        var sender = new RecordingOSCSender();
        var router = new ControlScriptOSCCommandRouter(
            new ControlSystemConfig
            {
                Devices = [OSCDevice(Guid.NewGuid(), "X32", "x32", "192.168.2.76", 10023, isEnabled: false)],
            },
            sender);

        var result = await router.SendAsync(
            new ControlScriptOSCMessage("x32", "/ch/01/mix/fader", [ControlScriptOSCArgument.Float32(0.75)]));

        Assert.False(result.Succeeded);
        Assert.Contains("No enabled OSC device matches key 'x32'", result.ErrorMessage);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task SendAsync_RejectsAmbiguousProfileKey()
    {
        var sender = new RecordingOSCSender();
        var router = new ControlScriptOSCCommandRouter(
            new ControlSystemConfig
            {
                Devices =
                [
                    OSCDevice(Guid.NewGuid(), "X32 Main", "main", "192.168.2.76", 10023, profileId: "x32"),
                    OSCDevice(Guid.NewGuid(), "X32 Monitor", "monitor", "192.168.2.77", 10023, profileId: "x32"),
                ],
            },
            sender);

        var result = await router.SendAsync(
            new ControlScriptOSCMessage("x32", "/ch/01/mix/fader", [ControlScriptOSCArgument.Float32(0.75)]));

        Assert.False(result.Succeeded);
        Assert.Contains("ambiguous", result.ErrorMessage);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task SendAsync_UpdatesCacheWhenOptimisticSendModeIsEnabled()
    {
        var cache = new ControlValueCache();
        var sender = new RecordingOSCSender();
        var router = new ControlScriptOSCCommandRouter(
            new ControlSystemConfig
            {
                OSCCacheUpdateMode = ControlOSCCacheUpdateMode.OptimisticSendAndIncoming,
                Devices = [OSCDevice(Guid.NewGuid(), "X32", "x32", "192.168.2.76", 10023, profileId: "x32")],
            },
            sender,
            cache);

        var result = await router.SendAsync(
            new ControlScriptOSCMessage("x32", "/ch/01/mix/fader", [ControlScriptOSCArgument.Float32(0.6)]));

        Assert.True(result.Succeeded);
        Assert.True(cache.TryGetNumber("x32", "/ch/01/mix/fader", out var value));
        Assert.Equal(0.6, value, precision: 12);
        Assert.True(cache.TryGet(new ControlValueCacheKey("x32", "/ch/01/mix/fader"), out var entry));
        Assert.Equal(ControlValueCacheSource.OptimisticSend, entry.Source);
    }

    [Fact]
    public async Task SendAsync_DoesNotUpdateCacheWhenIncomingOnlyModeIsEnabled()
    {
        var cache = new ControlValueCache();
        var sender = new RecordingOSCSender();
        var router = new ControlScriptOSCCommandRouter(
            new ControlSystemConfig
            {
                OSCCacheUpdateMode = ControlOSCCacheUpdateMode.IncomingOnly,
                Devices = [OSCDevice(Guid.NewGuid(), "X32", "x32", "192.168.2.76", 10023)],
            },
            sender,
            cache);

        var result = await router.SendAsync(
            new ControlScriptOSCMessage("x32", "/ch/01/mix/fader", [ControlScriptOSCArgument.Float32(0.6)]));

        Assert.True(result.Succeeded);
        Assert.False(cache.TryGetNumber("x32", "/ch/01/mix/fader", out _));
    }

    [Fact]
    public async Task SendAsync_OverrideForcesIncomingOnlyForMatchingCommandEvenWhenProjectIsOptimistic()
    {
        var cache = new ControlValueCache();
        var sender = new RecordingOSCSender();
        var router = new ControlScriptOSCCommandRouter(
            new ControlSystemConfig
            {
                OSCCacheUpdateMode = ControlOSCCacheUpdateMode.OptimisticSendAndIncoming,
                OSCCacheOverrides =
                [
                    new ControlOSCCacheCommandOverride
                    {
                        AddressPattern = "/ch/*/mix/fader",
                        Mode = ControlOSCCacheUpdateMode.IncomingOnly,
                    },
                ],
                Devices = [OSCDevice(Guid.NewGuid(), "X32", "x32", "192.168.2.76", 10023)],
            },
            sender,
            cache);

        var fader = await router.SendAsync(
            new ControlScriptOSCMessage("x32", "/ch/01/mix/fader", [ControlScriptOSCArgument.Float32(0.6)]));
        var mute = await router.SendAsync(
            new ControlScriptOSCMessage("x32", "/ch/01/mix/on", [ControlScriptOSCArgument.Int32(1)]));

        Assert.True(fader.Succeeded);
        Assert.True(mute.Succeeded);
        // The overridden fader command must not write optimistically...
        Assert.False(cache.TryGetNumber("x32", "/ch/01/mix/fader", out _));
        // ...but other commands still follow the optimistic project default.
        Assert.True(cache.TryGetNumber("x32", "/ch/01/mix/on", out var muteValue));
        Assert.Equal(1, muteValue, precision: 12);
    }

    [Fact]
    public async Task SendAsync_OverrideEnablesOptimisticForMatchingCommandWhenProjectIsIncomingOnly()
    {
        var cache = new ControlValueCache();
        var sender = new RecordingOSCSender();
        var router = new ControlScriptOSCCommandRouter(
            new ControlSystemConfig
            {
                OSCCacheUpdateMode = ControlOSCCacheUpdateMode.IncomingOnly,
                OSCCacheOverrides =
                [
                    new ControlOSCCacheCommandOverride
                    {
                        AddressPattern = "/ch/01/mix/fader",
                        Mode = ControlOSCCacheUpdateMode.OptimisticSendAndIncoming,
                    },
                ],
                Devices = [OSCDevice(Guid.NewGuid(), "X32", "x32", "192.168.2.76", 10023)],
            },
            sender,
            cache);

        var result = await router.SendAsync(
            new ControlScriptOSCMessage("x32", "/ch/01/mix/fader", [ControlScriptOSCArgument.Float32(0.6)]));

        Assert.True(result.Succeeded);
        Assert.True(cache.TryGetNumber("x32", "/ch/01/mix/fader", out var value));
        Assert.Equal(0.6, value, precision: 12);
    }

    [Fact]
    public async Task SendAsync_DeviceScopedOverrideOnlyAppliesToThatDevice()
    {
        var mainId = Guid.NewGuid();
        var monitorId = Guid.NewGuid();
        var cache = new ControlValueCache();
        var sender = new RecordingOSCSender();
        var router = new ControlScriptOSCCommandRouter(
            new ControlSystemConfig
            {
                OSCCacheUpdateMode = ControlOSCCacheUpdateMode.OptimisticSendAndIncoming,
                OSCCacheOverrides =
                [
                    new ControlOSCCacheCommandOverride
                    {
                        AddressPattern = "/ch/01/mix/fader",
                        DeviceInstanceId = mainId,
                        Mode = ControlOSCCacheUpdateMode.IncomingOnly,
                    },
                ],
                Devices =
                [
                    OSCDevice(mainId, "X32 Main", "main", "192.168.2.76", 10023),
                    OSCDevice(monitorId, "X32 Monitor", "monitor", "192.168.2.77", 10023),
                ],
            },
            sender,
            cache);

        await router.SendAsync(
            new ControlScriptOSCMessage("main", "/ch/01/mix/fader", [ControlScriptOSCArgument.Float32(0.6)]));
        await router.SendAsync(
            new ControlScriptOSCMessage("monitor", "/ch/01/mix/fader", [ControlScriptOSCArgument.Float32(0.6)]));

        Assert.False(cache.TryGetNumber("main", "/ch/01/mix/fader", out _));
        Assert.True(cache.TryGetNumber("monitor", "/ch/01/mix/fader", out var monitorValue));
        Assert.Equal(0.6, monitorValue, precision: 12);
    }

    [Fact]
    public async Task SendAsync_MoreSpecificOverrideWinsOverWildcard()
    {
        var x32Id = Guid.NewGuid();
        var cache = new ControlValueCache();
        var sender = new RecordingOSCSender();
        var router = new ControlScriptOSCCommandRouter(
            new ControlSystemConfig
            {
                OSCCacheUpdateMode = ControlOSCCacheUpdateMode.OptimisticSendAndIncoming,
                OSCCacheOverrides =
                [
                    new ControlOSCCacheCommandOverride
                    {
                        AddressPattern = "/ch/*/mix/fader",
                        Mode = ControlOSCCacheUpdateMode.IncomingOnly,
                    },
                    new ControlOSCCacheCommandOverride
                    {
                        AddressPattern = "/ch/01/mix/fader",
                        DeviceInstanceId = x32Id,
                        Mode = ControlOSCCacheUpdateMode.OptimisticSendAndIncoming,
                    },
                ],
                Devices = [OSCDevice(x32Id, "X32", "x32", "192.168.2.76", 10023)],
            },
            sender,
            cache);

        await router.SendAsync(
            new ControlScriptOSCMessage("x32", "/ch/01/mix/fader", [ControlScriptOSCArgument.Float32(0.6)]));
        await router.SendAsync(
            new ControlScriptOSCMessage("x32", "/ch/02/mix/fader", [ControlScriptOSCArgument.Float32(0.4)]));

        // Channel 1 matches the exact device-scoped override -> optimistic write wins.
        Assert.True(cache.TryGetNumber("x32", "/ch/01/mix/fader", out var ch1));
        Assert.Equal(0.6, ch1, precision: 12);
        // Channel 2 only matches the wildcard incoming-only override -> no optimistic write.
        Assert.False(cache.TryGetNumber("x32", "/ch/02/mix/fader", out _));
    }

    private static ControlDeviceInstanceConfig OSCDevice(
        Guid id,
        string name,
        string alias,
        string host,
        int port,
        bool isEnabled = true,
        string profileId = "generic-osc") =>
        new()
        {
            Id = id,
            Name = name,
            ProfileId = profileId,
            Protocol = ControlDeviceProtocol.OSC,
            IsEnabled = isEnabled,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                OSCHost = host,
                OSCPort = port,
            },
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
}
