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

    /// <summary>Submits a generated filler frame immediately after the session's current video
    /// cursor. Unlike ordinary media, the frame's own presentation time is not a source timeline.</summary>
    internal void SubmitTimelineContinuation(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _session.SubmitVideo(frame, continueTimeline: true);
    }

    /// <summary>Submits a frame against a continuous carrier's wall-clock cadence. A valid program
    /// frame remains new output even if its source PTS is frozen.</summary>
    internal void SubmitLive(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _session.SubmitLiveVideo(frame);
    }
}

/// <summary>
/// Source-timeline adapter for content-only recordings. A composition may continue presenting its held
/// canvas while paused/stopped, with every presentation carrying the exact same media timestamp. Those
/// repeats are not new source time and are discarded here, so an idle gap is collapsed consistently for
/// both fixed-rate and source-following encodes. Backward timestamps remain valid: the encode session
/// re-anchors them for a newly fired clip or seek.
/// </summary>
public sealed class ContentOnlyEncodeVideoSink : IVideoOutput
{
    private readonly FFmpegEncodeVideoSink _inner;
    private readonly Lock _gate = new();
    private long _lastSubmittedPts = long.MinValue;

    public ContentOnlyEncodeVideoSink(FFmpegEncodeVideoSink inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public VideoFormat Format => _inner.Format;
    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _inner.AcceptedPixelFormats;

    public void Configure(VideoFormat format)
    {
        lock (_gate)
            _inner.Configure(format);
    }

    public void Submit(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            var pts = frame.PresentationTime.Ticks;
            if (pts == _lastSubmittedPts)
            {
                frame.Dispose();
                return;
            }

            _lastSubmittedPts = pts;
            _inner.Submit(frame);
        }
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

/// <summary>
/// All audio tracks of an <see cref="FFmpegEncodeSession"/> as ONE router-attachable sink whose channel
/// layout is the concatenation of every leg's channels (tracks [stereo "Program", mono "Commentary"]
/// ⇒ a 3-channel sink: ch 0-1 → track 1, ch 2 → track 2). This is what makes multi-track encodes
/// routable with the EXISTING N→M channel-matrix editors - the operator routes source channels onto
/// the combined channels; the splitter fans each chunk out to the per-track encoders.
/// </summary>
public sealed class FFmpegEncodeCombinedAudioSink : IAudioOutput, IAudioOutputChannelCapabilities
{
    private readonly FFmpegEncodeSession _session;
    private readonly Lock _submitGate = new();
    private readonly int[] _legChannels;
    private readonly float[][] _legScratch;

    internal FFmpegEncodeCombinedAudioSink(FFmpegEncodeSession session, IReadOnlyList<AudioFormat> legFormats)
    {
        _session = session;
        _legChannels = legFormats.Select(f => f.Channels).ToArray();
        _legScratch = new float[legFormats.Count][];
        for (var i = 0; i < legFormats.Count; i++)
            _legScratch[i] = [];
        Format = new AudioFormat(legFormats[0].SampleRate, _legChannels.Sum());
    }

    public AudioFormat Format { get; }

    public AudioOutputChannelCapabilities ChannelCapabilities =>
        AudioOutputChannelCapabilities.Fixed(Format.Channels);

    /// <summary>Combined channel offset of each track (for UIs labelling the matrix columns).</summary>
    public IReadOnlyList<int> LegChannelCounts => _legChannels;

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        lock (_submitGate)
        {
            var totalChannels = Format.Channels;
            if (packedSamples.Length % totalChannels != 0)
                throw new ArgumentException(
                    $"packedSamples length {packedSamples.Length} is not a multiple of the combined channel count {totalChannels}.",
                    nameof(packedSamples));

            var frames = packedSamples.Length / totalChannels;
            var channelOffset = 0;
            for (var leg = 0; leg < _legChannels.Length; leg++)
            {
                var legChannels = _legChannels[leg];
                var needed = frames * legChannels;
                if (_legScratch[leg].Length < needed)
                    _legScratch[leg] = new float[needed];
                var dst = _legScratch[leg].AsSpan(0, needed);
                for (var f = 0; f < frames; f++)
                {
                    var src = packedSamples.Slice(f * totalChannels + channelOffset, legChannels);
                    src.CopyTo(dst.Slice(f * legChannels, legChannels));
                }

                _session.SubmitAudio(leg, dst);
                channelOffset += legChannels;
            }
        }
    }
}
