namespace S.Media.Routing;

/// <summary>
/// Picks a concrete pixel format for a fan-out branch given a negotiated stream
/// format and a output's <see cref="IVideoOutput.AcceptedPixelFormats"/> list.
/// </summary>
public static class VideoOutputFanoutFormats
{
    /// <summary>
    /// When the negotiated pixel format is not accepted by a branch, prefer 4:2:2 UYVY
    /// (better chroma than 4:2:0 for 422 sources), then packed RGB, then NV12 / I420.
    /// If the branch already accepts the negotiated format, that format is used first so
    /// fan-out avoids a CPU swscale path (required for DRM dma-buf / D3D11 shared NV12).
    /// </summary>
    private static readonly PixelFormat[] BranchFormatPreference =
    [
        PixelFormat.Uyvy,
        PixelFormat.Bgra32,
        PixelFormat.Rgba32,
        PixelFormat.Nv12,
        PixelFormat.I420,
    ];

    /// <summary>Picks the best destination pixel format using a caller-scoped converter probe.</summary>
    public static PixelFormat PickBranchPixelFormat(
        VideoFormat negotiated,
        IReadOnlyList<PixelFormat> branchAccepted,
        Func<PixelFormat, PixelFormat, int, int, bool> canConvert)
    {
        ArgumentNullException.ThrowIfNull(branchAccepted);
        ArgumentNullException.ThrowIfNull(canConvert);
        var w = negotiated.Width;
        var h = negotiated.Height;
        var src = negotiated.PixelFormat;

        // Empty accepted list = permissive output ("takes anything"), same convention the primary
        // negotiation applies (see DiscardingVideoOutput): pass the negotiated format through
        // unconverted instead of treating the branch as accepting nothing.
        if (branchAccepted.Count == 0)
            return src;

        static bool OutputHas(IReadOnlyList<PixelFormat> list, PixelFormat p)
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i] == p) return true;
            return false;
        }

        // Prefer the negotiated format whenever the branch accepts it. Walking
        // BranchFormatPreference first would pick UYVY for NV12→UYVY swscale even
        // though both are 4:2:0 — that forces a per-branch converter and breaks
        // multi-output routes on GPU-backed NV12 frames.
        if (OutputHas(branchAccepted, src))
            return src;

        foreach (var pref in BranchFormatPreference)
        {
            if (!OutputHas(branchAccepted, pref)) continue;
            if (canConvert(src, pref, w, h))
                return pref;
        }

        for (var i = 0; i < branchAccepted.Count; i++)
        {
            var p = branchAccepted[i];
            if (p == src || canConvert(src, p, w, h))
                return p;
        }

        throw new InvalidOperationException(
            $"video fan-out: no swscale path from {src} to any branch format [{string.Join(", ", branchAccepted)}].");
    }
}
