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

public sealed class ControlMonitorBuffer : IControlMonitorSink
{
    private readonly object _gate = new();
    private readonly List<ControlMonitorRecord> _records = new();
    private readonly int _maxRecords;

    public ControlMonitorBuffer(int maxRecords)
    {
        _maxRecords = Math.Max(1, maxRecords);
    }

    public IReadOnlyList<ControlMonitorRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.ToArray();
            }
        }
    }

    public void Record(ControlMonitorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_gate)
        {
            _records.Add(record);
            while (_records.Count > _maxRecords)
                _records.RemoveAt(0);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _records.Clear();
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
