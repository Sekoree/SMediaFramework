using System.Text.Json;
using System.Text.Json.Serialization;
using OSCLib;

namespace S.Control;

public interface IControlMonitorSink
{
    void Record(ControlMonitorRecord record);
}

public sealed class NullControlMonitorSink : IControlMonitorSink
{
    public static NullControlMonitorSink Instance { get; } = new();

    private NullControlMonitorSink()
    {
    }

    public void Record(ControlMonitorRecord record)
    {
    }
}

/// <summary>
/// Fixed-capacity, drop-oldest monitor ring (CTRL-03). Backed by a pre-sized array with a moving start
/// index, so <see cref="Record"/> is O(1) at any fill level - the previous <c>List.RemoveAt(0)</c> shifted
/// every remaining element on each add once the buffer was full, i.e. O(n) at exactly the sustained
/// meter/update rate the monitor is meant to absorb.
/// </summary>
public sealed class ControlMonitorBuffer : IControlMonitorSink
{
    private readonly object _gate = new();
    private readonly ControlMonitorRecord[] _ring;
    private int _start; // index of the oldest record
    private int _count;

    public ControlMonitorBuffer(int maxRecords)
    {
        _ring = new ControlMonitorRecord[Math.Max(1, maxRecords)];
    }

    /// <summary>Snapshot of buffered records, oldest first.</summary>
    public IReadOnlyList<ControlMonitorRecord> Records
    {
        get
        {
            lock (_gate)
            {
                var result = new ControlMonitorRecord[_count];
                for (var i = 0; i < _count; i++)
                    result[i] = _ring[(_start + i) % _ring.Length];
                return result;
            }
        }
    }

    public void Record(ControlMonitorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            if (_count < _ring.Length)
            {
                _ring[(_start + _count) % _ring.Length] = record;
                _count++;
            }
            else
            {
                // Full: overwrite the oldest slot and advance the window - O(1) drop-oldest.
                _ring[_start] = record;
                _start = (_start + 1) % _ring.Length;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring, 0, _ring.Length);
            _start = 0;
            _count = 0;
        }
    }
}

public sealed record ControlMonitorRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public ControlMonitorDirection Direction { get; init; }

    public ControlMonitorProtocol Protocol { get; init; }

    public ControlMonitorResult Result { get; init; }

    public Guid? DeviceInstanceId { get; init; }

    public Guid? ListenerId { get; init; }

    public Guid? ScriptId { get; init; }

    public Guid? TriggerId { get; init; }

    public Guid? OriginId { get; init; }

    public Guid? CorrelationId { get; init; }

    public string? DeviceKey { get; init; }

    public string? ProfileId { get; init; }

    public string? Endpoint { get; init; }

    public string? RemoteHost { get; init; }

    public int? RemotePort { get; init; }

    public string? Address { get; init; }

    public List<ControlMonitorOSCArgumentRecord> OSCArguments { get; init; } = new();

    public int? MIDIChannel { get; init; }

    public ControlMIDIMessageType? MIDIMessageType { get; init; }

    public int? MIDIController { get; init; }

    public int? MIDINote { get; init; }

    public int? MIDIValue { get; init; }

    public int? MIDIParameter { get; init; }

    public bool? MIDIHighResolution14Bit { get; init; }

    public string? Message { get; init; }

    public string? ErrorMessage { get; init; }

    public byte[]? RawBytes { get; init; }
}

public sealed record ControlMonitorOSCArgumentRecord
{
    public string Kind { get; init; } = string.Empty;

    public string? StringValue { get; init; }

    public long? IntegerValue { get; init; }

    public double? FloatValue { get; init; }

    public bool? BoolValue { get; init; }

    public byte[]? BlobValue { get; init; }

    public static ControlMonitorOSCArgumentRecord FromOSCArgument(OSCArgument argument) =>
        argument.Type switch
        {
            OSCArgumentType.Int32 => new() { Kind = nameof(OSCArgumentType.Int32), IntegerValue = argument.AsInt32() },
            OSCArgumentType.Int64 => new() { Kind = nameof(OSCArgumentType.Int64), IntegerValue = argument.AsInt64() },
            OSCArgumentType.Float32 => new() { Kind = nameof(OSCArgumentType.Float32), FloatValue = argument.AsFloat32() },
            OSCArgumentType.Double64 => new() { Kind = nameof(OSCArgumentType.Double64), FloatValue = argument.AsDouble64() },
            OSCArgumentType.String => new() { Kind = nameof(OSCArgumentType.String), StringValue = argument.AsString() },
            OSCArgumentType.Symbol => new() { Kind = nameof(OSCArgumentType.Symbol), StringValue = argument.AsString() },
            OSCArgumentType.True => new() { Kind = nameof(OSCArgumentType.True), BoolValue = true },
            OSCArgumentType.False => new() { Kind = nameof(OSCArgumentType.False), BoolValue = false },
            OSCArgumentType.Blob => new() { Kind = nameof(OSCArgumentType.Blob), BlobValue = argument.AsBlob().ToArray() },
            OSCArgumentType.Nil => new() { Kind = nameof(OSCArgumentType.Nil) },
            _ => new() { Kind = argument.Type.ToString() },
        };

