using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using S.Media.Core.Diagnostics;

namespace HaPlay.ViewModels;

/// <summary>
/// Relaunches the desktop app so a persisted appearance change (base theme / variant / density) is composed
/// fresh at startup. Starts a new instance of the current executable (forwarding the original CLI args), then
/// asks the desktop lifetime to shut down - which runs the normal teardown (ShowSession cleanup +
/// MediaRuntime.Shutdown) so native holds are released cleanly. If the relaunch can't be started, the app is
/// left running (the saved choice still applies on the next manual restart) rather than exiting into nothing.
/// </summary>
internal static class AppRestart
{
    public static void Restart()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            MediaDiagnostics.LogWarning("App restart: ProcessPath unavailable; cannot relaunch - staying open.");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
            // Forward the original args (log config, etc.); index 0 is the executable/assembly itself.
            var args = Environment.GetCommandLineArgs();
            for (var i = 1; i < args.Length; i++)
                psi.ArgumentList.Add(args[i]);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogWarning(
                $"App restart: relaunch failed ({ex.GetType().Name}: {ex.Message}); staying open.");
            return;
        }

        desktop.Shutdown();
    }
}
