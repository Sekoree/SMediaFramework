using System.Text;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using S.Media.OpenGL;

namespace VideoPlaybackSmoke;

/// <summary>Formats the one-line <c>[video]</c> routing explanation used by <c>VideoPlaybackSmoke</c>.</summary>
public static class SmokeVideoRouting
{
    public static string FormatLine(
        IVideoSource videoSourceAfterNegotiate,
        VideoPlayer videoPlayer,
        VideoRouter router,
        string? routerInputId)
    {
        var sb = new StringBuilder();
        AppendLine(sb, videoSourceAfterNegotiate, videoPlayer, router, routerInputId);
        return sb.ToString();
    }

    public static void WriteLine(
        TextWriter writer,
        IVideoSource videoSourceAfterNegotiate,
        VideoPlayer videoPlayer,
        VideoRouter router,
        string? routerInputId)
    {
        var sb = new StringBuilder();
        AppendLine(sb, videoSourceAfterNegotiate, videoPlayer, router, routerInputId);
        writer.WriteLine(sb.ToString());
    }

    private static void AppendLine(
        StringBuilder sb,
        IVideoSource videoSourceAfterNegotiate,
        VideoPlayer videoPlayer,
        VideoRouter router,
        string? routerInputId)
    {
        var negotiated = videoPlayer.Format;
        var decoderNative = false;
        for (var i = 0; i < videoSourceAfterNegotiate.NativePixelFormats.Count; i++)
        {
            if (videoSourceAfterNegotiate.NativePixelFormats[i] == negotiated.PixelFormat)
            {
                decoderNative = true;
                break;
            }
        }

        if (!string.IsNullOrEmpty(routerInputId)
            && router.TryGetInputFanOutPixelFormats(routerInputId, out var neg, out var per)
            && per is { Count: > 0 })
        {
            var dec = decoderNative
                ? "decoder emits negotiated pixel format natively"
                : "decoder converts to negotiated format in FFmpeg (codec / sws / upload path)";
            sb.Append("[video] negotiated ")
                .Append(neg.PixelFormat).Append(" @ ").Append(neg.Width).Append('x').Append(neg.Height)
                .Append(" (").Append(dec).Append(')');
            for (var i = 0; i < per.Count; i++)
            {
                var r = per[i];
                var isPrimary = i == 0;
                var label = r.OutputId switch
                {
                    "_primary" => "primary",
                    "sdl" => "SDL local",
                    "ndi" => "NDI",
                    _ => r.OutputId
                };

                string tail;
                if (r.UsesRouterCpuConverter)
                {
                    tail =
                        $"{r.PixelFormat} — router CPU fan-out (VideoCpuFrameConverter / swscale from {neg.PixelFormat})";
                }
                else if (isPrimary)
                {
                    var gl = YuvVideoRenderer.SupportedPixelFormats.Contains(r.PixelFormat);
                    tail = gl
                        ? $"{r.PixelFormat} — same as negotiated; OpenGL shader upload (direct)"
                        : $"{r.PixelFormat} — same as negotiated (check OpenGL / YuvVideoRenderer support)";
                }
                else
                {
                    tail =
                        $"{r.PixelFormat} — no router CPU conversion on this branch (duplicate or shared backing)";
                }

                sb.Append("; ").Append(label).Append(": ").Append(tail);
            }

            return;
        }

        var glDirect = YuvVideoRenderer.SupportedPixelFormats.Contains(negotiated.PixelFormat);
        sb.Append("[video] ")
            .Append(negotiated.PixelFormat).Append(" @ ").Append(negotiated.Width).Append('x').Append(negotiated.Height)
            .Append(" — ")
            .Append(decoderNative
                ? "decoder emits format natively; "
                : "decoder converts to negotiated in FFmpeg; ")
            .Append(glDirect
                ? "SDL GL uses OpenGL shader upload (direct)."
                : "SDL GL: verify OpenGL support for this pixel format.");
    }
}
