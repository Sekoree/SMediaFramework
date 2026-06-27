using OSCLib;

namespace S.Control;

/// <summary>Creates runtime-scoped decoder registries with the framework's built-in binary capabilities.</summary>
public static class ControlMeterBlobDecoderRegistries
{
    public static ControlMeterBlobDecoderRegistry CreateWithBuiltIns()
    {
        var registry = new ControlMeterBlobDecoderRegistry();
        registry.Register("x32", new X32MeterBlobDecoder());
        return registry;
    }
}

/// <summary>The built-in X32 / M32 meter-blob decoder (wraps <see cref="X32MeterCacheDecoder"/>).</summary>
public sealed class X32MeterBlobDecoder : IControlMeterBlobDecoder
{
    public IEnumerable<ControlMeterReading> Decode(
        string oscAddress,
        IReadOnlyList<OSCArgument> arguments,
        int blobArgumentIndex,
        ReadOnlyMemory<byte> blob)
    {
        foreach (var entry in X32MeterCacheDecoder.Decode(oscAddress, arguments, blobArgumentIndex, blob))
            yield return new ControlMeterReading(entry.Address, entry.Value);
    }
}
