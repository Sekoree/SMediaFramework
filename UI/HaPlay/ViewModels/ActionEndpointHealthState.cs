namespace HaPlay.ViewModels;

/// <summary>Probe result for an OSC/MIDI action endpoint row.</summary>
public enum ActionEndpointHealthState
{
    Unknown,
    Checking,
    Ok,
    Failed,
}
