using System.Buffers.Binary;

namespace S.Control;

/// <summary>A decoded X32 / M32 meter blob: two header words plus the float values.</summary>
public sealed record X32MeterBlob(int Header0, int Header1, IReadOnlyList<float> Values);

/// <summary>
/// Parses the X32 / M32 binary meter blob formats. This is the one genuinely irreducible piece of X32-specific
/// C#: deserializing a packed little-endian byte payload at meter rate - not expressible (or fast enough) as a
/// Mond profile helper. It is reached only through the registered <see cref="X32MeterBlobDecoder"/> capability,
/// which a profile opts into by name (<c>Behaviors.MeterBlobDecoder = "x32"</c>); the output address is taken from
/// the OSC argument preceding the blob in the same message, so no protocol state is required.
/// </summary>
public static class X32Meters
{
    public static X32MeterBlob ParseFloatBlob(ReadOnlyMemory<byte> blob)
    {
        var span = blob.Span;
        if (span.Length < 8 || (span.Length - 8) % 4 != 0)
            throw new FormatException("X32 float meter blob must contain two 32-bit headers followed by 32-bit float values.");

        var header0 = BinaryPrimitives.ReadInt32LittleEndian(span[..4]);
        var header1 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4, 4));
        var values = new float[(span.Length - 8) / 4];
        for (var i = 0; i < values.Length; i++)
        {
            var bits = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8 + i * 4, 4));
            values[i] = BitConverter.Int32BitsToSingle(bits);
        }

        return new X32MeterBlob(header0, header1, values);
    }

    public static IReadOnlyList<float> ParseRtaDbBlob(ReadOnlyMemory<byte> blob)
    {
        var span = blob.Span;
        if (span.Length % 2 != 0)
            throw new FormatException("X32 RTA meter blob must contain little-endian 16-bit values.");

        var values = new float[span.Length / 2];
        for (var i = 0; i < values.Length; i++)
        {
            var raw = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(i * 2, 2));
            values[i] = raw / 256.0f;
        }

        return values;
    }
}
