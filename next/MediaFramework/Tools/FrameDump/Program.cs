using System.Linq;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Decode.FFmpeg;

// Phase 2 video-decode validation: registry → FFmpeg decode → pull frames (no display; on-screen video
// is Phase 3). Confirms the decode path and pixel-format/PTS plumbing work end-to-end.
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: FrameDump <media-file-or-uri> [maxFrames]");
    return 2;
}

var registry = MediaRegistry.Build(b => b.Use(new FFmpegModule()));

if (!registry.TryOpenVideo(args[0], null, out var video))
{
    Console.Error.WriteLine($"no decoder/video track for '{args[0]}'");
    return 3;
}

var max = args.Length > 1 && int.TryParse(args[1], out var m) ? m : 30;
var count = 0;
using (video as IDisposable)
{
    while (count < max && video.TryReadNextFrame(out var frame))
    {
        using (frame)
            Console.WriteLine($"frame {count,4}: {frame.Format.Width}x{frame.Format.Height} {frame.Format.PixelFormat} pts={frame.PresentationTime:mm\\:ss\\.fff}");
        count++;
    }
}

Console.WriteLine($"decoded {count} frame(s).");
return 0;
