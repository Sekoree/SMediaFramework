using System;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace HaPlay.Desktop;

internal static class DesktopCrashDiagnostics
{
    private static readonly object FileGate = new();
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Desktop.Crash");
    private static string? _crashFilePath;
    private static string? _rollingLogFilePath;
    private static int _installed;
    private static int _recordingFatal;
    private static bool _firstChanceEnabled;
    private static long _firstChanceCount;

    public static string? CrashFilePath => _crashFilePath;

    public static void Install(string logDirectory, string? rollingLogFilePath, string[] args)
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
            return;

        Directory.CreateDirectory(logDirectory);
        _crashFilePath = Path.Combine(logDirectory, $"haplay-crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _rollingLogFilePath = rollingLogFilePath;
        _firstChanceEnabled = HasArg(args, "--media-log-first-chance")
                              || string.Equals(Environment.GetEnvironmentVariable("HAPLAY_LOG_FIRST_CHANCE"), "1", StringComparison.OrdinalIgnoreCase);

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        NativeResourceHealth.StuckResourceRecorded += OnNativeResourceStuck;
        if (_firstChanceEnabled)
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;

        WriteSync("Crash diagnostics installed.");
        WriteSync(DescribeProcess(args));
        Trace.LogInformation(
            "Crash diagnostics installed: crashFile={CrashFile} rollingLog={RollingLog} firstChance={FirstChance}",
            _crashFilePath,
            _rollingLogFilePath ?? "<none>",
            _firstChanceEnabled);
    }

    /// <summary>Appends a free-form diagnostic report (heading + optional body) to the crash log. Used by
    /// the UI-hang watchdog to persist its findings alongside crash records, so a freeze leaves a durable
    /// trail in the same file even when nothing throws.</summary>
    public static void AppendReport(string heading, string body)
    {
        WriteSync(heading);
        if (!string.IsNullOrEmpty(body))
            WriteSync(body);
    }

    /// <summary>Copies the current rolling and crash logs beside a captured dump. The snapshot names
    /// share the dump stem, so later rolling retention can never separate incident context from stacks.</summary>
    public static void PinLogsBeside(string dumpPath)
    {
        var stem = Path.Combine(
            Path.GetDirectoryName(dumpPath) ?? ".",
            Path.GetFileNameWithoutExtension(dumpPath));
        CopyOpenFileSnapshot(_rollingLogFilePath, stem + ".run.log");
        CopyOpenFileSnapshot(_crashFilePath, stem + ".crash.log");
    }

