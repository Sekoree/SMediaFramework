using System.Diagnostics;
using System.Globalization;
using NDILib;
using S.Media.Audio.PortAudio;
using S.Media.Core.Registry;
using S.Media.NDI;
using S.Media.Players;
using S.Media.Present.SDL3;

// Phase 5 on-screen live smoke. Opens a real NDI source with the one-call MediaPlayer.OpenLive (registry →
// ndi: provider; warms the source + re-anchors it to the master at Play so it presents SCHEDULED against the
// session clock, the P7 path), attaches an SDL3 GL window, and plays. Needs a display + a live NDI sender.

var nameFilter = args.Length > 0 ? args[0] : null;
var seconds = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 30.0;

// Optional 3rd arg - the NDI audio jitter buffer. A number is the size in milliseconds; "auto" probes the
// lowest glitch-free size for this network (see ProbeAudioFloor below); absent keeps NDISource's ~50 ms
// default. This is the A/V sync lever: smaller brings the audio FORWARD to meet the low-latency live video
// (tighter sync + lower latency) at more underrun risk - we tune the audio buffer, not hold the video back.
var probeAuto = args.Length > 2 && args[2].Equals("auto", StringComparison.OrdinalIgnoreCase);
TimeSpan? audioBuffer = !probeAuto && args.Length > 2 && double.TryParse(args[2], out var bufMs)
    ? TimeSpan.FromMilliseconds(bufMs)
    : null;

// Initialise the NDI runtime up front so discovery + the optional auto-probe can open receivers before we
// build the playback registry (whose NDIModule bakes in the chosen audio buffer size).
var rc = NDIRuntime.Create(out _);
if (rc != 0)
{
    Console.Error.WriteLine($"NDI runtime init failed ({rc}).");
    return 3;
}

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

if (probeAuto)
{
    Console.WriteLine("auto-probing the lowest glitch-free audio buffer for this network…");
    var presets = NDIAudioBufferProbe.Probe(chosen, onStep: (buf, underruns) => Console.WriteLine(
        $"  {buf.TotalMilliseconds,3:0} ms → {underruns} underrun chunk(s)  {(underruns == 0 ? "OK" : "glitches - floor reached")}"));
    if (presets.HasAudio)
    {
        audioBuffer = presets.Lowest;
        Console.WriteLine($"  presets: lowest {presets.Lowest.TotalMilliseconds:0} ms · " +
            $"balanced {presets.Balanced.TotalMilliseconds:0} ms · safe {presets.Safe.TotalMilliseconds:0} ms - using lowest.");
    }
    else
        Console.WriteLine("  source has no audio - keeping the default buffer.");
}

// PortAudio gives us an output device so the NDI source's audio can be played alongside the video.
var registry = MediaRegistry.Build(b => b.Use(new NDIModule(audioBuffer)).Use(new PortAudioModule()));
Console.WriteLine($"opening '{chosen.Name}' via MediaPlayer.OpenLive - window for {seconds:0}s or until closed…");

// Pass the PortAudio backend so OpenLive opens the source's audio and plays it on a master output (both A and V
// then run off the one shared receiver, so A/V sync can be checked by ear). Select the JACK host API's default
// output rather than the system default: on Linux the system default is the exclusive ALSA device - unavailable
// while another app holds it - whereas JACK is shareable.
var audioBackend = registry.AudioBackends.FirstOrDefault();
string? audioDeviceId = null;
var jack = PortAudioDeviceCatalog.EnumerateHostApis()
    .FirstOrDefault(a => a.Name.Contains("JACK", StringComparison.OrdinalIgnoreCase));
if (jack.Name is not null && jack.DefaultOutputDeviceIndex >= 0)
{
    audioDeviceId = jack.DefaultOutputDeviceIndex.ToString(CultureInfo.InvariantCulture);
    Console.WriteLine($"audio: routing to the JACK host API default output (device #{audioDeviceId}).");
}
else if (audioBackend is not null)
{
    Console.WriteLine("audio: JACK host API unavailable - falling back to the system default output.");
}

Console.WriteLine($"audio buffer: {(audioBuffer is null ? "default (~50 ms NDI jitter reserve)" : $"{audioBuffer.Value.TotalMilliseconds:0} ms")} - smaller brings audio forward toward the live video, at more underrun risk.");
using var player = MediaPlayer.OpenLive(registry, $"ndi://{Uri.EscapeDataString(chosen.Name)}", audioBackend, audioDeviceId);
using var output = new SDL3GLVideoOutput($"Live NDI - {chosen.Name}", 1280, 720);
var closed = false;
output.CloseRequested += (_, _) => closed = true;

player.AttachVideoOutput(output, "win");
Console.WriteLine(player.AudioRouter is not null
    ? $"audio: NDI audio playing @ {player.SampleRate} Hz - listen for A/V sync against the video."
    : "audio: no audio (no PortAudio device available, or the sender's audio is off).");

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
    Console.Write($"\r{player.Position:mm\\:ss\\.ff}  src={f.Width}x{f.Height}  displayed={player.Video.DisplayedCount} dropped(late)={player.Video.DroppedLate}  audioChunks={player.AudioRouter?.ChunksProduced ?? 0}   ");
    Thread.Sleep(200);
}

Console.WriteLine();
if (player.Video.Fault is { } fault)
{
    Console.Error.WriteLine($"FAIL: player faulted: {fault.Message}");
    return 1;
}

var audioChunks = player.AudioRouter?.ChunksProduced ?? 0;
Console.WriteLine($"LivePlaybackSmoke done - displayed {player.Video.DisplayedCount} frames + {audioChunks} audio chunks " +
    $"({(closed ? "window closed" : "time elapsed")}); A and V played off one shared NDI connection.");
return 0;
