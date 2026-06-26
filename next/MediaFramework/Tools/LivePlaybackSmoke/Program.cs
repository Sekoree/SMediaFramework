using System.Diagnostics;
using S.Media.Core.Registry;
using S.Media.NDI;
using S.Media.Players;
using S.Media.Present.SDL3;

// Phase 5 on-screen live smoke. Opens a real NDI source with the one-call MediaPlayer.OpenLive (registry →
// ndi: provider; warms the source + re-anchors it to the master at Play so it presents SCHEDULED against the
// session clock, the P7 path), attaches an SDL3 GL window, and plays. Needs a display + a live NDI sender.

var nameFilter = args.Length > 0 ? args[0] : null;
var seconds = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 30.0;

// Building the registry initialises the NDI runtime (NDIModule), so discovery works straight after.
var registry = MediaRegistry.Build(b => b.Use(new NDIModule()));

Console.WriteLine("discovering NDI sources…");
var sources = NDISource.Find(TimeSpan.FromSeconds(3));
var chosen = sources.FirstOrDefault(x => nameFilter is null || x.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
if (chosen.Name is null)
{
    Console.Error.WriteLine(sources.Count == 0
        ? "no NDI sources on the network."
        : $"no NDI source matching '{nameFilter}'. Available: {string.Join(", ", sources.Select(x => x.Name))}");
    return 4;
}
Console.WriteLine($"opening '{chosen.Name}' via MediaPlayer.OpenLive — window for {seconds:0}s or until closed…");

using var player = MediaPlayer.OpenLive(registry, $"ndi://{Uri.EscapeDataString(chosen.Name)}");
using var output = new SDL3GLVideoOutput($"Live NDI — {chosen.Name}", 1280, 720);
var closed = false;
output.CloseRequested += (_, _) => closed = true;

player.AttachVideoOutput(output, "win");
player.Play();

Thread.Sleep(800); // let the window settle to its real drawable size
var srcFmt = player.Video.Format;
var vp = output.ViewportPixelSize;
Console.WriteLine($"DIAG: source {srcFmt.Width}x{srcFmt.Height} -> window drawable {vp.Width}x{vp.Height} " +
    $"(downscale {(double)srcFmt.Width / Math.Max(1, vp.Width):0.00}x)");

var sw = Stopwatch.StartNew();
while (sw.Elapsed.TotalSeconds < seconds && !closed && player.Video.Fault is null)
{
    var f = player.Video.Format;
    Console.Write($"\r{player.Position:mm\\:ss\\.ff}  src={f.Width}x{f.Height}  displayed={player.Video.DisplayedCount} dropped(late)={player.Video.DroppedLate}   ");
    Thread.Sleep(200);
}

Console.WriteLine();
if (player.Video.Fault is { } fault)
{
    Console.Error.WriteLine($"FAIL: player faulted: {fault.Message}");
    return 1;
}

Console.WriteLine($"LivePlaybackSmoke done — displayed {player.Video.DisplayedCount} frames ({(closed ? "window closed" : "time elapsed")}).");
return 0;
