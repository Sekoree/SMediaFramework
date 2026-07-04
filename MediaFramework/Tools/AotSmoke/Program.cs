// AOT gate. Proves the next/ toolchain NativeAOT-publishes the framework keystone (S.Media.Core) AND the Phase-6
// surfaces that carry real AOT risk — the Mond script engine + STJ profile load + the subtitle host glue — by
// exercising them so the toolchain actually compiles those code paths (not just references the assemblies).
using S.Control;
using S.Media.Interop;

// S.Control: STJ source-gen profile load, then Mond compile + run a script that uses a profile HelperScript
// (`x32`, loaded as a module global) and the `show` bridge — the whole control hot path under NativeAOT.
var profileCount = BuiltInControlDeviceProfileRepository.Instance.Profiles.Count;

var host = new ControlScriptFileHost(
    new InMemoryControlScriptSourceProvider(new Dictionary<string, string>
    {
        ["aot.mnd"] = "return { run: fun() { show.go(); return x32.channelFaderAddress(1); } };",
    }),
    runtimeServices: new ControlScriptRuntimeServices());
var faderAddress = (string)host.Invoke("aot.mnd", "run");

// Host subtitle factory (FFmpeg decode + libass render glue). A missing path returns null, but the FFmpeg/libass
// path is statically reachable from here, so NativeAOT compiles it.
var subtitle = SubtitleOverlayFactory.FromFile("aot-nonexistent.srt", 1280, 720);

Console.WriteLine(
    $"MFPlayer.Next AOT smoke OK — control profiles={profileCount}, mond+helper={faderAddress}, " +
    $"subtitle factory reachable={(subtitle is null)}");
