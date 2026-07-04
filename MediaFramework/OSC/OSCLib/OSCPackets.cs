namespace OSCLib;

/// <summary>
/// Distinguishes OSC message and bundle packet payloads.
/// </summary>
public enum OSCPacketKind
{
    Message,
    Bundle
}

/// <summary>
/// Wrapper for a top-level OSC packet.
/// </summary>
public sealed class OSCPacket
{
    private OSCPacket(OSCMessage message)
    {
        Kind = OSCPacketKind.Message;
        Message = message;
    }

    private OSCPacket(OSCBundle bundle)
    {
        Kind = OSCPacketKind.Bundle;
        Bundle = bundle;
    }

    /// <summary>
    /// Packet payload kind.
    /// </summary>
    public OSCPacketKind Kind { get; }

    /// <summary>
    /// Message payload when <see cref="Kind"/> is <see cref="OSCPacketKind.Message"/>.
    /// </summary>
    public OSCMessage? Message { get; }

    /// <summary>
    /// Bundle payload when <see cref="Kind"/> is <see cref="OSCPacketKind.Bundle"/>.
    /// </summary>
    public OSCBundle? Bundle { get; }

    /// <summary>
    /// Creates a message packet.
    /// </summary>
    public static OSCPacket FromMessage(OSCMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new OSCPacket(message);
    }

    /// <summary>
    /// Creates a bundle packet.
    /// </summary>
    public static OSCPacket FromBundle(OSCBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return new OSCPacket(bundle);
    }
}

/// <summary>
/// OSC message with address and typed arguments.
/// </summary>
public sealed class OSCMessage
{
    private static readonly System.Buffers.SearchValues<char> ForbiddenAddressChars =
        System.Buffers.SearchValues.Create(" #*,?[]{}");

    public OSCMessage(string address, IReadOnlyList<OSCArgument>? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(address) || address[0] != '/')
            throw new ArgumentException("OSC address must start with '/'.", nameof(address));

        Address = address;
        Arguments = arguments ?? [];
    }

    /// <summary>
    /// OSC address path, always starting with <c>/</c>.
    /// </summary>
    public string Address { get; }

    /// <summary>
    /// Ordered argument list that matches the type tag sequence.
    /// </summary>
    public IReadOnlyList<OSCArgument> Arguments { get; }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="address"/> is a valid OSC address.
    /// A valid address starts with <c>/</c> and no path component contains the characters
    /// <c>space # * , ? [ ] { }</c> — the characters reserved for OSC address patterns.
    /// </summary>
    public static bool IsValidAddress(string address)
    {
        if (string.IsNullOrEmpty(address) || address[0] != '/')
            return false;

        var span = address.AsSpan(1);
        foreach (var component in span.Split('/'))
        {
            if (span[component].ContainsAny(ForbiddenAddressChars))
                return false;
        }

        return true;
    }
}

/// <summary>
/// OSC bundle containing a timetag and nested packet elements.
/// </summary>
public sealed class OSCBundle
{
    public OSCBundle(OSCTimeTag timeTag, IReadOnlyList<OSCPacket>? elements = null)
    {
        TimeTag = timeTag;
        Elements = elements ?? [];
    }

    /// <summary>
    /// NTP-format timetag associated with this bundle.
    /// </summary>
    public OSCTimeTag TimeTag { get; }

    /// <summary>
    /// Nested message and/or bundle elements in wire order.
    /// </summary>
    public IReadOnlyList<OSCPacket> Elements { get; }
}
