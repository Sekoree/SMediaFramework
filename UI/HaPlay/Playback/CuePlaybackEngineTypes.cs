using Avalonia.Threading;
using HaPlay.Resources;
using HaPlay.ViewModels;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using S.Media.NDI;
using S.Media.Playback;
using S.Media.PortAudio;

namespace HaPlay.Playback;

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

/// <summary>Per-cue preparation status snapshot raised by
/// <see cref="CuePlaybackEngine.PreparedCueStatesChanged"/>.</summary>
public readonly record struct CuePreparationStatus(Guid CueId, PreparedCueState State, string? Error);

/// <summary>HaPlay adapter over the framework <see cref="ClipWindow"/>: builds one from a media
/// cue's start/end trim offsets. The window math itself now lives in <see cref="ClipWindow"/> so it
/// is shared with the media player and any future clip host.</summary>
internal static class CueClipWindow
{
    public static ClipWindow From(MediaCueNode cue, TimeSpan sourceDuration) =>
        ClipWindow.FromOffsets(
            TimeSpan.FromMilliseconds(Math.Max(0, cue.StartOffsetMs)),
            TimeSpan.FromMilliseconds(Math.Max(0, cue.EndOffsetMs)),
            sourceDuration);
}
