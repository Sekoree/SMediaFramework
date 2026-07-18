using S.Media.Decode.FFmpeg;

namespace HaPlay.Playback;

/// <summary>Snapshot of what an on-disk media file contains. Cached on the cue node after add so
/// the drawer can hide irrelevant tabs (no video stream → no Video tab) and surface details
/// (e.g. source channel count) without reopening the decoder.</summary>
internal readonly record struct CueMediaProbeResult(
    int? DurationMs,
    bool HasVideo,
    bool HasAudio,
    int AudioChannels,
    bool VideoIsAttachedPicture,
    int SourceFrameRateNum,
    int SourceFrameRateDen,
    int SourceVideoWidth,
    int SourceVideoHeight,
    IReadOnlyList<MediaStreamInfo> AudioTracks,
    IReadOnlyList<MediaStreamInfo> VideoTracks,
    IReadOnlyList<MediaStreamInfo> SubtitleTracks);

internal static class CueMediaProbe
{
    /// <summary>Back-compat shortcut for the duration-only call sites that haven't migrated yet
    /// (e.g. third-party code paths). Prefer <see cref="TryProbeAsync"/>.</summary>
    public static async Task<int?> TryProbeDurationMsAsync(string path)
    {
        var result = await TryProbeAsync(path).ConfigureAwait(false);
        return result?.DurationMs;
    }

    /// <summary>Probes the file once and returns everything the drawer needs to decide which
    /// tabs / hints to show. Returns null on any decoder failure (unreadable, unsupported, etc.).</summary>
    public static async Task<CueMediaProbeResult?> TryProbeAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var decoder = await Task.Run(() => MediaContainerDecoder.Open(path)).ConfigureAwait(false);
            try
            {
                var ms = (int)Math.Min(int.MaxValue, decoder.Duration.TotalMilliseconds);
                int? durationMs = ms > 0 ? ms : null;
                var channels = decoder.HasAudio ? Math.Max(0, decoder.Audio.Format.Channels) : 0;
                var fpsNum = 0;
                var fpsDen = 0;
                var videoWidth = 0;
                var videoHeight = 0;
                if (decoder.HasVideo)
                {
                    var format = decoder.Video.Format;
                    var rate = format.FrameRate;
                    if (rate.Numerator > 0 && rate.Denominator > 0)
                    {
                        fpsNum = rate.Numerator;
                        fpsDen = rate.Denominator;
                    }
                    videoWidth = Math.Max(0, format.Width);
                    videoHeight = Math.Max(0, format.Height);
                }

                return new CueMediaProbeResult(
                    DurationMs: durationMs,
                    HasVideo: decoder.HasVideo,
                    HasAudio: decoder.HasAudio,
                    AudioChannels: channels,
                    VideoIsAttachedPicture: decoder.HasVideo && decoder.VideoIsAttachedPicture,
                    SourceFrameRateNum: fpsNum,
                    SourceFrameRateDen: fpsDen,
                    SourceVideoWidth: videoWidth,
                    SourceVideoHeight: videoHeight,
                    AudioTracks: ListDecodableAudioTracks(decoder.Streams),
                    VideoTracks: ListVideoTracks(decoder.Streams),
                    SubtitleTracks: ListSubtitleTracks(decoder.Streams));
            }
            finally
            {
                decoder.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<MediaStreamInfo> ListDecodableAudioTracks(IReadOnlyList<MediaStreamInfo> streams) =>
        streams.Where(s => s.Kind == MediaStreamKind.Audio && s.IsDecodable).ToArray();

    // Attached pictures stay in the list ON PURPOSE: the whole point of the picker is that an
    // embedded cover/thumbnail can be selected explicitly (automatic election skips it).
    private static IReadOnlyList<MediaStreamInfo> ListVideoTracks(IReadOnlyList<MediaStreamInfo> streams) =>
        streams.Where(s => s.Kind == MediaStreamKind.Video && s.IsDecodable).ToArray();

    private static IReadOnlyList<MediaStreamInfo> ListSubtitleTracks(IReadOnlyList<MediaStreamInfo> streams) =>
        streams.Where(s => s.Kind == MediaStreamKind.Subtitle).ToArray();

    /// <summary>Stream-table-only probe of the embedded subtitle tracks (for the cue subtitle picker on cues
    /// loaded from disk). Empty on failure.</summary>
    public static async Task<IReadOnlyList<MediaStreamInfo>> TryProbeSubtitleTracksAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return [];
        try
        {
            var streams = await Task.Run(() => MediaContainerDecoder.ProbeStreams(path)).ConfigureAwait(false);
            return ListSubtitleTracks(streams);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Lightweight stream-table-only probe (no decoder build) for filling the audio-track picker on
    /// cues loaded from disk. Returns an empty list on failure.
    /// </summary>
    public static async Task<IReadOnlyList<MediaStreamInfo>> TryProbeAudioTracksAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return [];
        try
        {
            var streams = await Task.Run(() => MediaContainerDecoder.ProbeStreams(path)).ConfigureAwait(false);
            return ListDecodableAudioTracks(streams);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Video-side sibling of <see cref="TryProbeAudioTracksAsync"/> (fills the video-track
    /// picker for cues loaded from disk). Returns an empty list on failure.</summary>
    public static async Task<IReadOnlyList<MediaStreamInfo>> TryProbeVideoTracksAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return [];
        try
        {
            var streams = await Task.Run(() => MediaContainerDecoder.ProbeStreams(path)).ConfigureAwait(false);
            return ListVideoTracks(streams);
        }
        catch
        {
            return [];
        }
    }
}
