// AbiSmoke — the Phase-6 S.Abi gate. Compiles the native test_plugin.c (gcc) into a .so, loads it through the
// S.Abi plugin host, and verifies a native C-ABI plugin loads + registers a source AND a control decoder.
using System.Diagnostics;
using OSCLib;
using S.Abi;
using S.Control;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;

var root = FindNextRoot(AppContext.BaseDirectory);
var pluginC = Path.Combine(root, "MediaFramework", "Tools", "AbiSmoke", "test_plugin.c");
var includeDir = Path.Combine(root, "MediaFramework", "Interop", "S.Abi", "include");
var so = Path.Combine(Path.GetTempPath(), "mfp_test_plugin.so");

Console.WriteLine($"compiling {Path.GetFileName(pluginC)} -> {so}");
if (!CompilePlugin(pluginC, includeDir, so))
    return 1;

using var plugin = AbiPluginHost.Load(so);
Console.WriteLine($"loaded: id={plugin.Id}, name=\"{plugin.DisplayName}\", caps=0x{plugin.Capabilities:x2}");
foreach (var c in plugin.Registered)
    Console.WriteLine($"  registered {c.Capability}: '{c.Id}'");

var hasSource = plugin.Registered.Any(c => c.Capability == "media-source-provider");
var hasDecoder = plugin.Registered.Any(c => c.Capability == "control-decoder");
if (plugin.Id != "com.example.testplugin" || !hasSource || !hasDecoder)
{
    Console.Error.WriteLine("FAIL: expected the plugin to register a media-source-provider AND a control-decoder.");
    return 2;
}

// Exercise the registered control decoder through its managed adapter — proves the plugin's code actually RUNS
// (not just that it registered): decode a one-byte blob and check the reading the plugin produced.
var decoder = AbiPluginHost.BindControlDecoders(plugin).Single().Decoder;
var readings = decoder.Decode("/test", Array.Empty<OSCArgument>(), 0, new byte[] { 128 }).ToList();
Console.WriteLine($"decoder ran: {readings.Count} reading(s)");
foreach (var r in readings)
    Console.WriteLine($"  {r.Address} = {r.Value:F3}");
if (readings.Count != 1 || readings[0].Address != "/test/decoded" || Math.Abs(readings[0].Value - (128f / 255f)) > 0.001f)
{
    Console.Error.WriteLine("FAIL: the plugin's decoder did not return the expected reading.");
    return 3;
}

// (a) Bind the video-source provider directly and confirm the resolved format (proves the adapter + get_format).
var provider = AbiPluginHost.BindMediaSourceProviders(plugin).Single().Provider;
var bound = provider.TryOpenVideo("testsrc://demo");
if (bound is null || bound.Format.Width != 4 || bound.Format.Height != 4 || bound.Format.PixelFormat != PixelFormat.Bgra32)
{
    Console.Error.WriteLine("FAIL: the provider/get_format did not yield a 4x4 Bgra32 source.");
    return 4;
}
Console.WriteLine($"source format: {bound.Format.Width}x{bound.Format.Height} {bound.Format.PixelFormat}");
(bound as IDisposable)?.Dispose();

// (b) Register the plugin into a LIVE IMediaRegistry, route the URI through it, and read the frame — proves the
// end-to-end live path + the frame-union marshalling (pixels must match what the plugin wrote).
var registry = MediaRegistry.Build(b => AbiPluginHost.RegisterInto(plugin, media: b));
if (!registry.TryOpenVideo("testsrc://demo", null, out var source) || !source.TryReadNextFrame(out var vframe) || vframe is null)
{
    Console.Error.WriteLine("FAIL: IMediaRegistry did not route + open + read the plugin's source.");
    return 5;
}
var px = vframe.Planes[0].Span;
Console.WriteLine($"registry-routed frame: {vframe.Format.Width}x{vframe.Format.Height} {vframe.Format.PixelFormat}, " +
                  $"px0=({px[0]},{px[1]},{px[2]},{px[3]}) via provider '{registry.Decoders[0].Name}'");
