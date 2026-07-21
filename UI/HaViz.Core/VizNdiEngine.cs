using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using S.Media.Compositor;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.NDI;
using S.Media.Visualizer.ProjectM;

namespace HaViz.Core;

/// <summary>
/// The portable visualizer→NDI engine: one <see cref="ProjectMVisualSource"/> in continuous mode
/// (its renderer thread owns the offscreen GL context from the injected factory), one
/// <see cref="NDIOutput"/>, and a pump thread that paces rendered frames to the configured fps and
/// submits them as NDI video. Audio submitted via <see cref="SubmitPcm"/> fans out to the
/// visualizer's PCM tap and (downmixed to stereo) to the NDI audio stream, so receivers hear what
/// drives the visuals.
///
/// <para>Platform-free: the Android app supplies an EGL context factory and pushes PCM from its
/// player/capture; a desktop host could do the same with the SDL3 factory. All heavy work happens
/// on the engine's own threads - <see cref="SubmitPcm"/> is the only hot call on a caller thread
/// and only copies into ring/staging buffers.</para>
/// </summary>
public sealed class VizNdiEngine : IDisposable
{
    private readonly ILogger _log;
    private readonly VizNdiSettings _settings;
    private readonly Func<IOffscreenGlContext?> _contextFactory;
    private readonly object _audioGate = new();

    private ProjectMVisualSource? _source;
    private NDIOutput? _ndi;
    private IAudioOutput? _ndiAudio;
    private AudioFormat _ndiAudioFormat;
    private float[] _stereoScratch = [];
    private Thread? _pumpThread;
    private CancellationTokenSource? _cts;
    private int _disposed;

    public VizNdiEngine(
        VizNdiSettings settings,
        Func<IOffscreenGlContext?> offscreenGlContextFactory,
        ILoggerFactory? loggerFactory = null)
    {
        _settings = settings.Normalized();
        _contextFactory = offscreenGlContextFactory;
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("HaViz.Engine");
    }

    public VizNdiSettings Settings => _settings;

    public bool IsRunning => _pumpThread is { IsAlive: true };

    /// <summary>True when GL/projectM failed to come up (frames stay black; NDI still runs).</summary>
    public bool VisualizerFailed => _source?.IsContinuous != true;

    public string? CurrentPresetName => _source?.CurrentPresetName;

    public int PresetCount => _source?.PresetCount ?? -1;

    /// <summary>Connected NDI receivers (0 when nothing is watching).</summary>
    public int ConnectionCount => _ndi?.ConnectionCount ?? 0;

    /// <summary>Mean luma (0-255) of the last submitted frame, sparsely sampled - a cheap
    /// black-output probe surfaced in the UI (-1 before the first frame).</summary>
    public int LastFrameLuma => Volatile.Read(ref _lastFrameLuma);

    /// <summary>Frames submitted to NDI since start (0 = the renderer never published).</summary>
    public long FramesSent => Volatile.Read(ref _framesSent);

    private int _lastFrameLuma = -1;
    private long _framesSent;
    private long _pollsWithoutFrame;
    private int _avgSubmitMs;

    /// <summary>Pump polls that found NO new rendered frame. Large vs <see cref="FramesSent"/> =
    /// the renderer is the bottleneck; near zero = the pump (NDI encode) is.</summary>
    public long PollsWithoutFrame => Volatile.Read(ref _pollsWithoutFrame);

    /// <summary>Rolling average of the NDI submit (pack + SpeedHQ encode) cost per frame.</summary>
    public int AverageSubmitMs => Volatile.Read(ref _avgSubmitMs);

    public void RequestNextPreset() => _source?.RequestNextPreset();

    /// <summary>Creates the NDI sender + visualizer and starts the frame pump. One-shot: dispose
    /// and create a fresh engine to change settings (NDI names/resolutions are not hot-swappable).</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_pumpThread is not null)
            throw new InvalidOperationException("engine already started");

        _ndi = new NDIOutput(_settings.NdiName);

        // The factory hands each renderer its own context on the renderer's thread (the
        // IOffscreenGlContext contract); the static is process-wide by framework design.
        ProjectMVisualSource.OffscreenGlContextFactory = _contextFactory;
        _source = new ProjectMVisualSource(
            _settings.Width, _settings.Height, new Rational(_settings.Fps, 1),
            new ProjectMOptions
            {
                PresetDirectory = string.IsNullOrWhiteSpace(_settings.PresetDirectory) ? null : _settings.PresetDirectory,
                RenderWidth = _settings.Width,
                RenderHeight = _settings.Height,
                Fps = _settings.Fps,
                PresetDurationSeconds = _settings.PresetDurationSeconds,
                Shuffle = _settings.ShufflePresets,
                BeatSensitivity = _settings.BeatSensitivity,
                TransitionSeconds = _settings.TransitionSeconds,
            });

