using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JackLib;

/// <summary>
/// Logging configuration for the JackLib library.
/// Call <see cref="Configure"/> once at application startup to enable logging.
/// </summary>
public static class JackLogging
{
    private static readonly Lock Gate = new();
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    public static void Configure(ILoggerFactory? loggerFactory)
    {
        lock (Gate)
            _factory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public static ILogger GetLogger(string category) => _factory.CreateLogger(category);
    public static ILogger<T> GetLogger<T>() => _factory.CreateLogger<T>();
}

