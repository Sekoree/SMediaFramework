using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace S.Media.Core.Diagnostics;

/// <summary>
/// Optional framework-wide logger plumbing for subscriber/sink failures, informational
/// diagnostics, and verbose trace logs at the playback pipeline boundaries.
/// </summary>
/// <remarks>
/// <para>
/// Hosts configure logging by setting <see cref="LoggerFactory"/> once at startup (typically the
/// very first line of <c>Main</c>). Framework classes resolve per-category loggers via
/// <see cref="CreateLogger(string)"/> or <see cref="CreateLogger{T}"/> and cache them in a static
/// field. When no factory is installed, all category loggers resolve to <see cref="NullLogger"/>
/// (zero-cost) and the static helpers fall back to <see cref="Debug.WriteLine(string?)"/>.
/// </para>
/// <para>
/// The historical <see cref="Logger"/> property is preserved as a "default" logger used by the
/// static <see cref="LogTrace(string, object?[])"/> / <see cref="LogDebug(string, object?[])"/> /
/// <see cref="LogInformation(string, object?[])"/> / <see cref="LogWarning(string, object?[])"/>
/// / <see cref="LogError(Exception, string)"/> helpers. Setting <see cref="LoggerFactory"/>
/// auto-populates <see cref="Logger"/> with the <c>S.Media</c> category if it has not already been
/// assigned.
/// </para>
/// </remarks>
public static class MediaDiagnostics
{
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;
    private static ILogger? _logger;

    /// <summary>Default category logger used by the static helpers. Falls back to <see cref="Debug.WriteLine(string?)"/> when null.</summary>
    public static ILogger? Logger
    {
        get => _logger;
        set => _logger = value;
    }

    /// <summary>
    /// Optional <see cref="ILoggerFactory"/>; install once at host startup so framework classes can
    /// resolve per-category loggers via <see cref="CreateLogger(string)"/>. Defaults to
    /// <see cref="NullLoggerFactory.Instance"/> (every <see cref="CreateLogger(string)"/> returns
    /// <see cref="NullLogger.Instance"/>).
    /// </summary>
    /// <remarks>
    /// Setting this property also lazy-populates <see cref="Logger"/> with a <c>"S.Media"</c>
    /// category logger when <see cref="Logger"/> has not already been assigned, so the static log
    /// helpers light up automatically once a host has wired its factory.
    /// </remarks>
    public static ILoggerFactory LoggerFactory
    {
        get => _factory;
        set
        {
            _factory = value ?? NullLoggerFactory.Instance;
            _logger ??= _factory.CreateLogger("S.Media");
        }
    }

    /// <summary>
    /// Resolve a per-category logger from <see cref="LoggerFactory"/>. Returns <see cref="NullLogger.Instance"/>
    /// when no factory is installed; cheap to call and safe to cache in a static field.
    /// </summary>
    public static ILogger CreateLogger(string category) => _factory.CreateLogger(category);

    /// <summary>Resolve a logger keyed on the fully-qualified <typeparamref name="T"/> name.</summary>
    public static ILogger CreateLogger<T>() => _factory.CreateLogger<T>();

    public static void LogTrace(string message, params object?[] args)
    {
        if (_logger is { } log)
        {
            if (args.Length > 0) log.LogTrace(message, args);
            else log.LogTrace("{Message}", message);
        }
        else if (args.Length == 0)
        {
            Debug.WriteLine($"[Media TRACE] {message}");
        }
    }

    public static void LogDebug(string message, params object?[] args)
    {
        if (_logger is { } log)
        {
            if (args.Length > 0) log.LogDebug(message, args);
            else log.LogDebug("{Message}", message);
        }
        else if (args.Length == 0)
        {
            Debug.WriteLine($"[Media DEBUG] {message}");
        }
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
