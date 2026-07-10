using System.Diagnostics.CodeAnalysis;
using S.Media.Core.Video;

namespace S.Media.Compositor;

internal static class CompositorBgraHelper
{
    /// <summary>
    /// Ensures <paramref name="source"/> is BGRA32 for compositing: passes BGRA through unchanged,
    /// otherwise converts via a CPU converter created from <paramref name="converterFactory"/> (the
    /// registry's <c>IMediaRegistry.CreateCpuConverter</c>, P3 - no direct FFmpeg dependency). When no
    /// factory is supplied (no CPU-converter module registered) or the format pair is unsupported, returns
    /// <see langword="false"/> so the caller skips the frame - the compositor still runs GPU/BGRA-only.
    /// </summary>
    public static bool TryToBgra(
        VideoFrame source,
        Func<IVideoCpuFrameConverter>? converterFactory,
        ref IVideoCpuFrameConverter? converter,
        [NotNullWhen(true)] out VideoFrame? layer)
    {
        ArgumentNullException.ThrowIfNull(source);
        layer = null;
        var fmt = source.Format;
        if (fmt.PixelFormat == PixelFormat.Bgra32)
        {
            layer = source;
            return true;
        }

        if (converterFactory is null)
            return false;

        try
        {
            converter ??= converterFactory();
            converter.Configure(fmt.PixelFormat, PixelFormat.Bgra32, fmt.Width, fmt.Height);
            layer = converter.Convert(source, source.ColorTransferHint);
            return true;
        }
        catch (Exception)
        {
            // Unsupported source format / converter failure - equivalent to the old static CanConvert
            // returning false. The caller treats false as "skip this layer frame".
            layer = null;
            return false;
        }
    }
}
