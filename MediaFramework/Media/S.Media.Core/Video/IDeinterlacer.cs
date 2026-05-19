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

/// <summary>
/// Process-wide hook for the production deinterlacer. <c>S.Media.FFmpeg</c> installs a yadif-based
/// implementation at <c>FFmpegRuntime.EnsureInitialized()</c>. When no factory is registered,
/// callers should fall back to <see cref="BobDeinterlacer"/> (always available in Core).
/// </summary>
public static class VideoDeinterlacerRegistry
{
    /// <summary>Returns a fresh <see cref="IDeinterlacer"/> instance configured for <paramref name="input"/>. <c>null</c> until a package installs one.</summary>
    public static Func<VideoFormat, IDeinterlacer>? Factory { get; set; }

    /// <summary>Creates a deinterlacer via the registered <see cref="Factory"/>, or a <see cref="BobDeinterlacer"/> when none is installed.</summary>
    public static IDeinterlacer Create(VideoFormat input) =>
        Factory?.Invoke(input) ?? new BobDeinterlacer(input);
}
