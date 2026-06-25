using Microsoft.Extensions.Logging;

namespace S.Media.Core.Diagnostics;

/// <summary>One native-boundary resource that could not be shut down within its cooperative join cap.</summary>
public sealed record NativeResourceStuckRecord(
    DateTimeOffset TimestampUtc,
    string Owner,
    string ResourceKind,
    string? Detail,
    TimeSpan JoinTimeout,
    Exception? Exception);

/// <summary>
/// Process-level registry for leak-over-use-after-dispose fallbacks. Native wrappers should report here
/// when a worker thread remains inside native code after its bounded join, so hosts can surface a single
/// "restart recommended" health signal instead of requiring operators to inspect individual logs.
/// </summary>
public static class NativeResourceHealth
{
    private const int MaxRecords = 256;
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Core.Diagnostics.NativeResourceHealth");
    private static readonly object Gate = new();
    private static readonly List<NativeResourceStuckRecord> Records = new();

    public static event Action<NativeResourceStuckRecord>? StuckResourceRecorded;

    public static IReadOnlyList<NativeResourceStuckRecord> Snapshot()
    {
        lock (Gate)
            return Records.ToArray();
    }

    public static void Clear()
    {
        lock (Gate)
            Records.Clear();
    }

    public static NativeResourceStuckRecord ReportStuck(
        string owner,
        string resourceKind,
        string? detail,
        TimeSpan joinTimeout,
        Exception? exception = null)
    {
        var record = new NativeResourceStuckRecord(
            DateTimeOffset.UtcNow,
            owner,
            resourceKind,
            detail,
            joinTimeout,
            exception);

        lock (Gate)
        {
            Records.Add(record);
            if (Records.Count > MaxRecords)
                Records.RemoveRange(0, Records.Count - MaxRecords);
        }

        if (exception is not null)
        {
            Trace.LogError(
                exception,
                "Native resource stuck: owner={Owner} kind={Kind} detail={Detail} joinTimeout={Timeout}",
                owner,
                resourceKind,
                detail ?? "<none>",
                joinTimeout);
        }
        else
        {
            Trace.LogError(
                "Native resource stuck: owner={Owner} kind={Kind} detail={Detail} joinTimeout={Timeout}",
                owner,
                resourceKind,
                detail ?? "<none>",
                joinTimeout);
        }

        StuckResourceRecorded?.Invoke(record);
        return record;
    }
}
