using S.Media.Core.Video;
using S.Media.FFmpeg.Video;

namespace HaPlay.Playback;

/// <summary>
/// Prepares frames for <see cref="CpuVideoCompositor"/> / <see cref="CompositorVideoSink"/> slots
/// (BGRA32 layers only).
/// </summary>
internal static class CompositorLayerConverter
{
    /// <summary>
    /// Returns a BGRA32 frame suitable for compositor submission. When conversion is required, the
    /// returned frame is owned by the caller (<paramref name="disposeLayer"/> is <c>true</c>).
    /// </summary>
    public static bool TryToBgraLayer(
        VideoFrame source,
        ref VideoCpuFrameConverter? converter,
        out VideoFrame? layer,
        out bool disposeLayer)
    {
        ArgumentNullException.ThrowIfNull(source);
        layer = null;
        disposeLayer = false;

        var fmt = source.Format;
        if (fmt.PixelFormat == PixelFormat.Bgra32)
        {
            layer = source;
            return true;
        }

        if (!VideoCpuFrameConverter.CanConvert(fmt.PixelFormat, PixelFormat.Bgra32, fmt.Width, fmt.Height))
            return false;

        converter ??= new VideoCpuFrameConverter();
        converter.Configure(fmt.PixelFormat, PixelFormat.Bgra32, fmt.Width, fmt.Height);
        layer = converter.Convert(source, source.ColorTransferHint);
        disposeLayer = true;
        return true;
    }
}
