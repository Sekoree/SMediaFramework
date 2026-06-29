using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace HaPlay.Playback;

internal interface IPlaybackSleepInhibitorBackend
{
    IDisposable? Inhibit(string reason);
}

internal sealed class PlaybackSleepInhibitor
{
    public static PlaybackSleepInhibitor Default { get; } = new(PlatformPlaybackSleepInhibitorBackend.Instance);

    private readonly object _gate = new();
    private readonly IPlaybackSleepInhibitorBackend _backend;
    private IDisposable? _platformLease;
    private int _leaseCount;

    internal PlaybackSleepInhibitor(IPlaybackSleepInhibitorBackend backend) =>
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));

    internal int ActiveLeaseCount
    {
        get { lock (_gate) return _leaseCount; }
    }

    public IDisposable Acquire(string reason)
    {
        lock (_gate)
        {
            if (_leaseCount++ == 0)
            {
                try { _platformLease = _backend.Inhibit(reason); }
                catch { _platformLease = null; }
            }

            return new Lease(this);
        }
    }

    private void Release()
    {
        IDisposable? toDispose = null;
        lock (_gate)
        {
            if (_leaseCount <= 0)
                return;

            _leaseCount--;
            if (_leaseCount == 0)
            {
                toDispose = _platformLease;
                _platformLease = null;
            }
        }

        try { toDispose?.Dispose(); }
        catch { /* best effort */ }
    }

    private sealed class Lease : IDisposable
    {
        private PlaybackSleepInhibitor? _owner;

        public Lease(PlaybackSleepInhibitor owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

internal sealed class PlatformPlaybackSleepInhibitorBackend : IPlaybackSleepInhibitorBackend
{
    public static PlatformPlaybackSleepInhibitorBackend Instance { get; } = new();

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Playback.PlaybackSleepInhibitor");
    private static int _unsupportedLogged;
    private static int _failureLogged;

    private PlatformPlaybackSleepInhibitorBackend() { }

    public IDisposable? Inhibit(string reason)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return new WindowsExecutionStateLease(reason);
            if (OperatingSystem.IsLinux())
                return LinuxSystemdInhibitLease.TryStart(reason);

            LogUnsupportedOnce();
            return null;
        }
        catch (Exception ex)
        {
            LogFailureOnce(ex);
            return null;
        }
    }

    private static void LogUnsupportedOnce()
    {
        if (Interlocked.Exchange(ref _unsupportedLogged, 1) == 0)
            Trace.LogInformation("Playback sleep inhibition is not implemented on this OS.");
    }

    private static void LogFailureOnce(Exception ex)
    {
        if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
            Trace.LogWarning(ex, "Playback sleep inhibition could not be enabled.");
    }

    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
    }

    private sealed class WindowsExecutionStateLease : IDisposable
    {
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;
        private int _disposed;

        public WindowsExecutionStateLease(string reason)
        {
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "HaPlay sleep inhibitor: " + reason,
            };
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(1));
        }

        private void Run()
        {
            var enabled = SetThreadExecutionState(
                ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired) != 0;
            if (!enabled)
                LogFailureOnce(new InvalidOperationException("SetThreadExecutionState returned 0."));

            _ready.Set();
            _stop.Wait();

            if (enabled)
                _ = SetThreadExecutionState(ExecutionState.Continuous);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _stop.Set();
            if (!_thread.Join(TimeSpan.FromSeconds(2)))
                Trace.LogWarning("Windows sleep-inhibitor thread did not stop within the shutdown timeout.");
            _stop.Dispose();
            _ready.Dispose();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);
    }

    private sealed class LinuxSystemdInhibitLease : IDisposable
    {
        private readonly Process _process;
        private int _disposed;

        private LinuxSystemdInhibitLease(Process process) => _process = process;

        public static IDisposable? TryStart(string reason)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "systemd-inhibit",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--what=sleep:idle");
            psi.ArgumentList.Add("--mode=block");
            psi.ArgumentList.Add("--who=HaPlay");
            psi.ArgumentList.Add("--why=" + reason);
            psi.ArgumentList.Add("sleep");
            psi.ArgumentList.Add("2147483647");

            var process = Process.Start(psi);
            if (process is null)
                return null;

            if (process.WaitForExit(250))
            {
                var error = SafeReadToEnd(process.StandardError);
                process.Dispose();
                if (!string.IsNullOrWhiteSpace(error))
                    LogFailureOnce(new InvalidOperationException(error.Trim()));
                return null;
            }

            return new LinuxSystemdInhibitLease(process);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch { /* best effort */ }

            try { _process.WaitForExit(1000); }
            catch { /* best effort */ }
            _process.Dispose();
        }

        private static string SafeReadToEnd(StreamReader reader)
        {
            try { return reader.ReadToEnd(); }
            catch { return string.Empty; }
        }
    }
}
