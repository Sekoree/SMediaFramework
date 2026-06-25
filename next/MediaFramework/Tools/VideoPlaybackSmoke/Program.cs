// Phase 3 VideoPlaybackSmoke — proves the VideoPlayer salvage: open a video through the registry (FFmpeg,
// no globals), schedule it against a free-running MediaClock, and present frames to a headless sink. We
// count presented frames + track the PTS span via VideoPlayer.FramePresentationTimePresented, so this
// verifies decode -> sync -> scheduled-present without needing a window. Plays a capped wall-clock window.
using System.Diagnostics;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Decode.FFmpeg;
using S.Media.Players;
using S.Media.Time;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: VideoPlaybackSmoke <video-file-or-uri> [seconds=4]");
    return 2;
}

var uri = args[0];
var seconds = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 4.0;

var registry = MediaRegistry.Build(b => b.Use(new FFmpegModule()));
if (!registry.TryOpenVideo(uri, null, out var source))
{
    Console.Error.WriteLine($"FAIL: no decoder opened '{uri}' (registered: {string.Join(", ", registry.Decoders.Select(d => d.Name))}).");
    return 1;
}

Console.WriteLine($"opened {source.Format.Width}x{source.Format.Height} {source.Format.PixelFormat} @ {source.Format.FrameRate.Numerator}/{source.Format.FrameRate.Denominator}");

// Prime the decoder: a HW-decode source only publishes NativePixelFormats after its first frame, which
// VideoPlayer's up-front format negotiation needs. Read + dispose one frame to populate it (smoke nicety;
// the real session warms the decoder before wiring the player).
if (source.NativePixelFormats.Count == 0 && source.TryReadNextFrame(out var warm))
    warm.Dispose();

var output = new DiscardingVideoOutput();
var clock = new MediaClock();
using var player = new VideoPlayer(source, output, clock);

long frames = 0;
var firstPts = TimeSpan.MinValue;
var lastPts = TimeSpan.Zero;
Exception? fault = null;
player.FramePresentationTimePresented += pts =>
{
    if (Interlocked.CompareExchange(ref frames, 0, 0) == 0)
        firstPts = pts;
    Interlocked.Increment(ref frames);
    lastPts = pts;
};
player.Faulted += (_, e) => fault = e.Exception;

clock.Start();
player.Play();

var sw = Stopwatch.StartNew();
while (sw.Elapsed.TotalSeconds < seconds && !player.IsSourceExhausted && fault is null)
{
    Console.Write($"\r{clock.CurrentPosition:mm\\:ss\\.ff}  frames={Interlocked.Read(ref frames)}   ");
    Thread.Sleep(100);
}

Thread.Sleep(150); // let the last scheduled frames drain
var total = Interlocked.Read(ref frames);
Console.WriteLine($"\npresented {total} frames; pts {firstPts:mm\\:ss\\.fff}..{lastPts:mm\\:ss\\.fff}; exhausted={player.IsSourceExhausted}");

if (fault is not null)
{
    Console.Error.WriteLine($"FAIL: player faulted: {fault.Message}");
    return 1;
}

if (total < 10 || lastPts <= firstPts)
{
    Console.Error.WriteLine($"FAIL: expected a steady frame stream with advancing PTS (frames={total}, firstPts={firstPts}, lastPts={lastPts}).");
    return 1;
}

Console.WriteLine("VideoPlaybackSmoke OK — decode -> sync -> present through VideoPlayer, via the registry.");
return 0;
