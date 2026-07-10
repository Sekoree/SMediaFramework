// AbiSmoke - the Phase-6 S.Abi gate. Compiles the native test_plugin.c (gcc) into a .so, loads it through the
// S.Abi plugin host, and verifies a native C-ABI plugin loads + registers a source AND a control decoder.
using System.Diagnostics;
using OSCLib;
using S.Abi;
using S.Control;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Time;

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

// Exercise the registered control decoder through its managed adapter - proves the plugin's code actually RUNS
// (not just that it registered): decode a one-byte blob and check the reading the plugin produced.
var decoder = AbiPluginHost.BindControlDecoders(plugin).Single().Decoder;
var controlArguments = new[] { OSCArgument.String("meters"), OSCArgument.Blob(new byte[] { 128 }) };
var readings = decoder.Decode("/test", controlArguments, 1, new byte[] { 128 }).ToList();
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
var boundAudio = provider.TryOpenAudio("testsrc://demo");
Span<float> boundSamples = stackalloc float[8];
var boundSampleCount = boundAudio?.ReadInto(boundSamples) ?? 0;
Console.WriteLine($"paired media audio: {boundAudio?.Format.SampleRate} Hz/{boundAudio?.Format.Channels}ch, floats={boundSampleCount}");
(boundAudio as IDisposable)?.Dispose();
if (boundSampleCount != 8 || boundSamples[0] != 0.25f)
{
    Console.Error.WriteLine("FAIL: the provider's correlated audio source did not produce the expected samples.");
    return 5;
}

// (b) Register the plugin into a LIVE IMediaRegistry, route the URI through it, and read the frame - proves the
// end-to-end live path + the frame-union marshalling (pixels must match what the plugin wrote).
var registry = MediaRegistry.Build(b => AbiPluginHost.RegisterInto(plugin, media: b));
if (!registry.TryOpenVideo("testsrc://demo", null, out var source) || !source.TryReadNextFrame(out var vframe) || vframe is null)
{
    Console.Error.WriteLine("FAIL: IMediaRegistry did not route + open + read the plugin's source.");
    return 6;
}
var px = vframe.Planes[0].Span;
Console.WriteLine($"registry-routed frame: {vframe.Format.Width}x{vframe.Format.Height} {vframe.Format.PixelFormat}, " +
                  $"px0=({px[0]},{px[1]},{px[2]},{px[3]}) via provider '{registry.Decoders[0].Name}'");
if (vframe.Format.Width != 4 || vframe.Format.Height != 4 || vframe.Format.PixelFormat != PixelFormat.Bgra32
    || px[0] != 10 || px[1] != 20 || px[2] != 30 || px[3] != 255)
{
    Console.Error.WriteLine("FAIL: the registry-routed frame's format/pixels did not match the plugin's output.");
    return 7;
}
(source as IDisposable)?.Dispose();
if (!registry.TryOpenAudio("testsrc://demo", null, out var registryAudio))
{
    Console.Error.WriteLine("FAIL: IMediaRegistry did not route the plugin's audio source.");
    return 8;
}
Span<float> registrySamples = stackalloc float[4];
var registrySampleCount = registryAudio.ReadInto(registrySamples);
(registryAudio as IDisposable)?.Dispose();
if (registrySampleCount != 4 || registrySamples[0] != 0.25f)
{
    Console.Error.WriteLine("FAIL: registry-routed plugin audio samples did not match.");
    return 9;
}

// (c) Register the control decoder into a decoder registry and resolve it by id (the live control path).
var controlDecoders = new ControlMeterBlobDecoderRegistry();
AbiPluginHost.RegisterInto(plugin, control: controlDecoders);
if (controlDecoders.Resolve("test.decoder") is null)
{
    Console.Error.WriteLine("FAIL: the control decoder was not registered into ControlMeterBlobDecoderRegistry.");
    return 10;
}

// (d) Audio backend: enumerate a device, open an output, submit samples, read the played-frame clock.
var audio = AbiPluginHost.BindAudioBackends(plugin).Single().Backend;
var devices = audio.EnumerateOutputDevices();
var output = audio.CreateOutput(null, new AudioFormat(48000, 2));
output.Submit(stackalloc float[8]); // 8 floats / 2ch = 4 frames
var played = (output as IAudioOutputPlaybackStats)!.PlayedSamples;
var clocked = output as IClockedOutput;
var playbackClock = output as IPlaybackClock;
var input = audio.CreateInput(null, new AudioFormat(48000, 2));
Span<float> captured = stackalloc float[6];
var capturedCount = input.ReadInto(captured);
Console.WriteLine($"audio backend: {devices.Count} device(s) '{(devices.Count > 0 ? devices[0].Name : "?")}', played frames={played}, captured floats={capturedCount}");
(output as IDisposable)?.Dispose();
(input as IDisposable)?.Dispose();
if (devices.Count != 1 || devices[0].Name != "Plugin Output" || played != 4
    || clocked is null || playbackClock is null || playbackClock.ElapsedSinceStart <= TimeSpan.Zero
    || capturedCount != 6 || captured[0] != 0.5f)
{
    Console.Error.WriteLine("FAIL: audio backend enumerate/clock/backpressure/input semantics mismatch.");
    return 11;
}

// (e) Video output: configure + submit the frame the source produced; the plugin validates it + reports via host log.
var vout = AbiPluginHost.BindVideoOutputs(plugin).Single().Output;
vout.Configure(vframe.Format);
vout.Submit(vframe);
Console.WriteLine($"video output reported: {AbiPluginHost.LastLogMessage}");
if (AbiPluginHost.LastLogMessage != "vout:ok")
{
    Console.Error.WriteLine("FAIL: the video output did not receive the expected frame (reverse marshalling).");
    return 12;
}

