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
