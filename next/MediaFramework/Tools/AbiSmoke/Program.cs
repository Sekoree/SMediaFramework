// AbiSmoke — the Phase-6 S.Abi gate. Compiles the native test_plugin.c (gcc) into a .so, loads it through the
// S.Abi plugin host, and verifies a native C-ABI plugin loads + registers a source AND a control decoder.
using System.Diagnostics;
using S.Abi;

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

Console.WriteLine("AbiSmoke OK — a native C plugin loaded through S.Abi and registered a source AND a control decoder.");
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
