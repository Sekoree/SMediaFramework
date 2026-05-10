using S.Media.Core.Audio;
using S.Media.FFmpeg.Audio;
using S.Media.PortAudio;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: PlaybackSmoke <audio-file>");
    return 1;
}

var path = args[0];
if (!File.Exists(path))
{
    Console.Error.WriteLine($"file not found: {path}");
    return 1;
}

using var decoder = AudioFileDecoder.Open(path);
Console.WriteLine($"opened: {Path.GetFileName(path)}");
Console.WriteLine($"  codec   : {decoder.CodecName}");
Console.WriteLine($"  format  : {decoder.Format.SampleRate} Hz × {decoder.Format.Channels} ch");
Console.WriteLine($"  duration: {decoder.Duration:mm\\:ss\\.fff}");

// Mixer bus runs at the source's format — no resampling yet, no upmix.
using var output = new PortAudioOutput(decoder.Format, ringCapacityFrames: decoder.Format.SampleRate);
Console.WriteLine($"  pa dev  : {output.DeviceIndex} (capacity {output.CapacitySamples} samp/ch)");

// 10 ms chunks — short enough that prebuffer settles fast, long enough that the
// per-chunk overhead is negligible.
var chunkSamples = decoder.Format.SampleRate / 100;
using var router = new AudioRouter(decoder.Format.SampleRate, chunkSamples);
router.AddSource(decoder, "music");
router.AddSink(output, "speakers");
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
    if (status.ElapsedMilliseconds >= 250)
    {
        PrintStatus(decoder, output, router);
        status.Restart();
    }
}

Console.WriteLine();
Console.WriteLine($"  router stopped (completed naturally: {router.CompletedNaturally}) — draining {output.QueuedSamples} samp...");
var drain = System.Diagnostics.Stopwatch.StartNew();
while (output.QueuedSamples > 0 && !cts.IsCancellationRequested && drain.ElapsedMilliseconds < 5000)
{
    Thread.Sleep(50);
    if (status.ElapsedMilliseconds >= 250)
    {
        PrintStatus(decoder, output, router);
        status.Restart();
    }
}

Console.WriteLine();
Console.WriteLine($"  done — played {output.PlayedSamples} samp/ch, underruns {output.UnderrunSamples}, dropped {output.DroppedSamples}, callbacks {output.CallbackCount}, chunks {router.ChunksProduced}");
return 0;

static void PrintStatus(AudioFileDecoder decoder, PortAudioOutput output, AudioRouter router)
{
    Console.Write($"\r  pos {decoder.Position:mm\\:ss\\.fff} / {decoder.Duration:mm\\:ss\\.fff}   queued {output.QueuedSamples,5}   played {output.PlayedSamples,7}   underruns {output.UnderrunSamples,5}   chunks {router.ChunksProduced}");
}