var dmaSource = provider.TryOpenVideo("testsrc://dmabuf");
if (dmaSource is null || !dmaSource.TryReadNextFrame(out var dmaFrame) || dmaFrame.DmabufNv12 is null)
{
    Console.Error.WriteLine("FAIL: the native dma-buf frame was not imported into a Core hardware backing.");
    return 13;
}
vout.Configure(dmaFrame.Format);
vout.Submit(dmaFrame);
Console.WriteLine($"dma-buf source/output: fds=({dmaFrame.DmabufNv12.YPlaneFd},{dmaFrame.DmabufNv12.UvPlaneFd}), {AbiPluginHost.LastLogMessage}");
if (AbiPluginHost.LastLogMessage != "vout:dmabuf")
{
    Console.Error.WriteLine("FAIL: the native video output did not receive the imported dma-buf frame.");
    return 14;
}
dmaFrame.Dispose();
(dmaSource as IDisposable)?.Dispose();
(vout as IDisposable)?.Dispose();

// (f) Subtitle provider: open + render an overlay frame at a position.
var subProvider = AbiPluginHost.BindSubtitleProviders(plugin).Single().Provider;
var overlay = subProvider.TryOpen("testsub://demo", 4, 4);
var subFrame = overlay?.RenderAt(TimeSpan.FromSeconds(1));
if (subFrame is null)
{
    Console.Error.WriteLine("FAIL: the subtitle overlay rendered no frame.");
    return 15;
}
var sp = subFrame.Planes[0].Span;
Console.WriteLine($"subtitle overlay: {subFrame.Format.Width}x{subFrame.Format.Height} px0=({sp[0]},{sp[1]},{sp[2]},{sp[3]})");
overlay?.Dispose();
subFrame.Dispose();
if (sp[0] != 99 || sp[1] != 99 || sp[2] != 99 || sp[3] != 255)
{
    Console.Error.WriteLine("FAIL: the subtitle overlay pixels did not match.");
    return 16;
}

// (g) PLUG-02: dispose a plugin while a native call is BLOCKED inside it. The per-adapter lease must keep the
// library mapped until the in-flight call returns - unloading it out from under a running callback would crash.
// This uses an ISOLATED copy of the .so so this plugin's unload actually unmaps ITS OWN library (the plugin
// above is a different mapping), making it a real free-during-blocked-call race. The plugin's audio read blocks
// on MFP_TEST_PLUGIN_SLOW_MS; we start a read on a thread, dispose the plugin mid-block, then release the adapter.
{
    var slowSo = Path.Combine(Path.GetTempPath(), "mfp_test_plugin_slow.so");
    File.Copy(so, slowSo, overwrite: true);
    Environment.SetEnvironmentVariable("MFP_TEST_PLUGIN_SLOW_MS", "400");
    try
    {
        var slowPlugin = AbiPluginHost.Load(slowSo);
        var slowProvider = AbiPluginHost.BindMediaSourceProviders(slowPlugin).Single().Provider;
        var slowAudio = slowProvider.TryOpenAudio("testsrc://demo")
                        ?? throw new InvalidOperationException("slow plugin audio open failed");

        var readCount = int.MinValue;
        var reader = new Thread(() => { var buf = new float[4]; readCount = slowAudio.ReadInto(buf); }) { IsBackground = true };
        reader.Start();
        Thread.Sleep(100);        // let the read enter the native usleep
        slowPlugin.Dispose();     // request unload WHILE the read is blocked - the lease must defer NativeLibrary.Free

        if (!reader.Join(TimeSpan.FromSeconds(5)))
        {
            Console.Error.WriteLine("FAIL: a native read blocked during dispose never returned (lease/unload deadlock?).");
            return 18;
        }
        // The blocked native call returned without the library being unmapped under it (no crash reaching here).
        (slowAudio as IDisposable)?.Dispose();
        (slowProvider as IDisposable)?.Dispose();
        Console.WriteLine($"PLUG-02: dispose during a blocked native read was safely deferred by the lease " +
                          $"(read returned {readCount} floats, library stayed mapped through the in-flight call).");
    }
    finally
    {
        Environment.SetEnvironmentVariable("MFP_TEST_PLUGIN_SLOW_MS", null);
        try { File.Delete(Path.Combine(Path.GetTempPath(), "mfp_test_plugin_slow.so")); } catch { /* best effort */ }
    }
}

vframe.Dispose();
(decoder as IDisposable)?.Dispose();
provider.Dispose();
audio.Dispose();
subProvider.Dispose();
foreach (var disposable in registry.Decoders.OfType<IDisposable>())
    disposable.Dispose();
foreach (var disposable in registry.AudioBackends.OfType<IDisposable>())
    disposable.Dispose();
(controlDecoders.Resolve("test.decoder") as IDisposable)?.Dispose();

plugin.Dispose();
if (!plugin.IsUnloaded || AbiPluginHost.LastLogMessage != "unregister:ok")
{
    Console.Error.WriteLine("FAIL: plugin unload was not deferred/completed cleanly or unregister was not called.");
    return 17;
}

Console.WriteLine("AbiSmoke OK - all six ABI capabilities run through managed adapters, including correlated A/V, audio clock/backpressure/input semantics, scoped registries, deferred plugin unload, and (PLUG-02) a plugin dispose safely deferred while a native call was blocked inside it.");
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
    var configured = Environment.GetEnvironmentVariable("MFPLAYER_NEXT_ROOT");
    if (!string.IsNullOrWhiteSpace(configured)
        && File.Exists(Path.Combine(configured, "MFPlayer.sln")))
        return Path.GetFullPath(configured);

    var d = new DirectoryInfo(start);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "MFPlayer.sln")))
        d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("MFPlayer.sln not found above " + start);
}
