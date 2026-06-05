namespace S.Control;

/// <summary>Lifecycle state of a control device/listener session, surfaced to health-change triggers.</summary>
public enum ControlSessionState
{
    Stopped,
    Starting,
    Running,
    Faulted,
}

/// <summary>A point-in-time health snapshot for a control session.</summary>
public sealed record ControlSessionHealth(
    ControlSessionState State,
    string Detail = "",
    DateTimeOffset UpdatedAtUtc = default)
{
    public static ControlSessionHealth Stopped(string detail = "") =>
        new(ControlSessionState.Stopped, detail, DateTimeOffset.UtcNow);

    public static ControlSessionHealth Running(string detail = "") =>
        new(ControlSessionState.Running, detail, DateTimeOffset.UtcNow);

    public static ControlSessionHealth Faulted(string detail) =>
        new(ControlSessionState.Faulted, detail, DateTimeOffset.UtcNow);
}
