// AbiSmoke — the Phase-6 S.Abi gate. Compiles the native test_plugin.c (gcc) into a .so, loads it through the
// S.Abi plugin host, and verifies a native C-ABI plugin loads + registers a source AND a control decoder.
using System.Diagnostics;
using OSCLib;
using S.Abi;
using S.Control;
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

// Exercise the registered video source through its managed adapter — proves the plugin's source actually FEEDS
// FRAMES: open it, read one frame, and check the format (via get_format) + the pixels the plugin wrote.
var provider = AbiPluginHost.BindMediaSourceProviders(plugin).Single().Provider;
var source = provider.TryOpenVideo("testsrc://demo");
if (source is null)
{
    Console.Error.WriteLine("FAIL: the provider opened no video source.");
    return 4;
}

Console.WriteLine($"source format: {source.Format.Width}x{source.Format.Height} {source.Format.PixelFormat}");
if (!source.TryReadNextFrame(out var vframe) || vframe is null)
{
    Console.Error.WriteLine("FAIL: the plugin's video source produced no frame.");
    return 5;
}

var px = vframe.Planes[0].Span;
Console.WriteLine($"frame: {vframe.Format.Width}x{vframe.Format.Height} {vframe.Format.PixelFormat}, " +
                  $"px0=({px[0]},{px[1]},{px[2]},{px[3]})");
if (vframe.Format.Width != 4 || vframe.Format.Height != 4 || vframe.Format.PixelFormat != PixelFormat.Bgra32
    || px[0] != 10 || px[1] != 20 || px[2] != 30 || px[3] != 255)
{
    Console.Error.WriteLine("FAIL: the video frame's format/pixels did not match the plugin's output.");
    return 6;
}
(source as IDisposable)?.Dispose();

Console.WriteLine("AbiSmoke OK — native C plugin loaded; its control decoder RAN and its video source FED A FRAME, both through managed adapters.");
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
