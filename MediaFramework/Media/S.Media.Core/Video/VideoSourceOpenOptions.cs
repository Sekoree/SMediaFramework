namespace S.Media.Core.Video;

/// <summary>Optional settings forwarded to the installed file/stream video source backend.</summary>
public sealed class VideoSourceOpenOptions
{
    /// <summary>When true, ask the backend to try hardware acceleration before software fallback.</summary>
    public bool TryHardwareAcceleration { get; init; } = true;

    /// <summary>When true, retain Linux DRM PRIME dma-buf handles for GL import.</summary>
    public bool RetainDmabufForGl { get; init; }

    /// <summary>When true, retain Windows D3D11 NV12 shared handles for GL import.</summary>
    public bool RetainD3D11SharedHandleForGl { get; init; }

    /// <summary>When true with <see cref="RetainD3D11SharedHandleForGl"/>, export only Win32 shared handles.</summary>
    public bool Win32Nv12SharedHandleOnlyExport { get; init; }

    /// <summary>Max demuxed audio packets buffered ahead of the decoder. <c>0</c> = backend default.</summary>
    public int AudioPacketQueueDepth { get; init; }

    /// <summary>Max demuxed video packets buffered ahead of the decoder. <c>0</c> = backend default.</summary>
    public int VideoPacketQueueDepth { get; init; }

    /// <summary>Large read buffer for local-file backends that support custom I/O. <c>0</c> = backend default.</summary>
    public int FileReadBufferBytes { get; init; }

    /// <summary>Hint name for stream probe when opening via <see cref="VideoSource.OpenStream"/>.</summary>
    public string? StreamProbeHintName { get; init; }

    /// <summary>When true, libav may seek within the stream (also requires <see cref="Stream.CanSeek"/>).</summary>
    public bool StreamIsSeekable { get; init; }

    /// <summary>When true, spools the stream to a temp file instead of AVIO (fallback path).</summary>
    public bool SpoolToDisk { get; init; }

    /// <summary>Explicit related audio stream index, or <c>-1</c> to disable audio in shared-demux backends.</summary>
    public int? AudioStreamIndex { get; init; }

    /// <summary>Explicit video stream index, or <c>-1</c> to disable video in shared-demux backends.</summary>
    public int? VideoStreamIndex { get; init; }
}
