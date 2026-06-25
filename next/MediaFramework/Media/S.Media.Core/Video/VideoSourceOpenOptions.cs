namespace S.Media.Core.Video;

/// <summary>Optional settings forwarded to the installed file/stream video source backend.</summary>
public sealed class VideoSourceOpenOptions
{
    /// <summary>When true, retain Linux DRM PRIME dma-buf handles for GL import.</summary>
    public bool RetainDmabufForGl { get; init; }

    /// <summary>When true, retain Windows D3D11 NV12 shared handles for GL import.</summary>
    public bool RetainD3D11SharedHandleForGl { get; init; }

    /// <summary>Hint name for stream probe when opening via <see cref="VideoSource.OpenStream"/>.</summary>
    public string? StreamProbeHintName { get; init; }

    /// <summary>When true, libav may seek within the stream (also requires <see cref="Stream.CanSeek"/>).</summary>
    public bool StreamIsSeekable { get; init; }

    /// <summary>When true, spools the stream to a temp file instead of AVIO (fallback path).</summary>
    public bool SpoolToDisk { get; init; }
}
