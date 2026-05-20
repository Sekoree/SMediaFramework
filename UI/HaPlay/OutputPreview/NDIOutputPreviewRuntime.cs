using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using HaPlay.Models;
using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.NDI;
using S.Media.NDI.Audio;
using S.Media.NDI.Video;

namespace HaPlay.OutputPreview;

/// <summary>
/// Holds an <see cref="NDIOutput"/> open for the lifetime of an NDI <see cref="OutputLineViewModel"/>,
/// continuously emitting black video and silent audio so receivers stay locked on across idle ↔ playback
/// transitions. Playback temporarily acquires part of the underlying <see cref="NDIOutput"/> via
/// <see cref="AcquireForPlayback"/> — only the side that's actually being wired pauses, so e.g. an
/// audio-only file on a VideoAndAudio NDI keeps the carrier's black video going while playback drives audio.
/// </summary>
internal sealed class NDIOutputPreviewRuntime : IDisposable
{
    private const int CarrierVideoWidth = 1920;
    private const int CarrierVideoHeight = 1080;
    private const int CarrierVideoFpsNumerator = 30;
    private const int CarrierVideoFpsDenominator = 1;
    private const int CarrierAudioChannels = 2;
    private const int CarrierAudioChunkHz = 50;

    private readonly NDIOutputDefinition _definition;
    private readonly object _gate = new();
    private NDIOutput? _output;
    private NDIAudioSink? _audio;
    private Timer? _videoTimer;
    private Timer? _audioTimer;
    private float[]? _silenceScratch;
    private long _videoOrdinal;
    private long _audioSamplePosition;
    private nint _connectionMetadataUtf8;
    private bool _disposed;
    private bool _videoAcquired;
    private bool _audioAcquired;
    private bool _disposeOnRelease;
    private VideoFrame? _logoTemplate;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.OutputPreview.NDIOutputPreviewRuntime");

    public NDIOutputPreviewRuntime(NDIOutputDefinition definition) =>
        _definition = definition;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var mode = _definition.StreamMode;
        var clockVideo = mode != NDIOutputStreamMode.AudioOnly;
        var clockAudio = mode != NDIOutputStreamMode.VideoOnly;
        var videoTc = mode == NDIOutputStreamMode.VideoAndAudio
            ? NDIVideoTimecodeMode.PresentationRelativeTicks
            : NDIVideoTimecodeMode.Synthesize;

        var groups = string.IsNullOrWhiteSpace(_definition.Groups) ? null : _definition.Groups;
        var output = new NDIOutput(_definition.SourceName, groups, clockVideo, clockAudio,
            minimumVideoSubmitSpacing: null, videoTimecodeMode: videoTc);

