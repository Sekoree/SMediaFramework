namespace S.Media.Core.Audio;

/// <summary>
/// An audio source that can be explicitly stopped - e.g. a clip voice in a choke group. Lets the
/// <c>AudioRouter</c> stop a member without referencing concrete player/clip types (keeps Routing free
/// of the player tier). Clip voices (Players/Session phase) implement this.
/// </summary>
public interface IStoppableSource
{
    /// <summary>Stop emitting; the source becomes exhausted.</summary>
    void Stop();
}
