using HaPlay.ControlGraph;
using OSCLib;
using System.Buffers.Binary;
using Xunit;

namespace HaPlay.Tests;

public sealed class X32PresetTests
{
    [Fact]
    public void EndpointPreset_UsesX32Defaults()
    {
        var preset = X32Presets.CreateEndpointPreset("192.168.1.50");

        Assert.Equal("192.168.1.50", preset.Host);
        Assert.Equal(10023, preset.Port);
        Assert.Null(preset.LocalPort);
        Assert.Equal(TimeSpan.FromSeconds(8), preset.XRemoteRenewInterval);
        Assert.Equal(TimeSpan.FromSeconds(8), preset.SubscriptionRenewInterval);
    }

    [Fact]
    public void ParameterPresets_GenerateExpectedMixerAddresses()
    {
        var channel = X32Presets.ChannelStripPresets(1);
        Assert.Contains(channel, p => p.DisplayName == "Ch 01 Fader" && p.Address == "/ch/01/mix/fader");
        Assert.Contains(channel, p => p.DisplayName == "Ch 01 Mute" && p.Address == "/ch/01/mix/on");
        Assert.Contains(channel, p => p.DisplayName == "Ch 01 Pan" && p.Address == "/ch/01/mix/pan");
        Assert.Contains(channel, p => p.DisplayName == "Ch 01 Solo" && p.Address == "/-stat/solosw/01" && p.IsReadOnly);

        Assert.Equal("/dca/8/fader", X32Presets.DcaFaderAddress(99));
        Assert.Equal("/dca/8/on", X32Presets.DcaMuteAddress(99));
        Assert.Equal("/bus/16/mix/fader", X32Presets.BusFaderAddress(99));
        Assert.Equal("/mtx/06/mix/on", X32Presets.MatrixMuteAddress(99));
        Assert.Equal("/main/st/mix/fader", X32Presets.MainStereoFaderAddress());
        Assert.Equal("/main/st/mix/on", X32Presets.MainStereoMuteAddress());
    }

    [Theory]
    [InlineData(0.0, double.NegativeInfinity)]
    [InlineData(0.0625, -60.0)]
    [InlineData(0.25, -30.0)]
    [InlineData(0.5, -10.0)]
    [InlineData(0.75, 0.0)]
    [InlineData(1.0, 10.0)]
    public void Fader_FromNormalized_UsesX32PiecewiseCurve(double normalized, double expectedDb)
    {
        var actual = X32Fader.FromNormalized(normalized);

        if (double.IsNegativeInfinity(expectedDb))
            Assert.True(double.IsNegativeInfinity(actual));
        else
            Assert.Equal(expectedDb, actual, precision: 6);
    }

    [Theory]
    [InlineData(double.NegativeInfinity, 0.0)]
    [InlineData(-90.0, 0.0)]
    [InlineData(-60.0, 0.0625)]
    [InlineData(-30.0, 0.25)]
    [InlineData(-10.0, 0.5)]
    [InlineData(0.0, 0.75)]
    [InlineData(10.0, 1.0)]
    public void Fader_ToNormalized_UsesX32PiecewiseCurve(double db, double expectedNormalized)
    {
        Assert.Equal(expectedNormalized, X32Fader.ToNormalized(db), precision: 6);
    }

    [Theory]
    [InlineData(-45.0)]
    [InlineData(-20.0)]
    [InlineData(-3.0)]
    [InlineData(6.0)]
    public void Fader_Conversion_RoundTripsPracticalValues(double db)
    {
        var normalized = X32Fader.ToNormalized(db);
        var roundTripped = X32Fader.FromNormalized(normalized);

        Assert.Equal(db, roundTripped, precision: 6);
    }

    [Fact]
    public async Task Session_RenewOnce_SendsXRemoteSubscriptionsAndMeters()
    {
        var sender = new RecordingOscSender();
        var session = new X32Session(X32Presets.CreateEndpointPreset("192.168.1.50"), sender);
        session.AddSubscription("/ch/01/mix/fader", frequency: 50);
        session.AddMeterSubscription("/meters/6", argument: 16, priority: 1);

        await session.RenewOnceAsync();

        Assert.Equal(3, sender.Sent.Count);
        Assert.Equal("/xremote", sender.Sent[0].Address);
        Assert.Empty(sender.Sent[0].Arguments);
        Assert.Equal("/subscribe", sender.Sent[1].Address);
        Assert.Equal("/ch/01/mix/fader", sender.Sent[1].Arguments[0].AsString());
        Assert.Equal(50, sender.Sent[1].Arguments[1].AsInt32());
        Assert.Equal("/meters", sender.Sent[2].Address);
        Assert.Equal("/meters/6", sender.Sent[2].Arguments[0].AsString());
        Assert.Equal(16, sender.Sent[2].Arguments[1].AsInt32());
        Assert.Equal(1, sender.Sent[2].Arguments[2].AsInt32());
    }

    [Fact]
    public void Meters_ParseFloatBlob_ReadsLittleEndianHeadersAndValues()
    {
        Span<byte> blob = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(blob[..4], 4);
        BinaryPrimitives.WriteInt32LittleEndian(blob.Slice(4, 4), 253);
        BinaryPrimitives.WriteInt32LittleEndian(blob.Slice(8, 4), BitConverter.SingleToInt32Bits(0.25f));
        BinaryPrimitives.WriteInt32LittleEndian(blob.Slice(12, 4), BitConverter.SingleToInt32Bits(1.5f));

        var parsed = X32Meters.ParseFloatBlob(blob.ToArray());

        Assert.Equal(4, parsed.Header0);
        Assert.Equal(253, parsed.Header1);
        Assert.Equal([0.25f, 1.5f], parsed.Values);
    }

    [Fact]
    public void Meters_ParseRtaDbBlob_ReadsLittleEndianSignedShorts()
    {
        Span<byte> blob = stackalloc byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(blob[..2], unchecked((short)0x8000));
        BinaryPrimitives.WriteInt16LittleEndian(blob.Slice(2, 2), unchecked((short)0xC000));

        var parsed = X32Meters.ParseRtaDbBlob(blob.ToArray());

        Assert.Equal(-128.0f, parsed[0]);
        Assert.Equal(-64.0f, parsed[1]);
    }

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
