using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace S.Media.Core.Diagnostics;

/// <summary>
/// Optional framework-wide logger for subscriber/sink failures and informational
/// diagnostics that are intentionally non-fatal. Falls back to <see cref="Debug.WriteLine(string?)"/>
/// when no logger is assigned.
/// </summary>
public static class MediaDiagnostics
{
    private static ILogger? _logger;

    /// <summary>Assign once at host startup, or leave null for debug-only output.</summary>
    public static ILogger? Logger
    {
        get => _logger;
        set => _logger = value;
    }

    public static void LogWarning(string message, params object?[] args)
    {
        if (_logger is { } log)
        {
            if (args.Length > 0) log.LogWarning(message, args);
            else log.LogWarning("{Message}", message);
        }
        else
        {
            Debug.WriteLine($"[Media] {message}");
        }
    }

    public static void LogInformation(string message, params object?[] args)
    {
        if (_logger is { } log)
        {
            if (args.Length > 0) log.LogInformation(message, args);
            else log.LogInformation("{Message}", message);
        }
        else
        {
            Debug.WriteLine($"[Media] {message}");
        }
    }

    public static void LogError(Exception exception, string context)
    {
        if (_logger is { } log)
            log.LogError(exception, "{Context}", context);
        else
            Debug.WriteLine($"[Media] {context}: {exception}");
    }
}
