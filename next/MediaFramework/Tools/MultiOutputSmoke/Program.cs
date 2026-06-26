using System.Diagnostics;
using S.Media.Core.Registry;
using S.Media.Decode.FFmpeg;
using S.Media.Players;
using S.Media.Present.SDL3;

// Phase 5 multi-output smoke. Opens one source through the registry (FFmpeg) and fans its video to TWO
// independent SDL3 GL windows via MediaPlayer.AttachVideoOutput → VideoRouter (per-branch negotiation, works
// with hardware decode). Both outputs present on the same VideoTick off the one master clock, so they are
// phase-locked — the multi-output path from Doc 03 §5. Needs a display.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: MultiOutputSmoke <media-file-or-uri> [seconds=30]");
    return 2;
}

var uri = args[0];
var seconds = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 30.0;

var registry = MediaRegistry.Build(b => b.Use(new FFmpegModule()));

// Software decode so the source publishes its pixel format up front (a hardware decoder only does so after
// its first frame, which the player's up-front negotiation needs primed). The router then fans CPU frames
// to two INDEPENDENT outputs (each does its own GL upload — no shared-texture constraint).
using var player = MediaPlayer.Open(registry, uri, new MediaPlayerOpenOptions { TryHardwareAcceleration = false });
Console.WriteLine($"opened '{uri}' — fanning to two phase-locked windows for {seconds:0}s or until closed…");

using var out1 = new SDL3GLVideoOutput("Multi-Output 1/2", 960, 540);
using var out2 = new SDL3GLVideoOutput("Multi-Output 2/2", 960, 540);
var closed = false;
out1.CloseRequested += (_, _) => closed = true;
out2.CloseRequested += (_, _) => closed = true;

player.AttachVideoOutput(out1, "win1");
player.AttachVideoOutput(out2, "win2");
player.Play();

var sw = Stopwatch.StartNew();
var nextReport = TimeSpan.FromSeconds(2);
while (sw.Elapsed.TotalSeconds < seconds && !closed && !player.Video.IsSourceExhausted)
{
    if (sw.Elapsed >= nextReport)
    {
        Console.WriteLine($"  t={sw.Elapsed.TotalSeconds,4:0}s  pos={player.Position:mm\\:ss\\.ff}  displayed={player.Video.DisplayedCount}  dropped(late)={player.Video.DroppedLate}");
        nextReport += TimeSpan.FromSeconds(2);
    }
    Thread.Sleep(100);
}

Console.WriteLine();
Console.WriteLine($"MultiOutputSmoke done — two windows fanned {player.Video.DisplayedCount} frames " +
    $"({(closed ? "window closed" : player.Video.IsSourceExhausted ? "source ended" : "time elapsed")}).");
return 0;