        try
        {
            AddConnectionMetadata(output);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"NDIOutputPreviewRuntime: AddConnectionMetadata failed: {ex}");
        }

        lock (_gate)
        {
            _output = output;

            if (HasVideoStream(mode))
            {
                output.VideoSink.Configure(CarrierVideoFormat);
                StartVideoTimerLocked();
            }

            if (HasAudioStream(mode))
            {
                _audio = output.EnableAudio(CarrierAudioFormat);
                StartAudioTimerLocked();
            }
        }
        Trace.LogInformation("Start: '{Name}' mode={Mode} clockVideo={CV} clockAudio={CA} videoTcMode={VTC}",
            _definition.SourceName, mode, clockVideo, clockAudio, videoTc);
    }

    /// <summary>
    /// Replaces the carrier's video template — when <paramref name="logoFrame"/> is non-null the carrier
    /// emits the supplied still instead of black. Pass <c>null</c> to revert to black. The runtime takes
    /// ownership of <paramref name="logoFrame"/>.
    /// </summary>
    public void SetLogoTemplate(VideoFrame? logoFrame)
    {
        VideoFrame? toDispose;
        lock (_gate)
        {
            toDispose = _logoTemplate;
            _logoTemplate = logoFrame;
        }

        toDispose?.Dispose();
    }

    /// <summary>
    /// Pauses only the timers playback actually needs (so audio-only files on a VideoAndAudio NDI keep
    /// the carrier's video going) and returns the live <see cref="NDIOutput"/>. Returns <c>null</c> if
    /// disposed, never started, or another acquirer already owns one of the requested sides.
    /// </summary>
    public NDIOutput? AcquireForPlayback(bool needsVideo, bool needsAudio)
    {
        if (!needsVideo && !needsAudio)
            return null;

        var waitStart = Environment.TickCount64;
        lock (_gate)
        {
            var waitMs = Environment.TickCount64 - waitStart;
            if (waitMs > 50)
                Trace.LogWarning("AcquireForPlayback: '{Name}' gate wait={WaitMs}ms (carrier video/audio tick may be slow inside lock)",
                    _definition.SourceName, waitMs);
            if (_disposed || _output is null)
            {
                Trace.LogTrace("AcquireForPlayback: '{Name}' returning null (disposed={Disposed} output={HasOutput})",
                    _definition.SourceName, _disposed, _output is not null);
                return null;
            }
            if (needsVideo && _videoAcquired)
            {
                Trace.LogTrace("AcquireForPlayback: '{Name}' video already acquired", _definition.SourceName);
                return null;
            }
            if (needsAudio && _audioAcquired)
            {
                Trace.LogTrace("AcquireForPlayback: '{Name}' audio already acquired", _definition.SourceName);
                return null;
            }

            var mode = _definition.StreamMode;
            if (needsVideo && HasVideoStream(mode))
            {
                _videoAcquired = true;
                _videoTimer?.Dispose();
                _videoTimer = null;
                try { _output.VideoSink.ResetPresentationTimecodeAnchor(); }
                catch { /* best effort */ }
            }

            if (needsAudio && HasAudioStream(mode))
            {
                _audioAcquired = true;
                _audioTimer?.Dispose();
                _audioTimer = null;
            }

            Trace.LogDebug("AcquireForPlayback: '{Name}' acquired video={V} audio={A}",
                _definition.SourceName, needsVideo && HasVideoStream(mode), needsAudio && HasAudioStream(mode));
            return _output;
        }
    }

    /// <summary>
    /// Resumes whichever carrier timers were paused by <see cref="AcquireForPlayback"/>. For video, also
    /// reconfigures the sender back to the carrier format so the next frame matches the timer's rate.
    /// </summary>
    public void ReleaseFromPlayback()
    {
        bool dispose;
        lock (_gate)
        {
            var wasVideo = _videoAcquired;
            var wasAudio = _audioAcquired;
            if (!wasVideo && !wasAudio)
                return;
            Trace.LogDebug("ReleaseFromPlayback: '{Name}' wasVideo={V} wasAudio={A}",
                _definition.SourceName, wasVideo, wasAudio);

            _videoAcquired = false;
            _audioAcquired = false;
            dispose = _disposeOnRelease;
            _disposeOnRelease = false;

            if (!dispose && !_disposed && _output is not null)
            {
                var mode = _definition.StreamMode;
                if (wasVideo && HasVideoStream(mode))
                {
                    // Playback may have left the NDI sender configured for a different size / pixel format.
                    // Phase 2A: skip the reconfigure when the sender's current format already matches the
                    // carrier — saves an unnecessary format flip (which receivers can see as a glitch).
                    try
                    {
                        if (!CurrentSinkFormatMatchesCarrier(_output.VideoSink))
                        {
                            _output.VideoSink.Configure(CarrierVideoFormat);
                            _output.VideoSink.ResetPresentationTimecodeAnchor();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"NDIOutputPreviewRuntime: re-Configure after playback failed: {ex}");
                    }

                    StartVideoTimerLocked();
                }

                if (wasAudio && HasAudioStream(mode))
                    StartAudioTimerLocked();
            }
        }

        if (dispose)
            Dispose();
    }

    private void AddConnectionMetadata(NDIOutput output)
    {
        var longName = SecurityElement.Escape(_definition.DisplayName + " (HaPlay carrier)");
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<ndi_product long_name=\"" + longName + "\" short_name=\"HaPlay\"/>";

        _connectionMetadataUtf8 = Marshal.StringToCoTaskMemUTF8(xml);
        output.AddConnectionMetadata(new NDIMetadataFrame
        {
            Length = 0,
            Timecode = 0,
            PData = _connectionMetadataUtf8,
        });
    }

    private void StartVideoTimerLocked()
    {
        var periodMs = Math.Max(1, (int)Math.Round(1000.0 * CarrierVideoFpsDenominator / CarrierVideoFpsNumerator));
        _videoTimer?.Dispose();
        _videoTimer = new Timer(OnVideoTick, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(periodMs));
    }

    private void StartAudioTimerLocked()
    {
        var periodMs = Math.Max(1, 1000 / CarrierAudioChunkHz);
        _audioTimer?.Dispose();
        _audioTimer = new Timer(OnAudioTick, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(periodMs));
    }

    private static bool CurrentSinkFormatMatchesCarrier(NDIVideoSender sink)
    {
        try
        {
            var f = sink.Format;
            var c = CarrierVideoFormat;
            return f.Width == c.Width && f.Height == c.Height && f.PixelFormat == c.PixelFormat;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasVideoStream(NDIOutputStreamMode mode) =>
        mode is NDIOutputStreamMode.VideoAndAudio or NDIOutputStreamMode.VideoOnly;

    private static bool HasAudioStream(NDIOutputStreamMode mode) =>
        mode is NDIOutputStreamMode.VideoAndAudio or NDIOutputStreamMode.AudioOnly;

    private void OnVideoTick(object? _)
    {
        var tickStart = Environment.TickCount64;
        lock (_gate)
        {
            if (_disposed || _videoAcquired || _output is null)
                return;

            try
            {
                var idx = _videoOrdinal++;
                var pt = TimeSpan.FromTicks(idx * TimeSpan.TicksPerSecond * CarrierVideoFpsDenominator / CarrierVideoFpsNumerator);

                var frame = _logoTemplate is { } tpl
                    ? CloneLogoFrame(tpl, pt)
                    : PreviewVideoFrames.CreateBlackBgra(CarrierVideoFormat, pt);
                _output.VideoSink.Submit(frame);
                var dur = Environment.TickCount64 - tickStart;
                // ~33ms is one frame period. NDI SDK's clockVideo:true can pace the send internally,
                // so a slow tick here just means the SDK was throttling — only worth reading at Trace.
                if (dur > 100 && Trace.IsEnabled(LogLevel.Trace))
                    Trace.LogTrace("OnVideoTick: '{Name}' ran for {Ms}ms while holding gate",
                        _definition.SourceName, dur);
            }
            catch (Exception ex)
            {
                Trace.LogError(ex, $"NDIOutputPreviewRuntime '{_definition.SourceName}' video tick");
                Debug.WriteLine($"NDIOutputPreviewRuntime video tick: {ex}");
            }
        }
    }

    private static VideoFrame CloneLogoFrame(VideoFrame template, TimeSpan presentationTime) =>
        new(presentationTime, template.Format, template.Planes, template.Strides,
            release: null, metadata: template.Metadata);

    private void OnAudioTick(object? _)
    {
        lock (_gate)
        {
            if (_disposed || _audioAcquired || _output is null || _audio is null)
                return;

            try
            {
                var rate = _definition.AudioSampleRate;
                if (rate <= 0)
                    return;
                var spc = Math.Max(1, rate / CarrierAudioChunkHz);
                var pt = TimeSpan.FromTicks(_audioSamplePosition * TimeSpan.TicksPerSecond / rate);
                _audioSamplePosition += spc;
                SubmitSilence(pt, spc);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NDIOutputPreviewRuntime audio tick: {ex}");
            }
        }
    }

    private void SubmitSilence(TimeSpan presentationTime, int samplesPerChannel)
    {
        if (_audio is null)
            return;

        var channels = CarrierAudioChannels;
        var rate = _definition.AudioSampleRate;
        var need = samplesPerChannel * channels;
        if (need <= 0)
            return;

        if (_silenceScratch is null || _silenceScratch.Length < need)
            _silenceScratch = new float[need];
        else
            Array.Clear(_silenceScratch, 0, need);

        var fmt = new AudioFormat(rate, channels);
        _audio.Submit(new AudioFrame(presentationTime, fmt, samplesPerChannel, _silenceScratch.AsMemory(0, need)));
    }

    private static VideoFormat CarrierVideoFormat => new(
        CarrierVideoWidth, CarrierVideoHeight, PixelFormat.Bgra32,
        new Rational(CarrierVideoFpsNumerator, CarrierVideoFpsDenominator));

    private AudioFormat CarrierAudioFormat => new(_definition.AudioSampleRate, CarrierAudioChannels);

    public void Dispose()
    {
        VideoFrame? logoToDispose;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_videoAcquired || _audioAcquired)
            {
                // Playback still owns part of the sender — defer teardown so we don't yank it mid-stream.
                _disposeOnRelease = true;
                return;
            }

            _disposed = true;
            _videoTimer?.Dispose();
            _videoTimer = null;
            _audioTimer?.Dispose();
            _audioTimer = null;
            logoToDispose = _logoTemplate;
            _logoTemplate = null;
            try
            {
                _output?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NDIOutputPreviewRuntime.Dispose: {ex}");
            }

            _output = null;
            _audio = null;
        }

        logoToDispose?.Dispose();

        if (_connectionMetadataUtf8 != nint.Zero)
        {
            Marshal.FreeCoTaskMem(_connectionMetadataUtf8);
            _connectionMetadataUtf8 = nint.Zero;
        }
    }
}
