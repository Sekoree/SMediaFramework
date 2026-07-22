using System.Collections.Concurrent;

namespace S.Control;

public sealed class ControlValueCache
{
    // Keep the public record key in entries/change notifications, but use a value-type lookup key internally.
    // UI/profile readers poll hundreds of addresses; a reference-type dictionary key would otherwise allocate
    // one short-lived ControlValueCacheKey for every unsuccessful probe.
    private readonly ConcurrentDictionary<LookupKey, ControlValueCacheEntry> _entries = new();
    private long _version;

    /// <summary>A stable snapshot; the runtime writes this cache while UI consumers may inspect it.</summary>
    public IReadOnlyCollection<ControlValueCacheEntry> Entries => _entries.Values.ToArray();

    /// <summary>Monotonic mutation version for allocation-free UI polling.</summary>
    public long Version => Interlocked.Read(ref _version);

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
        if (!_entries.TryGetValue(new LookupKey(deviceKey, address, argumentIndex), out var entry)
            || entry.IsStale || entry.Value.Kind != ControlCachedValueKind.Number)
            return false;

        value = entry.Value.NumberValue;
        return true;
    }

    public string GetStringOrDefault(string deviceKey, string address, string defaultValue, int argumentIndex = 0) =>
        TryGetString(deviceKey, address, out var value, argumentIndex) ? value : defaultValue;

    public bool TryGetString(string deviceKey, string address, out string value, int argumentIndex = 0)
    {
        value = string.Empty;
        if (!_entries.TryGetValue(new LookupKey(deviceKey, address, argumentIndex), out var entry)
            || entry.IsStale || entry.Value.Kind != ControlCachedValueKind.String)
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

    public bool TryGet(ControlValueCacheKey key, out ControlValueCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _entries.TryGetValue(LookupKey.From(key), out entry!);
    }

    /// <summary>Allocation-free address lookup for profile/UI polling.</summary>
    public bool TryGet(string deviceKey, string address, out ControlValueCacheEntry entry, int argumentIndex = 0) =>
        _entries.TryGetValue(new LookupKey(deviceKey, address, argumentIndex), out entry!);

    /// <summary>True when a fresh (non-stale) value of any kind is cached for this device/address/argument.</summary>
    public bool Has(string deviceKey, string address, int argumentIndex = 0) =>
        _entries.TryGetValue(new LookupKey(deviceKey, address, argumentIndex), out var entry) && !entry.IsStale;

    public void MarkDeviceStale(string deviceKey)
    {
        var changed = false;
        foreach (var entry in _entries.Values.Where(e => string.Equals(e.Key.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            if (!entry.IsStale && _entries.TryUpdate(LookupKey.From(entry.Key), entry with { IsStale = true }, entry))
                changed = true;
        }

        if (changed)
            Interlocked.Increment(ref _version);
    }

    public void ClearDevice(string deviceKey)
    {
        var changed = false;
        foreach (var key in _entries.Keys.Where(k => string.Equals(k.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            changed |= _entries.TryRemove(key, out _);
        }

        if (changed)
            Interlocked.Increment(ref _version);
    }

    private ControlValueCacheChange? Set(
        ControlValueCacheKey key,
        ControlCachedValue value,
        ControlValueCacheSource source,
        Guid? correlationId,
        DateTimeOffset? timestamp)
    {
        var effectiveTimestamp = timestamp ?? DateTimeOffset.UtcNow;
        var lookup = LookupKey.From(key);
        var hadFreshValue = _entries.TryGetValue(lookup, out var previous) && !previous!.IsStale;
        var changed = !hadFreshValue || previous!.Value != value;

        _entries[lookup] = new ControlValueCacheEntry(
            key,
            value,
            effectiveTimestamp,
            source,
            IsStale: false,
            correlationId);
        Interlocked.Increment(ref _version);

        return changed
            ? new ControlValueCacheChange(key, value, source, effectiveTimestamp, correlationId)
            : null;
    }

    private readonly record struct LookupKey
    {
        public LookupKey(string deviceKey, string address, int argumentIndex)
        {
            if (string.IsNullOrWhiteSpace(deviceKey))
                throw new ArgumentException("Device key is required.", nameof(deviceKey));
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("OSC address is required.", nameof(address));
            if (argumentIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(argumentIndex), "Argument index must be non-negative.");
            DeviceKey = deviceKey;
            Address = address;
            ArgumentIndex = argumentIndex;
        }

        public string DeviceKey { get; }

        public string Address { get; }

        public int ArgumentIndex { get; }

        public static LookupKey From(ControlValueCacheKey key) =>
            new(key.DeviceKey, key.Address, key.ArgumentIndex);
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
