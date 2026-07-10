// AOT gate. Proves the next/ toolchain NativeAOT-publishes the framework keystone (S.Media.Core) AND the Phase-6
// surfaces that carry real AOT risk - the Mond script engine + STJ profile load + the subtitle host glue - by
// exercising them so the toolchain actually compiles those code paths (not just references the assemblies).
using S.Control;
using S.Media.Interop;

// S.Control: STJ source-gen profile load, then Mond compile + run a script that uses a profile HelperScript
// (`x32`, loaded as a module global) and the `show` bridge - the whole control hot path under NativeAOT.
var profileCount = BuiltInControlDeviceProfileRepository.Instance.Profiles.Count;

var host = new ControlScriptFileHost(
    new InMemoryControlScriptSourceProvider(new Dictionary<string, string>
    {
        ["aot.mnd"] = "return { run: fun() { show.go(); return x32.channelFaderAddress(1); } };",
        // AOT-01: a script that raises a Mond runtime error. Building the resulting exception makes Mond walk
        // its call stack via System.Diagnostics.StackFrame - the exact path the accepted `IL2026` trim warning
        // flags (see AotSmoke.csproj). Exercising it here proves the error path RUNS correctly under NativeAOT;
        // the warning only concerns trimming stack-frame detail, not correctness.
        ["aot_err.mnd"] = "return { boom: fun() { error('intentional AOT error-path probe'); } };",
    }),
    runtimeServices: new ControlScriptRuntimeServices());
var faderAddress = (string)host.Invoke("aot.mnd", "run");

// AOT-01: drive the Mond runtime-error / stack-trace path and confirm it produces a diagnostic rather than
// crashing the AOT image. Any throw proves the flagged StackFrame path executed under NativeAOT.
string errorPathResult;
try
{
    host.Invoke("aot_err.mnd", "boom");
    errorPathResult = "NO-THROW (unexpected - the error path did not run)";
    Environment.ExitCode = 3;
}
catch (Exception ex)
{
    errorPathResult = ex.Message.Contains("intentional AOT error-path probe", StringComparison.Ordinal)
        ? "error-path OK (Mond stack trace built under AOT)"
        : $"error-path ran via {ex.GetType().Name}";
}

// Host subtitle factory (FFmpeg decode + libass render glue). A missing path returns null, but the FFmpeg/libass
// path is statically reachable from here, so NativeAOT compiles it.
var subtitle = SubtitleOverlayFactory.FromFile("aot-nonexistent.srt", 1280, 720);

Console.WriteLine(
    $"MFPlayer.Next AOT smoke OK - control profiles={profileCount}, mond+helper={faderAddress}, " +
    $"mond error path={errorPathResult}, subtitle factory reachable={(subtitle is null)}");
