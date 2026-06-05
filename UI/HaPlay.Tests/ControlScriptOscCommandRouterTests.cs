using S.Control;
using OSCLib;
using Xunit;

namespace HaPlay.Tests;

public sealed class ControlScriptOscCommandRouterTests
{
    [Fact]
    public async Task SendAllAsync_RoutesMessagesToMultipleOscDevicesByAlias()
    {
        var x32Id = Guid.NewGuid();
        var lightsId = Guid.NewGuid();
        var sender = new RecordingOscSender();
        var router = new ControlScriptOscCommandRouter(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices =
                [
                    OscDevice(x32Id, "X32", "x32", "192.168.2.76", 10023),
                    OscDevice(lightsId, "Lights", "lights", "192.168.2.80", 9000),
                ],
            },
            sender);

        var results = await router.SendAllAsync(
        [
            new ControlScriptOscMessage("x32", "/ch/01/mix/fader", [ControlScriptOscArgument.Float32(0.75)]),
            new ControlScriptOscMessage("lights", "/scene", [ControlScriptOscArgument.Int32(4)]),
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
    public async Task SendAsync_DoesNotRouteToDisabledOscDevice()
    {
        var sender = new RecordingOscSender();
        var router = new ControlScriptOscCommandRouter(
            new ControlSystemConfig
            {
                Devices = [OscDevice(Guid.NewGuid(), "X32", "x32", "192.168.2.76", 10023, isEnabled: false)],
            },
            sender);

        var result = await router.SendAsync(
            new ControlScriptOscMessage("x32", "/ch/01/mix/fader", [ControlScriptOscArgument.Float32(0.75)]));

        Assert.False(result.Succeeded);
        Assert.Contains("No enabled OSC device matches key 'x32'", result.ErrorMessage);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task SendAsync_RejectsAmbiguousProfileKey()
    {
        var sender = new RecordingOscSender();
        var router = new ControlScriptOscCommandRouter(
            new ControlSystemConfig
            {
                Devices =
                [
                    OscDevice(Guid.NewGuid(), "X32 Main", "main", "192.168.2.76", 10023, profileId: "x32"),
                    OscDevice(Guid.NewGuid(), "X32 Monitor", "monitor", "192.168.2.77", 10023, profileId: "x32"),
                ],
            },
            sender);

        var result = await router.SendAsync(
            new ControlScriptOscMessage("x32", "/ch/01/mix/fader", [ControlScriptOscArgument.Float32(0.75)]));

        Assert.False(result.Succeeded);
        Assert.Contains("ambiguous", result.ErrorMessage);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task SendAsync_UpdatesCacheWhenOptimisticSendModeIsEnabled()
    {
        var cache = new ControlValueCache();
        var sender = new RecordingOscSender();
        var router = new ControlScriptOscCommandRouter(
            new ControlSystemConfig
            {
                OscCacheUpdateMode = ControlOscCacheUpdateMode.OptimisticSendAndIncoming,
                Devices = [OscDevice(Guid.NewGuid(), "X32", "x32", "192.168.2.76", 10023, profileId: "x32")],
            },
            sender,
            cache);

        var result = await router.SendAsync(
            new ControlScriptOscMessage("x32", "/ch/01/mix/fader", [ControlScriptOscArgument.Float32(0.6)]));

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
        var sender = new RecordingOscSender();
        var router = new ControlScriptOscCommandRouter(
            new ControlSystemConfig
            {
                OscCacheUpdateMode = ControlOscCacheUpdateMode.IncomingOnly,
                Devices = [OscDevice(Guid.NewGuid(), "X32", "x32", "192.168.2.76", 10023)],
            },
            sender,
            cache);

        var result = await router.SendAsync(
            new ControlScriptOscMessage("x32", "/ch/01/mix/fader", [ControlScriptOscArgument.Float32(0.6)]));

        Assert.True(result.Succeeded);
        Assert.False(cache.TryGetNumber("x32", "/ch/01/mix/fader", out _));
    }

    [Fact]
    public async Task SendAsync_OverrideForcesIncomingOnlyForMatchingCommandEvenWhenProjectIsOptimistic()
    {
        var cache = new ControlValueCache();
        var sender = new RecordingOscSender();
        var router = new ControlScriptOscCommandRouter(
            new ControlSystemConfig
            {
                OscCacheUpdateMode = ControlOscCacheUpdateMode.OptimisticSendAndIncoming,
                OscCacheOverrides =
                [
                    new ControlOscCacheCommandOverride
                    {
                        AddressPattern = "/ch/*/mix/fader",
                        Mode = ControlOscCacheUpdateMode.IncomingOnly,
                    },
                ],
                Devices = [OscDevice(Guid.NewGuid(), "X32", "x32", "192.168.2.76", 10023)],
            },
            sender,
            cache);

        var fader = await router.SendAsync(
            new ControlScriptOscMessage("x32", "/ch/01/mix/fader", [ControlScriptOscArgument.Float32(0.6)]));
        var mute = await router.SendAsync(
            new ControlScriptOscMessage("x32", "/ch/01/mix/on", [ControlScriptOscArgument.Int32(1)]));

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
        var sender = new RecordingOscSender();
        var router = new ControlScriptOscCommandRouter(
            new ControlSystemConfig
            {
                OscCacheUpdateMode = ControlOscCacheUpdateMode.IncomingOnly,
                OscCacheOverrides =
                [
                    new ControlOscCacheCommandOverride
                    {
                        AddressPattern = "/ch/01/mix/fader",
                        Mode = ControlOscCacheUpdateMode.OptimisticSendAndIncoming,
                    },
                ],
                Devices = [OscDevice(Guid.NewGuid(), "X32", "x32", "192.168.2.76", 10023)],
            },
            sender,
            cache);

        var result = await router.SendAsync(
            new ControlScriptOscMessage("x32", "/ch/01/mix/fader", [ControlScriptOscArgument.Float32(0.6)]));

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
        var sender = new RecordingOscSender();
        var router = new ControlScriptOscCommandRouter(
            new ControlSystemConfig
            {
                OscCacheUpdateMode = ControlOscCacheUpdateMode.OptimisticSendAndIncoming,
                OscCacheOverrides =
                [
                    new ControlOscCacheCommandOverride
                    {
                        AddressPattern = "/ch/01/mix/fader",
                        DeviceInstanceId = mainId,
                        Mode = ControlOscCacheUpdateMode.IncomingOnly,
                    },
                ],
                Devices =
                [
                    OscDevice(mainId, "X32 Main", "main", "192.168.2.76", 10023),
                    OscDevice(monitorId, "X32 Monitor", "monitor", "192.168.2.77", 10023),
                ],
            },
            sender,
            cache);

        await router.SendAsync(
            new ControlScriptOscMessage("main", "/ch/01/mix/fader", [ControlScriptOscArgument.Float32(0.6)]));
        await router.SendAsync(
            new ControlScriptOscMessage("monitor", "/ch/01/mix/fader", [ControlScriptOscArgument.Float32(0.6)]));

        Assert.False(cache.TryGetNumber("main", "/ch/01/mix/fader", out _));
        Assert.True(cache.TryGetNumber("monitor", "/ch/01/mix/fader", out var monitorValue));
        Assert.Equal(0.6, monitorValue, precision: 12);
    }

    [Fact]
    public async Task SendAsync_MoreSpecificOverrideWinsOverWildcard()
    {
        var x32Id = Guid.NewGuid();
        var cache = new ControlValueCache();
        var sender = new RecordingOscSender();
        var router = new ControlScriptOscCommandRouter(
            new ControlSystemConfig
            {
                OscCacheUpdateMode = ControlOscCacheUpdateMode.OptimisticSendAndIncoming,
                OscCacheOverrides =
                [
                    new ControlOscCacheCommandOverride
                    {
                        AddressPattern = "/ch/*/mix/fader",
                        Mode = ControlOscCacheUpdateMode.IncomingOnly,
                    },
                    new ControlOscCacheCommandOverride
                    {
                        AddressPattern = "/ch/01/mix/fader",
                        DeviceInstanceId = x32Id,
                        Mode = ControlOscCacheUpdateMode.OptimisticSendAndIncoming,
                    },
                ],
                Devices = [OscDevice(x32Id, "X32", "x32", "192.168.2.76", 10023)],
            },
            sender,
            cache);

        await router.SendAsync(
            new ControlScriptOscMessage("x32", "/ch/01/mix/fader", [ControlScriptOscArgument.Float32(0.6)]));
        await router.SendAsync(
            new ControlScriptOscMessage("x32", "/ch/02/mix/fader", [ControlScriptOscArgument.Float32(0.4)]));

        // Channel 1 matches the exact device-scoped override -> optimistic write wins.
        Assert.True(cache.TryGetNumber("x32", "/ch/01/mix/fader", out var ch1));
        Assert.Equal(0.6, ch1, precision: 12);
        // Channel 2 only matches the wildcard incoming-only override -> no optimistic write.
        Assert.False(cache.TryGetNumber("x32", "/ch/02/mix/fader", out _));
    }

    private static ControlDeviceInstanceConfig OscDevice(
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
            Protocol = ControlDeviceProtocol.Osc,
            IsEnabled = isEnabled,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = alias,
                OscHost = host,
                OscPort = port,
            },
        };

    private sealed class RecordingOscSender : IControlOscSender
    {
        public List<SentOscMessage> Sent { get; } = new();

        public ValueTask SendAsync(
            string host,
            int port,
            string address,
            IReadOnlyList<OSCArgument> arguments,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(new SentOscMessage(host, port, address, arguments.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record SentOscMessage(
        string Host,
        int Port,
        string Address,
        IReadOnlyList<OSCArgument> Arguments);
}
