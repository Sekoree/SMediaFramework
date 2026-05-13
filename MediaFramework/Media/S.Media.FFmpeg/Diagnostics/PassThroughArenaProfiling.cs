using System.Diagnostics;
using System.Threading;

namespace S.Media.FFmpeg.Diagnostics;

/// <summary>
/// Opt-in counters for pass-through descriptor arena lock hold times in <see cref="Video.VideoFileDecoder"/>
/// and <see cref="MediaContainerSharedDemux"/>. Set <c>MF_MEDIA_PROFILE_PASS_THROUGH_ARENA=1</c> (or <c>true</c>) to enable.
/// Measures time while the lock is held (critical-section duration), not queue wait.
/// On the return path, <c>Array.Clear</c> on descriptor arrays runs immediately before entering the lock,
/// so return lock ticks exclude that work.
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

    private static long _rentCalls;
    private static long _rentTicksTotal;
    private static long _rentMaxTicks;
    private static long _returnCalls;
    private static long _returnTicksTotal;
    private static long _returnMaxTicks;
    private static long _clearCalls;
    private static long _clearTicksTotal;

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
