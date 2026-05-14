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
/// Holds an <see cref="NDIOutput"/> open with black video and/or silent audio so receivers can discover and connect before playback is wired.
/// </summary>
internal sealed class NdiOutputPreviewRuntime : IDisposable
{
    private const int VideoWidth = 1280;
    private const int VideoHeight = 720;
    private const int VideoFpsNumerator = 30;
    private const int VideoFpsDenominator = 1;

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
                var vf = new VideoFormat(VideoWidth, VideoHeight, PixelFormat.Bgra32,
                    new Rational(VideoFpsNumerator, VideoFpsDenominator));
                output.VideoSink.Configure(vf);
                var periodMs = Math.Max(1, (int)Math.Round(1000.0 * VideoFpsDenominator / VideoFpsNumerator));
                _videoTimer = new Timer(OnVideoTick, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(periodMs));
            }

            if (mode == NdiOutputStreamMode.VideoAndAudio)
            {
                _audio = output.EnableAudio(new AudioFormat(_definition.AudioSampleRate, _definition.AudioChannelCount));
            }
            else if (mode == NdiOutputStreamMode.AudioOnly)
            {
                _audio = output.EnableAudio(new AudioFormat(_definition.AudioSampleRate, _definition.AudioChannelCount));
                _audioTimer = new Timer(OnAudioOnlyTick, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(20));
            }
        }
    }

    private void AddConnectionMetadata(NDIOutput output)
    {
        var longName = SecurityElement.Escape(_definition.DisplayName + " (HaPlay idle)");
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

    private void OnVideoTick(object? _)
    {
        lock (_gate)
        {
            if (_disposed || _output is null)
                return;

            try
            {
                var mode = _definition.StreamMode;
                var vf = new VideoFormat(VideoWidth, VideoHeight, PixelFormat.Bgra32,
                    new Rational(VideoFpsNumerator, VideoFpsDenominator));
                var idx = _videoOrdinal++;
                var pt = TimeSpan.FromTicks(idx * TimeSpan.TicksPerSecond * VideoFpsDenominator / VideoFpsNumerator);

                if (mode is NdiOutputStreamMode.VideoAndAudio or NdiOutputStreamMode.VideoOnly)
                {
                    var frame = PreviewVideoFrames.CreateBlackBgra(vf, pt);
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

    private void OnAudioOnlyTick(object? _)
    {
        lock (_gate)
        {
            if (_disposed || _output is null || _audio is null)
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

        var channels = _definition.AudioChannelCount;
        var rate = _definition.AudioSampleRate;
        var spc = samplesPerChannelOverride ?? (int)Math.Round(rate * (double)VideoFpsDenominator / VideoFpsNumerator);
        if (spc <= 0)
            return;

        var need = spc * channels;
        if (_silenceScratch is null || _silenceScratch.Length < need)
            _silenceScratch = new float[need];

        Array.Clear(_silenceScratch, 0, need);
        var fmt = new AudioFormat(rate, channels);
        _audio.Submit(new AudioFrame(presentationTime, fmt, spc, _silenceScratch.AsMemory(0, need)));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _videoTimer?.Dispose();
            _videoTimer = null;
            _audioTimer?.Dispose();
            _audioTimer = null;
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

        if (_connectionMetadataUtf8 != nint.Zero)
        {
            Marshal.FreeCoTaskMem(_connectionMetadataUtf8);
            _connectionMetadataUtf8 = nint.Zero;
        }
    }
}
