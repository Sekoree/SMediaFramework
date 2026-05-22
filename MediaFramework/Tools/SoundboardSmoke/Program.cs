using System.Diagnostics;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using S.Media.PortAudio;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: SoundboardSmoke <audio-file> [voice-count] [duration-sec]");
    return 1;
}

var path = args[0];
var voiceCount = args.Length > 1 && int.TryParse(args[1], out var n) ? Math.Clamp(n, 1, 64) : 32;
var durationSec = args.Length > 2 && double.TryParse(args[2], out var d) ? Math.Clamp(d, 1, 30) : 5.0;

MediaFrameworkRuntime.Init().UseFFmpeg().UsePortAudio();

try
{
    var clip = AudioClip.OpenFile(path);
    if (clip.Duration < TimeSpan.FromMilliseconds(50))
    {
        Console.Error.WriteLine($"clip too short ({clip.Duration.TotalMilliseconds:F0} ms); use at least ~100 ms of audio");
        return 1;
    }

    using var output = new PortAudioOutput(new AudioFormat(48_000, 2));
    using var router = new AudioRouter(output.Format.SampleRate);
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

    var pump = router.GetAggregatePumpStats();
    Console.WriteLine(
        $"fires={fires} chunks={router.ChunksProduced} pumpDropped={pump.TotalDropped} voices={player.ActiveVoices.Count}");

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

    Console.WriteLine("OK");
    return 0;
}
finally
{
    MediaFrameworkRuntime.Shutdown();
}
