using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using HaPlay.Models;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.NDI;
using S.Media.NDI.Audio;
using S.Media.NDI.Video;

namespace HaPlay.OutputPreview;

/// <summary>
/// Holds an <see cref="NDIOutput"/> open for the lifetime of an NDI <see cref="OutputLineViewModel"/>,
/// continuously emitting black video and silent audio so receivers stay locked on across idle ↔ playback
/// transitions. Playback temporarily acquires the underlying <see cref="NDIOutput"/> via
/// <see cref="AcquireForPlayback"/> and returns it via <see cref="ReleaseFromPlayback"/>; the carrier
/// pauses while playback owns the sender and resumes on release.
/// </summary>
internal sealed class NdiOutputPreviewRuntime : IDisposable
{
    private const int CarrierVideoWidth = 1920;
    private const int CarrierVideoHeight = 1080;
    private const int CarrierVideoFpsNumerator = 30;
    private const int CarrierVideoFpsDenominator = 1;
    private const int CarrierAudioChannels = 2;

    private readonly NdiOutputDefinition _definition;
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
    private bool _acquired;
    private bool _disposeOnRelease;
    private VideoFrame? _logoTemplate;

    public NdiOutputPreviewRuntime(NdiOutputDefinition definition) =>
        _definition = definition;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var mode = _definition.StreamMode;
        var clockVideo = mode != NdiOutputStreamMode.AudioOnly;
        var clockAudio = mode != NdiOutputStreamMode.VideoOnly;
        var videoTc = mode == NdiOutputStreamMode.VideoAndAudio
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
            Debug.WriteLine($"NdiOutputPreviewRuntime: AddConnectionMetadata failed: {ex}");
        }

        lock (_gate)
        {
            _output = output;

            if (mode is NdiOutputStreamMode.VideoAndAudio or NdiOutputStreamMode.VideoOnly)
            {
                output.VideoSink.Configure(CarrierVideoFormat);
                StartVideoTimerLocked();
            }

            if (mode is NdiOutputStreamMode.VideoAndAudio or NdiOutputStreamMode.AudioOnly)
            {
                _audio = output.EnableAudio(CarrierAudioFormat);
                if (mode == NdiOutputStreamMode.AudioOnly)
                    StartAudioOnlyTimerLocked();
            }
        }
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
    /// Pauses the carrier pump and returns the live <see cref="NDIOutput"/> so playback can wire its sinks
    /// onto the existing sender (receivers stay locked). Caller must invoke <see cref="ReleaseFromPlayback"/>
    /// when finished. Returns <c>null</c> if already acquired or disposed.
    /// </summary>
    public NDIOutput? AcquireForPlayback()
    {
        lock (_gate)
        {
            if (_disposed || _output is null || _acquired)
                return null;

            _acquired = true;
            StopTimersLocked();
            // Drop any in-flight presentation anchor so playback PTS becomes the new baseline timecode.
            try
            {
                _output.VideoSink.ResetPresentationTimecodeAnchor();
            }
            catch
            {
                /* best effort */
            }

            return _output;
        }
    }

    /// <summary>
    /// Resumes the carrier after playback has finished using the sender. Reconfigures the video sink back
    /// to the carrier format so the next frame matches the timer's rate, and restarts the pump.
    /// </summary>
    public void ReleaseFromPlayback()
    {
        bool dispose;
        lock (_gate)
        {
            if (!_acquired)
                return;
            _acquired = false;
            dispose = _disposeOnRelease;
            _disposeOnRelease = false;

            if (!dispose && !_disposed && _output is not null)
            {
                // Playback may have left the NDI sender configured for a different size / pixel format.
                // Re-configure to carrier defaults before resuming so receivers see one consistent stream.
                var mode = _definition.StreamMode;
                if (mode is NdiOutputStreamMode.VideoAndAudio or NdiOutputStreamMode.VideoOnly)
                {
                    try
                    {
                        _output.VideoSink.Configure(CarrierVideoFormat);
                        _output.VideoSink.ResetPresentationTimecodeAnchor();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"NdiOutputPreviewRuntime: re-Configure after playback failed: {ex}");
                    }

                    StartVideoTimerLocked();
                }

                if (mode == NdiOutputStreamMode.AudioOnly)
                    StartAudioOnlyTimerLocked();
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

    private void StartAudioOnlyTimerLocked()
    {
        _audioTimer?.Dispose();
        _audioTimer = new Timer(OnAudioOnlyTick, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
    }

    private void StopTimersLocked()
    {
        _videoTimer?.Dispose();
        _videoTimer = null;
        _audioTimer?.Dispose();
        _audioTimer = null;
    }

    private void OnVideoTick(object? _)
    {
        lock (_gate)
        {
            if (_disposed || _acquired || _output is null)
                return;

            try
            {
                var mode = _definition.StreamMode;
                var idx = _videoOrdinal++;
                var pt = TimeSpan.FromTicks(idx * TimeSpan.TicksPerSecond * CarrierVideoFpsDenominator / CarrierVideoFpsNumerator);

                if (mode is NdiOutputStreamMode.VideoAndAudio or NdiOutputStreamMode.VideoOnly)
                {
                    var frame = _logoTemplate is { } tpl
                        ? CloneLogoFrame(tpl, pt)
                        : PreviewVideoFrames.CreateBlackBgra(CarrierVideoFormat, pt);
                    _output.VideoSink.Submit(frame);
                }

                if (mode == NdiOutputStreamMode.VideoAndAudio)
                    SubmitSilence(pt);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NdiOutputPreviewRuntime video tick: {ex}");
            }
        }
    }

    private static VideoFrame CloneLogoFrame(VideoFrame template, TimeSpan presentationTime) =>
        new(presentationTime, template.Format, template.Planes, template.Strides,
            template.ColorTransferHint, release: null);

    private void OnAudioOnlyTick(object? _)
    {
        lock (_gate)
        {
            if (_disposed || _acquired || _output is null || _audio is null)
                return;

            try
            {
                var rate = _definition.AudioSampleRate;
                var spc = Math.Max(1, rate / 50);
                var pt = TimeSpan.FromTicks(_audioSamplePosition * TimeSpan.TicksPerSecond / rate);
                _audioSamplePosition += spc;
                SubmitSilence(pt, spc);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NdiOutputPreviewRuntime audio tick: {ex}");
            }
        }
    }

    private void SubmitSilence(TimeSpan presentationTime, int? samplesPerChannelOverride = null)
    {
        if (_audio is null)
            return;

        var channels = CarrierAudioChannels;
        var rate = _definition.AudioSampleRate;
        var spc = samplesPerChannelOverride ?? (int)Math.Round(rate * (double)CarrierVideoFpsDenominator / CarrierVideoFpsNumerator);
        if (spc <= 0)
            return;

        var need = spc * channels;
        if (_silenceScratch is null || _silenceScratch.Length < need)
            _silenceScratch = new float[need];

        Array.Clear(_silenceScratch, 0, need);
        var fmt = new AudioFormat(rate, channels);
        _audio.Submit(new AudioFrame(presentationTime, fmt, spc, _silenceScratch.AsMemory(0, need)));
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
            if (_acquired)
            {
                // Playback still owns the sender — defer teardown so we don't yank it mid-stream.
                _disposeOnRelease = true;
                return;
            }

            _disposed = true;
            StopTimersLocked();
            logoToDispose = _logoTemplate;
            _logoTemplate = null;
            try
            {
                _output?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NdiOutputPreviewRuntime.Dispose: {ex}");
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
