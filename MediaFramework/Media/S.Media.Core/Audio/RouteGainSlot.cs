namespace S.Media.Core.Audio;

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

    public float Target;
    public float Current;
}
