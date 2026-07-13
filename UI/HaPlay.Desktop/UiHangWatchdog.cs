using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace HaPlay.Desktop;

/// <summary>
/// Watches the Avalonia UI dispatcher for hangs. A low-priority heartbeat timer stamps the UI thread's
/// liveness once a second; a dedicated background thread raises the alarm when that stamp goes stale for
/// longer than the configured threshold - i.e. the UI thread stopped pumping (a freeze), while the rest
/// of the process (audio, background workers) keeps running.
/// <para>
/// On a hang it: (1) logs loudly at Critical and appends to the crash log; (2) records a native
/// per-thread state summary (which OS threads are running vs waiting, and their wait reason - a UI thread
/// wedged in native X11 looks different from one blocked on a managed lock); (3) best-effort captures a
/// FULL process dump via the runtime's <c>createdump</c> so the frozen UI thread's managed stack can be
/// read later with <c>dotnet-dump analyze &lt;file&gt;</c> → <c>clrstack -all</c> / <c>parallelstacks</c>.
/// </para>
/// Deliberately dependency-free and AOT-safe: no ClrMD, no attempt to walk another managed thread's stack
/// in-process (the CLR exposes no such API). The dump is the artifact that carries the real stack.
/// </summary>
internal static partial class UiHangWatchdog
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Desktop.UiHangWatchdog");
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);

    private static long _lastBeatTimestamp;
    private static TimeSpan _threshold;
    private static string _logDirectory = "";
    private static int _installed;
    private static bool _dumpedThisHang;
    private static DispatcherTimer? _heartbeat;
    private static readonly ManualResetEventSlim StopSignal = new(false);

    /// <summary>Resolves the configured threshold from CLI/env (default 8 s). <c>0</c>/<c>off</c> disables.
    /// Longer than a plausible gen2 GC pause or a big folder enumeration, short enough to catch a real
    /// lock-up while the user is still looking at it.</summary>
    public static TimeSpan ResolveThreshold(string? cliValue)
    {
        var raw = cliValue ?? Environment.GetEnvironmentVariable("HAPLAY_UI_HANG_TIMEOUT");
        if (string.IsNullOrWhiteSpace(raw))
            return TimeSpan.FromSeconds(8);
        if (string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "disabled", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.Zero;
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        return TimeSpan.Zero; // "0" or unparseable → disabled
    }

    /// <summary>Arms the watchdog. Must be called on the UI thread (creates the dispatcher heartbeat).</summary>
    public static void Install(string logDirectory, TimeSpan threshold)
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;
        if (threshold <= TimeSpan.Zero)
        {
            Trace.LogInformation("UI hang watchdog disabled (threshold <= 0)");
            return;
        }

        _logDirectory = logDirectory;
        _threshold = threshold;
        _lastBeatTimestamp = Stopwatch.GetTimestamp();

        // Heartbeat on the dispatcher at Background priority: a busy-but-alive render/input loop still
        // beats within the threshold, but a fully wedged loop stops beating → the monitor detects it.
        _heartbeat = new DispatcherTimer(HeartbeatInterval, DispatcherPriority.Background, OnHeartbeat);
        _heartbeat.Start();

        var monitor = new Thread(MonitorLoop) { IsBackground = true, Name = "UiHangWatchdog" };
        monitor.Start();

        Trace.LogInformation(
            "UI hang watchdog armed: threshold={ThresholdMs}ms logDir={LogDir} (tune with --ui-hang-timeout <sec> or HAPLAY_UI_HANG_TIMEOUT; 'off' disables)",
            (int)threshold.TotalMilliseconds, logDirectory);

        MaybeScheduleSelfTest();
    }

    /// <summary>Diagnostic self-test: with <c>HAPLAY_UI_HANG_SELFTEST=&lt;seconds&gt;</c> the watchdog
    /// deliberately blocks the UI thread once, a few seconds after arming, so you can confirm the alarm
    /// fires and a dump lands. Never runs without the env var - it is a validation tool, not a feature.</summary>
    private static void MaybeScheduleSelfTest()
    {
        var raw = Environment.GetEnvironmentVariable("HAPLAY_UI_HANG_SELFTEST");
        if (string.IsNullOrWhiteSpace(raw)
            || !double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || seconds <= 0)
            return;

        var block = TimeSpan.FromSeconds(seconds);
        Trace.LogWarning("UI hang SELF-TEST armed: the UI thread will be blocked for {Seconds}s in 4s.", seconds);
        // One-shot: fire 4 s after arming, then freeze the dispatcher for the requested span.
        DispatcherTimer? oneShot = null;
        oneShot = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Normal, (_, _) =>
        {
            oneShot!.Stop();
            Trace.LogWarning("UI hang SELF-TEST: blocking the UI thread now for {Ms}ms", (int)block.TotalMilliseconds);
            Thread.Sleep(block); // intentional dispatcher freeze - the watchdog should catch this
        });
        oneShot.Start();
    }

    public static void Shutdown()
    {
        StopSignal.Set();
        try { _heartbeat?.Stop(); }
        catch { /* shutting down */ }
    }

    private static void OnHeartbeat(object? sender, EventArgs e) =>
        Volatile.Write(ref _lastBeatTimestamp, Stopwatch.GetTimestamp());

    private static void MonitorLoop()
    {
        // Poll at the heartbeat cadence; StopSignal doubles as the sleep so shutdown is prompt.
        while (!StopSignal.Wait(HeartbeatInterval))
        {
            var stale = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastBeatTimestamp));
            if (stale >= _threshold)
            {
                if (!_dumpedThisHang)
                {
                    _dumpedThisHang = true; // one capture per hang episode - don't spam while it stays wedged
                    OnHang(stale);
                }
            }
            else if (_dumpedThisHang)
            {
                _dumpedThisHang = false;
                Trace.LogWarning("UI thread recovered from hang (last stall ~{StalledMs}ms)", (int)stale.TotalMilliseconds);
                DesktopCrashDiagnostics.AppendReport(
                    $"UI_HANG_RECOVERED lastStallMs={(int)stale.TotalMilliseconds}", "");
            }
        }
    }

    private static void OnHang(TimeSpan stale)
    {
        var report = BuildThreadReport();
        Trace.LogCritical(
            "UI THREAD HANG: no dispatcher heartbeat for {StalledMs}ms (threshold {ThresholdMs}ms). Capturing diagnostics.\n{Report}",
            (int)stale.TotalMilliseconds, (int)_threshold.TotalMilliseconds, report);
        DesktopCrashDiagnostics.AppendReport(
            $"UI_HANG stalledMs={(int)stale.TotalMilliseconds} thresholdMs={(int)_threshold.TotalMilliseconds} pid={Environment.ProcessId}",
            report);

        var dumpPath = TryCaptureDump();
        if (dumpPath is not null)
        {
            Trace.LogCritical(
                "UI hang: full process dump written to {DumpPath}. Analyze with: dotnet-dump analyze \"{DumpPath}\"  then  clrstack -all  (or  parallelstacks)",
                dumpPath, dumpPath);
            DesktopCrashDiagnostics.AppendReport($"UI_HANG_DUMP path={dumpPath}", "");
        }
        else
        {
            Trace.LogCritical(
                "UI hang: automatic dump unavailable. Capture one NOW while it's frozen: dotnet-dump collect -p {Pid}",
                Environment.ProcessId);
            DesktopCrashDiagnostics.AppendReport(
                $"UI_HANG_DUMP unavailable - run: dotnet-dump collect -p {Environment.ProcessId}", "");
        }
    }

    /// <summary>Native per-thread snapshot (id, state, wait reason, cumulative CPU). Not managed stacks -
    /// the CLR has no in-process API for that - but enough to see which thread is wedged and how.</summary>
    private static string BuildThreadReport()
    {
        var sb = new StringBuilder();
        try
        {
            using var proc = Process.GetCurrentProcess();
            sb.Append("threadCount=").Append(proc.Threads.Count).Append('\n');
            foreach (ProcessThread t in proc.Threads)
            {
                sb.Append("  tid=").Append(t.Id).Append(" state=").Append(t.ThreadState);
                try
                {
                    if (t.ThreadState == System.Diagnostics.ThreadState.Wait)
                        sb.Append(" wait=").Append(t.WaitReason);
                }
                catch (Exception)
                {
                    // WaitReason is unavailable on some platforms / transient states - skip it.
                }

                try { sb.Append(" cpuMs=").Append(t.TotalProcessorTime.TotalMilliseconds.ToString("F0")); }
                catch (Exception) { /* per-thread CPU not always readable */ }
                sb.Append('\n');
            }
        }
        catch (Exception ex)
        {
            sb.Append("thread enumeration failed: ").Append(ex.Message);
        }

        return sb.ToString();
    }

    /// <summary>Best-effort full-heap dump via the runtime's bundled <c>createdump</c>. Returns the dump
    /// path, or null when unavailable (self-contained/AOT build with no createdump, or the OS blocked it).</summary>
    private static string? TryCaptureDump()
    {
        try
        {
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            var exe = OperatingSystem.IsWindows() ? "createdump.exe" : "createdump";
            var createdump = Path.Combine(runtimeDir, exe);
            if (!File.Exists(createdump))
            {
                Trace.LogWarning(
                    "createdump not found at {Path} (self-contained/AOT build?). Falling back to manual capture.",
                    createdump);
                return null;
            }

            Directory.CreateDirectory(_logDirectory);
            var dumpPath = Path.Combine(_logDirectory, $"haplay-uihang-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");

            // Linux yama ptrace_scope=1 blocks a child from ptracing its parent (the direction createdump
            // needs). Declaring "any tracer may attach" lets the createdump child snapshot us.
            if (OperatingSystem.IsLinux())
                AllowAnyPtracer();

            var psi = new ProcessStartInfo(createdump)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // Dump scope: default "normal" (thread stacks + module list, no heap) - small (a few MB) and
            // enough for `clrstack -all` / `parallelstacks` to reconstruct the frozen UI thread's managed
            // stack, which is the point. A --withheap/--full dump of a media app is hundreds of MB to a GB.
            // Override via HAPLAY_UI_HANG_DUMPTYPE=normal|withheap|triage|full when you need heap inspection.
            psi.ArgumentList.Add(DumpTypeFlag());
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(dumpPath);
            psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            try
            {
                using var p = Process.Start(psi);
                if (p is null)
                    return null;
                if (!p.WaitForExit(30_000))
                {
                    try { p.Kill(); } catch { /* already gone */ }
                    Trace.LogWarning("createdump timed out after 30s");
                    return null;
                }

                if (p.ExitCode != 0)
                {
                    Trace.LogWarning("createdump exited {Code}: {Err}", p.ExitCode, p.StandardError.ReadToEnd().Trim());
                    return null;
                }
            }
            finally
            {
                // Review M11: re-lock ptrace when the capture is over (success, failure, or timeout) -
                // process memory contains stream/REST credentials, so "any tracer may attach" must not
                // outlive the dump.
                if (OperatingSystem.IsLinux())
                    ResetPtracer();
            }

            if (!File.Exists(dumpPath))
                return null;

            var size = new FileInfo(dumpPath).Length;
            Trace.LogInformation(
                "UI hang dump captured: {Path} ({SizeMb:0} MB, type {Type})",
                dumpPath, size / 1024.0 / 1024.0, DumpTypeFlag());
            PruneOldDumps(keep: dumpPath);
            return dumpPath;
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "createdump attempt failed");
            return null;
        }
    }

    /// <summary>Retention (review M11): keep the newest few hang dumps and cap their total bytes so a
    /// recurring hang cannot fill the show machine's disk. The just-written dump is always kept.</summary>
    private static void PruneOldDumps(string keep)
    {
        const int maxDumps = 3;
        const long maxTotalBytes = 2L * 1024 * 1024 * 1024; // 2 GB across all retained dumps
        try
        {
            var dumps = Directory.GetFiles(_logDirectory, "haplay-uihang-*.dmp")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            long total = 0;
            var kept = 0;
            foreach (var dump in dumps)
            {
                total += dump.Length;
                kept++;
                var isNewest = string.Equals(dump.FullName, Path.GetFullPath(keep), StringComparison.Ordinal);
                if (!isNewest && (kept > maxDumps || total > maxTotalBytes))
                {
                    try
                    {
                        dump.Delete();
                        Trace.LogInformation("pruned old hang dump {Path} ({SizeMb:0} MB)",
                            dump.FullName, dump.Length / 1024.0 / 1024.0);
                    }
                    catch (Exception ex) { Trace.LogWarning(ex, "hang-dump prune failed for {Path}", dump.FullName); }
                }
            }
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "hang-dump retention sweep failed");
        }
    }

    private static string DumpTypeFlag() =>
        (Environment.GetEnvironmentVariable("HAPLAY_UI_HANG_DUMPTYPE") ?? "normal").Trim().ToLowerInvariant() switch
        {
            "withheap" => "--withheap", // GC heap + stacks - adds object inspection
            "triage" => "--triage",     // minimal + PII-scrubbed
            "full" => "--full",         // entire process address space - largest
            _ => "--normal",            // thread stacks + modules - smallest, enough for clrstack
        };

    // prctl(PR_SET_PTRACER, PR_SET_PTRACER_ANY): allow any process to ptrace us for the duration.
    private const int PR_SET_PTRACER = 0x59616d61;

    private static void AllowAnyPtracer()
    {
        try { prctl(PR_SET_PTRACER, new IntPtr(-1) /* PR_SET_PTRACER_ANY */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception) { /* not Linux / libc shape differs - createdump may still work as a descendant */ }
    }

    /// <summary>Revokes the temporary any-tracer grant (0 = no extra tracer beyond yama defaults).</summary>
    private static void ResetPtracer()
    {
        try { prctl(PR_SET_PTRACER, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception) { /* best effort */ }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int prctl(int option, IntPtr arg2, IntPtr arg3, IntPtr arg4, IntPtr arg5);
}
