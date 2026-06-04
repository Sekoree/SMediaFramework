namespace HaPlay.ControlGraph;

/// <summary>
/// Holds the latest reported <see cref="ControlSessionHealth"/> per device instance. Device-session
/// health is reported here (e.g. from <see cref="ControlScriptRuntimeSession.ReportDeviceHealthAsync"/>)
/// and read back by the script-facing <c>HaPlay.Devices</c> library and the UI. Thread-safe because
/// health can be reported from background session loops while scripts read it on the dispatch thread.
/// </summary>
public sealed class ControlDeviceHealthRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ControlSessionHealth> _health = new();

    public void Report(Guid deviceInstanceId, ControlSessionHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        lock (_gate)
        {
            _health[deviceInstanceId] = health;
        }
    }

    public ControlSessionHealth? TryGet(Guid deviceInstanceId)
    {
        lock (_gate)
        {
            return _health.TryGetValue(deviceInstanceId, out var health) ? health : null;
        }
    }
}
