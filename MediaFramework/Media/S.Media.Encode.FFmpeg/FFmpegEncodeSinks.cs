namespace S.Media.Encode.FFmpeg;

/// <summary>
/// The video leg of an <see cref="FFmpegEncodeSession"/>: a router-attachable <see cref="IVideoOutput"/>
/// whose <see cref="Submit"/> hands the frame to the session's bounded queue and returns immediately
/// (the encode worker does the FFmpeg work). <see cref="IFlushableOutput"/> is intentionally absent -
/// a recording never discards already-submitted frames on transport pauses.
/// </summary>
public sealed class FFmpegEncodeVideoSink : IVideoOutput
{
    // Every CPU layout the sws-based encoder ingests directly; ordered so negotiation prefers
    // formats that avoid a second conversion before the encoder's own pick.
    private static readonly PixelFormat[] Accepted =
    [
        PixelFormat.I420,
        PixelFormat.Nv12,
        PixelFormat.Yuv420P10Le,
        PixelFormat.P010,
        PixelFormat.Yuv422P,
        PixelFormat.Yuv422P10Le,
        PixelFormat.Yuv444P12Le,
        PixelFormat.Yuva444P12Le,
        PixelFormat.Yuv420P12Le,
        PixelFormat.Bgra32,
        PixelFormat.Rgba32,
    ];

    private readonly FFmpegEncodeSession _session;
    private VideoFormat _format;

    internal FFmpegEncodeVideoSink(FFmpegEncodeSession session) => _session = session;

    public VideoFormat Format => _format;

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => Accepted;

    public void Configure(VideoFormat format)
    {
        _session.ConfigureVideo(format);
        _format = format;
    }

    public void Submit(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _session.SubmitVideo(frame);
    }
}

/// <summary>
/// One audio track of an <see cref="FFmpegEncodeSession"/>: a router-attachable
/// <see cref="IAudioOutput"/> declaring the track's input layout (the router's channel map mixes into
/// it). <see cref="Submit"/> copies the chunk into the session's bounded backlog and returns.
/// </summary>
public sealed class FFmpegEncodeAudioSink : IAudioOutput
{
    private readonly FFmpegEncodeSession _session;
    private readonly int _legIndex;

    internal FFmpegEncodeAudioSink(FFmpegEncodeSession session, int legIndex, AudioFormat format)
    {
        _session = session;
        _legIndex = legIndex;
        Format = format;
    }

    public AudioFormat Format { get; }

    public void Submit(ReadOnlySpan<float> packedSamples) => _session.SubmitAudio(_legIndex, packedSamples);
}
