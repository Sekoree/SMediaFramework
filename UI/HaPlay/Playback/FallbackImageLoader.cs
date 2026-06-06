using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;

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
            var info = Image.Identify(path);
            if (info is null || info.Width <= 0 || info.Height <= 0)
                return false;
            width = info.Width;
            height = info.Height;
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
            using var img = Image.Load<Rgba32>(path);
            img.Mutate(m => m.Resize(new ResizeOptions
            {
                Size = new Size(target.Width, target.Height),
                Mode = ResizeMode.Pad,
                PadColor = Color.Black,
            }));

            var srcFmt = new VideoFormat(target.Width, target.Height, PixelFormat.Bgra32, target.FrameRate);
            var stride = target.Width * 4;
            var bgra = new byte[stride * target.Height];
            img.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    var dst = bgra.AsSpan(y * stride, stride);
                    for (var x = 0; x < row.Length; x++)
                    {
                        var p = row[x];
                        var o = x * 4;
                        dst[o] = p.B;
                        dst[o + 1] = p.G;
                        dst[o + 2] = p.R;
                        dst[o + 3] = p.A;
                    }
                }
            });

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
            using var img = new Image<Rgba32>(target.Width, target.Height, new Rgba32(0, 0, 0, 255));

            var srcFmt = new VideoFormat(target.Width, target.Height, PixelFormat.Bgra32, target.FrameRate);
            var stride = target.Width * 4;
            var bgra = new byte[stride * target.Height];
            img.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    var dst = bgra.AsSpan(y * stride, stride);
                    for (var x = 0; x < row.Length; x++)
                    {
                        var p = row[x];
                        var o = x * 4;
                        dst[o] = p.B;
                        dst[o + 1] = p.G;
                        dst[o + 2] = p.R;
                        dst[o + 3] = p.A;
                    }
                }
            });

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
            using var img = Image.Load<Rgba32>(path);
            var width = img.Width;
            var height = img.Height;
            var fmt = new VideoFormat(width, height, PixelFormat.Bgra32, frameRate);
            var stride = width * 4;
            var bgra = new byte[stride * height];
            img.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    var dst = bgra.AsSpan(y * stride, stride);
                    for (var x = 0; x < row.Length; x++)
                    {
                        var p = row[x];
                        var o = x * 4;
                        dst[o] = p.B;
                        dst[o + 1] = p.G;
                        dst[o + 2] = p.R;
                        dst[o + 3] = p.A;
                    }
                }
            });
            using var bgraFrame = new VideoFrame(TimeSpan.Zero, fmt, bgra, stride, release: null);
            return VideoCpuFrameConverter.DuplicateCpuBacking(bgraFrame, default);
        }
        catch
        {
            return null;
        }
    }
}
