using OSCLib;

namespace S.Control;

/// <summary>A reading decoded from a device feedback blob: a stable numeric cache address and its value.</summary>
public readonly record struct ControlMeterReading(string Address, float Value);

/// <summary>A Session-free extension contract for device-specific binary feedback decoders.</summary>
public interface IControlMeterBlobDecoder
{
    IEnumerable<ControlMeterReading> Decode(
        string oscAddress,
        IReadOnlyList<OSCArgument> arguments,
        int blobArgumentIndex,
        ReadOnlyMemory<byte> blob);
}

/// <summary>A scoped decoder registry keyed by the name referenced from a device profile.</summary>
public sealed class ControlMeterBlobDecoderRegistry
{
    private readonly Dictionary<string, IControlMeterBlobDecoder> _decoders =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(string name, IControlMeterBlobDecoder decoder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(decoder);
        _decoders[name] = decoder;
    }

    public bool Contains(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _decoders.ContainsKey(name);

    public IControlMeterBlobDecoder? Resolve(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _decoders.TryGetValue(name, out var decoder) ? decoder : null;
}
