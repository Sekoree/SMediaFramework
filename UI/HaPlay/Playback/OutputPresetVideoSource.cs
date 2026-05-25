using HaPlay.Models;
using S.Media.Core.Video;
using S.Media.Effects;

namespace HaPlay.Playback;

/// <summary>
/// Scales decoder video into a fixed program raster via <see cref="VideoCompositor"/>.
/// </summary>
internal sealed class OutputPresetVideoSource : IVideoSource, IDisposable
{
    private readonly IVideoSource _inner;
    private readonly VideoCompositor _compositor;
    private readonly bool _disposeInner;
    private bool _disposed;

    public OutputPresetVideoSource(IVideoSource inner, VideoFormat target, bool disposeInner = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _disposeInner = disposeInner;
        _compositor = VideoCompositor.Create(target, VideoCompositorBackend.Cpu);
        _compositor.AddLayer(inner, LayerConfig.Background);
    }

    public VideoFormat Format => _compositor.Format;

    public IReadOnlyList<PixelFormat> NativePixelFormats => _compositor.NativePixelFormats;

    public bool IsExhausted => _disposed || _inner.IsExhausted;

    public void SelectOutputFormat(PixelFormat format) => _compositor.SelectOutputFormat(format);

    public bool TryReadNextFrame(out VideoFrame frame) => _compositor.TryReadNextFrame(out frame);

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
        try { _compositor.Dispose(); }
        catch { /* best effort */ }
        if (_disposeInner)
        {
            try { (_inner as IDisposable)?.Dispose(); }
            catch { /* best effort */ }
        }
    }
}
