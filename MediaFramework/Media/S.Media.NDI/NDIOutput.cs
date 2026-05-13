using NDILib;
using S.Media.Core.Audio;
using S.Media.NDI.Audio;
using S.Media.NDI.Video;

namespace S.Media.NDI;

/// <summary>
/// One NDI source on the network — owns a single <see cref="NDISender"/> plus
/// the <see cref="NDIRuntime"/> ref-count, and exposes child sinks for audio
/// and video. Receivers see one combined source carrying both streams.
/// </summary>
/// <remarks>
/// <para>
/// Audio-only / video-only is just a matter of which children you enable.
/// <see cref="EnableAudio"/> creates the audio sink; <see cref="VideoSink"/>
/// is lazy and created on first access. Don't touch the side you don't
/// need and the SDK simply won't transmit that stream.
/// </para>
/// <para>
/// Lifetime: child sinks must not outlive this <see cref="NDIOutput"/>.
/// They share the parent's sender; disposing the parent invalidates them.
/// </para>
/// <para>
/// For SDK-level receiver feedback (tally, upstream metadata), use <see cref="TryGetReceiverTally"/>,
/// <see cref="CaptureReceiverMetadata"/>, and <see cref="FreeReceiverMetadata"/> on a background thread
/// with short timeouts so sends are not blocked.
/// </para>
/// </remarks>
public sealed class NDIOutput : IDisposable
{
    private readonly TimeSpan? _minimumVideoSubmitSpacing;
    private readonly NDIVideoTimecodeMode _videoTimecodeMode;
    /// <summary>When <see cref="NDIVideoTimecodeMode.PresentationRelativeTicks"/> is selected, shared by video + audio sinks.</summary>
    private readonly NdiEgressPresentationTimeline? _egressPresentationTimeline;
    private readonly NDIRuntime _runtime;
    private readonly NDISender _sender;
    private readonly object _gate = new();
    private NDIAudioSink? _audioSink;
    private NDIVideoSender? _videoSink;
    private bool _disposed;

    public string SourceName { get; }
    public int ConnectionCount => _sender.GetConnectionCount();

    /// <summary>
    /// Video sink — always available; format negotiated via
    /// <see cref="Core.Video.IVideoSink.Configure"/>.
    /// </summary>
    public NDIVideoSender VideoSink
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _videoSink ??= CreateVideoSinkLocked();
        }
    }

    /// <summary>
    /// Construct an NDI source. <paramref name="clockVideo"/> /
    /// <paramref name="clockAudio"/> tell the SDK to pace each stream against
    /// its declared rate (typical for a self-driving sender).
    /// </summary>
    /// <param name="minimumVideoSubmitSpacing">
    /// Optional wall-clock throttle between video frames (<see cref="NDIVideoSender"/> submit path).
    /// Use with <paramref name="clockVideo"/>:false when you want MFPlayer to pace instead of NDI timestamps.
    /// </param>
    /// <param name="videoTimecodeMode">
    /// How <see cref="NDIVideoSender"/> fills NDI video timecodes — use
    /// <see cref="NDIVideoTimecodeMode.PresentationRelativeTicks"/> for an explicit timeline aligned with
    /// <see cref="NDIAudioSink"/> when muxing file A/V.
    /// </param>
    public NDIOutput(string sourceName, string? groups = null, bool clockVideo = true, bool clockAudio = true,
                      TimeSpan? minimumVideoSubmitSpacing = null,
                      NDIVideoTimecodeMode videoTimecodeMode = NDIVideoTimecodeMode.Synthesize)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        SourceName = sourceName;

        if (minimumVideoSubmitSpacing is { } spacing && spacing < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumVideoSubmitSpacing));
        _minimumVideoSubmitSpacing = minimumVideoSubmitSpacing;
        _videoTimecodeMode = videoTimecodeMode;
        _egressPresentationTimeline = videoTimecodeMode == NDIVideoTimecodeMode.PresentationRelativeTicks
            ? new NdiEgressPresentationTimeline()
            : null;

        var rc = NDIRuntime.Create(out var rt);
        if (rc != 0 || rt is null) throw new NDIException(rc, "NDIRuntime.Create");
        _runtime = rt;

        try
        {
            rc = NDISender.Create(out var sender, sourceName, groups, clockVideo: clockVideo, clockAudio: clockAudio);
            if (rc != 0 || sender is null) throw new NDIException(rc, "NDISender.Create");
            _sender = sender;
        }
        catch
        {
            _runtime.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Create the audio sink. Idempotent: returns the same instance on
    /// subsequent calls (an NDI source has at most one audio stream). Throws
    /// if already created with a different format.
    /// </summary>
    public NDIAudioSink EnableAudio(AudioFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_audioSink is not null)
            {
                if (_audioSink.Format != format)
                    throw new InvalidOperationException(
                        $"audio sink already configured with format {_audioSink.Format}; cannot reconfigure to {format}");
                return _audioSink;
            }
            _audioSink = new NDIAudioSink(_sender, format, _egressPresentationTimeline);
            return _audioSink;
        }
    }

    private NDIVideoSender CreateVideoSinkLocked()
    {
        lock (_gate)
        {
            return _videoSink ??= new NDIVideoSender(_sender, _minimumVideoSubmitSpacing, _videoTimecodeMode,
                _egressPresentationTimeline);
        }
    }

    /// <summary>
    /// Resets the presentation-time anchor used when <see cref="NDIVideoTimecodeMode.PresentationRelativeTicks"/>
    /// is active (for example after <see cref="S.Media.Core.Video.VideoPlayer.Seek"/>). Clears the shared
    /// presentation anchor used by both <see cref="NDIVideoSender"/> and <see cref="NDIAudioSink"/> when
    /// <see cref="NDIVideoTimecodeMode.PresentationRelativeTicks"/> is selected on this output.
    /// </summary>
    public void ResetVideoPresentationTimecodeAnchor()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _egressPresentationTimeline?.Reset();
            _videoSink?.ResetPresentationTimecodeAnchor();
        }
    }

    /// <summary>
    /// Polls aggregate tally state from connected NDI receivers (<c>NDIlib_send_get_tally</c>).
    /// </summary>
    /// <returns><see langword="true"/> if the tally changed within the wait window.</returns>
    public bool TryGetReceiverTally(out NDITally tally, uint timeoutMs = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sender.GetTally(out tally, timeoutMs);
    }

    /// <summary>
    /// Captures metadata sent upstream by receivers (e.g. PTZ). Pair with <see cref="FreeReceiverMetadata"/>.
    /// </summary>
    public NDIFrameType CaptureReceiverMetadata(out NDIMetadataFrame metadata, uint timeoutMs = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sender.CaptureMetadata(out metadata, timeoutMs);
    }

    /// <summary>Frees a metadata frame returned from <see cref="CaptureReceiverMetadata"/>.</summary>
    public void FreeReceiverMetadata(in NDIMetadataFrame metadata)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sender.FreeMetadata(metadata);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Tear down the video sink first so any in-flight async send is
        // flushed against a still-valid sender.
        try { _videoSink?.Dispose(); } catch { /* best effort */ }
        try { _audioSink?.Dispose(); } catch { /* best effort */ }
        _sender.Dispose();
        _runtime.Dispose();
    }
}
