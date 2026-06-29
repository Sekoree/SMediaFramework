namespace HaPlay.ViewModels;

/// <summary>Aggregated health for an output line (§8.1 / §8.11).</summary>
public enum OutputLineHealthState
{
    Unknown,
    Healthy,
    Warning,
    Error,
}
