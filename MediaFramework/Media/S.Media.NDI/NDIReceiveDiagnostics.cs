namespace S.Media.NDI;

/// <summary>
/// NDI-02: a point-in-time health snapshot of an <see cref="NDISource"/> receiver. All counts are monotonic
/// (cumulative since the source opened) except <see cref="QueuedVideoFrames"/> (an instantaneous depth) and
/// <see cref="CaptureThreadStuck"/>/<see cref="LiveNDIConnections"/> (live state). Read via
/// <see cref="NDISource.GetReceiveDiagnostics"/>.
/// </summary>
/// <param name="VideoFramesUnpacked">Video frames successfully unpacked from the native receiver.</param>
/// <param name="VideoUnpackDrops">Frames dropped because unpacking/conversion could not keep up.</param>
/// <param name="VideoOverflowFrames">Frames dropped because the bounded receive queue was full.</param>
/// <param name="AudioOverflowFloats">Interleaved floating-point channel values dropped on ring-buffer overflow.</param>
/// <param name="AudioConversionDrops">Audio blocks dropped during format conversion.</param>
/// <param name="QueuedVideoFrames">Video frames currently buffered waiting for the consumer.</param>
/// <param name="CaptureThreadStuck">True when the native capture thread was detected wedged.</param>
/// <param name="LiveNDIConnections">Process-wide count of live NDI receiver connections.</param>
public readonly record struct NDIReceiveDiagnostics(
    long VideoFramesUnpacked,
    long VideoUnpackDrops,
    long VideoOverflowFrames,
    long AudioOverflowFloats,
    long AudioConversionDrops,
    int QueuedVideoFrames,
    bool CaptureThreadStuck,
    int LiveNDIConnections);
