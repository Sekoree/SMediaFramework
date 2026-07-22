using Microsoft.Extensions.Logging;

namespace HaViz.Android.Platform;

/// <summary>Routes framework logs (MediaDiagnostics et al.) to logcat under the tag "HaViz" -
/// without this every renderer/NDI error disappears into NullLogger.</summary>
public sealed class LogcatLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new LogcatLogger(categoryName);

    public void Dispose()
    {
    }

    private sealed class LogcatLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = $"[{category}] {formatter(state, exception)}{(exception is null ? "" : $"\n{exception}")}";
            _ = logLevel switch
            {
                LogLevel.Trace or LogLevel.Debug => global::Android.Util.Log.Debug("HaViz", message),
                LogLevel.Information => global::Android.Util.Log.Info("HaViz", message),
                LogLevel.Warning => global::Android.Util.Log.Warn("HaViz", message),
                _ => global::Android.Util.Log.Error("HaViz", message),
            };
        }
    }
}
