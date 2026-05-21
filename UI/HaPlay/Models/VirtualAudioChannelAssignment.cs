namespace HaPlay.Models;

/// <summary>
/// Project-level mapping from a concrete output channel to a virtual audio channel number (VOut N).
/// </summary>
public sealed record VirtualAudioChannelAssignment
{
    public Guid OutputDefinitionId { get; init; }

    public int OutputChannel { get; init; }

    public int VirtualOutputChannel { get; init; }
}
