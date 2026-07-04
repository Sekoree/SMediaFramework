using System.Linq;
using NDILib;
using S.Media.Audio.MiniAudio;
using S.Media.Audio.PortAudio;
using S.Media.Core.Registry;
using S.Media.Decode.FFmpeg;
using S.Media.NDI;

// Phase 5 backend smoke: build one registry with every backend module (no globals), proving each module
// registers and its native library loads. NDIModule.Register initialises the NDI runtime (loads libndi);
// enumerating each audio backend's devices loads PortAudio + miniaudio; FFmpeg is the decode provider.

var registry = MediaRegistry.Build(b => b
    .Use(new FFmpegModule())
    .Use(new PortAudioModule())
    .Use(new MiniAudioModule())
    .Use(new NDIModule()));

Console.WriteLine($"decoders:       {string.Join(", ", registry.Decoders.Select(d => d.Name))}");
Console.WriteLine($"audio backends: {string.Join(", ", registry.AudioBackends.Select(x => x.Name))}");

foreach (var backend in registry.AudioBackends)
{
    try
    {
        var outs = backend.EnumerateOutputDevices();
        var ins = backend.EnumerateInputDevices();
        Console.WriteLine($"  {backend.Name,-10}: {outs.Count} output / {ins.Count} input device(s)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  {backend.Name,-10}: device enumeration failed: {ex.Message}");
    }
}

Console.WriteLine($"NDI runtime:    version {NDIRuntime.Version}, CPU {(NDIRuntime.IsSupportedCpu() ? "supported" : "UNSUPPORTED")}");

// Optional: resolve an ndi: source if a name is supplied (none on a quiet network is fine).
if (args.Length > 0)
{
    var found = NDISource.Find(TimeSpan.FromSeconds(2));
    Console.WriteLine($"NDI discovery:  {found.Count} source(s) on the network: {string.Join(", ", found.Select(s => s.Name))}");
}

Console.WriteLine("BackendsSmoke OK — FFmpeg + PortAudio + MiniAudio + NDI registered; native libs loaded.");
return 0;
