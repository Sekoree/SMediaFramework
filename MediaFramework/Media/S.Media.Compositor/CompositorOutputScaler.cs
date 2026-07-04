namespace S.Media.Compositor;

/// <summary>
/// One-shot CPU compositor for scaling/letterboxing a single frame into a fixed output raster.
/// Used by output-side wrappers (NDI format lock, logo template render) that receive frames via
/// <see cref="IVideoOutput.Submit"/> rather than pulling from <see cref="IVideoSource"/>.
/// </summary>
public sealed class CompositorOutputScaler : IDisposable
{
    private readonly VideoCompositorSource _source;
    private readonly VideoCompositorSource.Slot _slot;
    private LayerConfig _config;
    private readonly VideoFormat _output;
    private readonly Func<IVideoCpuFrameConverter>? _cpuConverterFactory;
    private IVideoCpuFrameConverter? _toBgra;
    private bool _disposed;

    public CompositorOutputScaler(
        VideoFormat output,
        LayerConfig? config = null,
        Func<IVideoCpuFrameConverter>? cpuConverterFactory = null)
    {
        _output = output;
        _config = config ?? LayerConfig.Background;
        _cpuConverterFactory = cpuConverterFactory;
        var compositor = new CpuVideoCompositor(output);
        _source = new VideoCompositorSource(output, compositor, disposeCompositorOnDispose: true);
        _slot = _source.AddSlot();
    }

    public LayerConfig Config
    {
        get => _config;
        set => _config = value;
    }

    public VideoFormat OutputFormat => _output;

    /// <summary>Letterboxes <paramref name="input"/> into <see cref="OutputFormat"/> using <see cref="Config"/>.</summary>
    public bool TryComposite(VideoFrame input, out VideoFrame? output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        output = null;
        ArgumentNullException.ThrowIfNull(input);

        var ownsInput = input.Format.PixelFormat != PixelFormat.Bgra32;
        if (!CompositorBgraHelper.TryToBgra(input, _cpuConverterFactory, ref _toBgra, out var layer) || layer is null)
            return false;

        if (ownsInput)
            input.Dispose();

        _slot.Transform = LayerConfigResolver.ToTransform(_config, layer.Format, _output);
        _slot.Opacity = _config.Opacity;
        _slot.BlendMode = _config.Blend;
        _slot.Output.Configure(layer.Format);
        _slot.Output.Submit(layer);

        if (!_source.TryReadNextFrame(out var composed))
            return false;

        output = composed;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.Dispose();
        _toBgra?.Dispose();
        _toBgra = null;
    }
}