        _cts = new CancellationTokenSource();
        _pumpThread = new Thread(() => PumpLoop(_cts.Token)) { IsBackground = true, Name = "HaVizNdiPump" };
        _pumpThread.Start();
        _log.LogInformation(
            "HaViz engine started: NDI '{Name}' {W}x{H}@{Fps}, presets='{Presets}'",
            _settings.NdiName, _settings.Width, _settings.Height, _settings.Fps,
            _settings.PresetDirectory ?? "(builtin)");
    }

    /// <summary>
    /// Feeds interleaved float PCM from the active audio source (player or capture). Any sample
    /// rate/channel count; the engine downmixes to stereo for the visualizer tap and the NDI audio
    /// stream. The NDI audio stream is created lazily from the FIRST submit's format; a mid-run
    /// format change is submitted as-is only when it matches (NDI sources carry one audio format).
    /// </summary>
    public void SubmitPcm(ReadOnlySpan<float> interleaved, int sampleRate, int channels)
    {
        if (_disposed != 0 || channels <= 0 || interleaved.IsEmpty)
            return;
        var source = _source;
        var ndi = _ndi;
        if (source is null || ndi is null)
            return;

        lock (_audioGate)
        {
            var frames = interleaved.Length / channels;
            var stereo = EnsureStereoScratch(frames * 2);
            DownmixToStereo(interleaved, channels, stereo, frames);
            var span = stereo.AsSpan(0, frames * 2);

            source.Submit(span);

            if (_ndiAudio is null)
            {
                _ndiAudioFormat = new AudioFormat(sampleRate, 2);
                _ndiAudio = ndi.EnableAudio(_ndiAudioFormat);
            }

            if (_ndiAudioFormat.SampleRate == sampleRate)
                _ndiAudio.Submit(span);
        }
    }

    /// <summary>Interleaved downmix: mono duplicates; >2 channels average L/R-ish pairs (channel 0/1)
    /// - deliberately simple, the feed drives beat detection and monitoring, not mastering.</summary>
    internal static void DownmixToStereo(ReadOnlySpan<float> interleaved, int channels, float[] stereo, int frames)
    {
        if (channels == 2)
        {
            interleaved[..(frames * 2)].CopyTo(stereo);
            return;
        }

        for (var f = 0; f < frames; f++)
        {
            var idx = f * channels;
            var left = interleaved[idx];
            var right = channels > 1 ? interleaved[idx + 1] : left;
            stereo[f * 2] = left;
            stereo[f * 2 + 1] = right;
        }
    }

    private float[] EnsureStereoScratch(int length)
    {
        if (_stereoScratch.Length < length)
            _stereoScratch = new float[length];
        return _stereoScratch;
    }

    private void PumpLoop(CancellationToken token)
    {
        var source = _source!;
        var ndi = _ndi!;
        var width = _settings.Width;
        var height = _settings.Height;
        var stride = width * 4;
        // The pixel layout is the renderer's native readback (BGRA on desktop GL, RGBA on GLES);
        // it is only final once the renderer published a frame, so NDI is configured lazily on the
        // first frame - the sender takes either layout without conversion.
        VideoFormat? format = null;
        var renderFrame = new byte[stride * height]; // renderer copy (FBO row order)
        var ndiFrame = new byte[stride * height];    // flipped top-down copy handed to NDI
        long seenVersion = 0;
        var intervalTicks = Stopwatch.Frequency / (double)_settings.Fps;
        var started = Stopwatch.GetTimestamp();
        var nextDeadline = started;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (source.TryCopyLatestRenderedFrame(renderFrame, ref seenVersion))
                {
                    if (format is null)
                    {
                        format = new VideoFormat(
                            width, height, source.RenderedFramePixelFormat, new Rational(_settings.Fps, 1));
                        ndi.Video.Configure(format.Value); // required before the first Submit
                    }

                    // Renderer rows are FBO order (bottom-up for a top-down consumer): flip for NDI.
                    for (var y = 0; y < height; y++)
                    {
                        System.Buffer.BlockCopy(
                            renderFrame, (height - 1 - y) * stride,
                            ndiFrame, y * stride, stride);
                    }

                    var pts = Stopwatch.GetElapsedTime(started);
                    // NDIVideoSender packs synchronously into its staging buffer, so reusing the
                    // arrays after Submit returns is safe (no release wiring needed).
                    var submitStarted = Stopwatch.GetTimestamp();
                    ndi.Video.Submit(new VideoFrame(pts, format.Value, [ndiFrame], [stride]));
                    var submitMs = (int)Stopwatch.GetElapsedTime(submitStarted).TotalMilliseconds;
                    Volatile.Write(ref _avgSubmitMs, (Volatile.Read(ref _avgSubmitMs) * 7 + submitMs) / 8);
                    Interlocked.Increment(ref _framesSent);

                    // Sparse luma sample (~1k pixels) - a black-output probe for the status line.
                    var lumaSum = 0L;
                    var step = Math.Max(4, ndiFrame.Length / 4096) & ~3;
                    var count = 0;
                    for (var i = 0; i + 2 < ndiFrame.Length; i += step)
                    {
                        lumaSum += (ndiFrame[i] + ndiFrame[i + 1] * 2 + ndiFrame[i + 2]) >> 2;
                        count++;
                    }

                    Volatile.Write(ref _lastFrameLuma, count > 0 ? (int)(lumaSum / count) : -1);
                }
                else
                {
                    Interlocked.Increment(ref _pollsWithoutFrame);
                }

                nextDeadline += (long)Math.Round(intervalTicks);
                var now = Stopwatch.GetTimestamp();
                if (nextDeadline <= now)
                {
                    // Behind schedule (Android timer slack oversleeps background waits): poll again
                    // immediately to catch up; resync only when hopelessly behind. Matches the
                    // renderer's pacing so slack doesn't compound across the two loops.
                    if (now - nextDeadline > Stopwatch.Frequency)
                        nextDeadline = now;
                    if (token.IsCancellationRequested)
                        break;
                    continue;
                }

                var delay = TimeSpan.FromSeconds((nextDeadline - now) / (double)Stopwatch.Frequency);
                if (token.WaitHandle.WaitOne(delay))
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "HaViz NDI pump faulted - output stopped");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts?.Cancel();
        if (_pumpThread is { } pump && pump.IsAlive && !pump.Join(TimeSpan.FromSeconds(2)))
            _log.LogWarning("HaViz NDI pump did not stop within 2 s");
        _cts?.Dispose();

        _source?.Dispose();
        lock (_audioGate)
            _ndiAudio = null;
        _ndi?.Dispose();
    }
}
