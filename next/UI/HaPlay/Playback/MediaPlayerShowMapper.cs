using HaPlay.Models;
using S.Media.Session;

namespace HaPlay.Playback;

/// <summary>
/// Maps a standalone media-player source onto a 1-cue <see cref="ShowDocument"/> so the player can run on a
/// per-player <see cref="ShowSession"/> (Phase-8 full convergence — the <c>HaPlayPlaybackSession</c> it replaced is deleted).
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
        int canvasHeight = 1080,
        IReadOnlyList<CueSubtitleSelection>? subtitles = null)
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
                    // The deck states its outputs EXPLICITLY — never null. No route routed ⇒ an empty list ⇒ NO
                    // audio output at all: the deck plays to nothing (a "no output routed" banner is shown), and
                    // no default device is ever opened. Only a null here would trip the framework's default-device
                    // fallback, which must never happen for the deck — so coerce to an empty list.
                    AudioRoutes = audioRoutes ?? [],
                    Subtitles = MapSubtitles(subtitles, hasVideo),
                },
            ],
            Compositions = hasVideo
                ? [new ShowComposition(PlayerCompositionId, "Player", canvasWidth, canvasHeight, 30, 1)]
                : [],
        };
    }

    /// <summary>Maps the deck's selected subtitle tracks to the framework's <see cref="ShowSubtitleSelection"/>
    /// (an embedded stream index, or a sidecar path). Subtitles only render on a video composition, so this is
    /// null for an audio-only source (no canvas to draw on). Per-selection styling (font family / scale /
    /// alignment) is not yet carried on <see cref="ShowSubtitleSelection"/> — the document's styling applies;
    /// per-track overrides are a follow-up.</summary>
    private static IReadOnlyList<ShowSubtitleSelection>? MapSubtitles(
        IReadOnlyList<CueSubtitleSelection>? subtitles, bool hasVideo)
    {
        if (!hasVideo || subtitles is not { Count: > 0 })
            return null;

        var mapped = new List<ShowSubtitleSelection>(subtitles.Count);
        foreach (var s in subtitles)
        {
            if (s.StreamIndex is { } idx)
                mapped.Add(new ShowSubtitleSelection(StreamIndex: idx)); // embedded container stream
            else if (!string.IsNullOrWhiteSpace(s.Path))
                mapped.Add(new ShowSubtitleSelection(Path: s.Path)); // sidecar file (StreamIndex stays -1)
        }
        return mapped.Count > 0 ? mapped : null;
    }
}
