using System.Diagnostics.CodeAnalysis;
using System.Text;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using S.Media.NDI;
using S.Media.NDI.Audio;
using S.Media.NDI.Video;
using S.Media.Playback;
using S.Media.PortAudio;
using S.Media.SDL3;

namespace VideoPlaybackSmoke;

/// <summary>Shortest-path factory for <see cref="VideoPlaybackSmokeSession"/> with <see cref="SmokePlaybackOptions.Default"/>.</summary>
public static class VideoPlaybackSmokeHost
{
    public static bool TryOpen(
        string mediaPath,
        Action<string>? onAudioWireFailedMessage,
        [NotNullWhen(true)] out VideoPlaybackSmokeSession? session,
        out string? errorMessage) =>
        VideoPlaybackSmokeSession.TryCreate(mediaPath, SmokePlaybackOptions.Default, onAudioWireFailedMessage,
            out session, out errorMessage);

    public static bool TryOpen(
        string mediaPath,
        in SmokePresentationOptions presentation,
        Action<string>? onAudioWireFailedMessage,
        [NotNullWhen(true)] out VideoPlaybackSmokeSession? session,
        out string? errorMessage) =>
        VideoPlaybackSmokeSession.TryCreate(mediaPath, SmokePlaybackOptions.Default, presentation,
            onAudioWireFailedMessage, out session, out errorMessage);
}
