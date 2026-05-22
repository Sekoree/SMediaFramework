using System.Buffers;

namespace OSCLib;

/// <summary>
/// Logical OSC argument type represented by <see cref="OSCArgument"/>.
/// </summary>
public enum OSCArgumentType
{
    Int32,
    Float32,
    String,
    Blob,
    Int64,
    TimeTag,
    Double64,
    Symbol,
    Char,
    RgbaColor,
    MIDI,
    True,
    False,
    Nil,
    Impulse,
    Array,
    Unknown
}

/// <summary>
/// Handling behavior for inbound datagrams larger than the configured limit.
/// </summary>
public enum OSCOversizePolicy
{
    /// <summary>
    /// Drop the packet and emit throttled warning logs.
    /// </summary>
    DropAndLog,

    /// <summary>
    /// Throw from the receive loop when an oversize packet is encountered.
    /// </summary>
    Throw
}

/// <summary>
/// OSC/NTP timetag value.
/// </summary>
public readonly record struct OSCTimeTag(ulong Value)
{
    private static readonly DateTimeOffset NtpEpochUtc = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Timetag constant that denotes immediate execution.
    /// </summary>
    public static OSCTimeTag Immediately => new(1UL);

    /// <summary>
    /// Indicates whether the timetag is the OSC special immediate value.
    /// </summary>
    public bool IsImmediately => Value == 1UL;

    /// <summary>
    /// Converts a UTC timestamp to an OSC/NTP timetag. Use <see cref="Immediately"/> for immediate dispatch.
    /// </summary>
    public static OSCTimeTag FromDateTimeOffset(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        if (utc < NtpEpochUtc)
            throw new ArgumentOutOfRangeException(nameof(timestamp), timestamp, "OSC timetag cannot precede the NTP epoch.");

        var delta = utc - NtpEpochUtc;
        var seconds = (ulong)delta.Ticks / (ulong)TimeSpan.TicksPerSecond;
        var remainderTicks = (ulong)delta.Ticks % (ulong)TimeSpan.TicksPerSecond;
        var fraction = (remainderTicks << 32) / (ulong)TimeSpan.TicksPerSecond;
        return new OSCTimeTag((seconds << 32) | fraction);
    }

    /// <summary>
    /// Converts this OSC/NTP timetag to a UTC timestamp. The special <see cref="Immediately"/>
    /// value cannot be represented as an absolute timestamp.
    /// </summary>
    public DateTimeOffset ToDateTimeOffset()
    {
        if (IsImmediately)
            throw new InvalidOperationException("OSC immediate timetag does not represent an absolute timestamp.");

        var seconds = Value >> 32;
        var fraction = Value & 0xFFFFFFFFUL;
        var ticks = checked((long)seconds * TimeSpan.TicksPerSecond)
            + (long)((fraction * (ulong)TimeSpan.TicksPerSecond) >> 32);
        return NtpEpochUtc.AddTicks(ticks);
    }
}

/// <summary>
/// Packed 4-byte MIDI payload carried by OSC tag <c>m</c>.
/// </summary>
public readonly record struct OSCMIDIMessage(byte PortId, byte Status, byte Data1, byte Data2)
{
    public uint ToUInt32()
        => (uint)((PortId << 24) | (Status << 16) | (Data1 << 8) | Data2);

    public static OSCMIDIMessage FromUInt32(uint value)
        => new(
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF));
}

/// <summary>
/// Unknown OSC argument payload preserved by non-strict decode settings.
/// </summary>
public readonly record struct OSCUnknownArgument(char Tag, ReadOnlyMemory<byte> RawData);

