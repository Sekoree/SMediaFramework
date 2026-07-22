using ProjectMLib;
using S.Media.Compositor;

namespace S.Media.Visualizer.ProjectM;

/// <summary>
/// The projectM (Milkdrop) visualizer as an effect-bus visual source: its <see cref="IAudioOutput"/>
/// face is an audio tap (attach to the router / <c>ShowSession.RegisterAudioTapAsync</c>), its
/// <see cref="IVideoSource"/> face is a placeable video source. On a GL (surface-hosting) compositor
/// the session promotes it to a <see cref="ProjectMGlLayerSurface"/> via
/// <see cref="ILayerSurfaceVideoSource"/> - the same NXT-10 seam as the MMD source - and projectM
/// renders GPU-side with zero readback; the frame path then emits cheap transparent frames to keep
/// priming/clock plumbing alive (a CPU compositor shows nothing - the visualizer needs GL).
///
/// <para>Audio flows through an internal SPSC ring (an <see cref="AudioBus"/>), written by the router's
/// pump thread and drained on the compositor thread into <c>projectm_pcm_add_float</c>. Also an
/// <see cref="IBusMetadataSink"/>: attached to the session hub so future preset/overlay logic can react
/// to track changes (unused at render time in v1 - projectM's C API has no text overlay).</para>
/// </summary>
public sealed class ProjectMVisualSource : IAudioVisualSource, ILayerSurfaceVideoSource, IBusMetadataSink, IDisposable
{
    /// <summary>
    /// App-wired factory for a dedicated offscreen GL context (called ON the render thread). When set,
    /// every new source runs the CONTINUOUS mode: projectM renders on its own thread for the source's
    /// whole lifetime, and composition surfaces just blit the latest frame - so the visualizer survives
    /// composition rebuilds (track changes) and a stalling preset load can never block a composition
    /// pump or the session dispatcher. Null (default) keeps the legacy in-composition rendering.
    /// </summary>
    public static Func<S.Media.Compositor.IOffscreenGlContext?>? OffscreenGlContextFactory { get; set; }

    private readonly ProjectMOptions _options;
    private readonly AudioBus _pcmRing;
    private readonly VideoFormat _videoFormat;
    private readonly ProjectMOffscreenRenderer? _renderer;
    private byte[]? _transparentFrame;
    private long _frameIndex;
    private volatile MediaItemMetadata? _currentItem;
    private volatile bool _disposed;
    private volatile int _legacyPresetCount = -1;
    private volatile string? _legacyPresetName;

    public ProjectMVisualSource(int width, int height, Rational frameRate, ProjectMOptions? options = null)
    {
        if (!ProjectMRuntime.IsAvailable)
            throw new InvalidOperationException(
                ProjectMRuntime.UnavailableReason ?? "libprojectM-4 is not available.");

        _options = options ?? new ProjectMOptions();
        _videoFormat = new VideoFormat(
            Math.Clamp(width, 16, 7680) & ~1,
            Math.Clamp(height, 16, 4320) & ~1,
            PixelFormat.Bgra32,
            frameRate.Numerator > 0 ? frameRate : new Rational(30, 1));
        // ~1 s of stereo float at the declared rate: enough that a busy compositor tick never starves
        // projectM's beat detection, small enough that the visuals stay current.
        _pcmRing = new AudioBus(
            new AudioFormat(_options.AudioSampleRate, 2),
            TimeSpan.FromSeconds(1));

        // Continuous mode: the renderer starts NOW and runs until Dispose - independent of any
        // composition. Render at the configured size (options override the placement size).
        if (OffscreenGlContextFactory is { } factory)
        {
            _renderer = new ProjectMOffscreenRenderer(
                this, _options,
                _options.RenderWidth > 0 ? _options.RenderWidth : _videoFormat.Width,
                _options.RenderHeight > 0 ? _options.RenderHeight : _videoFormat.Height,
                _options.Fps > 0 ? _options.Fps : _videoFormat.FrameRate.Numerator / Math.Max(1, _videoFormat.FrameRate.Denominator),
                factory);
        }
    }

    public ProjectMOptions Options => _options;

    /// <summary>Presets found in the configured directory (-1 until enumerated; continuous mode
    /// enumerates on its render thread shortly after construction, legacy mode at first GL configure).</summary>
    public int PresetCount => _renderer?.PresetCount ?? _legacyPresetCount;

    /// <summary>The currently playing preset's file name (null before the first load).</summary>
    public string? CurrentPresetName => _renderer?.CurrentPresetName ?? _legacyPresetName;

    /// <summary>True when this source renders continuously on its own thread (survives track changes).</summary>
    public bool IsContinuous => _renderer is not null;

    /// <summary>True when the continuous renderer came up but then FAILED (offscreen context
    /// creation or GL/projectM init on the render thread). Distinct from <see cref="IsContinuous"/>,
    /// which only says the continuous mode was configured - hosts that surface "visualizer
    /// unavailable" must check this, not infer it from IsContinuous.</summary>
    public bool ContinuousRenderFailed => _renderer?.Failed == true;

