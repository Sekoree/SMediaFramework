namespace S.Media.Core.Video;

/// <summary>
/// Picks the cheapest pixel format that an <see cref="IVideoSource"/> and an
/// <see cref="IVideoSink"/> can agree on, then wires both ends to it.
/// </summary>
/// <remarks>
/// <para>
/// Walk the sink's preferences in order and pick the first one the source can
/// deliver natively <strong>and</strong> that passes an optional filter. If no
/// format is both native-overlap and filtered, fall back to the first sink
/// preference that passes the filter (forces a converter on whichever side).
/// </para>
/// <para>
/// When none of the sink preferences pass <paramref name="formatFilter"/> an
/// <see cref="InvalidOperationException"/> is thrown. When the sink lists no
/// preferences, choose the first source-native format passing the filter.
/// </para>
/// </remarks>
public static class VideoFormatNegotiator
{
    /// <inheritdoc cref="Negotiate(IVideoSource,IVideoSink,Func{PixelFormat,bool}?)"/>
    public static PixelFormat Negotiate(IVideoSource source, IVideoSink sink) =>
        Negotiate(source, sink, formatFilter: null);

    /// <summary>
    /// Returns the pixel format both ends will use after <see cref="Connect"/>.
    /// Does not touch either component — pure decision.
    /// </summary>
    /// <param name="formatFilter">When non-null, only formats for which this returns true may be negotiated.</param>
    public static PixelFormat Negotiate(IVideoSource source, IVideoSink sink, Func<PixelFormat, bool>? formatFilter)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);

        static bool Allows(Func<PixelFormat, bool>? f, PixelFormat p) =>
            f is null || f.Invoke(p);

        var sinkAccepted = sink.AcceptedPixelFormats;
        var sourceNative = source.NativePixelFormats;

        if (sinkAccepted.Count == 0)
        {
            if (sourceNative.Count == 0)
                throw new InvalidOperationException(
                    "neither source nor sink declared any pixel formats — cannot negotiate");

            for (var i = 0; i < sourceNative.Count; i++)
            {
                var nf = sourceNative[i];
                if (Allows(formatFilter, nf))
                    return nf;
            }

            throw new InvalidOperationException(
                "no source-native pixel format satisfied the negotiated format filter.");
        }

        for (var i = 0; i < sinkAccepted.Count; i++)
        {
            var candidate = sinkAccepted[i];
            if (Contains(sourceNative, candidate) && Allows(formatFilter, candidate))
                return candidate;
        }

        for (var i = 0; i < sinkAccepted.Count; i++)
        {
            var c = sinkAccepted[i];
            if (Allows(formatFilter, c))
                return c;
        }

        throw new InvalidOperationException(
            "none of the sink's preferred pixel formats satisfied the negotiated format filter.");
    }

    /// <summary>
    /// Negotiate, then call <see cref="IVideoSource.SelectOutputFormat"/> and
    /// <see cref="IVideoSink.Configure"/>.
    /// </summary>
    public static VideoFormat Connect(IVideoSource source, IVideoSink sink) =>
        Connect(source, sink, formatFilter: null);

    /// <inheritdoc cref="Connect(IVideoSource,IVideoSink)"/>
    /// <param name="formatFilter">When non-null, only formats for which this returns true may be negotiated.</param>
    public static VideoFormat Connect(IVideoSource source, IVideoSink sink, Func<PixelFormat, bool>? formatFilter)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);

        var pf = Negotiate(source, sink, formatFilter);
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
