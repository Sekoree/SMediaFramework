using HaPlay.ControlGraph;
using HaPlay.Models;
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
