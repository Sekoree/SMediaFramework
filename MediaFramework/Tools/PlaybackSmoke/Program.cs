using System.Linq;
using S.Media.Audio.PortAudio;
using S.Media.Core.Registry;
using S.Media.Decode.FFmpeg;
using S.Media.Players;

// Phase 2 audio playback smoke. Proves the whole spine: registry (no globals) → FFmpeg decode →
// AudioRouter → PortAudio master output (its clock is the session master) → synced playback.
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: PlaybackSmoke <media-file-or-uri> [seconds] [--device <id>]");
    return 2;
}

string? deviceId = null;
for (var i = 1; i < args.Length - 1; i++)
    if (args[i] == "--device")
        deviceId = args[i + 1];

var registry = MediaRegistry.Build(b => b
    .Use(new FFmpegModule())
    .Use(new PortAudioModule()));

var backend = registry.AudioBackends.FirstOrDefault();
if (backend is null)
{
    Console.Error.WriteLine("no audio backend registered");
    return 3;
}

Console.WriteLine($"decoders: {string.Join(", ", registry.Decoders.Select(d => d.Name))}; audio backend: {backend.Name}");

using var player = MediaPlayer.OpenAudio(registry, backend, args[0], deviceId);
Console.WriteLine($"playing '{args[0]}' @ {player.SampleRate} Hz");
player.Play();

var limit = args.Length > 1 && double.TryParse(args[1], out var s) ? TimeSpan.FromSeconds(s) : TimeSpan.FromHours(1);
while (player.IsRunning && player.Position < limit)
{
    Console.Write($"\r{player.Position:mm\\:ss\\.ff}    ");
    Thread.Sleep(200);
}

Console.WriteLine("\ndone.");
return 0;
