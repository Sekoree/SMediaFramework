using S.Media.Core.Audio;
using S.Media.FFmpeg.Audio;
using S.Media.PortAudio;

// PlaybackSmoke — decode an audio file and play it through a PortAudio output.
//   PlaybackSmoke <audio-file> [--hostapi <substr>] [--device <substr>]
//   PlaybackSmoke --list                 (enumerate host APIs + output devices, then exit)
// --hostapi / --device pick by case-insensitive name substring (e.g. --hostapi JACK --device Scarlett).

string? path = null;
string? hostApiNeedle = null;
string? deviceNeedle = null;
var listOnly = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--list":
            listOnly = true;
            break;
        case "--hostapi":
            if (++i >= args.Length) { Usage(); return 1; }
            hostApiNeedle = args[i];
            break;
        case "--device":
            if (++i >= args.Length) { Usage(); return 1; }
            deviceNeedle = args[i];
            break;
        default:
            if (path is null) { path = args[i]; break; }
            Console.Error.WriteLine($"unexpected argument: {args[i]}");
            Usage();
            return 1;
    }
}

if (listOnly)
{
    PrintCatalog();
    return 0;
}

if (path is null) { Usage(); return 1; }
if (!File.Exists(path))
{
    Console.Error.WriteLine($"file not found: {path}");
    return 1;
}

// Resolve the requested host API + device to a global PortAudio device index.
int? deviceIndex = null;
if (hostApiNeedle is not null || deviceNeedle is not null)
{
    int? hostApiIndex = null;
    if (hostApiNeedle is not null)
    {
        var api = PortAudioDeviceCatalog.EnumerateHostApis()
            .FirstOrDefault(a => a.Name.Contains(hostApiNeedle, StringComparison.OrdinalIgnoreCase));
        if (api.Name is null || !api.Name.Contains(hostApiNeedle, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"no host API matched '{hostApiNeedle}'. Available:");
            PrintCatalog();
            return 1;
        }
        hostApiIndex = api.Index;
        Console.WriteLine($"  host api: [{api.Index}] {api.Name} ({api.DeviceCount} devices)");
    }

    var devices = PortAudioDeviceCatalog.EnumerateOutputDevices(hostApiIndex);
    var dev = deviceNeedle is null
        ? (devices.Count > 0 ? devices[0] : default)
        : devices.FirstOrDefault(d => d.Name.Contains(deviceNeedle, StringComparison.OrdinalIgnoreCase));
    if (dev.Name is null || (deviceNeedle is not null && !dev.Name.Contains(deviceNeedle, StringComparison.OrdinalIgnoreCase)))
    {
        Console.Error.WriteLine($"no output device matched '{deviceNeedle}' under the selected host API. Available:");
        foreach (var d in devices)
            Console.Error.WriteLine($"    [{d.GlobalDeviceIndex}] {d.Name} ({d.MaxOutputChannels}ch @ {d.DefaultSampleRate:F0} Hz)");
        return 1;
    }
    deviceIndex = dev.GlobalDeviceIndex;
    Console.WriteLine($"  device  : [{dev.GlobalDeviceIndex}] {dev.Name} ({dev.MaxOutputChannels}ch @ {dev.DefaultSampleRate:F0} Hz)");
}

using var decoder = AudioFileDecoder.Open(path);
Console.WriteLine($"opened: {Path.GetFileName(path)}");
Console.WriteLine($"  codec   : {decoder.CodecName}");
Console.WriteLine($"  format  : {decoder.Format.SampleRate} Hz × {decoder.Format.Channels} ch");
Console.WriteLine($"  duration: {decoder.Duration:mm\\:ss\\.fff}");

// Mixer bus runs at the source's format — no resampling yet, no upmix.
using var output = new PortAudioOutput(decoder.Format, deviceIndex, ringCapacityFrames: decoder.Format.SampleRate);
Console.WriteLine($"  pa dev  : {output.DeviceIndex} (capacity {output.CapacitySamples} samp/ch)");

// 10 ms chunks — short enough that prebuffer settles fast, long enough that the
// per-chunk overhead is negligible.
var chunkSamples = decoder.Format.SampleRate / 100;
using var router = new AudioRouter(decoder.Format.SampleRate, chunkSamples);
router.AddSource(decoder, "music");
router.AddOutput(output, "speakers");
router.AddRoute("music", "speakers", ChannelMap.Identity(decoder.Format.Channels));

// Prebuffer ~250 ms by submitting silent chunks via the router's pre-roll path.
// We do this by reading directly from the decoder before starting the audio
// stream — same pattern as before, but using the new ReadInto API to mirror
// what the router will do once it spins up.
var prebufferTarget = decoder.Format.SampleRate / 4;
var preFloats = chunkSamples * decoder.Format.Channels;
var preBuf = new float[preFloats];
while (output.QueuedSamples < prebufferTarget)
{
    var read = decoder.ReadInto(preBuf);
    if (read == 0) break;
    output.Submit(preBuf.AsSpan(0, read));
}
Console.WriteLine($"  prebuf  : {output.QueuedSamples} samp queued");

output.Start();
router.Start();
Console.WriteLine($"  router started — {router.SampleRate} Hz, {chunkSamples} samp/chunk (Ctrl+C to stop)");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var status = System.Diagnostics.Stopwatch.StartNew();
while (router.IsRunning && !cts.IsCancellationRequested)
{
    Thread.Sleep(50);
    if (status.ElapsedMilliseconds >= 100)
    {
        PrintStatus(decoder, output, router);
        status.Restart();
    }
}

Console.WriteLine();
Console.WriteLine($"  router stopped (completed naturally: {router.CompletedNaturally}) — draining {output.QueuedSamples} samp...");
var drain = System.Diagnostics.Stopwatch.StartNew();
status.Restart(); // drain loop reuses the same 100 ms HUD cadence (don't inherit pre-drain elapsed time)
while (output.QueuedSamples > 0 && !cts.IsCancellationRequested && drain.ElapsedMilliseconds < 5000)
{
    Thread.Sleep(50);
    if (status.ElapsedMilliseconds >= 100)
    {
        PrintStatus(decoder, output, router);
        status.Restart();
    }
}

Console.WriteLine();
Console.WriteLine($"  done — played {output.PlayedSamples} samp/ch, underruns {output.UnderrunSamples}, dropped {output.DroppedSamples}, callbacks {output.CallbackCount}, chunks {router.ChunksProduced}");
return 0;

static void Usage()
{
    Console.Error.WriteLine("usage: PlaybackSmoke <audio-file> [--hostapi <substr>] [--device <substr>]");
    Console.Error.WriteLine("       PlaybackSmoke --list");
}

static void PrintCatalog()
{
    foreach (var api in PortAudioDeviceCatalog.EnumerateHostApis())
    {
        Console.WriteLine($"[{api.Index}] {api.Name}  (type {api.TypeId}, {api.DeviceCount} devices)");
        foreach (var d in PortAudioDeviceCatalog.EnumerateOutputDevices(api.Index))
            Console.WriteLine($"      out [{d.GlobalDeviceIndex}] {d.Name}  ({d.MaxOutputChannels}ch @ {d.DefaultSampleRate:F0} Hz)");
    }
}

static void PrintStatus(AudioFileDecoder decoder, PortAudioOutput output, AudioRouter router)
{
    Console.Write($"\r  pos {decoder.Position:mm\\:ss\\.fff} / {decoder.Duration:mm\\:ss\\.fff}   queued {output.QueuedSamples,5}   played {output.PlayedSamples,7}   underruns {output.UnderrunSamples,5}   chunks {router.ChunksProduced}");
}
