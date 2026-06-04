using System.Text.Json;
using System.Text.Json.Serialization;
using OSCLib;

namespace HaPlay.ControlGraph;

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

    public List<ControlMonitorOscArgumentRecord> OscArguments { get; init; } = new();

    public int? MidiChannel { get; init; }

    public int? MidiController { get; init; }

    public int? MidiNote { get; init; }

    public int? MidiValue { get; init; }

    public bool? MidiHighResolution14Bit { get; init; }

    public string? Message { get; init; }

    public string? ErrorMessage { get; init; }

    public byte[]? RawBytes { get; init; }
}

public sealed record ControlMonitorOscArgumentRecord
{
    public string Kind { get; init; } = string.Empty;

    public string? StringValue { get; init; }

    public long? IntegerValue { get; init; }

    public double? FloatValue { get; init; }

    public bool? BoolValue { get; init; }

    public byte[]? BlobValue { get; init; }

    public static ControlMonitorOscArgumentRecord FromOscArgument(OSCArgument argument) =>
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

    public static ControlMonitorOscArgumentRecord FromCachedValue(ControlCachedValue value) =>
        value.Kind switch
        {
            ControlCachedValueKind.Number => new() { Kind = nameof(OSCArgumentType.Double64), FloatValue = value.NumberValue },
            ControlCachedValueKind.String => new() { Kind = nameof(OSCArgumentType.String), StringValue = value.StringValue },
            ControlCachedValueKind.Boolean => value.BooleanValue
                ? new() { Kind = nameof(OSCArgumentType.True), BoolValue = true }
                : new() { Kind = nameof(OSCArgumentType.False), BoolValue = false },
            _ => new() { Kind = value.Kind.ToString() },
        };

    public static ControlMonitorOscArgumentRecord FromScriptArgument(ControlScriptOscArgument argument) =>
        argument.Type switch
        {
            ControlScriptOscArgumentType.Float32 => new() { Kind = nameof(ControlScriptOscArgumentType.Float32), FloatValue = argument.NumberValue },
            ControlScriptOscArgumentType.Double64 => new() { Kind = nameof(ControlScriptOscArgumentType.Double64), FloatValue = argument.NumberValue },
            ControlScriptOscArgumentType.Int32 => new() { Kind = nameof(ControlScriptOscArgumentType.Int32), IntegerValue = (long)argument.NumberValue },
            ControlScriptOscArgumentType.Int64 => new() { Kind = nameof(ControlScriptOscArgumentType.Int64), IntegerValue = (long)argument.NumberValue },
            ControlScriptOscArgumentType.String => new() { Kind = nameof(ControlScriptOscArgumentType.String), StringValue = argument.StringValue },
            ControlScriptOscArgumentType.Symbol => new() { Kind = nameof(ControlScriptOscArgumentType.Symbol), StringValue = argument.StringValue },
            ControlScriptOscArgumentType.True => new() { Kind = nameof(ControlScriptOscArgumentType.True), BoolValue = true },
            ControlScriptOscArgumentType.False => new() { Kind = nameof(ControlScriptOscArgumentType.False), BoolValue = false },
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
    Midi,
    Osc,
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
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Write(IReadOnlyList<ControlMonitorRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return string.Join(
            Environment.NewLine,
            records.Select(record => JsonSerializer.Serialize(record, Options)));
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

            var record = JsonSerializer.Deserialize<ControlMonitorRecord>(line, Options)
                ?? throw new JsonException("Monitor record line deserialized to null.");
            records.Add(record);
        }

        return records;
    }
}
