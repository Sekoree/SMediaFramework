using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;

namespace HaPlay.Playback;

internal static class FallbackImageLoader
{
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

    /// <summary>Deep-copies a template so each sink can own its own instance.</summary>
    public static VideoFrame CloneHoldTemplate(VideoFrame template) =>
        VideoCpuFrameConverter.DuplicateCpuBacking(template, template.ColorTransferHint);
}
