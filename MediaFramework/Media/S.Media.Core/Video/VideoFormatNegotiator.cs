namespace S.Media.Core.Video;

/// <summary>
/// Picks the cheapest pixel format that an <see cref="IVideoSource"/> and an
/// <see cref="IVideoSink"/> can agree on, then wires both ends to it.
/// </summary>
/// <remarks>
/// The contract is intentionally narrow: walk the sink's preferences in order
/// and pick the first one the source can deliver natively. If there's no
/// overlap, fall back to the sink's first preference (which forces the source
/// to insert an internal converter). When the sink lists no preferences at
/// all, use the source's first native format and let the sink convert
/// internally if it must.
/// </remarks>
public static class VideoFormatNegotiator
{
    /// <summary>
    /// Returns the pixel format both ends will use after <see cref="Connect"/>.
    /// Does not touch either component — pure decision.
    /// </summary>
    public static PixelFormat Negotiate(IVideoSource source, IVideoSink sink)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);

        var sinkAccepted = sink.AcceptedPixelFormats;
        var sourceNative = source.NativePixelFormats;

        if (sinkAccepted.Count == 0)
        {
            // Sink takes anything → give it the source's native format.
            if (sourceNative.Count == 0)
                throw new InvalidOperationException(
                    "neither source nor sink declared any pixel formats — cannot negotiate");
            return sourceNative[0];
        }

        // First sink preference that the source delivers natively wins.
        for (var i = 0; i < sinkAccepted.Count; i++)
        {
            var candidate = sinkAccepted[i];
            if (Contains(sourceNative, candidate))
                return candidate;
        }

        // No common ground — give the sink its top choice and let the source
        // insert a CPU conversion. Logged downstream by callers that care.
        return sinkAccepted[0];
    }

    /// <summary>
    /// Negotiate, then call <see cref="IVideoSource.SelectOutputFormat"/> and
    /// <see cref="IVideoSink.Configure"/>. Returns the final negotiated
    /// <see cref="VideoFormat"/> (post-source-Format which may differ from the
    /// source's pre-call Format if a conversion was inserted).
    /// </summary>
    public static VideoFormat Connect(IVideoSource source, IVideoSink sink)
    {
        var pf = Negotiate(source, sink);
        source.SelectOutputFormat(pf);
        sink.Configure(source.Format);
        return source.Format;
    }

    private static bool Contains(IReadOnlyList<PixelFormat> list, PixelFormat fmt)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == fmt) return true;
        return false;
    }
}
