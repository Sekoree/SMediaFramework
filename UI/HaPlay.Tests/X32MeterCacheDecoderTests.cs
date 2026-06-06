using System.Buffers.Binary;
using OSCLib;
using S.Control;
using Xunit;

namespace HaPlay.Tests;

public sealed class X32MeterCacheDecoderTests
{
    [Fact]
    public void DecodeFloatBlob_UsesLeadingStringAsMeterBasePath()
    {
        Span<byte> blob = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(blob[..4], 4);
        BinaryPrimitives.WriteInt32LittleEndian(blob.Slice(4, 4), 253);
        BinaryPrimitives.WriteInt32LittleEndian(blob.Slice(8, 4), BitConverter.SingleToInt32Bits(0.75f));

        var entries = X32MeterCacheDecoder.Decode(
            "/meters",
            [OSCArgument.String("/meters/6"), OSCArgument.Blob(blob.ToArray())],
            blobArgumentIndex: 1,
            blob.ToArray()).ToArray();

        Assert.Collection(
            entries,
            entry =>
            {
                Assert.Equal("/meters/6/0", entry.Address);
                Assert.Equal(0.75f, entry.Value);
            });
    }

    [Fact]
    public void DecodeRtaBlob_UsesMeterPathFromArguments()
    {
        Span<byte> blob = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(blob, unchecked((short)0x8000));

        var entries = X32MeterCacheDecoder.Decode(
            "/meters",
            [OSCArgument.String("/meters/1"), OSCArgument.Blob(blob.ToArray())],
            blobArgumentIndex: 1,
            blob.ToArray()).ToArray();

        var entry = Assert.Single(entries);
        Assert.Equal("/meters/1/0", entry.Address);
        Assert.Equal(-128.0f, entry.Value);
    }
}

public sealed class ControlX32ProtocolMaintenanceManagerTests
{
    [Fact]
    public async Task TickAsync_SkipsUntilConfiguredIntervalAfterSuccessfulRenew()
    {
        var deviceId = Guid.NewGuid();
        var sender = new RecordingOscSender();
        var manager = new ControlX32ProtocolMaintenanceManager(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(deviceId, Periodic("/xremote", intervalMs: 8000))],
            },
            sender);
        var now = DateTimeOffset.Parse("2026-06-04T10:00:00Z");

        var first = await manager.TickAsync(now);
        var tooSoon = await manager.TickAsync(now.AddMilliseconds(7999));
        var second = await manager.TickAsync(now.AddMilliseconds(8000));

        Assert.True(Assert.Single(first).Succeeded);
        Assert.Empty(tooSoon);
        Assert.True(Assert.Single(second).Succeeded);
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task TickAsync_RetriesFailedRenewEveryTwoSecondsWithoutAdvancingSuccessClock()
    {
        var sender = new FailingOscSender();
        var manager = new ControlX32ProtocolMaintenanceManager(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices = [OscDevice(Guid.NewGuid(), Periodic("/xremote", intervalMs: 8000))],
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

    [Fact]
    public async Task TickAsync_SendsXRemoteBeforeSubscribeAndMeters()
    {
        var sender = new RecordingOscSender();
        var manager = new ControlX32ProtocolMaintenanceManager(
            new ControlSystemConfig
            {
                IsArmed = true,
                Devices =
                [
                    OscDevice(
                        Guid.NewGuid(),
                        Periodic("/meters", intervalMs: 8000, [new ControlOscArgumentConfig { Kind = ControlOscArgumentKind.String, StringValue = "/meters/6" }]),
                        Periodic("/subscribe", intervalMs: 8000, [new ControlOscArgumentConfig { Kind = ControlOscArgumentKind.String, StringValue = "/ch/01/mix/fader" }]),
                        Periodic("/xremote", intervalMs: 8000)),
                ],
            },
            sender);

        await manager.TickAsync(DateTimeOffset.UtcNow);

        Assert.Collection(
            sender.Sent.Select(s => s.Address),
            address => Assert.Equal("/xremote", address),
            address => Assert.Equal("/subscribe", address),
            address => Assert.Equal("/meters", address));
    }

    private static ControlDeviceInstanceConfig OscDevice(
        Guid id,
        params ControlPeriodicOscSendConfig[] sends) =>
        new()
        {
            Id = id,
            Name = "X32",
            ProfileId = "behringer.x32.osc",
            Protocol = ControlDeviceProtocol.Osc,
            IsEnabled = true,
            Binding = new ControlDeviceBindingConfig
            {
                Alias = "x32",
                OscHost = "192.168.2.76",
                OscPort = 10023,
            },
            PeriodicOscSends = sends.ToList(),
        };

    private static ControlPeriodicOscSendConfig Periodic(
        string address,
        int intervalMs = 8000,
        List<ControlOscArgumentConfig>? arguments = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = address,
            Address = address,
            IntervalMs = intervalMs,
            Arguments = arguments ?? [],
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

    private sealed class FailingOscSender : IControlOscSender
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
