using S.Media.Core.Video;
using S.Media.FFmpeg.Video;

namespace S.Media.Effects;

internal static class CompositorBgraHelper
{
    public static bool TryToBgra(
        VideoFrame source,
        ref VideoCpuFrameConverter? converter,
        out VideoFrame? layer)
    {
        ArgumentNullException.ThrowIfNull(source);
        layer = null;
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
        return true;
    }
}
