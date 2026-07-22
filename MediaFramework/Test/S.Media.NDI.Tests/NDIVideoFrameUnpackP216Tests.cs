using System.Buffers.Binary;
using S.Media.NDI.Video;
using Xunit;

namespace S.Media.NDI.Tests;

/// <summary>
/// Pins the vectorized P216/PA16 16→8-bit UYVY reduction to the scalar reference it replaced.
/// The scalar semantics are deliberately quirky: <c>(byte)((v + 128) &gt;&gt; 8)</c> truncates, so
/// samples ≥ 0xFF80 wrap to 0 rather than saturating to 255 - the vector path must match exactly,
/// including for widths that are not a multiple of the vector lane count (scalar tail).
/// </summary>
public sealed class NDIVideoFrameUnpackP216Tests
{
    /// <summary>Verbatim copy of the pre-vectorization per-pixel-pair loop.</summary>
    private static void ScalarReference(ReadOnlySpan<byte> yRow16, ReadOnlySpan<byte> uvRow16, Span<byte> dstUyvy)
    {
        var width = dstUyvy.Length / 2;
        var outBase = 0;
        for (var x = 0; x < width; x += 2)
        {
            var y0 = BinaryPrimitives.ReadUInt16LittleEndian(yRow16[(x * 2)..]);
            var y1 = BinaryPrimitives.ReadUInt16LittleEndian(yRow16[((x + 1) * 2)..]);
            var u = BinaryPrimitives.ReadUInt16LittleEndian(uvRow16[(x * 2)..]);
            var v = BinaryPrimitives.ReadUInt16LittleEndian(uvRow16[((x + 1) * 2)..]);

            dstUyvy[outBase++] = ToByte8(u);
            dstUyvy[outBase++] = ToByte8(y0);
            dstUyvy[outBase++] = ToByte8(v);
            dstUyvy[outBase++] = ToByte8(y1);
        }
    }

    private static byte ToByte8(ushort value) => (byte)((value + 128) >> 8);

    // Widths chosen to exercise: below any vector width (2, 6), just past 8/16-lane boundaries
    // (10, 18, 34), an unaligned tail on every lane count (30, 42), and a realistic row (1920).
    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(18)]
    [InlineData(30)]
    [InlineData(34)]
    [InlineData(42)]
    [InlineData(1920)]
    public void ReduceP216Row_MatchesScalarReference(int width)
    {
        var rng = new Random(1234 + width);
        var y = new byte[width * 2];
        var uv = new byte[width * 2];
        rng.NextBytes(y);
        rng.NextBytes(uv);

        // Force the rounding/wrap edge cases into both rows: 0xFF7F (last value that rounds to 0xFF),
        // 0xFF80 (first value that wraps to 0), and 0xFFFF.
        ushort[] edges = [0xFF7F, 0xFF80, 0xFFFF, 0x0000, 0x007F, 0x0080];
        for (var i = 0; i < Math.Min(edges.Length, width); i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(y.AsSpan(i * 2), edges[i]);
            BinaryPrimitives.WriteUInt16LittleEndian(uv.AsSpan(((width - 1 - i) * 2)..), edges[i]);
        }

        var expected = new byte[width * 2];
        ScalarReference(y, uv, expected);

        var actual = new byte[width * 2];
        NDIVideoFrameUnpack.ReduceP216RowToUyvy(y, uv, actual);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ReduceP216Row_WrapSemantics_HighSamplesReduceToZero()
    {
        // (byte)((0xFFFF + 128) >> 8) == 0 - saturation would be 255. The vector path performs the
        // +128 in 16-bit lanes, which wraps identically; this pins that equivalence explicitly.
        var y = new byte[4];
        var uv = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(y, 0xFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(y.AsSpan(2), 0xFF80);
        BinaryPrimitives.WriteUInt16LittleEndian(uv, 0xFF7F);
        BinaryPrimitives.WriteUInt16LittleEndian(uv.AsSpan(2), 0x0080);

        var dst = new byte[4];
        NDIVideoFrameUnpack.ReduceP216RowToUyvy(y, uv, dst);

        Assert.Equal(0xFF, dst[0]); // u  = 0xFF7F rounds up to 255
        Assert.Equal(0x00, dst[1]); // y0 = 0xFFFF wraps to 0
        Assert.Equal(0x01, dst[2]); // v  = 0x0080 rounds up to 1
        Assert.Equal(0x00, dst[3]); // y1 = 0xFF80 wraps to 0
    }
}
