namespace S.Media.Core.Audio;

/// <summary>Optional settings forwarded to the installed file/stream audio source backend.</summary>
public sealed class AudioSourceOpenOptions
{
    /// <summary>Libav codec thread count for standalone audio decode (0 = libav default).</summary>
    public int CodecThreadCount { get; init; }

    /// <summary>Hint name for stream probe when opening via <see cref="AudioSource.OpenStream"/>.</summary>
    public string? StreamProbeHintName { get; init; }

    /// <summary>When true, libav may seek within the stream (also requires <see cref="Stream.CanSeek"/>).</summary>
    public bool StreamIsSeekable { get; init; }

    /// <summary>When true, spools the stream to a temp file instead of AVIO (fallback path).</summary>
    public bool SpoolToDisk { get; init; }
}