/// <summary>
/// Compact tagged OSC argument representation.
/// </summary>
public readonly struct OSCArgument
{
    private readonly int _int32;
    private readonly long _int64;
    private readonly double _double64;
    private readonly object? _reference;

    private OSCArgument(OSCArgumentType type, int int32, long int64, double double64, object? reference)
    {
        Type = type;
        _int32 = int32;
        _int64 = int64;
        _double64 = double64;
        _reference = reference;
    }

    /// <summary>
    /// Encoded argument kind.
    /// </summary>
    public OSCArgumentType Type { get; }

    public static OSCArgument Int32(int value) => new(OSCArgumentType.Int32, value, default, default, null);

    public static OSCArgument Float32(float value)
        => new(OSCArgumentType.Float32, BitConverter.SingleToInt32Bits(value), default, default, null);

    public static OSCArgument String(string value)
        => new(OSCArgumentType.String, default, default, default, value);

    public static OSCArgument Blob(ReadOnlyMemory<byte> value)
        => new(OSCArgumentType.Blob, default, default, default, value);

    public static OSCArgument Int64(long value)
        => new(OSCArgumentType.Int64, default, value, default, null);

    public static OSCArgument TimeTag(OSCTimeTag value)
        => new(OSCArgumentType.TimeTag, default, unchecked((long)value.Value), default, null);

    public static OSCArgument Double64(double value)
        => new(OSCArgumentType.Double64, default, default, value, null);

    public static OSCArgument Symbol(string value)
        => new(OSCArgumentType.Symbol, default, default, default, value);

    public static OSCArgument Char(char value)
        => new(OSCArgumentType.Char, value, default, default, null);

    public static OSCArgument RgbaColor(uint value)
        => new(OSCArgumentType.RgbaColor, unchecked((int)value), default, default, null);

    public static OSCArgument MIDI(OSCMIDIMessage value)
        => new(OSCArgumentType.MIDI, unchecked((int)value.ToUInt32()), default, default, null);

    public static OSCArgument True() => new(OSCArgumentType.True, default, default, default, null);

    public static OSCArgument False() => new(OSCArgumentType.False, default, default, default, null);

    public static OSCArgument Nil() => new(OSCArgumentType.Nil, default, default, default, null);

    public static OSCArgument Impulse() => new(OSCArgumentType.Impulse, default, default, default, null);

    public static OSCArgument Array(IReadOnlyList<OSCArgument> value)
        => new(OSCArgumentType.Array, default, default, default, value);

    public static OSCArgument Unknown(char tag, ReadOnlyMemory<byte> rawData)
        => new(OSCArgumentType.Unknown, default, default, default, new OSCUnknownArgument(tag, rawData));

    public int AsInt32() => Type == OSCArgumentType.Int32 ? _int32 : ThrowType<int>(OSCArgumentType.Int32);

    public float AsFloat32() => Type == OSCArgumentType.Float32 ? BitConverter.Int32BitsToSingle(_int32) : ThrowType<float>(OSCArgumentType.Float32);

    public string AsString()
        => Type is OSCArgumentType.String or OSCArgumentType.Symbol
            ? (string)_reference!
            : ThrowType<string>(OSCArgumentType.String);

    public ReadOnlyMemory<byte> AsBlob() => Type == OSCArgumentType.Blob ? (ReadOnlyMemory<byte>)_reference! : ThrowType<ReadOnlyMemory<byte>>(OSCArgumentType.Blob);

    public long AsInt64() => Type == OSCArgumentType.Int64 ? _int64 : ThrowType<long>(OSCArgumentType.Int64);

    public OSCTimeTag AsTimeTag() => Type == OSCArgumentType.TimeTag ? new(unchecked((ulong)_int64)) : ThrowType<OSCTimeTag>(OSCArgumentType.TimeTag);

    public double AsDouble64() => Type == OSCArgumentType.Double64 ? _double64 : ThrowType<double>(OSCArgumentType.Double64);

    public char AsChar() => Type == OSCArgumentType.Char ? (char)_int32 : ThrowType<char>(OSCArgumentType.Char);

    public uint AsRgbaColor() => Type == OSCArgumentType.RgbaColor ? unchecked((uint)_int32) : ThrowType<uint>(OSCArgumentType.RgbaColor);

    public OSCMIDIMessage AsMIDI() => Type == OSCArgumentType.MIDI ? OSCMIDIMessage.FromUInt32(unchecked((uint)_int32)) : ThrowType<OSCMIDIMessage>(OSCArgumentType.MIDI);

    public IReadOnlyList<OSCArgument> AsArray() => Type == OSCArgumentType.Array ? (IReadOnlyList<OSCArgument>)_reference! : ThrowType<IReadOnlyList<OSCArgument>>(OSCArgumentType.Array);

    public OSCUnknownArgument AsUnknown() => Type == OSCArgumentType.Unknown ? (OSCUnknownArgument)_reference! : ThrowType<OSCUnknownArgument>(OSCArgumentType.Unknown);

    private T ThrowType<T>(OSCArgumentType expected)
        => throw new InvalidOperationException($"Argument type is {Type}, expected {expected}.");
}

public sealed class RentedBuffer : IDisposable
{
    private byte[]? _buffer;

    public RentedBuffer(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(RentedBuffer));

    public int Length { get; }

    public ReadOnlyMemory<byte> Memory => Buffer.AsMemory(0, Length);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is null)
            return;

        ArrayPool<byte>.Shared.Return(buffer);
    }
}
