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
    private readonly object _layersGate = new();
    private readonly List<LayerHandle> _layers = [];
    private readonly VideoFormat _output;
    private readonly long _framePeriodTicks;
    private long _compositeReads;
    private bool _disposed;

    private VideoCompositor(VideoFormat output, IVideoCompositor compositor)
    {
        _output = output;
        _framePeriodTicks = FramePeriodTicks(output.FrameRate);
        _source = new VideoCompositorSource(output, compositor, disposeCompositorOnDispose: true);
    }

    public VideoFormat Format => _source.Format;

    public IReadOnlyList<S.Media.Core.Video.PixelFormat> NativePixelFormats => _source.NativePixelFormats;

    public bool IsExhausted => _disposed || _source.IsExhausted;

    public IPlaybackClock? Clock { get; set; }

    public IReadOnlyList<LayerHandle> Layers { get { lock (_layersGate) return _layers.ToArray(); } }

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
                TryCreateRegisteredBackend(output, options.AutoBackends, out var error)
                ?? throw new ArgumentException(
                    $"No registered GL compositor backend could create {output}. " +
                    "Provide VideoCompositorOptions.Gl / VideoCompositorOptions.AutoBackends or register a host backend such as S.Media.SDL3. " +
                    (string.IsNullOrWhiteSpace(error) ? string.Empty : $"Last error: {error}"),
                    nameof(options)),
            _ => TryCreateRegisteredBackend(output, options.AutoBackends, out _) ?? new CpuVideoCompositor(output, options.CpuSampling),
        };
        return new VideoCompositor(output, compositor);
    }

    public LayerHandle AddLayer(IVideoSource source, LayerConfig config)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_layersGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var slot = _source.AddSlot();
            var handle = new LayerHandle(this, source, slot, config);
            _layers.Add(handle);
            return handle;
        }
    }

    public bool RemoveLayer(LayerHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        bool removed;
        lock (_layersGate)
        {
            if (_disposed)
                return false;
            if (!_layers.Remove(handle))
                return false;
            removed = _source.RemoveSlot(handle.Slot.Id);
        }
        handle.Close(); // dispose the layer's look-ahead buffer + converter
        return removed;
    }

    public void SelectOutputFormat(S.Media.Core.Video.PixelFormat format) => _source.SelectOutputFormat(format);

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LayerHandle[] layers;
        lock (_layersGate)
            layers = _layers.ToArray();
        if (Clock is { } clock)
        {
            // Timeline mode: every layer advances to the master clock rather than by one frame per
            // downstream read — so a downstream player prebuffering its queue no longer races every
            // layer forward at decode speed, and each layer shows the frame whose interval contains the
            // clock position (frame-accurate). The composite is stamped with the master time.
            var masterTime = clock.ElapsedSinceStart;
            foreach (var layer in layers)
                layer.AdvanceTo(masterTime, _output);
            return _source.TryReadNextFrame(masterTime, out frame);
        }

        // Read-paced mode (no clock): exactly one inner frame per read — preserves a single-layer
        // scaler/adapter's 1:1 passthrough (e.g. HaPlay's OutputPresetVideoSource) with no PTS-grid
        // drift. Transitions resolve against a synthetic read-index timeline; output PTS stays synthetic.
        var readTime = TimeSpan.FromTicks(_framePeriodTicks * _compositeReads);
        _compositeReads++;
        foreach (var layer in layers)
            layer.PullOneAndSubmit(readTime, _output);
        return _source.TryReadNextFrame(out frame);
    }

    public void Dispose()
    {
        LayerHandle[] layers;
        lock (_layersGate)
        {
            if (_disposed) return;
            _disposed = true;
            layers = _layers.ToArray();
            _layers.Clear();
        }
        foreach (var layer in layers)
            layer.Close();
        _source.Dispose();
    }

    private static long FramePeriodTicks(S.Media.Core.Video.Rational frameRate)
    {
        if (frameRate.Numerator <= 0 || frameRate.Denominator <= 0)
            return TimeSpan.FromMilliseconds(33).Ticks;
        return TimeSpan.FromSeconds((double)frameRate.Denominator / frameRate.Numerator).Ticks;
    }

    private static IVideoCompositor? TryCreateRegisteredBackend(
        VideoFormat output,
        IReadOnlyList<VideoCompositorBackendFactory>? perCallBackends,
        out string? error)
    {
        error = null;
        VideoCompositorBackendFactory[] registered;
        lock (AutoBackendGate)
            registered = AutoBackendFactories.ToArray();

        // Per-call (session-scoped) backends take precedence over process-wide registered ones.
        IEnumerable<VideoCompositorBackendFactory> factories = perCallBackends is { Count: > 0 }
            ? [.. perCallBackends, .. registered]
            : registered;

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
