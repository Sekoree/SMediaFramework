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
    private readonly ProjectMOptions _options;
    private readonly AudioBus _pcmRing;
    private readonly VideoFormat _videoFormat;
    private byte[]? _transparentFrame;
    private long _frameIndex;
    private volatile MediaItemMetadata? _currentItem;
    private volatile bool _disposed;

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
    }

    public ProjectMOptions Options => _options;

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

    public IVideoCompositorLayerSurface CreateLayerSurface() => new ProjectMGlLayerSurface(this);

    // --- IBusMetadataSink ------------------------------------------------------

    public void OnItemMetadata(MediaItemMetadata metadata) => _currentItem = metadata;

    public void OnFrameStats(in FrameStatsMetadata stats)
    {
        // v1: unused. The hook exists so color-matching (tint presets toward the program's dominant
        // color) can land without touching the wiring.
    }

    /// <summary>Drains up to <paramref name="destination"/>.Length floats of tapped PCM (render thread).</summary>
    internal int DrainPcm(Span<float> destination) => _pcmRing.ReadInto(destination);

    public void Dispose() => _disposed = true;
}
