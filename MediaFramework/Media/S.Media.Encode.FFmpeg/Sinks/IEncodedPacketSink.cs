namespace S.Media.Encode.FFmpeg.Sinks;

/// <summary>Media kind of one encoded stream in stream-index order.</summary>
public enum EncodedStreamKind
{
    Video,
    Audio,
}

/// <summary>
/// One encoded elementary stream a session produces, in session stream-index order (video first when
/// present, then audio legs in options order). <see cref="CodecParameters"/> points at the encoder's
/// context-derived parameters - owned by the session's encoder core and valid until the session is
/// disposed; sinks that build an <c>AVFormatContext</c> copy them with
/// <c>avcodec_parameters_copy</c> during <see cref="IEncodedPacketSink.OnStreamsReady"/>.
/// </summary>
public sealed unsafe class EncodedStreamInfo
{
    internal EncodedStreamInfo(
        EncodedStreamKind kind,
        AVCodecParameters* codecParameters,
        AVRational timeBase,
        string? name,
        string? language)
    {
        Kind = kind;
        _codecParameters = codecParameters;
        TimeBase = timeBase;
        Name = name;
        Language = language;
    }

    public EncodedStreamKind Kind { get; }

    /// <summary>Timebase the session's packets for this stream are stamped in (the encoder timebase).
    /// Sinks rescale to their own stream timebases on write.</summary>
    public AVRational TimeBase { get; }

    /// <summary>Track title metadata (audio legs).</summary>
    public string? Name { get; }

    /// <summary>ISO 639-2 language metadata (audio legs).</summary>
    public string? Language { get; }

    private readonly AVCodecParameters* _codecParameters;

    public AVCodecParameters* CodecParameters => _codecParameters;
}

/// <summary>
/// Consumer of one encode session's packet stream. Contract (all calls on the session's encode worker
/// thread, in order): <see cref="OnStreamsReady"/> once, then <see cref="OnPacket"/> per packet, then
/// <see cref="Finish"/> exactly once at session end. The packet belongs to the caller - a sink that
/// queues it must <c>av_packet_ref</c> its own copy before returning. A sink that throws is faulted:
/// the session detaches it, records the error, and keeps feeding the healthy sinks (one dead push
/// target must not kill the recording or the other outputs).
/// </summary>
internal unsafe interface IEncodedPacketSink : IDisposable
{
    /// <summary>Sink display name for health/metrics ("file:/path/out.mp4", "rtmp://…").</summary>
    string Name { get; }

    /// <summary>Container bytes delivered so far (best effort; 0 when unknown).</summary>
    long BytesWritten { get; }

    void OnStreamsReady(IReadOnlyList<EncodedStreamInfo> streams);

    /// <summary>One encoded packet, timestamped in <paramref name="streams"/>[pkt.stream_index]'s timebase
    /// (as delivered by <see cref="OnStreamsReady"/>). <paramref name="keyframe"/> mirrors AV_PKT_FLAG_KEY.</summary>
    void OnPacket(AVPacket* packet, bool keyframe);

    /// <summary>End of stream: flush and finalize (write trailer, close avio). Called once, after the last packet.</summary>
    void Finish();
}