    private static void CopyOpenFileSnapshot(string? source, string destination)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return;
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read);
            input.CopyTo(output);
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "could not pin incident log {Source} beside dump {DumpLog}", source, destination);
        }
    }

    public static void RecordFatalException(string source, Exception exception)
    {
        EnsureCrashFile();
        if (Interlocked.Exchange(ref _recordingFatal, 1) != 0)
            return;

        try
        {
            Trace.LogCritical(exception, "{Source}: fatal exception reached process boundary", source);
            WriteExceptionSync($"FATAL {source}", exception);
        }
        finally
        {
            Volatile.Write(ref _recordingFatal, 0);
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Trace.LogCritical(ex, "AppDomain unhandled exception; terminating={Terminating}", e.IsTerminating);
            WriteExceptionSync($"APPDOMAIN_UNHANDLED terminating={e.IsTerminating}", ex);
        }
        else
        {
            var text = e.ExceptionObject?.ToString() ?? "<null>";
            Trace.LogCritical("AppDomain unhandled non-Exception object; terminating={Terminating}: {Object}", e.IsTerminating, text);
            WriteSync($"APPDOMAIN_UNHANDLED_NON_EXCEPTION terminating={e.IsTerminating}: {text}");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Trace.LogError(e.Exception, "TaskScheduler unobserved task exception; marking observed after logging");
        WriteExceptionSync("UNOBSERVED_TASK_EXCEPTION", e.Exception);
        e.SetObserved();
    }

    private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Trace.LogCritical(e.Exception, "Avalonia UI dispatcher unhandled exception");
        WriteExceptionSync("AVALONIA_DISPATCHER_UNHANDLED", e.Exception);
        e.Handled = false;
    }

    private static void OnNativeResourceStuck(NativeResourceStuckRecord record)
    {
        if (record.Exception is { } ex)
        {
            Trace.LogError(
                ex,
                "Native resource stuck: owner={Owner} kind={Kind} detail={Detail} joinTimeout={Timeout}",
                record.Owner,
                record.ResourceKind,
                record.Detail ?? "<none>",
                record.JoinTimeout);
        }
        else
        {
            Trace.LogError(
                "Native resource stuck: owner={Owner} kind={Kind} detail={Detail} joinTimeout={Timeout}",
                record.Owner,
                record.ResourceKind,
                record.Detail ?? "<none>",
                record.JoinTimeout);
        }

        WriteSync(
            $"NATIVE_RESOURCE_STUCK owner={record.Owner} kind={record.ResourceKind} detail={record.Detail ?? "<none>"} timeout={record.JoinTimeout}");
        if (record.Exception is not null)
            WriteExceptionSync("NATIVE_RESOURCE_STUCK_EXCEPTION", record.Exception);
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        var count = Interlocked.Increment(ref _firstChanceCount);
        if (Trace.IsEnabled(LogLevel.Trace))
        {
            Trace.LogTrace(
                e.Exception,
                "First-chance exception #{Count}: type={Type} message={Message}",
                count,
                e.Exception.GetType().FullName,
                e.Exception.Message);
        }
    }

    private static void WriteExceptionSync(string heading, Exception exception)
    {
        var sb = new StringBuilder();
        sb.Append(DateTimeOffset.Now.ToString("O"))
            .Append(' ')
            .Append(heading)
            .AppendLine();
        sb.Append("threadId=").Append(Environment.CurrentManagedThreadId)
            .Append(" threadName=").Append(Thread.CurrentThread.Name ?? "<none>")
            .AppendLine();
        sb.AppendLine(exception.ToString());
        WriteSync(sb.ToString().TrimEnd());
    }

    private static void WriteSync(string line)
    {
        EnsureCrashFile();
        var path = _crashFilePath;
        if (path is null)
            return;

        lock (FileGate)
        {
            try
            {
                File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine($"DesktopCrashDiagnostics: failed writing crash log: {ex}"); }
                catch { /* process may be terminating */ }
            }
        }
    }

    private static void EnsureCrashFile()
    {
        if (_crashFilePath is not null)
            return;

        lock (FileGate)
        {
            if (_crashFilePath is not null)
                return;

            try
            {
                var dir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
                Directory.CreateDirectory(dir);
                _crashFilePath = Path.Combine(dir, $"haplay-crash-fallback-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            }
            catch
            {
                _crashFilePath = null;
            }
        }
    }

    private static string DescribeProcess(string[] args)
    {
        using var process = Process.GetCurrentProcess();
        return string.Join(Environment.NewLine, new[]
        {
            $"pid={Environment.ProcessId}",
            $"process={process.ProcessName}",
            $"startedUtc={DateTimeOffset.UtcNow:O}",
            $"os={RuntimeInformation.OSDescription}",
            $"arch={RuntimeInformation.ProcessArchitecture}",
            $"runtime={RuntimeInformation.FrameworkDescription}",
            $"serverGc={GCSettings.IsServerGC}",
            $"currentDirectory={Directory.GetCurrentDirectory()}",
            $"commandLine={Environment.CommandLine}",
            $"args={string.Join(' ', args)}",
            $"rollingLog={_rollingLogFilePath ?? "<none>"}",
            $"firstChance={_firstChanceEnabled}",
        });
    }

    private static bool HasArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
