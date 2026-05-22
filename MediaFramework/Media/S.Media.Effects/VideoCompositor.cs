using S.Media.Core.Clock;
using S.Media.Effects.OpenGL;

namespace S.Media.Effects;

/// <summary>
/// Declarative multi-layer video compositor: <see cref="LayerConfig"/> + <see cref="Transition"/>
/// on top of <see cref="VideoCompositorSource"/>.
/// </summary>
public sealed class VideoCompositor : IVideoSource, IDisposable
{
    private readonly VideoCompositorSource _source;
    private readonly List<LayerHandle> _layers = [];
    private readonly VideoFormat _output;
    private readonly DateTime _wallStart = DateTime.UtcNow;
    private bool _disposed;

    private VideoCompositor(VideoFormat output, IVideoCompositor compositor)
    {
        _output = output;
        _source = new VideoCompositorSource(output, compositor, disposeCompositorOnDispose: true);
    }

    public VideoFormat Format => _source.Format;

    public IReadOnlyList<S.Media.Core.Video.PixelFormat> NativePixelFormats => _source.NativePixelFormats;

    public bool IsExhausted => _disposed || _source.IsExhausted;

    public IPlaybackClock? Clock { get; set; }

    public IReadOnlyList<LayerHandle> Layers => _layers;

    internal TimeSpan TimelinePosition =>
        Clock?.ElapsedSinceStart ?? DateTime.UtcNow - _wallStart;

    public static VideoCompositor Create(
        VideoFormat output,
        VideoCompositorBackend backend = VideoCompositorBackend.Auto,
        VideoCompositorOptions? options = null)
    {
        options ??= new VideoCompositorOptions();
        IVideoCompositor compositor = backend switch
        {
            VideoCompositorBackend.Cpu => new CpuVideoCompositor(output, options.CpuSampling),
            VideoCompositorBackend.Gl when options.Gl is not null =>
                new GlVideoCompositor(options.Gl, output, options.GlOutputPrecision),
            VideoCompositorBackend.Gl =>
                throw new ArgumentException("VideoCompositorOptions.Gl is required for the Gl backend.", nameof(options)),
            _ => new CpuVideoCompositor(output, options.CpuSampling),
        };
        return new VideoCompositor(output, compositor);
    }

    public LayerHandle AddLayer(IVideoSource source, LayerConfig config)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var slot = _source.AddSlot();
        var handle = new LayerHandle(this, source, slot, config);
        _layers.Add(handle);
        return handle;
    }

    public bool RemoveLayer(LayerHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!_layers.Remove(handle))
            return false;
        return _source.RemoveSlot(handle.Slot.Id);
    }

    public void SelectOutputFormat(S.Media.Core.Video.PixelFormat format) => _source.SelectOutputFormat(format);

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var layer in _layers)
            layer.TryPullFrame(_output, out _, out _);
        return _source.TryReadNextFrame(out frame);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _layers.Clear();
        _source.Dispose();
    }

}
