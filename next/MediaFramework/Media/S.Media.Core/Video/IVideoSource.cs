namespace S.Media.Core.Video;

/// <summary>
/// Pull-based video producer. Mirrors <see cref="Audio.IAudioSource"/> but for
/// frames instead of sample chunks. Sources include file decoders (FFmpeg),
/// network receivers (NDI), and capture devices.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are not required to be thread-safe — a single consumer
/// pulls frames serially.
/// </para>
/// <para>
/// Pixel-format negotiation: <see cref="NativePixelFormats"/> lists what the
/// source can deliver without a CPU conversion (typically just the codec's
/// native format). A consumer that wants something else calls
/// <see cref="SelectOutputFormat"/>; the source either matches it natively or
/// inserts an internal converter and updates <see cref="Format"/> to reflect
/// the new layout. Pair this with <see cref="IVideoOutput.AcceptedPixelFormats"/>
/// via <see cref="VideoFormatNegotiator"/> to wire up the cheapest path.
/// </para>
/// </remarks>
public interface IVideoSource
{
    /// <summary>Current frame format. Reflects the most recent <see cref="SelectOutputFormat"/> result, or the source's native format if none was selected.</summary>
    VideoFormat Format { get; }

    /// <summary>
    /// Pixel formats the source can deliver without a CPU-side conversion.
    /// Producers should list every layout they can hand out zero-copy; the
    /// negotiator picks the first one a output also supports.
    /// </summary>
    IReadOnlyList<PixelFormat> NativePixelFormats { get; }

    /// <summary>True when the source has no more frames to emit (file EOF, network disconnect, …).</summary>
    bool IsExhausted { get; }

    /// <summary>
    /// Configures the source to emit subsequent frames in <paramref name="format"/>.
    /// If the format is in <see cref="NativePixelFormats"/> the source operates
    /// pass-through; otherwise it inserts an internal converter (e.g. sws_scale)
    /// — callers should prefer one of <see cref="NativePixelFormats"/> when the
    /// output accepts it.
    /// </summary>
    void SelectOutputFormat(PixelFormat format);

    /// <summary>Pulls the next frame. Returns false at EOF (also sets <see cref="IsExhausted"/>).</summary>
    bool TryReadNextFrame(out VideoFrame frame);
}
