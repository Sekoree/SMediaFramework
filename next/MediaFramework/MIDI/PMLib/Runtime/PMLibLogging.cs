using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PMLib.Runtime;

public static class PMLibLogging
{
    private static readonly Lock Gate = new();

    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    public static void Configure(ILoggerFactory? loggerFactory)
    {
        lock (Gate)
            _factory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public static ILogger GetLogger(string category) => _factory.CreateLogger(category);


    public static string PtrMeta(nint ptr) => ptr == nint.Zero ? "0x0" : $"0x{ptr:X}";
}
