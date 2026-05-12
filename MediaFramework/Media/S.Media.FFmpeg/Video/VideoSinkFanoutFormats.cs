using S.Media.Core.Video;

namespace S.Media.FFmpeg.Video;

/// <summary>
/// Picks a concrete pixel format for a fan-out branch given a negotiated stream
/// format and a sink's <see cref="IVideoSink.AcceptedPixelFormats"/> list.
/// </summary>
public static class VideoSinkFanoutFormats
{
    /// <summary>
    /// Prefer 4:2:2 UYVY when the sink supports it (better chroma than 4:2:0 for 422 sources),
    /// then packed RGB, then NV12 / I420.
    /// </summary>
    private static readonly PixelFormat[] BranchFormatPreference =
    [
        PixelFormat.Uyvy,
        PixelFormat.Bgra32,
        PixelFormat.Rgba32,
        PixelFormat.Nv12,
        PixelFormat.I420,
    ];

    /// <summary>Picks the best destination pixel format for <paramref name="branchAccepted"/>.</summary>
    public static PixelFormat PickBranchPixelFormat(VideoFormat negotiated, IReadOnlyList<PixelFormat> branchAccepted)
    {
        ArgumentNullException.ThrowIfNull(branchAccepted);
        var w = negotiated.Width;
        var h = negotiated.Height;
        var src = negotiated.PixelFormat;

        static bool SinkHas(IReadOnlyList<PixelFormat> list, PixelFormat p)
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i] == p) return true;
            return false;
        }

        foreach (var pref in BranchFormatPreference)
        {
            if (!SinkHas(branchAccepted, pref)) continue;
            if (VideoCpuFrameConverter.CanConvert(src, pref, w, h))
                return pref;
        }

        for (var i = 0; i < branchAccepted.Count; i++)
        {
            var p = branchAccepted[i];
            if (p == src || VideoCpuFrameConverter.CanConvert(src, p, w, h))
                return p;
        }

        throw new InvalidOperationException(
            $"video fan-out: no swscale path from {src} to any branch format [{string.Join(", ", branchAccepted)}].");
    }
}
