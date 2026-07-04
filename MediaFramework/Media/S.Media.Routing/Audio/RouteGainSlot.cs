namespace S.Media.Routing;

/// <summary>
/// Per-route gain state: <see cref="Target"/> is updated by <see cref="AudioRouter.SetRouteGain"/>;
/// <see cref="Current"/> is what the run loop last applied (click-free ramp each chunk).
/// </summary>
public sealed class RouteGainSlot
{
    public RouteGainSlot(float initial)
    {
        Target = initial;
        Current = initial;
    }

    /// <summary>Desired gain. Read-only to external callers — mutate through
    /// <see cref="AudioRouter.SetRouteGain"/> so the click-free ramp invariant is preserved.</summary>
    public float Target { get; internal set; }

    /// <summary>Gain the run loop last applied (ramps toward <see cref="Target"/> each chunk). Read-only externally.</summary>
    public float Current { get; internal set; }
}
