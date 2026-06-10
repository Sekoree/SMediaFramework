using System;
using System.IO;
using Avalonia;
using HaPlay.Playback;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace HaPlay.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        ConfigureLogging(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>
    /// Wires <see cref="MediaDiagnostics.LoggerFactory"/> to console + rolling file outputs. Runs
    /// before <see cref="BuildAvaloniaApp"/> so any static-field logger captured by media framework
    /// classes resolves through this factory.
    /// </summary>
    /// <remarks>
    /// CLI overrides:
    /// <c>--media-log-level trace|debug|information|warning|error</c> sets the framework verbosity
    /// (default <c>debug</c>); <c>--media-log-dir &lt;path&gt;</c> overrides the file destination
    /// (default <c>%TMPDIR%/HaPlay/logs</c>);
    /// <c>--media-live-uyvy-passthrough</c> skips live UYVY→BGRA conversion (native SDL UYVY path).
    /// </remarks>
    private static void ConfigureLogging(string[] args)
    {
        // --media-log off disables logging entirely (skips factory creation, file open, console
        // provider). Useful when triaging whether logging is implicated in a misbehavior.
        var raw = GetArg(args, "--media-log");
        if (string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("HaPlay.Desktop logging disabled via --media-log off");
            return;
        }

        var level = ParseLevel(GetArg(args, "--media-log-level"), LogLevel.Debug);
        var dir = GetArg(args, "--media-log-dir")
                  ?? Path.Combine(Directory.GetCurrentDirectory(), "logs");

        var fileProvider = new RollingFileLoggerProvider(new RollingFileLoggerOptions
        {
            Directory = dir,
            FileNamePrefix = "haplay",
            MinimumLevel = level,
            RetainCount = 10,
        });

        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(level);
            builder.AddSimpleConsole(o =>
            {
                o.IncludeScopes = false;
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss.fff ";
            });
            builder.AddProvider(fileProvider);
        });

        MediaDiagnostics.LoggerFactory = factory;

        if (HasArg(args, "--media-live-uyvy-passthrough"))
        {
            //PlaybackVideoPipeline.CliRequestedUyvyPassthrough = true;
            //PlaybackVideoPipeline.PreferNativePixelFormatForLiveVideo = true;
            MediaDiagnostics.LogInformation("HaPlay: live video using native pixel format (UYVY passthrough when source delivers UYVY)");
        }

        MediaDiagnostics.LogInformation(
            $"HaPlay.Desktop logging configured: minLevel={level} fileSink={fileProvider.FilePath}");

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { factory.Dispose(); } catch { /* shutdown best-effort */ }
        };
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

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static LogLevel ParseLevel(string? raw, LogLevel fallback) =>
        Enum.TryParse<LogLevel>(raw, ignoreCase: true, out var lvl) ? lvl : fallback;
}
