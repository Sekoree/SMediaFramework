using System.Diagnostics;
using System.Threading;

namespace S.Media.Decode.FFmpeg.Diagnostics;

/// <summary>
/// Opt-in counters for pass-through descriptor arena operations in <see cref="Video.VideoFileDecoder"/>
/// and <see cref="MediaContainerSharedDemux"/>. Set <c>MF_MEDIA_PROFILE_PASS_THROUGH_ARENA=1</c> (or <c>true</c>) to enable.
/// Measures wall time for <see cref="S.Media.FFmpeg.Video.PassThroughDescriptorArena"/> rent/return/clear paths:
/// Treiber free-list CAS loops for rent/return (no <c>lock</c>); dispose records flag flip only.
/// On the return path, <c>Array.Clear</c> on descriptor arrays runs before Treiber push / early-out,
/// so return ticks exclude that work.
/// When enabled, failed <c>CompareExchange</c> attempts on the Treiber stacks increment
/// <see cref="TreiberCasRetries"/> (lab signal for whether an outer lock / structural change is worth pursuing).
/// For a deterministic per-arena mutex (no Treiber contention on a single arena), set <c>MF_MEDIA_PASS_THROUGH_ARENA_SERIALIZE=1</c>
/// — see <see cref="PassThroughArenaSerialization"/>.
/// </summary>
public static class PassThroughArenaProfiling
{
    private static readonly bool EnvEnabled = ReadEnvFlag("MF_MEDIA_PROFILE_PASS_THROUGH_ARENA");
    private static int _overrideState;

    public static bool IsEnabled => Volatile.Read(ref _overrideState) switch
    {
        1 => true,
        2 => false,
        _ => EnvEnabled
    };

    public static long RentLockCalls => Volatile.Read(ref _rentCalls);
    public static long RentLockTicksTotal => Volatile.Read(ref _rentTicksTotal);
    public static long RentLockMaxTicks => Volatile.Read(ref _rentMaxTicks);

    public static long ReturnLockCalls => Volatile.Read(ref _returnCalls);
    public static long ReturnLockTicksTotal => Volatile.Read(ref _returnTicksTotal);
    public static long ReturnLockMaxTicks => Volatile.Read(ref _returnMaxTicks);

    public static long ClearLockCalls => Volatile.Read(ref _clearCalls);
    public static long ClearLockTicksTotal => Volatile.Read(ref _clearTicksTotal);

    /// <summary>Failed Treiber <c>CompareExchange</c> attempts (pop + push) while profiling is enabled.</summary>
    public static long TreiberCasRetries => Volatile.Read(ref _treiberCasRetries);

    private static long _rentCalls;
    private static long _rentTicksTotal;
    private static long _rentMaxTicks;
    private static long _returnCalls;
    private static long _returnTicksTotal;
    private static long _returnMaxTicks;
    private static long _clearCalls;
    private static long _clearTicksTotal;
    private static long _treiberCasRetries;

    private static bool ReadEnvFlag(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(v)) return false;
        return v.Equals("1", StringComparison.OrdinalIgnoreCase)
               || v.Equals("true", StringComparison.OrdinalIgnoreCase)
               || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public static void ResetCounters()
    {
        Interlocked.Exchange(ref _rentCalls, 0);
        Interlocked.Exchange(ref _rentTicksTotal, 0);
        Interlocked.Exchange(ref _rentMaxTicks, 0);
        Interlocked.Exchange(ref _returnCalls, 0);
        Interlocked.Exchange(ref _returnTicksTotal, 0);
        Interlocked.Exchange(ref _returnMaxTicks, 0);
        Interlocked.Exchange(ref _clearCalls, 0);
        Interlocked.Exchange(ref _clearTicksTotal, 0);
        Interlocked.Exchange(ref _treiberCasRetries, 0);
    }

    public static void SetTestOverride(bool? enabled) =>
        Volatile.Write(ref _overrideState, enabled is null ? 0 : (enabled.Value ? 1 : 2));

    internal static void RecordRent(long ticks)
    {
        Interlocked.Increment(ref _rentCalls);
        Interlocked.Add(ref _rentTicksTotal, ticks);
        UpdateMax(ref _rentMaxTicks, ticks);
    }

    internal static void RecordReturn(long ticks)
    {
        Interlocked.Increment(ref _returnCalls);
        Interlocked.Add(ref _returnTicksTotal, ticks);
        UpdateMax(ref _returnMaxTicks, ticks);
    }

    internal static void RecordClear(long ticks)
    {
        Interlocked.Increment(ref _clearCalls);
        Interlocked.Add(ref _clearTicksTotal, ticks);
    }

    internal static void RecordTreiberCasRetry() => Interlocked.Increment(ref _treiberCasRetries);

    private static void UpdateMax(ref long maxField, long value)
    {
        while (true)
        {
            var cur = Volatile.Read(ref maxField);
            if (value <= cur) return;
            if (Interlocked.CompareExchange(ref maxField, value, cur) == cur) return;
        }
    }
}
