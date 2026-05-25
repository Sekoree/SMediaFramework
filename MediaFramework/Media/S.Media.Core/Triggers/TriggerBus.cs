namespace S.Media.Core.Triggers;

/// <summary>
/// Stable, allocation-free trigger surface for scripts and protocol adapters (OSC, MIDI).
/// </summary>
public sealed class TriggerBus
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TriggerHandler> _handlers = new(StringComparer.Ordinal);

    /// <summary>Registered trigger ids (snapshot).</summary>
    public IReadOnlyCollection<string> RegisteredIds
    {
        get
        {
            lock (_gate)
                return _handlers.Keys.ToArray();
        }
    }

    public void Register(string triggerId, TriggerHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
            _handlers[triggerId] = handler;
    }

    public bool Unregister(string triggerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        lock (_gate)
            return _handlers.Remove(triggerId);
    }

    /// <summary>Invokes the handler when registered; returns <c>false</c> when no handler exists.</summary>
    public bool Fire(string triggerId, in TriggerPayload payload = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        TriggerHandler? handler;
        lock (_gate)
        {
            if (!_handlers.TryGetValue(triggerId, out handler))
                return false;
        }

        handler!(payload);
        return true;
    }
}
