// Phase 4 SoundboardSmoke — polyphonic soundboard end-to-end through the registry (no globals): decode a
// file to a resident AudioClip, fire many AudioClipPlayer voices into an AudioRouter, play on PortAudio.
using System.Diagnostics;
using S.Media.Audio.PortAudio;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Decode.FFmpeg;
using S.Media.Routing;
using S.Media.Session;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: SoundboardSmoke <audio-file> [voice-count] [duration-sec]");
    return 1;
}

var path = args[0];
var voiceCount = args.Length > 1 && int.TryParse(args[1], out var n) ? Math.Clamp(n, 1, 64) : 32;
var durationSec = args.Length > 2 && double.TryParse(args[2], out var d) ? Math.Clamp(d, 1, 30) : 5.0;

var registry = MediaRegistry.Build(b => b.Use(new FFmpegModule()).Use(new PortAudioModule()));
var backend = registry.AudioBackends.FirstOrDefault();
if (backend is null)
{
    Console.Error.WriteLine("no audio backend registered");
    return 3;
}

var devices = backend.EnumerateOutputDevices();
var device = devices.FirstOrDefault(x => x.IsDefault) ?? devices.FirstOrDefault();
var rate = device is { DefaultSampleRate: > 0 } ? (int)device.DefaultSampleRate : 48_000;

// Decode the file into a resident PCM clip at the output rate (registry source + resampler — no globals).
if (!registry.TryOpenAudio(path, null, out var src))
{
    Console.Error.WriteLine($"FAIL: no decoder opened '{path}'");
    return 1;
}

AudioClip clip;
try
{
    clip = AudioClip.LoadFromSource(src, targetSampleRate: rate,
        resamplerFactory: (s, r) => registry.CreateResampler(s, r)
            ?? throw new InvalidOperationException("resampler needs an FFmpeg (or other) module"));
}
finally
{
    (src as IDisposable)?.Dispose();
}

if (clip.Duration < TimeSpan.FromMilliseconds(50))
{
    Console.Error.WriteLine($"clip too short ({clip.Duration.TotalMilliseconds:F0} ms); use at least ~100 ms of audio");
    return 1;
}

Console.WriteLine($"decoders: {string.Join(", ", registry.Decoders.Select(x => x.Name))}; backend: {backend.Name}; clip {clip.Duration.TotalSeconds:F1}s @ {rate} Hz");

var output = backend.CreateOutput(device?.Id, new AudioFormat(rate, 2));
using var router = new AudioRouter(rate);
var outputId = router.AddOutput(output);
var player = new AudioClipPlayer(clip)
{
    Mode = AudioClipPlayerMode.Polyphonic,
    MaxPolyphony = voiceCount,
};

router.Play();
var rng = Random.Shared;
var sw = Stopwatch.StartNew();
var fires = 0;
while (sw.Elapsed < TimeSpan.FromSeconds(durationSec))
{
    player.Fire(router, outputId, gain: 0.5f + (float)rng.NextDouble() * 0.5f);
    fires++;
    Thread.Sleep(rng.Next(20, 120));
}

Thread.Sleep(300);
router.Stop();
(output as IDisposable)?.Dispose();

var pump = router.GetAggregatePumpStats();
Console.WriteLine($"fires={fires} chunks={router.ChunksProduced} pumpDropped={pump.TotalDropped} voices={player.ActiveVoices.Count}");

if (router.ChunksProduced == 0)
{
    Console.Error.WriteLine("FAIL: router produced no chunks");
    return 2;
}

if (pump.TotalDropped > 0)
{
    Console.Error.WriteLine($"FAIL: pump dropped {pump.TotalDropped} chunks");
    return 3;
}

if (fires < Math.Min(voiceCount, 8))
{
    Console.Error.WriteLine("FAIL: too few voice fires");
    return 4;
}

Console.WriteLine("SoundboardSmoke OK");
return 0;
