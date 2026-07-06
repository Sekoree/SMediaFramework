using System.Text;
using OSCLib;
using Xunit;

namespace OSCLib.Tests;

/// <summary>OSC-01: a malformed or truncated datagram must be rejected cleanly — <see cref="OSCPacketCodec.TryDecode"/>
/// returns <c>false</c> with a diagnostic message and a null packet, and never throws across the receive path or
/// yields a half-decoded packet. A hostile or buggy sender therefore cannot crash the listener with crafted bytes.</summary>
public sealed class OSCMalformedPacketTests
{
    // (label, bytes) pairs. Each must fail to decode without throwing.
    public static IEnumerable<object[]> MalformedPackets()
    {
        yield return Case("empty", []);
        yield return Case("too-small (< 4 bytes)", [1, 2, 3]);
        yield return Case("unterminated address string", Ascii("/xyz")); // 4 bytes, no NUL terminator
        yield return Case("type tag missing leading comma", Concat(Pad("/x"), Pad("i")));
        yield return Case("unknown type tag", Concat(Pad("/x"), Pad(",Q")));
        yield return Case("truncated int32 argument", Concat(Pad("/x"), Pad(",i"), [0x00, 0x00])); // 2 of 4 bytes
        yield return Case("bundle marker not '#bundle'", Pad("#x"));
        yield return Case("trailing bytes after a valid message", Concat(Pad("/x"), Pad(","), [0xFF, 0xFF, 0xFF, 0xFF]));
    }

    [Theory]
    [MemberData(nameof(MalformedPackets))]
    public void TryDecode_RejectsMalformedPacket_WithoutThrowing(string label, byte[] packet)
    {
        var ok = OSCPacketCodec.TryDecode(packet, new OSCDecodeOptions(), out var decoded, out var error);

        Assert.False(ok, $"expected '{label}' to be rejected");
        Assert.Null(decoded);
        Assert.False(string.IsNullOrEmpty(error), $"'{label}' rejected without a diagnostic message");
    }

    [Fact]
    public void Truncation_IsNeverMistakenForTheCompleteCommand()
    {
        // A partially-received datagram (short read / MTU fragmentation) must never decode into the full command:
        // it either fails, or (the codec tolerates a bare address as a no-argument message) decodes with FEWER
        // arguments than were sent. Truncation can only lose trailing bytes, so it can never fabricate arguments.
        const int sentArgs = 3;
        using var encoded = OSCPacketCodec.EncodeToRented(OSCPacket.FromMessage(new OSCMessage(
            "/cue/start", [OSCArgument.Int32(42), OSCArgument.String("go"), OSCArgument.Float32(1.5f)])));
        var full = encoded.Memory.Span.ToArray();

        // The full packet decodes with all arguments intact.
        Assert.True(OSCPacketCodec.TryDecode(full, new OSCDecodeOptions(), out var complete, out var fullError), fullError);
        Assert.Equal(sentArgs, complete!.Message!.Arguments.Count);

        // No proper prefix reproduces the complete command (and none throws — TryDecode is a bool contract).
        for (var len = 0; len < full.Length; len++)
        {
            if (OSCPacketCodec.TryDecode(full.AsSpan(0, len), new OSCDecodeOptions(), out var decoded, out _))
                Assert.True(
                    (decoded!.Message?.Arguments.Count ?? 0) < sentArgs,
                    $"prefix of length {len} decoded as the full {sentArgs}-argument command");
        }
    }

    private static object[] Case(string label, byte[] bytes) => [label, bytes];

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>OSC string: NUL-terminated, then zero-padded to the next 4-byte boundary.</summary>
    private static byte[] Pad(string s)
    {
        var raw = Encoding.ASCII.GetBytes(s);
        var len = (raw.Length + 1 + 3) & ~3; // + NUL, rounded up to 4
        var buf = new byte[len];
        Array.Copy(raw, buf, raw.Length);
        return buf;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var buf = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, buf, offset, part.Length);
            offset += part.Length;
        }
        return buf;
    }
}
