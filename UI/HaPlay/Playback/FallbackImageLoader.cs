using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using StbImageSharp;

namespace HaPlay.Playback;

internal static class FallbackImageLoader
{
    /// <summary>Reads an image's pixel dimensions without decoding the whole file. Returns false on
    /// any failure (missing / unreadable / unsupported). Used to size a still-image cue's placement.</summary>
    public static bool TryGetImageSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        try
        {
            using var stream = File.OpenRead(path);
            var info = ImageInfo.FromStream(stream);
            if (info is null || info.Value.Width <= 0 || info.Value.Height <= 0)
                return false;
            width = info.Value.Width;
            height = info.Value.Height;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds a CPU-backed template frame matching <paramref name="target"/> (pixel format + size).
    /// The image is scaled to <strong>fit inside</strong> the target rectangle (aspect preserved) and padded with black
    /// so there is no stretch distortion (square art on a 16:9 output gets letterboxing).
    /// </summary>
    public static VideoFrame? TryBuildHoldCpuFrame(VideoFormat target, string path)
    {
        try
        {
            FFmpegRuntime.EnsureInitialized();
            var image = LoadRgba(path);
            var srcFmt = new VideoFormat(target.Width, target.Height, PixelFormat.Bgra32, target.FrameRate);
            var stride = target.Width * 4;
            var bgra = new byte[stride * target.Height];
            CopyLetterboxedRgbaToBgra(image.Data, image.Width, image.Height, bgra, target.Width, target.Height);

            using var bgraFrame = new VideoFrame(TimeSpan.Zero, srcFmt, bgra, stride, release: null);
            if (target.PixelFormat == PixelFormat.Bgra32)
                return VideoCpuFrameConverter.DuplicateCpuBacking(bgraFrame, default);

            if (!VideoCpuFrameConverter.CanConvert(PixelFormat.Bgra32, target.PixelFormat, target.Width, target.Height))
                return null;

            using var conv = new VideoCpuFrameConverter();
            conv.Configure(PixelFormat.Bgra32, target.PixelFormat, target.Width, target.Height);
            using var converted = conv.Convert(bgraFrame, default);
            return VideoCpuFrameConverter.DuplicateCpuBacking(converted, converted.ColorTransferHint);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Opaque black frame in <paramref name="target"/> pixel format (for NDI/SDL priming).</summary>
    public static VideoFrame? TrySolidCpuFrame(VideoFormat target, TimeSpan presentationTime)
    {
        try
        {
            FFmpegRuntime.EnsureInitialized();
            var srcFmt = new VideoFormat(target.Width, target.Height, PixelFormat.Bgra32, target.FrameRate);
            var stride = target.Width * 4;
            var bgra = new byte[stride * target.Height];
            FillOpaqueBlack(bgra);

            using var bgraFrame = new VideoFrame(presentationTime, srcFmt, bgra, stride, release: null);
            if (target.PixelFormat == PixelFormat.Bgra32)
                return VideoCpuFrameConverter.DuplicateCpuBacking(bgraFrame, default);

            if (!VideoCpuFrameConverter.CanConvert(PixelFormat.Bgra32, target.PixelFormat, target.Width, target.Height))
                return null;

            using var conv = new VideoCpuFrameConverter();
            conv.Configure(PixelFormat.Bgra32, target.PixelFormat, target.Width, target.Height);
            using var converted = conv.Convert(bgraFrame, default);
            return VideoCpuFrameConverter.DuplicateCpuBacking(converted, converted.ColorTransferHint);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Deep-copies a template so each output can own its own instance.</summary>
    public static VideoFrame CloneHoldTemplate(VideoFrame template) =>
        VideoCpuFrameConverter.DuplicateCpuBacking(template, template.ColorTransferHint);

    /// <summary>
    /// Loads an image at its <strong>native</strong> dimensions as BGRA32 and returns a CPU video frame
    /// at that resolution + framerate hint. Used by the "Hold image" feature so video outputs can be
    /// resized to the image instead of the image being letterboxed into the media's negotiated format.
    /// Returns <c>null</c> on load / format failure.
    /// </summary>
    public static VideoFrame? TryBuildHoldFrameAtImageSize(string path, S.Media.Core.Video.Rational frameRate)
    {
        try
        {
            FFmpegRuntime.EnsureInitialized();
            var image = LoadRgba(path);
            var width = image.Width;
            var height = image.Height;
            var fmt = new VideoFormat(width, height, PixelFormat.Bgra32, frameRate);
            var stride = width * 4;
            var bgra = new byte[stride * height];
            CopyRgbaToBgra(image.Data, width, height, bgra);
            using var bgraFrame = new VideoFrame(TimeSpan.Zero, fmt, bgra, stride, release: null);
            return VideoCpuFrameConverter.DuplicateCpuBacking(bgraFrame, default);
        }
        catch
        {
            return null;
        }
    }

    private static ImageResult LoadRgba(string path)
    {
        using var stream = File.OpenRead(path);
        return ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
    }

    private static void CopyRgbaToBgra(byte[] rgba, int width, int height, byte[] bgra)
    {
        var pixelCount = checked(width * height);
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            bgra[o] = rgba[o + 2];
            bgra[o + 1] = rgba[o + 1];
            bgra[o + 2] = rgba[o];
            bgra[o + 3] = rgba[o + 3];
        }
    }

    private static void CopyLetterboxedRgbaToBgra(byte[] rgba, int srcWidth, int srcHeight, byte[] bgra, int dstWidth, int dstHeight)
    {
        FillOpaqueBlack(bgra);
        if (srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0)
            return;

        var scale = Math.Min(dstWidth / (double)srcWidth, dstHeight / (double)srcHeight);
        var scaledWidth = Math.Max(1, (int)Math.Round(srcWidth * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(srcHeight * scale));
        var offsetX = (dstWidth - scaledWidth) / 2;
        var offsetY = (dstHeight - scaledHeight) / 2;

        for (var y = 0; y < scaledHeight; y++)
        {
            var srcY = Math.Min(srcHeight - 1, (int)((y + 0.5) * srcHeight / scaledHeight));
            for (var x = 0; x < scaledWidth; x++)
            {
                var srcX = Math.Min(srcWidth - 1, (int)((x + 0.5) * srcWidth / scaledWidth));
                var src = ((srcY * srcWidth) + srcX) * 4;
                var dst = (((offsetY + y) * dstWidth) + offsetX + x) * 4;
                bgra[dst] = rgba[src + 2];
                bgra[dst + 1] = rgba[src + 1];
                bgra[dst + 2] = rgba[src];
                bgra[dst + 3] = rgba[src + 3];
            }
        }
    }

    private static void FillOpaqueBlack(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
            bgra[i + 3] = 255;
    }
}
