namespace S.Media.Core.Video;

/// <summary>
/// Converts an interlaced <see cref="VideoFrame"/> into one or more progressive frames. Same
/// pluggable-registry shape as <see cref="IVideoCpuFrameConverter"/> — Core defines the contract,
/// <c>S.Media.FFmpeg</c> ships the production implementation (libavfilter <c>yadif</c>), and the
/// always-available <see cref="BobDeinterlacer"/> fallback lives in Core for headless test paths.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Output cardinality is implementation-defined</strong>. Yadif emits one progressive frame
/// per input by default (frame-rate preserving). Bob emits two (field-rate doubling). Callers pass
/// a 2-slot <c>Span&lt;VideoFrame?&gt;</c> buffer and read the returned count; unfilled slots stay
/// null. Owner of every emitted frame is the caller.
/// </para>
/// <para>
/// Progressive frames are passed through unchanged (emit 1 output, identical to input — same plane
/// memory, no copy). Implementations should treat <see cref="VideoFieldOrder.Progressive"/> input
/// as a no-op fast path.
/// </para>
/// </remarks>
public interface IDeinterlacer : IDisposable
{
    /// <summary>Format the deinterlacer was configured for. <c>default</c> until <see cref="Configure"/> is called.</summary>
    VideoFormat InputFormat { get; }

    /// <summary>
    /// Output frame format. For Bob this is the input format with frame rate doubled; for Yadif (mode 0)
    /// this equals the input format.
    /// </summary>
    VideoFormat OutputFormat { get; }

    /// <summary>Reconfigure for a new input format. Releases any cached state.</summary>
    void Configure(VideoFormat input);

    /// <summary>
    /// Push one input frame, write 0..2 progressive frames into <paramref name="outputs"/>, return
    /// how many were filled. <paramref name="frame"/> is not disposed by the deinterlacer — the
    /// caller still owns it (and may pass it back in <paramref name="outputs"/> for the progressive
    /// fast path).
    /// </summary>
    int Process(VideoFrame frame, Span<VideoFrame?> outputs);
}

// Phase 1: the old `VideoDeinterlacerRegistry` (a process-wide hook over MediaFrameworkPlugins) is
// removed — deinterlacers are resolved through `IMediaRegistry`, with the built-in `BobDeinterlacer`
// fallback (see Registry/). The interface above and `BobDeinterlacer` stay in Core.
