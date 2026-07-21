namespace HaViz.Core;

public enum LoopMode
{
    Off,
    One,
    All,
}

/// <summary>One playable item. <see cref="Uri"/> is platform-opaque (a SAF content:// URI on
/// Android, a file path elsewhere) - the platform player resolves it.</summary>
public sealed record TrackInfo(string Uri, string DisplayName);

/// <summary>
/// Pure playlist/loop logic for the mini player, kept platform-free so it is unit-testable: the
/// platform layer feeds decoded audio and calls <see cref="AdvanceAfterTrackEnd"/> /
/// <see cref="Next"/> / <see cref="Previous"/>; this class only decides WHICH track plays next.
/// Not thread-safe - call from one thread (the player's control thread).
/// </summary>
public sealed class PlaylistController
{
    private readonly List<TrackInfo> _tracks = [];

    public IReadOnlyList<TrackInfo> Tracks => _tracks;

    public int CurrentIndex { get; private set; } = -1;

    public TrackInfo? Current => CurrentIndex >= 0 && CurrentIndex < _tracks.Count ? _tracks[CurrentIndex] : null;

    public LoopMode LoopMode { get; set; } = LoopMode.All;

    /// <summary>Replaces the playlist. The current position resets; playback starts via <see cref="Next"/>.</summary>
    public void SetTracks(IEnumerable<TrackInfo> tracks)
    {
        _tracks.Clear();
        _tracks.AddRange(tracks);
        CurrentIndex = -1;
    }

    /// <summary>Selects a specific track (UI tap on a list entry).</summary>
    public TrackInfo? Select(int index)
    {
        if (index < 0 || index >= _tracks.Count)
            return null;
        CurrentIndex = index;
        return Current;
    }

    /// <summary>Manual skip forward. Wraps at the end regardless of loop mode (a deliberate user
    /// action should always do something); null only when the playlist is empty.</summary>
    public TrackInfo? Next()
    {
        if (_tracks.Count == 0)
            return null;
        CurrentIndex = (CurrentIndex + 1) % _tracks.Count;
        return Current;
    }

    /// <summary>Manual skip backward, wrapping like <see cref="Next"/>.</summary>
    public TrackInfo? Previous()
    {
        if (_tracks.Count == 0)
            return null;
        CurrentIndex = CurrentIndex <= 0 ? _tracks.Count - 1 : CurrentIndex - 1;
        return Current;
    }

    /// <summary>The track that should play after the current one ENDED naturally, per the loop
    /// mode: One repeats it, All advances (wrapping), Off advances until the end then stops (null).</summary>
    public TrackInfo? AdvanceAfterTrackEnd()
    {
        if (_tracks.Count == 0 || CurrentIndex < 0)
            return null;

        switch (LoopMode)
        {
            case LoopMode.One:
                return Current;
            case LoopMode.All:
                CurrentIndex = (CurrentIndex + 1) % _tracks.Count;
                return Current;
            default:
                if (CurrentIndex + 1 >= _tracks.Count)
                {
                    CurrentIndex = -1;
                    return null;
                }

                CurrentIndex++;
                return Current;
        }
    }
}
