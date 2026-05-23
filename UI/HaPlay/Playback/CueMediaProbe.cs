using S.Media.FFmpeg;

namespace HaPlay.Playback;

/// <summary>Snapshot of what an on-disk media file contains. Cached on the cue node after add so
/// the drawer can hide irrelevant tabs (no video stream → no Video tab) and surface details
/// (e.g. source channel count) without reopening the decoder.</summary>
internal readonly record struct CueMediaProbeResult(
    int? DurationMs,
    bool HasVideo,
    bool HasAudio,
    int AudioChannels,
    bool VideoIsAttachedPicture);

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
                return new CueMediaProbeResult(
                    DurationMs: durationMs,
                    HasVideo: decoder.HasVideo,
                    HasAudio: decoder.HasAudio,
                    AudioChannels: channels,
                    VideoIsAttachedPicture: decoder.HasVideo && decoder.VideoIsAttachedPicture);
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
}
