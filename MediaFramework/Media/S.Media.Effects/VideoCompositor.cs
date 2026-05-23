using S.Media.Core.Clock;
using S.Media.Effects.OpenGL;

namespace S.Media.Effects;

/// <summary>
/// Declarative multi-layer video compositor: <see cref="LayerConfig"/> + <see cref="Transition"/>
/// on top of <see cref="VideoCompositorSource"/>.
/// </summary>
public sealed class VideoCompositor : IVideoSource, IDisposable
{
    private static readonly Lock AutoBackendGate = new();
    private static readonly List<VideoCompositorBackendFactory> AutoBackendFactories = [];

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

    /// <summary>
    /// Registers an optional process-wide compositor backend used by <see cref="VideoCompositorBackend.Auto"/>.
    /// Host modules such as SDL register here because they own windowing/context creation.
    /// </summary>
    /// <remarks>
    /// Factories are tried in registration order. Returning <see langword="false"/> leaves room for the
    /// next factory or the CPU fallback; throwing is treated as a failed probe for <c>Auto</c>.
    /// Dispose the returned registration to remove the backend.
    /// </remarks>
    public static IDisposable RegisterAutoBackend(VideoCompositorBackendFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (AutoBackendGate)
            AutoBackendFactories.Add(factory);
        return new AutoBackendRegistration(factory);
    }

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
                TryCreateRegisteredBackend(output, out var error)
                ?? throw new ArgumentException(
                    $"No registered GL compositor backend could create {output}. " +
                    "Provide VideoCompositorOptions.Gl or register a host backend such as S.Media.SDL3. " +
                    (string.IsNullOrWhiteSpace(error) ? string.Empty : $"Last error: {error}"),
                    nameof(options)),
            _ => TryCreateRegisteredBackend(output, out _) ?? new CpuVideoCompositor(output, options.CpuSampling),
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

    private static IVideoCompositor? TryCreateRegisteredBackend(VideoFormat output, out string? error)
    {
        error = null;
        VideoCompositorBackendFactory[] factories;
        lock (AutoBackendGate)
            factories = AutoBackendFactories.ToArray();

        var errors = new List<string>();
        foreach (var factory in factories)
        {
            try
            {
                if (factory(output, out var compositor, out var candidateError) && compositor is not null)
                    return compositor;
                if (!string.IsNullOrWhiteSpace(candidateError))
                    errors.Add(candidateError);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        error = errors.Count == 0 ? null : string.Join("; ", errors);
        return null;
    }

    private sealed class AutoBackendRegistration(VideoCompositorBackendFactory factory) : IDisposable
    {
        private VideoCompositorBackendFactory? _factory = factory;

        public void Dispose()
        {
            var f = Interlocked.Exchange(ref _factory, null);
            if (f is null)
                return;
            lock (AutoBackendGate)
                AutoBackendFactories.Remove(f);
        }
    }
}

public delegate bool VideoCompositorBackendFactory(
    VideoFormat output,
    out IVideoCompositor? compositor,
    out string? error);
