using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NDILib.Runtime;

public static class NDILibLogging
{
    private static readonly Lock Gate = new();

    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    /// <summary>
    /// Configures the logger factory used by all NDILib logging.
    /// Safe to call multiple times — the factory is updated on every call.
    /// </summary>
    public static void Configure(ILoggerFactory? loggerFactory)
    {
        lock (Gate)
            _factory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public static ILogger GetLogger(string category) => _factory.CreateLogger(category);

    public static string PtrMeta(nint ptr) => ptr == nint.Zero ? "0x0" : $"0x{ptr:X}";
}
