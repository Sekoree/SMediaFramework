using HaPlay.ViewModels;
using S.Media.Core;

namespace HaPlay.Playback;

// The UI-facing playback DTOs that survived the legacy-engine deletion (they used to live in
// CuePlaybackEngineTypes.cs / SoundboardEngine.cs): the ShowSession re-back raises/consumes the same shapes,
// so the cue list, now-playing panel, and soundboard tiles keep their contracts.

/// <summary>Periodic progress sample for the Now Playing panel.</summary>
public readonly record struct CuePlaybackProgress(Guid CueId, TimeSpan Position, TimeSpan Duration);

/// <summary>Standby preparation lifecycle for one cue. <c>Idle</c> = not in the warm window or not
/// attempted; <c>Preparing</c> = opening/seeking; <c>Ready</c> = opened, routed, seeked to start;
/// <c>Stale</c> = a previously-ready standby whose cue config changed, awaiting re-preparation by the
/// next pre-roll refresh; <c>Failed</c> = open failed (reason in
/// <see cref="CuePreparationStatus.Error"/>).</summary>
public enum PreparedCueState
{
    Idle,
    Preparing,
    Ready,
    Stale,
    Failed,
}

/// <summary>Per-cue preparation status snapshot (driven by <c>ShowSession.PreparedCuesChanged</c>).</summary>
public readonly record struct CuePreparationStatus(Guid CueId, PreparedCueState State, string? Error);

/// <summary>Everything a soundboard fire needs for one tile. The view model resolves board defaults
/// (output line etc.) before building this, so the playback side stays board-agnostic.</summary>
public readonly record struct SoundboardPlayRequest(
    Guid TileId,
    string FilePath,
    Guid OutputLineId,
    double Volume,
    bool Loop,
    int FadeOutMs);

/// <summary>Periodic progress sample for a playing tile. <see cref="FadeRemaining"/> is non-null
/// only while a tap-to-fade is running (drives the tile's fade countdown bar).</summary>
public readonly record struct SoundboardSoundProgress(
    Guid TileId,
    TimeSpan Position,
    TimeSpan Duration,
    TimeSpan? FadeRemaining);

/// <summary>Playlist-item pre-roll capability predicate (used by the cue workspace's warm-window target
/// enumerators; the ShowSession standby warms files, and live inputs open fast enough on fire).</summary>
internal static class PlaylistItemPreRollExtensions
{
    /// <summary>Items that can be warmed before GO: files pre-open decoders, live inputs pre-connect devices.</summary>
    public static bool SupportsPreRoll(this PlaylistItem? item) =>
        item is FilePlaylistItem
        || item is NDIInputPlaylistItem { VideoOnly: false }
        || item is PortAudioInputPlaylistItem;
}

/// <summary>HaPlay adapter over the framework <see cref="ClipWindow"/>: builds one from a media
/// cue's start/end trim offsets. The window math itself lives in <see cref="ClipWindow"/> so it
/// is shared with the media player and any future clip host.</summary>
internal static class CueClipWindow
{
    public static ClipWindow From(MediaCueNode cue, TimeSpan sourceDuration) =>
        ClipWindow.FromOffsets(
            TimeSpan.FromMilliseconds(Math.Max(0, cue.StartOffsetMs)),
            TimeSpan.FromMilliseconds(Math.Max(0, cue.EndOffsetMs)),
            sourceDuration);
}
