using System.Diagnostics;
using S.Media.Audio.PortAudio;
using S.Media.Core.Audio;
using S.Media.Core.Registry;

// Phase 5 mic-input smoke. Opens the default audio input through the PortAudio backend
// (IAudioBackend.CreateInput) and pulls live capture samples for a few seconds, reporting the captured
// frame count + RMS level - proving the live audio-capture path. Needs native PortAudio + an input device.

var seconds = args.Length > 0 && double.TryParse(args[0], out var s) ? s : 3.0;
var deviceId = args.Length > 1 ? args[1] : null; // null = system default input

var registry = MediaRegistry.Build(b => b.Use(new PortAudioModule()));
var backend = registry.AudioBackends.FirstOrDefault();
if (backend is null)
{
    Console.Error.WriteLine("no audio backend registered.");
    return 3;
}

var format = new AudioFormat(48_000, 2);
var input = backend.CreateInput(deviceId, format);
Console.WriteLine($"capturing from {(deviceId is null ? "default input" : $"device '{deviceId}'")} at {format.SampleRate} Hz / {format.Channels} ch for {seconds:0}s…");

try
{
    var buf = new float[480 * format.Channels];
    long frames = 0;
    double sumSq = 0;
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        var got = input.ReadInto(buf);
        if (got > 0)
        {
            for (var i = 0; i < got; i++)
                sumSq += buf[i] * (double)buf[i];
            frames += got / format.Channels;
        }
        else
        {
            Thread.Sleep(2);
        }
    }

    var samples = frames * format.Channels;
    var rms = samples > 0 ? Math.Sqrt(sumSq / samples) : 0;
    var dbfs = rms > 0 ? 20 * Math.Log10(rms) : double.NegativeInfinity;
    Console.WriteLine($"captured {frames} frames (~{frames / Math.Max(0.001, sw.Elapsed.TotalSeconds):0} fps); level {rms:0.0000} RMS ({dbfs:0.0} dBFS)");

    if (frames < format.SampleRate / 2) // expect at least ~0.5s of audio
    {
        Console.Error.WriteLine("FAIL: captured far fewer frames than expected - input not flowing.");
        return 1;
    }

    Console.WriteLine("MicCaptureSmoke OK - live audio capture path works (a quiet room reads near-silence, which is fine).");
    return 0;
}
finally
{
    (input as IDisposable)?.Dispose();
}
