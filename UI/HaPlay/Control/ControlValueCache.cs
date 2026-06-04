namespace HaPlay.ControlGraph;

public sealed class ControlValueCache
{
    private readonly Dictionary<ControlValueCacheKey, ControlValueCacheEntry> _entries = new();

    public IReadOnlyCollection<ControlValueCacheEntry> Entries => _entries.Values;

    public ControlValueCacheChange? SetNumber(
        string deviceKey,
        string address,
        double value,
        ControlValueCacheSource source,
        int argumentIndex = 0,
        Guid? correlationId = null,
        DateTimeOffset? timestamp = null) =>
        Set(
            new ControlValueCacheKey(deviceKey, address, argumentIndex),
            ControlCachedValue.Number(value),
            source,
            correlationId,
            timestamp);

    public double GetNumberOrDefault(string deviceKey, string address, double defaultValue, int argumentIndex = 0) =>
        TryGetNumber(deviceKey, address, out var value, argumentIndex) ? value : defaultValue;

    public bool TryGetNumber(string deviceKey, string address, out double value, int argumentIndex = 0)
    {
        value = 0;
        var key = new ControlValueCacheKey(deviceKey, address, argumentIndex);
        if (!_entries.TryGetValue(key, out var entry) || entry.IsStale || entry.Value.Kind != ControlCachedValueKind.Number)
            return false;

        value = entry.Value.NumberValue;
        return true;
    }

    public string GetStringOrDefault(string deviceKey, string address, string defaultValue, int argumentIndex = 0) =>
        TryGetString(deviceKey, address, out var value, argumentIndex) ? value : defaultValue;

    public bool TryGetString(string deviceKey, string address, out string value, int argumentIndex = 0)
    {
        value = string.Empty;
        var key = new ControlValueCacheKey(deviceKey, address, argumentIndex);
        if (!_entries.TryGetValue(key, out var entry) || entry.IsStale || entry.Value.Kind != ControlCachedValueKind.String)
            return false;

        value = entry.Value.StringValue ?? string.Empty;
        return true;
    }

    public ControlValueCacheChange? SetString(
        string deviceKey,
        string address,
        string value,
        ControlValueCacheSource source,
        int argumentIndex = 0,
        Guid? correlationId = null,
        DateTimeOffset? timestamp = null) =>
        Set(
            new ControlValueCacheKey(deviceKey, address, argumentIndex),
            ControlCachedValue.String(value),
            source,
            correlationId,
            timestamp);

    public ControlValueCacheChange? SetBoolean(
        string deviceKey,
        string address,
        bool value,
        ControlValueCacheSource source,
        int argumentIndex = 0,
        Guid? correlationId = null,
        DateTimeOffset? timestamp = null) =>
        Set(
            new ControlValueCacheKey(deviceKey, address, argumentIndex),
            ControlCachedValue.Boolean(value),
            source,
            correlationId,
            timestamp);

    public bool TryGet(ControlValueCacheKey key, out ControlValueCacheEntry entry) =>
        _entries.TryGetValue(key, out entry!);

    public void MarkDeviceStale(string deviceKey)
    {
        foreach (var entry in _entries.Values.Where(e => string.Equals(e.Key.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _entries[entry.Key] = entry with { IsStale = true };
        }
    }

    public void ClearDevice(string deviceKey)
    {
        foreach (var key in _entries.Keys.Where(k => string.Equals(k.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _entries.Remove(key);
        }
    }

    private ControlValueCacheChange? Set(
        ControlValueCacheKey key,
        ControlCachedValue value,
        ControlValueCacheSource source,
        Guid? correlationId,
        DateTimeOffset? timestamp)
    {
        var effectiveTimestamp = timestamp ?? DateTimeOffset.UtcNow;
        var hadFreshValue = _entries.TryGetValue(key, out var previous) && !previous!.IsStale;
        var changed = !hadFreshValue || previous!.Value != value;

        _entries[key] = new ControlValueCacheEntry(
            key,
            value,
            effectiveTimestamp,
            source,
            IsStale: false,
            correlationId);

        return changed
            ? new ControlValueCacheChange(key, value, source, effectiveTimestamp, correlationId)
            : null;
    }
}

public sealed record ControlValueCacheChange(
    ControlValueCacheKey Key,
    ControlCachedValue Value,
    ControlValueCacheSource Source,
    DateTimeOffset Timestamp,
    Guid? CorrelationId);

public sealed record ControlValueCacheKey(string DeviceKey, string Address, int ArgumentIndex = 0)
{
    public string DeviceKey { get; } = string.IsNullOrWhiteSpace(DeviceKey) ? throw new ArgumentException("Device key is required.", nameof(DeviceKey)) : DeviceKey;

    public string Address { get; } = string.IsNullOrWhiteSpace(Address) ? throw new ArgumentException("OSC address is required.", nameof(Address)) : Address;

    public int ArgumentIndex { get; } = ArgumentIndex < 0 ? throw new ArgumentOutOfRangeException(nameof(ArgumentIndex), "Argument index must be non-negative.") : ArgumentIndex;
}

public sealed record ControlValueCacheEntry(
    ControlValueCacheKey Key,
    ControlCachedValue Value,
    DateTimeOffset Timestamp,
    ControlValueCacheSource Source,
    bool IsStale,
    Guid? CorrelationId);

public readonly record struct ControlCachedValue(ControlCachedValueKind Kind, double NumberValue, string? StringValue, bool BooleanValue)
{
    public static ControlCachedValue Number(double value) =>
        new(ControlCachedValueKind.Number, value, null, false);

    public static ControlCachedValue String(string value) =>
        new(ControlCachedValueKind.String, 0, value, false);

    public static ControlCachedValue Boolean(bool value) =>
        new(ControlCachedValueKind.Boolean, 0, null, value);
}

public enum ControlCachedValueKind
{
    Number,
    String,
    Boolean,
}

public enum ControlValueCacheSource
{
    Incoming,
    OptimisticSend,
    Script,
}