if (vframe.Format.Width != 4 || vframe.Format.Height != 4 || vframe.Format.PixelFormat != PixelFormat.Bgra32
    || px[0] != 10 || px[1] != 20 || px[2] != 30 || px[3] != 255)
{
    Console.Error.WriteLine("FAIL: the registry-routed frame's format/pixels did not match the plugin's output.");
    return 6;
}
(source as IDisposable)?.Dispose();

// (c) Register the control decoder into a decoder registry and resolve it by id (the live control path).
AbiPluginHost.RegisterInto(plugin, control: ControlMeterBlobDecoderRegistry.Default);
if (ControlMeterBlobDecoderRegistry.Default.Resolve("test.decoder") is null)
{
    Console.Error.WriteLine("FAIL: the control decoder was not registered into ControlMeterBlobDecoderRegistry.");
    return 7;
}

// (d) Audio backend: enumerate a device, open an output, submit samples, read the played-frame clock.
var audio = AbiPluginHost.BindAudioBackends(plugin).Single().Backend;
var devices = audio.EnumerateOutputDevices();
var output = audio.CreateOutput(null, new AudioFormat(48000, 2));
output.Submit(stackalloc float[8]); // 8 floats / 2ch = 4 frames
var played = (output as IAudioOutputPlaybackStats)!.PlayedSamples;
Console.WriteLine($"audio backend: {devices.Count} device(s) '{(devices.Count > 0 ? devices[0].Name : "?")}', played frames={played}");
(output as IDisposable)?.Dispose();
if (devices.Count != 1 || devices[0].Name != "Plugin Output" || played != 4)
{
    Console.Error.WriteLine("FAIL: audio backend enumerate/submit/played-frames mismatch.");
    return 8;
}

// (e) Video output: configure + submit the frame the source produced; the plugin validates it + reports via host log.
var vout = AbiPluginHost.BindVideoOutputs(plugin).Single().Output;
vout.Configure(vframe.Format);
vout.Submit(vframe);
Console.WriteLine($"video output reported: {AbiPluginHost.LastLogMessage}");
(vout as IDisposable)?.Dispose();
if (AbiPluginHost.LastLogMessage != "vout:ok")
{
    Console.Error.WriteLine("FAIL: the video output did not receive the expected frame (reverse marshalling).");
    return 9;
}

// (f) Subtitle provider: open + render an overlay frame at a position.
var subProvider = AbiPluginHost.BindSubtitleProviders(plugin).Single().Provider;
var overlay = subProvider.TryOpen("testsub://demo", 4, 4);
var subFrame = overlay?.RenderAt(TimeSpan.FromSeconds(1));
if (subFrame is null)
{
    Console.Error.WriteLine("FAIL: the subtitle overlay rendered no frame.");
    return 10;
}
var sp = subFrame.Planes[0].Span;
Console.WriteLine($"subtitle overlay: {subFrame.Format.Width}x{subFrame.Format.Height} px0=({sp[0]},{sp[1]},{sp[2]},{sp[3]})");
overlay?.Dispose();
if (sp[0] != 99 || sp[1] != 99 || sp[2] != 99 || sp[3] != 255)
{
    Console.Error.WriteLine("FAIL: the subtitle overlay pixels did not match.");
    return 11;
}

Console.WriteLine("AbiSmoke OK — all six ABI capabilities run through managed adapters: source (registry-routed) + audio backend + video output + subtitle + control decoder, with media-source/audio/decoder registered into the live registries.");
return 0;

static bool CompilePlugin(string cFile, string includeDir, string outSo)
{
    var psi = new ProcessStartInfo("gcc", $"-shared -fPIC -I\"{includeDir}\" \"{cFile}\" -o \"{outSo}\"")
    {
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var p = Process.Start(psi);
    if (p is null)
    {
        Console.Error.WriteLine("could not start gcc");
        return false;
    }

    var err = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0)
    {
        Console.Error.WriteLine($"gcc failed ({p.ExitCode}):\n{err}");
        return false;
    }

    return true;
}

static string FindNextRoot(string start)
{
    var d = new DirectoryInfo(start);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "MFPlayer.Next.sln")))
        d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("MFPlayer.Next.sln not found above " + start);
}