    internal void ReportLegacyPresets(int count) => _legacyPresetCount = count;

    internal void ReportLegacyPresetName(string? name) => _legacyPresetName = name;

    /// <summary>Test seam: copies the continuous renderer's newest frame (false in legacy mode).</summary>
    /// <summary>Continuous-mode frame tap for hosts that pump frames themselves (the NDI visualizer
    /// apps): copies the newest rendered frame when it changed since
    /// <paramref name="lastSeenVersion"/>. Pixel layout is <see cref="RenderedFramePixelFormat"/>;
    /// rows are in FBO order - flip vertically for top-down consumers (NDI, files). False in
    /// legacy mode or before the first render.</summary>
    public bool TryCopyLatestRenderedFrame(byte[] destination, ref long lastSeenVersion) =>
        _renderer is not null && _renderer.TryCopyLatestFrame(destination, ref lastSeenVersion);

    /// <summary>Layout of <see cref="TryCopyLatestRenderedFrame"/> frames: BGRA32 on desktop GL,
    /// RGBA32 on GLES (native readback). Stable once the first frame published.</summary>
    public PixelFormat RenderedFramePixelFormat => _renderer?.PublishedPixelFormat ?? PixelFormat.Bgra32;

    internal bool TryCopyLatestFrameForTest(byte[] destination, ref long lastSeenVersion) =>
        _renderer?.TryCopyLatestFrame(destination, ref lastSeenVersion) ?? false;

    /// <summary>What's playing, per the session metadata hub (for future preset/overlay reactions).</summary>
    public MediaItemMetadata? CurrentItem => _currentItem;

    // --- IAudioOutput (the tap) ----------------------------------------------

    public AudioFormat Format => _pcmRing.Format;

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        if (!_disposed)
            _pcmRing.Submit(packedSamples);
    }

    // --- IVideoSource (frame-path fallback; the GL surface is the real render) --

    VideoFormat IVideoSource.Format => _videoFormat;

    public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];

    public bool IsExhausted => _disposed;

    public void SelectOutputFormat(PixelFormat format)
    {
        if (format != PixelFormat.Bgra32)
            throw new NotSupportedException("ProjectMVisualSource emits BGRA32 only.");
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        frame = null!;
        if (_disposed)
            return false;

        // Same trick as MMDVideoSource in surface mode: a cached fully-transparent buffer keeps the
        // transport/priming machinery fed while the GL surface draws the actual pixels. The buffer is
        // shared across frames (zero-copy) - VideoFrame's release is a no-op.
        _transparentFrame ??= new byte[_videoFormat.Width * _videoFormat.Height * 4];
        var index = Interlocked.Increment(ref _frameIndex) - 1;
        var pts = TimeSpan.FromTicks(
            TimeSpan.TicksPerSecond * index * _videoFormat.FrameRate.Denominator / _videoFormat.FrameRate.Numerator);
        frame = new VideoFrame(pts, _videoFormat, [_transparentFrame], [_videoFormat.Width * 4]);
        return true;
    }

    // --- ILayerSurfaceVideoSource (the real GPU render path) -----------------

    /// <summary>Continuous mode: a thin blit of the persistent renderer's frames (a NEW composition picks
    /// the stream up mid-flow - no restart). Legacy mode: projectM renders in the composition's context.</summary>
    public IVideoCompositorLayerSurface CreateLayerSurface() =>
        _renderer is not null
            ? new ProjectMFrameBlitSurface(this, _renderer)
            : new ProjectMGlLayerSurface(this);

    // --- IBusMetadataSink ------------------------------------------------------

    public void OnItemMetadata(MediaItemMetadata metadata) => _currentItem = metadata;

    public void OnFrameStats(in FrameStatsMetadata stats)
    {
        // v1: unused. The hook exists so color-matching (tint presets toward the program's dominant
        // color) can land without touching the wiring.
    }

    /// <summary>Drains up to <paramref name="destination"/>.Length floats of tapped PCM (render thread).</summary>
    internal int DrainPcm(Span<float> destination) => _pcmRing.ReadInto(destination);

    private int _nextPresetRequests;

    /// <summary>Requests a preset advance from any thread (UI hotkey/button); the GL surface consumes
    /// it on its next render.</summary>
    public void RequestNextPreset() => Interlocked.Increment(ref _nextPresetRequests);

    internal bool ConsumeNextPresetRequest()
    {
        while (true)
        {
            var pending = Volatile.Read(ref _nextPresetRequests);
            if (pending <= 0)
                return false;
            if (Interlocked.CompareExchange(ref _nextPresetRequests, 0, pending) == pending)
                return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _renderer?.Dispose(); // stops the continuous render thread + its GL context
    }
}
