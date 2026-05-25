using OSCLib;
using Xunit;

namespace OSCLib.Tests;

public sealed class OSCPacketCodecTests
{
    [Fact]
    public void Message_RoundTrips_CoreArgumentTypes()
    {
        var midi = new OSCMIDIMessage(1, 0x90, 60, 100);
        using var encoded = OSCPacketCodec.EncodeToRented(OSCPacket.FromMessage(new OSCMessage(
            "/cue/start",
            [
                OSCArgument.Int32(42),
                OSCArgument.Float32(1.25f),
                OSCArgument.String("go"),
                OSCArgument.Blob(new byte[] { 1, 2, 3 }),
                OSCArgument.Int64(1234567890123),
                OSCArgument.TimeTag(new OSCTimeTag(0x0000000200000003UL)),
                OSCArgument.Double64(2.5),
                OSCArgument.Symbol("sym"),
                OSCArgument.Char('A'),
                OSCArgument.RgbaColor(0x11223344),
                OSCArgument.MIDI(midi),
                OSCArgument.True(),
                OSCArgument.False(),
                OSCArgument.Nil(),
                OSCArgument.Impulse(),
                OSCArgument.Array([OSCArgument.Int32(7), OSCArgument.String("nested")]),
            ])));

        Assert.True(OSCPacketCodec.TryDecode(encoded.Memory.Span, new OSCDecodeOptions(), out var packet, out var error), error);
        Assert.NotNull(packet!.Message);
        var message = packet.Message;

        Assert.Equal("/cue/start", message.Address);
        Assert.Equal(16, message.Arguments.Count);
        Assert.Equal(42, message.Arguments[0].AsInt32());
        Assert.Equal(1.25f, message.Arguments[1].AsFloat32());
        Assert.Equal("go", message.Arguments[2].AsString());
        Assert.Equal([1, 2, 3], message.Arguments[3].AsBlob().ToArray());
        Assert.Equal(1234567890123, message.Arguments[4].AsInt64());
        Assert.Equal(0x0000000200000003UL, message.Arguments[5].AsTimeTag().Value);
        Assert.Equal(2.5, message.Arguments[6].AsDouble64());
        Assert.Equal("sym", message.Arguments[7].AsString());
        Assert.Equal('A', message.Arguments[8].AsChar());
        Assert.Equal(0x11223344U, message.Arguments[9].AsRgbaColor());
        Assert.Equal(midi, message.Arguments[10].AsMIDI());
        Assert.Equal(OSCArgumentType.True, message.Arguments[11].Type);
        Assert.Equal(OSCArgumentType.False, message.Arguments[12].Type);
        Assert.Equal(OSCArgumentType.Nil, message.Arguments[13].Type);
        Assert.Equal(OSCArgumentType.Impulse, message.Arguments[14].Type);

        var nested = message.Arguments[15].AsArray();
        Assert.Equal(7, nested[0].AsInt32());
        Assert.Equal("nested", nested[1].AsString());
    }

    [Fact]
    public void Bundle_RoundTrips_TimeTagAndElements()
    {
        var bundle = new OSCBundle(
            new OSCTimeTag(100),
            [
                OSCPacket.FromMessage(new OSCMessage("/a", [OSCArgument.Int32(1)])),
                OSCPacket.FromMessage(new OSCMessage("/b", [OSCArgument.String("two")])),
            ]);

        using var encoded = OSCPacketCodec.EncodeToRented(OSCPacket.FromBundle(bundle));

        Assert.True(OSCPacketCodec.TryDecode(encoded.Memory.Span, new OSCDecodeOptions(), out var packet, out var error), error);
        Assert.NotNull(packet!.Bundle);
        var decoded = packet.Bundle;

        Assert.Equal(100UL, decoded.TimeTag.Value);
        Assert.Equal(2, decoded.Elements.Count);
        Assert.Equal("/a", decoded.Elements[0].Message!.Address);
        Assert.Equal(1, decoded.Elements[0].Message!.Arguments[0].AsInt32());
        Assert.Equal("/b", decoded.Elements[1].Message!.Address);
        Assert.Equal("two", decoded.Elements[1].Message!.Arguments[0].AsString());
    }

    [Fact]
    public void StrictDecode_RejectsNestedBundleTimeTagBeforeParent()
    {
        var nested = new OSCBundle(
            new OSCTimeTag(5),
            [OSCPacket.FromMessage(new OSCMessage("/late"))]);
        var outer = new OSCBundle(new OSCTimeTag(10), [OSCPacket.FromBundle(nested)]);

        using var encoded = OSCPacketCodec.EncodeToRented(OSCPacket.FromBundle(outer));

        Assert.False(OSCPacketCodec.TryDecode(encoded.Memory.Span, new OSCDecodeOptions(), out _, out var error));
        Assert.Contains("precedes enclosing", error, StringComparison.Ordinal);
    }
}
