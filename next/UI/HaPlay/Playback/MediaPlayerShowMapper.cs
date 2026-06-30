using S.Media.Session;

namespace HaPlay.Playback;

/// <summary>
/// Maps a standalone media-player source onto a 1-cue <see cref="ShowDocument"/> so the player can run on a
/// per-player <see cref="ShowSession"/> (Phase-8 full convergence — replacing <c>HaPlayPlaybackSession</c>).
/// The single cue's clip plays on one composition (when there is video) that the host renders to the player's
/// video output lines, with audio on its <see cref="ShowClipAudioRoute"/>s.
/// </summary>
/// <remarks>
/// File-core slice: a file/URI source → one cue. Live-input flags (NDI/PortAudio), logo fallback, and
/// hold-frames are layered on by the player VM in later slices — see the convergence memory.
/// </remarks>
public static class MediaPlayerShowMapper
{
    /// <summary>The single cue id of a player's show (one player ⇒ one cue).</summary>
    public const string PlayerCueId = "player";

    /// <summary>The player canvas composition id (present only when the source has video).</summary>
    public const string PlayerCompositionId = "player-canvas";

    /// <summary>Builds the 1-cue show for a player source. <paramref name="hasVideo"/> gates the composition
    /// (audio-only sources skip it); <paramref name="audioRoutes"/> sends the audio to the player's output
    /// line(s) and devices (empty ⇒ the default output).</summary>
    public static ShowDocument ToShowDocument(
        string mediaPath,
        bool hasVideo,
        IReadOnlyList<ShowClipAudioRoute>? audioRoutes = null,
        int canvasWidth = 1920,
        int canvasHeight = 1080)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        return ShowDocument.Empty with
        {
            Cues = [new CueDefinition(PlayerCueId, 1, "Player")],
            Clips =
            [
                new ShowClipBinding(
                    PlayerCueId,
                    mediaPath,
                    CompositionId: hasVideo ? PlayerCompositionId : null,
                    LayerIndex: 0)
                {
                    AudioRoutes = audioRoutes is { Count: > 0 } ? audioRoutes : null,
                },
            ],
            Compositions = hasVideo
                ? [new ShowComposition(PlayerCompositionId, "Player", canvasWidth, canvasHeight, 30, 1)]
                : [],
        };
    }
}
