// SubtitleDecodeSmoke — the unified subtitle path. Text (sidecar SRT/VTT/MicroDVD/SAMI/… or an in-container
// stream) is FFmpeg-decoded to ASS events and rendered by libass; bitmap subs (PGS/DVB/VobSub) are FFmpeg-decoded
// to images and composited directly. Either way it renders an overlay and checks for visible pixels.
using S.Media.Core.Video;
using S.Media.Decode.FFmpeg;
using S.Media.Interop;
using S.Media.Subtitles;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: SubtitleDecodeSmoke <subtitle-or-container-file> [streamIndex]");
    return 1;
}

var path = args[0];
var streamIndex = args.Length > 1 && int.TryParse(args[1], out var si) ? si : -1;
const int W = 1280, H = 720;

IVideoOverlaySource source;
TimeSpan when;

// Text first (FFmpeg converts every text format + ASS → ASS events); fall back to bitmap (PGS/DVB/VobSub).
var text = FFmpegSubtitleDecoder.Decode(path, streamIndex);
if (text.Events.Count > 0)
{
    var events = text.Events.Select(e => new AssEventChunk(e.Body, e.StartMs, e.DurationMs)).ToList();
    var fonts = text.Fonts.Select(f => new AssFontAttachment(f.Name, f.Data)).ToList();
    source = new AssSubtitleLayerSource(W, H, text.Header, events, fonts);
    var first = text.Events[0];
    when = TimeSpan.FromMilliseconds(first.StartMs + Math.Max(1, first.DurationMs / 2));
    Console.WriteLine($"decoded '{Path.GetFileName(path)}': TEXT — {text.Events.Count} events, {text.Fonts.Count} fonts → libass");
}
else
{
    var bmp = FFmpegBitmapSubtitleDecoder.Decode(path, streamIndex);
    if (bmp.Cues.Count == 0)
    {
        Console.Error.WriteLine("FAIL: no text events and no bitmap cues");
        return 2;
    }

    source = new BitmapSubtitleLayerSource(bmp);
    var first = bmp.Cues[0];
    when = TimeSpan.FromMilliseconds(first.StartMs + Math.Max(1, (first.EndMs - first.StartMs) / 2));
    Console.WriteLine($"decoded '{Path.GetFileName(path)}': BITMAP — {bmp.Cues.Count} cues @ {bmp.Width}x{bmp.Height} → composite");
}

var frame = source.RenderAt(when);
var visible = 0;
if (frame is not null)
{
    var span = frame.Planes[0].Span;
    for (var i = 3; i < span.Length; i += 4)
        if (span[i] != 0)
            visible++;
}

source.Dispose();
Console.WriteLine($"rendered @ {when.TotalSeconds:F2}s: {visible} visible px");
if (visible == 0)
{
    Console.Error.WriteLine("FAIL: no visible pixels rendered");
    return 3;
}

Console.WriteLine("SubtitleDecodeSmoke OK — subtitle decoded + rendered.");
return 0;
