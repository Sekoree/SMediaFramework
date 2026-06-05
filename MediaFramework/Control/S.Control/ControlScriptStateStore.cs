namespace S.Control;

/// <summary>
/// Backing store for the script-facing <c>HaPlay.State</c> library. Holds scoped key/value state that
/// scripts keep between events: a single project-wide map shared by all scripts, a per-script map, and a
/// per-device map. The runtime sets the current script/device around each trigger invocation so the
/// script- and device-scoped views resolve to the right slot. Values are restricted to number, string,
/// boolean, or null (the conversion lives in the Mond library). This type is Mond-agnostic.
/// </summary>
public sealed class ControlScriptStateStore
{
    private readonly Dictionary<string, object?> _project = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Dictionary<string, object?>> _scripts = new();
    private readonly Dictionary<Guid, Dictionary<string, object?>> _devices = new();

    public Guid? CurrentScriptId { get; private set; }

    public Guid? CurrentDeviceId { get; private set; }

    /// <summary>The project-wide scope, always available.</summary>
    public IDictionary<string, object?> Project => _project;

    /// <summary>The current script's scope, or null when no script is executing.</summary>
    public IDictionary<string, object?>? Script =>
        CurrentScriptId is { } id ? GetOrCreate(_scripts, id) : null;

    /// <summary>The current device's scope, or null when no device is in context.</summary>
    public IDictionary<string, object?>? Device =>
        CurrentDeviceId is { } id ? GetOrCreate(_devices, id) : null;

    public void BeginInvocation(Guid? scriptId, Guid? deviceId)
    {
        CurrentScriptId = scriptId;
        CurrentDeviceId = deviceId;
    }

    public void EndInvocation()
    {
        CurrentScriptId = null;
        CurrentDeviceId = null;
    }

    private static Dictionary<string, object?> GetOrCreate(Dictionary<Guid, Dictionary<string, object?>> map, Guid key)
    {
        if (!map.TryGetValue(key, out var scope))
        {
            scope = new Dictionary<string, object?>(StringComparer.Ordinal);
            map[key] = scope;
        }

        return scope;
    }
}
