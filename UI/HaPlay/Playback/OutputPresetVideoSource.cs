using HaPlay.Models;
using S.Media.Core.Video;
using S.Media.FFmpeg.Video;

namespace HaPlay.Playback;

/// <summary>
/// Scales decoder video into a fixed program raster via <see cref="CpuVideoCompositor"/>.
/// </summary>
internal sealed class OutputPresetVideoSource : IVideoSource, IDisposable
{
    private readonly IVideoSource _inner;
    private readonly CompositorVideoSink _programSink;
    private readonly CompositorVideoSink.Slot _slot;
    private readonly VideoFormat _target;
    private readonly bool _disposeInner;
    private VideoCpuFrameConverter? _toBgra;
    private int _lastLayerWidth;
    private int _lastLayerHeight;
    private bool _disposed;

    public OutputPresetVideoSource(IVideoSource inner, VideoFormat target, bool disposeInner = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _disposeInner = disposeInner;
        _target = target;
        var compositor = new CpuVideoCompositor(target);
        _programSink = new CompositorVideoSink(target, compositor, disposeCompositorOnDispose: true);
        _slot = _programSink.AddSlot();
        _slot.Transform = OutputPresetFormats.LetterboxTransform(inner.Format, target);
        _lastLayerWidth = inner.Format.Width;
        _lastLayerHeight = inner.Format.Height;
    }

    public VideoFormat Format => _programSink.Format;

    public IReadOnlyList<PixelFormat> NativePixelFormats => _programSink.NativePixelFormats;

    public bool IsExhausted => _disposed || _inner.IsExhausted;

    public void SelectOutputFormat(PixelFormat format) => _programSink.SelectOutputFormat(format);

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = default!;
        if (!_inner.TryReadNextFrame(out var src))
            return false;

        // CpuVideoCompositor accepts BGRA32 layers only — convert NDI/file UYVY (etc.) first.
        if (!CompositorLayerConverter.TryToBgraLayer(src, ref _toBgra, out var layer, out var convertedToBgra)
            || layer is null)
        {
            src.Dispose();
            frame = default!;
            return false;
        }

        if (convertedToBgra)
            src.Dispose();

        if (layer.Format.Width != _lastLayerWidth || layer.Format.Height != _lastLayerHeight)
        {
            _lastLayerWidth = layer.Format.Width;
            _lastLayerHeight = layer.Format.Height;
            _slot.Transform = OutputPresetFormats.LetterboxTransform(layer.Format, _target);
        }

        _slot.Sink.Configure(layer.Format);
        // Slot ownership contract: Submit transfers frame ownership to the slot (disposed by compositor).
        _slot.Sink.Submit(layer);
        return _programSink.TryReadNextFrame(out frame);
    }

    public static IVideoSource? WrapForPreset(
        IVideoSource decoderVideo,
        PlayerOutputPreset preset,
        out IDisposable? ownedWrapper,
        int customWidth = 0,
        int customHeight = 0,
        bool disposeInnerOnWrapperDispose = false)
    {
        ownedWrapper = null;
        if (!OutputPresetFormats.TryResolve(preset, decoderVideo.Format.FrameRate, out var target, customWidth, customHeight))
            return null;

        var wrapped = new OutputPresetVideoSource(decoderVideo, target, disposeInner: disposeInnerOnWrapperDispose);
        ownedWrapper = wrapped;
        return wrapped;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _programSink.Dispose(); }
        catch { /* best effort */ }
        try { _toBgra?.Dispose(); }
        catch { /* best effort */ }
        _toBgra = null;
        if (_disposeInner)
        {
            try { (_inner as IDisposable)?.Dispose(); }
            catch { /* best effort */ }
        }
    }
}
