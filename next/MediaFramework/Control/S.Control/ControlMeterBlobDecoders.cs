using OSCLib;

namespace S.Control;

/// <summary>A reading decoded from a device meter blob: a stable numeric cache address and its value.</summary>
public readonly record struct ControlMeterReading(string Address, float Value);

/// <summary>
/// Decodes a device-specific meter blob (a binary OSC payload) into numeric cache readings.
/// <para>
/// This is the <b>one deliberate exception</b> to "devices are data": every other device behavior lives in the
/// profile (commands + a Mond <c>HelperScript</c> + Tasks), but raw binary blob parsing at meter rate stays C#.
/// It <i>could</i> be generalized into a profile binary-format descriptor, but it is a hot path (~50 Hz) where the
/// gain is marginal — see <c>Next/06-Control-Surface.md §5</c> for the full rationale before changing this.
/// </para>
/// The device still stays data: a profile opts in <b>by name</b> via
/// <see cref="ControlDeviceProfileBehaviors.MeterBlobDecoder"/>, and a host (or a C-ABI plugin) can register more
/// decoders without the runtime knowing the device. A decoder returns no readings for blobs it doesn't recognize.
/// </summary>
public interface IControlMeterBlobDecoder
{
    IEnumerable<ControlMeterReading> Decode(
        string oscAddress,
        IReadOnlyList<OSCArgument> arguments,
        int blobArgumentIndex,
        ReadOnlyMemory<byte> blob);
}

/// <summary>
/// Registry of meter-blob decoders keyed by the name profiles reference (e.g. <c>"x32"</c>). Ships the built-in
/// decoders; hosts can register more without the runtime knowing the device.
/// </summary>
public sealed class ControlMeterBlobDecoderRegistry
{
    private readonly Dictionary<string, IControlMeterBlobDecoder> _decoders =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The process-wide registry, seeded with the built-in decoders.</summary>
    public static ControlMeterBlobDecoderRegistry Default { get; } = CreateDefault();

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

    private static ControlMeterBlobDecoderRegistry CreateDefault()
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
