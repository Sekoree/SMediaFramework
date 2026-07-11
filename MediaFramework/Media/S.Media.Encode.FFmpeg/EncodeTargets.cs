namespace S.Media.Encode.FFmpeg;

/// <summary>
/// Where an encode session's muxed bytes go. <see cref="FileEncodeTarget"/> writes a local file;
/// <see cref="UrlEncodeTarget"/> hands a protocol URL to libavformat (rtmp://, srt://, rtsp://,
/// udp:// - whatever the build ships). Both resolve to an avio destination inside the mux sink.
/// </summary>
public abstract record EncodeIoTarget
{
    /// <summary>The string libavformat opens (file path or protocol URL).</summary>
    public abstract string AvioUrl { get; }

    /// <summary>Overrides the container's default libavformat muxer short-name when set
    /// (e.g. "flv" for rtmp targets, "mpegts" for srt).</summary>
    public virtual string? FormatNameOverride => null;

    /// <summary>Muxer av_dict options passed to write_header (e.g. HLS segmenting knobs).</summary>
    public IReadOnlyDictionary<string, string>? MuxerOptions { get; init; }
}

/// <summary>Record to a local file. The container's default muxer is chosen from the path/options.</summary>
public sealed record FileEncodeTarget(string Path) : EncodeIoTarget
{
    public override string AvioUrl => Path;
}

/// <summary>
/// Mux into a managed byte callback via custom AVIO (no file/socket on the FFmpeg side). Used by the
/// LAN broadcast path: the mpegts mux writes into the fan-out buffer that serves HTTP clients.
/// <see cref="OnBytes"/> runs on whatever thread drives the muxer (the sink's drain thread) and must
/// return promptly. <see cref="MuxerOptions"/> are av_dict options passed to the muxer's write_header.
/// </summary>
public sealed record CallbackEncodeTarget(string FormatName, Action<ReadOnlyMemory<byte>> OnBytes) : EncodeIoTarget
{
    public override string AvioUrl => $"callback:{FormatName}";

    public override string? FormatNameOverride => FormatName;
}

/// <summary>Push to a network URL (rtmp://host/app/key, srt://host:port, rtsp://host/mount, …).</summary>
public sealed record UrlEncodeTarget(string Url, string? FormatName = null) : EncodeIoTarget
{
    public override string AvioUrl => Url;

    public override string? FormatNameOverride => FormatName;

    /// <summary>Push target for an RTMP ingest (Twitch/YouTube/OBS-style). FLV container.</summary>
    public static UrlEncodeTarget Rtmp(string url) => new(url, "flv");

    /// <summary>Push target for an SRT listener. MPEG-TS container.</summary>
    public static UrlEncodeTarget Srt(string url) => new(url, "mpegts");

    /// <summary>Push (ANNOUNCE/RECORD) to an RTSP server such as MediaMTX.</summary>
    public static UrlEncodeTarget Rtsp(string url) => new(url, "rtsp");
}
