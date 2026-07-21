using BenchmarkDotNet.Attributes;
using OSCLib;

namespace OSCLib.Benchmarks;

/// <summary>
/// Measures <see cref="OSCPacketCodec"/> encode/decode for the three shapes that dominate real
/// control traffic: a fader message, an X32-style meter blob (string + 384-byte blob at 20-50 Hz),
/// and a small bundle. Allocated B/op is the number to watch — the decode path currently allocates
/// the type-tag string per message and copies blobs with ToArray().
/// </summary>
[MemoryDiagnoser]
public class OscCodecBenchmarks
{
    private readonly OSCDecodeOptions _decodeOptions = new();
    private OSCPacket _faderPacket = null!;
    private OSCPacket _meterPacket = null!;
    private OSCPacket _bundlePacket = null!;
    private byte[] _faderBytes = null!;
    private byte[] _meterBytes = null!;
    private byte[] _bundleBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _faderPacket = OSCPacket.FromMessage(new OSCMessage("/ch/01/mix/fader", [OSCArgs.F32(0.75f)]));

        var blob = new byte[384];
        Random.Shared.NextBytes(blob);
        _meterPacket = OSCPacket.FromMessage(new OSCMessage("/meters", [OSCArgs.Str("/meters/1"), OSCArgs.Blob(blob)]));

        var elements = new OSCPacket[8];
        for (var i = 0; i < elements.Length; i++)
            elements[i] = OSCPacket.FromMessage(new OSCMessage($"/ch/{i + 1:00}/mix/fader", [OSCArgs.F32(i * 0.1f)]));
        _bundlePacket = OSCPacket.FromBundle(new OSCBundle(OSCTimeTag.Immediately, elements));

        _faderBytes = Encode(_faderPacket);
        _meterBytes = Encode(_meterPacket);
        _bundleBytes = Encode(_bundlePacket);
    }

    private static byte[] Encode(OSCPacket packet)
    {
        using var rented = OSCPacketCodec.EncodeToRented(packet);
        return rented.Memory.ToArray();
    }

    [Benchmark]
    public void EncodeFader()
    {
        using var rented = OSCPacketCodec.EncodeToRented(_faderPacket);
    }

    [Benchmark]
    public void EncodeMeterBlob()
    {
        using var rented = OSCPacketCodec.EncodeToRented(_meterPacket);
    }

    [Benchmark]
    public void EncodeBundle8()
    {
        using var rented = OSCPacketCodec.EncodeToRented(_bundlePacket);
    }

    [Benchmark]
    public OSCPacket? DecodeFader()
    {
        OSCPacketCodec.TryDecode(_faderBytes, _decodeOptions, out var packet, out _);
        return packet;
    }

    [Benchmark]
    public OSCPacket? DecodeMeterBlob()
    {
        OSCPacketCodec.TryDecode(_meterBytes, _decodeOptions, out var packet, out _);
        return packet;
    }

    [Benchmark]
    public OSCPacket? DecodeBundle8()
    {
        OSCPacketCodec.TryDecode(_bundleBytes, _decodeOptions, out var packet, out _);
        return packet;
    }
}
