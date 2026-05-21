using HaPlay.Models;
using S.Media.Core.Video;

namespace HaPlay.Playback;

/// <summary>
/// Scales decoder video into a fixed program raster via <see cref="CpuVideoCompositor"/>.
/// </summary>
internal sealed class OutputPresetVideoSource : IVideoSource, IDisposable
{
    private readonly IVideoSource _inner;
    private readonly CompositorVideoSink _programSink;
    private readonly CompositorVideoSink.Slot _slot;
    private readonly LayerTransform2D _transform;
    private readonly bool _disposeInner;
    private bool _disposed;

    public OutputPresetVideoSource(IVideoSource inner, VideoFormat target, bool disposeInner = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _disposeInner = disposeInner;
        var compositor = new CpuVideoCompositor(target);
        _programSink = new CompositorVideoSink(target, compositor, disposeCompositorOnDispose: true);
        _slot = _programSink.AddSlot();
        _transform = OutputPresetFormats.LetterboxTransform(inner.Format, target);
        _slot.Transform = _transform;
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

        try
        {
            _slot.Sink.Configure(src.Format);
            _slot.Sink.Submit(src);
        }
        finally
        {
            src.Dispose();
        }

        return _programSink.TryReadNextFrame(out frame);
    }

    public static IVideoSource? WrapForPreset(
        IVideoSource decoderVideo,
        PlayerOutputPreset preset,
        out IDisposable? ownedWrapper)
    {
        ownedWrapper = null;
        if (!OutputPresetFormats.TryResolve(preset, decoderVideo.Format.FrameRate, out var target))
            return null;

        var wrapped = new OutputPresetVideoSource(decoderVideo, target, disposeInner: false);
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
        if (_disposeInner)
        {
            try { (_inner as IDisposable)?.Dispose(); }
            catch { /* best effort */ }
        }
    }
}
