using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace S.Media.Core.Diagnostics;

/// <summary>
/// Optional framework-wide logger plumbing for subscriber/output failures, informational
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

    /// <summary>
    /// Starts a low-overhead timing scope for lifecycle/native-boundary operations. Returns
    /// <see langword="null"/> when the logger would not emit trace, the completion level, or slow warnings.
    /// </summary>
    /// <remarks>
    /// Use this around operations that can block or hide native I/O latency (open, seek, stop, pump drain,
    /// hardware start). Avoid placing it in per-frame or per-audio-chunk hot paths unless the call is already
    /// guarded by <see cref="ILogger.IsEnabled(LogLevel)"/>.
    /// </remarks>
    public static TimedLogScope? BeginTimedOperation(
        ILogger? logger,
        string operation,
        LogLevel completionLevel = LogLevel.Debug,
        double slowWarningMs = 250,
        bool logStartAtTrace = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        logger ??= _logger;
        if (logger is null)
            return null;

        if (!logger.IsEnabled(LogLevel.Trace) &&
            !logger.IsEnabled(completionLevel) &&
            !(slowWarningMs > 0 && logger.IsEnabled(LogLevel.Warning)))
            return null;

        return new TimedLogScope(logger, operation, completionLevel, slowWarningMs, logStartAtTrace);
    }

    /// <summary>Returns elapsed milliseconds since a <see cref="Stopwatch.GetTimestamp"/> value.</summary>
    public static double ElapsedMillisecondsSince(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

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

    /// <summary>
    /// Run <paramref name="dispose"/> and swallow any exception after logging it with
    /// <paramref name="label"/>. Used by every <c>Dispose</c>/teardown that
    /// must tolerate sub-disposable failures without aborting the rest of the cleanup chain.
    /// </summary>
    public static void SwallowDisposeErrors(Action dispose, string label)
    {
        ArgumentNullException.ThrowIfNull(dispose);
        try
        {
            dispose();
        }
        catch (Exception ex)
        {
            LogError(ex, label);
        }
    }

    /// <summary>
    /// Disposable timing helper returned by <see cref="BeginTimedOperation"/>. Call
    /// <see cref="SetOutcome"/> before disposal when the final log should include a result label.
    /// </summary>
    public sealed class TimedLogScope : IDisposable
    {
        private readonly ILogger _logger;
        private readonly string _operation;
        private readonly LogLevel _completionLevel;
        private readonly double _slowWarningMs;
        private readonly long _startTimestamp;
        private string? _outcome;
        private int _disposed;

        internal TimedLogScope(
            ILogger logger,
            string operation,
            LogLevel completionLevel,
            double slowWarningMs,
            bool logStartAtTrace)
        {
            _logger = logger;
            _operation = operation;
            _completionLevel = completionLevel;
            _slowWarningMs = slowWarningMs;
            _startTimestamp = Stopwatch.GetTimestamp();

            if (logStartAtTrace && _logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("{Operation}: started", _operation);
        }

        /// <summary>Elapsed milliseconds from scope creation to now.</summary>
        public double ElapsedMilliseconds => ElapsedMillisecondsSince(_startTimestamp);

        /// <summary>Adds a short result label to the eventual completion log.</summary>
        public void SetOutcome(string outcome)
        {
            if (!string.IsNullOrWhiteSpace(outcome))
                _outcome = outcome;
        }

        /// <summary>Emits an in-flight checkpoint with the elapsed time so far.</summary>
        public void Checkpoint(string checkpoint, LogLevel level = LogLevel.Trace)
        {
            if (!_logger.IsEnabled(level))
                return;

            _logger.Log(level,
                "{Operation}: {Checkpoint} at {ElapsedMs:0.00}ms",
                _operation,
                checkpoint,
                ElapsedMilliseconds);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            var elapsedMs = ElapsedMilliseconds;
            var outcome = _outcome ?? "completed";
            if (_slowWarningMs > 0 && elapsedMs >= _slowWarningMs && _logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    "{Operation}: slow completion in {ElapsedMs:0.00}ms (threshold={ThresholdMs:0.00}ms, outcome={Outcome})",
                    _operation,
                    elapsedMs,
                    _slowWarningMs,
                    outcome);
                return;
            }

            if (_logger.IsEnabled(_completionLevel))
            {
                _logger.Log(
                    _completionLevel,
                    "{Operation}: completed in {ElapsedMs:0.00}ms (outcome={Outcome})",
                    _operation,
                    elapsedMs,
                    outcome);
            }
        }
    }
}
