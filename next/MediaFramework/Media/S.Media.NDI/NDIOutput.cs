using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.NDI.Audio;
using S.Media.NDI.Video;

namespace S.Media.NDI;

/// <summary>
/// One NDI source on the network — owns a single <see cref="NDISender"/> plus
/// the <see cref="NDIRuntime"/> ref-count, and exposes child outputs for audio
/// and video. Receivers see one combined source carrying both streams.
/// </summary>
/// <remarks>
/// <para>
/// Audio-only / video-only is just a matter of which children you enable.
/// <see cref="EnableAudio"/> creates the audio output; <see cref="VideoOutput"/>
/// is lazy and created on first access. Don't touch the side you don't
/// need and the SDK simply won't transmit that stream.
/// </para>
/// <para>
/// Lifetime: child outputs must not outlive this <see cref="NDIOutput"/>.
/// They share the parent's sender; disposing the parent invalidates them.
/// </para>
/// <para>
/// For SDK-level receiver feedback (tally, upstream metadata), use <see cref="TryGetReceiverTally"/>,
/// <see cref="CaptureReceiverMetadata"/>, and <see cref="FreeReceiverMetadata"/> on a background thread
/// with short timeouts so sends are not blocked. Pair with <see cref="GetReceiverConnectionCount"/> when
/// correlating tally changes with attach/detach in the field. For a single poll that also carries host
/// router pump counters (NDI Monitor style health beside <c>PumpPressure</c>), use
/// <see cref="TryPollMonitorReceiverPumpFusion"/>.
/// </para>
/// <para>
/// Per-connection metadata advertised to new receivers uses <see cref="ClearConnectionMetadata"/> /
/// <see cref="AddConnectionMetadata"/> (UTF-8 XML strings per NDI SDK rules).
/// </para>
/// <para>
/// <see cref="Dispose"/> tears down video output, audio output, <see cref="NDISender"/>, then <see cref="NDIRuntime"/>; each step is wrapped so
/// <strong>Debug</strong> builds log via <see cref="MediaDiagnostics.LogError"/> while <strong>Release</strong> continues best-effort.
/// </para>
/// </remarks>
public sealed class NDIOutput : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.NDI.NDIOutput");

    private readonly TimeSpan? _minimumVideoSubmitSpacing;
    private readonly NDIVideoTimecodeMode _videoTimecodeMode;

    /// <summary>When <see cref="NDIVideoTimecodeMode.PresentationRelativeTicks"/> is selected, shared by video + audio outputs.</summary>
    private readonly NDIEgressPresentationTimeline? _egressPresentationTimeline;

    private readonly NDIRuntime _runtime;
    private readonly NDISender _sender;
    private readonly object _gate = new();
    private NDIAudioOutput? _audioOutput;
    private NDIVideoSender? _videoOutput;
    private bool _disposed;

    public string SourceName { get; }

    /// <summary>Receiver count with no wait (same as <c>NDIlib_send_get_no_connections(..., 0)</c>).</summary>
    public int ConnectionCount => _sender.GetConnectionCount(0);

    /// <summary>
    /// Returns how many NDI receivers are connected to this sender.
    /// Use a non-zero <paramref name="timeoutMs"/> to block until at least one receiver attaches (full-wire harnesses).
    /// </summary>
    public int GetReceiverConnectionCount(uint timeoutMs = 0) => _sender.GetConnectionCount(timeoutMs);

    /// <summary>
    /// Video output — always available; format negotiated via
    /// <see cref="Core.Video.IVideoOutput.Configure"/>.
    /// </summary>
    public IVideoOutput Video
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _videoOutput ??= CreateVideoOutputLocked();
        }
    }

    /// <summary>Same instance as <see cref="Video"/> (typed for in-assembly callers).</summary>
    internal NDIVideoSender VideoSender => (NDIVideoSender)Video;

    /// <summary>Non-null after <see cref="EnableAudio"/>.</summary>
    public IAudioOutput? Audio => _audioOutput;

    /// <summary>
    /// Construct an NDI source. <paramref name="clockVideo"/> /
    /// <paramref name="clockAudio"/> tell the SDK to pace each stream against
    /// its declared rate (typical for a self-driving sender). Use <see cref="GetReceiverConnectionCount"/>
    /// with a short wait to detect the first receiver in harnesses.
    /// </summary>
    /// <param name="minimumVideoSubmitSpacing">
    /// Optional wall-clock throttle between video frames (<see cref="NDIVideoSender"/> submit path).
    /// Use with <paramref name="clockVideo"/>:false when you want MFPlayer to pace instead of NDI timestamps.
    /// </param>
    /// <param name="videoTimecodeMode">
    /// How <see cref="NDIVideoSender"/> fills NDI video timecodes — use
    /// <see cref="NDIVideoTimecodeMode.PresentationRelativeTicks"/> for an explicit timeline aligned with
    /// <see cref="NDIAudioOutput"/> when muxing file A/V.
    /// </param>
    public NDIOutput(string sourceName, string? groups = null, bool clockVideo = true, bool clockAudio = true,
        TimeSpan? minimumVideoSubmitSpacing = null,
        NDIVideoTimecodeMode videoTimecodeMode = NDIVideoTimecodeMode.Synthesize)
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "NDIOutput.Open", slowWarningMs: 1000);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        SourceName = sourceName;

        if (minimumVideoSubmitSpacing is { } spacing && spacing < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumVideoSubmitSpacing));
        _minimumVideoSubmitSpacing = minimumVideoSubmitSpacing;
        _videoTimecodeMode = videoTimecodeMode;
        _egressPresentationTimeline = videoTimecodeMode == NDIVideoTimecodeMode.PresentationRelativeTicks
            ? new NDIEgressPresentationTimeline()
            : null;

        var rc = NDIRuntime.Create(out var rt);
        if (rc != 0 || rt is null) throw new NDIException(rc, "NDIRuntime.Create");
        _runtime = rt;

        try
        {
            rc = NDISender.Create(out var sender, sourceName, groups, clockVideo: clockVideo, clockAudio: clockAudio);
            if (rc != 0 || sender is null) throw new NDIException(rc, "NDISender.Create");
            _sender = sender;
            Trace.LogInformation(
                "NDIOutput: source='{Source}' groups={Groups} clockVideo={ClockVideo} clockAudio={ClockAudio} videoTimecodeMode={Mode}",
                sourceName,
                groups ?? "(default)",
                clockVideo,
                clockAudio,
                videoTimecodeMode);
        }
        catch (Exception ex)
        {
#if DEBUG
            MediaDiagnostics.LogError(ex, "NDIOutput: NDISender.Create");
#else
            _ = ex;
#endif
            _runtime.Dispose();
            throw;
        }
        timing?.SetOutcome($"source={sourceName} clockVideo={clockVideo} clockAudio={clockAudio}");
    }

    /// <summary>
    /// Create the audio output. Idempotent: returns the same instance on
    /// subsequent calls (an NDI source has at most one audio stream). Throws
    /// if already created with a different format.
    /// </summary>
    public IAudioOutput EnableAudio(AudioFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_audioOutput is not null)
            {
                if (_audioOutput.Format != format)
                    throw new InvalidOperationException(
                        $"audio output already configured with format {_audioOutput.Format}; cannot reconfigure to {format}");
                return _audioOutput;
            }

            _audioOutput = new NDIAudioOutput(_sender, format, _egressPresentationTimeline);
            Trace.LogDebug("EnableAudio: source='{Source}' format={Format}", SourceName, format);
            return _audioOutput;
        }
    }

    private NDIVideoSender CreateVideoOutputLocked()
    {
        lock (_gate)
        {
            if (_videoOutput is not null)
                return _videoOutput;

            _videoOutput = new NDIVideoSender(_sender, _minimumVideoSubmitSpacing, _videoTimecodeMode,
                _egressPresentationTimeline);
            Trace.LogDebug("CreateVideoOutput: source='{Source}' spacing={Spacing}ms timecodeMode={Mode}",
                SourceName, _minimumVideoSubmitSpacing?.TotalMilliseconds ?? 0, _videoTimecodeMode);
            return _videoOutput;
        }
    }

    /// <summary>
    /// Resets the presentation-time anchor used when <see cref="NDIVideoTimecodeMode.PresentationRelativeTicks"/>
    /// is active (for example after <see cref="S.Media.Core.Video.VideoPlayer.Seek"/>). Clears the shared
    /// presentation anchor used by both <see cref="NDIVideoSender"/> and <see cref="NDIAudioOutput"/> when
    /// <see cref="NDIVideoTimecodeMode.PresentationRelativeTicks"/> is selected on this output.
    /// </summary>
    public void ResetVideoPresentationTimecodeAnchor()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _egressPresentationTimeline?.Reset();
            _videoOutput?.ResetPresentationTimecodeAnchor();
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

    /// <summary>
    /// Polls receiver count, tally, optional one-shot upstream metadata drain, and copies host pump counters
    /// into one struct for HUDs (NDI Monitor program/preview vs local drops). Prefer a background thread and
    /// <paramref name="tallyWaitMs"/> = 0 (or a few ms) so the send path is not wedged.
    /// </summary>
    /// <param name="tallyWaitMs">Wait window for <see cref="TryGetReceiverTally"/> (SDK tally change API).</param>
    /// <param name="drainOneUpstreamMetadataFrame">
    /// When true and <see cref="GetReceiverConnectionCount"/> (0) reports at least one receiver, performs a single
    /// non-blocking <see cref="CaptureReceiverMetadata"/>; frees the frame when the SDK returns
    /// <see cref="NDIFrameType.Metadata"/>.
    /// </param>
    public NDIMonitorReceiverPumpFusion TryPollMonitorReceiverPumpFusion(
        uint tallyWaitMs,
        bool drainOneUpstreamMetadataFrame,
        long ndiVideoPumpDropped,
        long ndiVideoPumpSubmitted,
        int ndiVideoPumpMaxQueueDepth,
        int ndiVideoPumpCurrentQueuedDepth,
        long ndiAudioPumpDropped)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connections = GetReceiverConnectionCount(0);
        var tallyChanged = TryGetReceiverTally(out var tally, tallyWaitMs);
        var drained = false;
        if (drainOneUpstreamMetadataFrame && connections > 0)
        {
            var frameType = CaptureReceiverMetadata(out var meta, 0);
            if (frameType == NDIFrameType.Metadata)
            {
                FreeReceiverMetadata(meta);
                drained = true;
            }
        }

        return new NDIMonitorReceiverPumpFusion(
            connections,
            tally,
            tallyChanged,
            drained,
            ndiVideoPumpDropped,
            ndiVideoPumpSubmitted,
            ndiVideoPumpMaxQueueDepth,
            ndiVideoPumpCurrentQueuedDepth,
            ndiAudioPumpDropped);
    }

    /// <summary>Clears strings previously registered with <see cref="AddConnectionMetadata"/>.</summary>
    public void ClearConnectionMetadata()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sender.ClearConnectionMetadata();
    }

    /// <summary>
    /// Adds a UTF-8 XML metadata string the SDK sends to each new receiver connection (see NDI sender docs).
    /// </summary>
    public void AddConnectionMetadata(in NDIMetadataFrame metadata)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sender.AddConnectionMetadata(metadata);
    }

    public void Dispose()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "NDIOutput.Dispose", slowWarningMs: 1000);
        if (_disposed)
        {
            timing?.SetOutcome($"source={SourceName} already-disposed");
            return;
        }
        _disposed = true;
        // Tear down the video output first so any in-flight async send is
        // flushed against a still-valid sender.
        MediaDiagnostics.SwallowDisposeErrors(() => _videoOutput?.Dispose(), "NDIOutput.Dispose: video output");
        MediaDiagnostics.SwallowDisposeErrors(() => _audioOutput?.Dispose(), "NDIOutput.Dispose: audio output");
        MediaDiagnostics.SwallowDisposeErrors(_sender.Dispose, "NDIOutput.Dispose: NDISender");
        MediaDiagnostics.SwallowDisposeErrors(_runtime.Dispose, "NDIOutput.Dispose: NDIRuntime");
        timing?.SetOutcome($"source={SourceName}");
    }
}