    public static ControlMonitorOSCArgumentRecord FromCachedValue(ControlCachedValue value) =>
        value.Kind switch
        {
            ControlCachedValueKind.Number => new() { Kind = nameof(OSCArgumentType.Double64), FloatValue = value.NumberValue },
            ControlCachedValueKind.String => new() { Kind = nameof(OSCArgumentType.String), StringValue = value.StringValue },
            ControlCachedValueKind.Boolean => value.BooleanValue
                ? new() { Kind = nameof(OSCArgumentType.True), BoolValue = true }
                : new() { Kind = nameof(OSCArgumentType.False), BoolValue = false },
            _ => new() { Kind = value.Kind.ToString() },
        };

    public static ControlMonitorOSCArgumentRecord FromScriptArgument(ControlScriptOSCArgument argument) =>
        argument.Type switch
        {
            ControlScriptOSCArgumentType.Float32 => new() { Kind = nameof(ControlScriptOSCArgumentType.Float32), FloatValue = argument.NumberValue },
            ControlScriptOSCArgumentType.Double64 => new() { Kind = nameof(ControlScriptOSCArgumentType.Double64), FloatValue = argument.NumberValue },
            ControlScriptOSCArgumentType.Int32 => new() { Kind = nameof(ControlScriptOSCArgumentType.Int32), IntegerValue = (long)argument.NumberValue },
            ControlScriptOSCArgumentType.Int64 => new() { Kind = nameof(ControlScriptOSCArgumentType.Int64), IntegerValue = (long)argument.NumberValue },
            ControlScriptOSCArgumentType.String => new() { Kind = nameof(ControlScriptOSCArgumentType.String), StringValue = argument.StringValue },
            ControlScriptOSCArgumentType.Symbol => new() { Kind = nameof(ControlScriptOSCArgumentType.Symbol), StringValue = argument.StringValue },
            ControlScriptOSCArgumentType.True => new() { Kind = nameof(ControlScriptOSCArgumentType.True), BoolValue = true },
            ControlScriptOSCArgumentType.False => new() { Kind = nameof(ControlScriptOSCArgumentType.False), BoolValue = false },
            _ => new() { Kind = argument.Type.ToString() },
        };
}

public enum ControlMonitorDirection
{
    Input,
    Output,
    Internal,
    Dropped,
    Error,
}

public enum ControlMonitorProtocol
{
    MIDI,
    OSC,
    Script,
    Runtime,
    Cache,
}

public enum ControlMonitorResult
{
    Received,
    Sent,
    Failed,
    Invoked,
    Cached,
    Dropped,
    Suppressed,
    Logged,
}

public static class ControlMonitorJsonLines
{
    // Source-generated, NativeAOT-safe contract. UseStringEnumConverter keeps enums as strings (matching the
    // previous reflection-based JsonStringEnumConverter) without runtime reflection/codegen.
    private static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<ControlMonitorRecord> RecordTypeInfo =
        ControlMonitorJsonContext.Default.ControlMonitorRecord;

    public static string Write(IReadOnlyList<ControlMonitorRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return string.Join(
            Environment.NewLine,
            records.Select(record => JsonSerializer.Serialize(record, RecordTypeInfo)));
    }

    public static IReadOnlyList<ControlMonitorRecord> Read(string jsonLines)
    {
        if (string.IsNullOrWhiteSpace(jsonLines))
            return [];

        var records = new List<ControlMonitorRecord>();
        foreach (var line in jsonLines.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var record = JsonSerializer.Deserialize(line, RecordTypeInfo)
                ?? throw new JsonException("Monitor record line deserialized to null.");
            records.Add(record);
        }

        return records;
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ControlMonitorRecord))]
internal partial class ControlMonitorJsonContext : JsonSerializerContext;
