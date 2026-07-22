using System.Diagnostics;

namespace S.Media.Core.Diagnostics;

/// <summary>
/// Lock-free hot-path timing counter: <see cref="Record"/> costs two interlocked adds plus a CAS max
/// update, and <see cref="Snapshot"/> is tear-tolerant (fields are read individually, so a snapshot
/// taken during a concurrent <see cref="Record"/> may mix adjacent samples - fine for diagnostics).
/// Callers time with <see cref="Stopwatch.GetTimestamp"/> deltas; consumers window/rate in their own
/// layer (the UI keeps the previous snapshot and diffs) - counters are never reset by readers.
/// </summary>
public sealed class TimingAccumulator
{
    private long _count;
    private long _totalTicks;
    private long _maxTicks;
    private long _lastTicks;

    /// <summary>Records one operation that took <paramref name="elapsedTicks"/> (Stopwatch ticks, from <see cref="Stopwatch.GetTimestamp"/> deltas).</summary>
    public void Record(long elapsedTicks)
    {
        if (elapsedTicks < 0)
            elapsedTicks = 0;
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _totalTicks, elapsedTicks);
        Volatile.Write(ref _lastTicks, elapsedTicks);
        var max = Volatile.Read(ref _maxTicks);
        while (elapsedTicks > max)
        {
            var seen = Interlocked.CompareExchange(ref _maxTicks, elapsedTicks, max);
            if (seen == max)
                break;
            max = seen;
        }
    }

    /// <summary>Convenience: records the time elapsed since <paramref name="startTimestamp"/> (a <see cref="Stopwatch.GetTimestamp"/> value).</summary>
    public void RecordSince(long startTimestamp) => Record(Stopwatch.GetTimestamp() - startTimestamp);

    /// <summary>Convenience: records an already-measured duration (converted from <see cref="TimeSpan"/> ticks to Stopwatch ticks).</summary>
    public void Record(TimeSpan elapsed) => Record((long)(elapsed.Ticks * (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond));

    public TimingSnapshot Snapshot()
    {
        var count = Volatile.Read(ref _count);
        var total = Volatile.Read(ref _totalTicks);
        var max = Volatile.Read(ref _maxTicks);
        var last = Volatile.Read(ref _lastTicks);
        return new TimingSnapshot(count, TicksToMs(total), count > 0 ? TicksToMs(total) / count : 0d, TicksToMs(max), TicksToMs(last));
    }

    private static double TicksToMs(long stopwatchTicks) => stopwatchTicks * 1000d / Stopwatch.Frequency;
}

/// <summary>
/// Point-in-time view of a <see cref="TimingAccumulator"/>. <see cref="Count"/> and <see cref="TotalMs"/>
/// are cumulative since creation so consumers can diff consecutive snapshots for windowed averages;
/// <see cref="AvgMs"/>/<see cref="MaxMs"/> are lifetime, <see cref="LastMs"/> is the most recent sample.
/// </summary>
public readonly record struct TimingSnapshot(long Count, double TotalMs, double AvgMs, double MaxMs, double LastMs)
{
    /// <summary>Average duration of the operations recorded between <paramref name="previous"/> and this snapshot (0 when none).</summary>
    public double WindowAvgMs(in TimingSnapshot previous)
    {
        var count = Count - previous.Count;
        return count > 0 ? (TotalMs - previous.TotalMs) / count : 0d;
    }
}
