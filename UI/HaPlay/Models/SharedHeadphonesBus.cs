namespace HaPlay.Models;

/// <summary>
/// §8.2 — A project-level named bus that aliases a PortAudio output. Multiple players can route
/// their per-player headphones cue send into the same bus, so an engineer mixing several decks can
/// monitor any of them on one shared pair of headphones (or a dedicated PA channel pair).
/// Identity is <see cref="Id"/>; <see cref="Label"/> is the human-readable name shown in the player
/// target picker. <see cref="PortAudioOutputId"/> resolves to a <see cref="PortAudioOutputDefinition"/>
/// in the same project (<see cref="HaPlayProject.Outputs"/>).
/// </summary>
public sealed record SharedHeadphonesBus
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Label { get; init; } = "Monitor Bus";

    /// <summary>References a <see cref="OutputDefinition.Id"/> of a PortAudio output. When the
    /// referenced output is missing on load, callers surface the bus as broken (similar to the
    /// action-endpoint rebind flow).</summary>
    public Guid? PortAudioOutputId { get; init; }
}
