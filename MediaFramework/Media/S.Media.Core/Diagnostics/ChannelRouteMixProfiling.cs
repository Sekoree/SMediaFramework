using System.Diagnostics;
using System.Threading;

namespace S.Media.Core.Diagnostics;

/// <summary>
/// Opt-in counters for <see cref="AudioRouter.ApplyRoute"/> slow paths (scalar mixing and <see cref="ChannelMap.ApplyAdditive"/>
/// when no SIMD fast path matches).
/// Set environment variable <c>MF_MEDIA_PROFILE_CHANNEL_MAP=1</c> (or <c>true</c>) to enable global recording in apps.
/// For unit tests, call <see cref="EnterTestRecordingScope"/> and <see cref="SetTestOverride"/> together so parallel test workers
/// do not share static counters when the env var is set. Disabled by default.
/// </summary>
public static class ChannelRouteMixProfiling
{
    private static readonly bool EnvEnabled = ReadEnvFlag("MF_MEDIA_PROFILE_CHANNEL_MAP");
    /// <summary>0 = follow env, 1 = force on, 2 = force off.</summary>
    private static int _overrideState;

    private static readonly AsyncLocal<int> TestRecordingDepth = new();

    /// <summary>True when env or test override requests profiling (intent); see <see cref="ShouldProfileApplyRoute"/> for whether counters record.</summary>
    public static bool IsEnabled => Volatile.Read(ref _overrideState) switch
    {
        1 => true,
        2 => false,
        _ => EnvEnabled
    };

    /// <summary>
    /// When <see cref="SetTestOverride"/> forces profiling on (<c>1</c>), only threads inside <see cref="EnterTestRecordingScope"/> record.
    /// When following env (<c>0</c>) and env is set, every <see cref="AudioRouter.ApplyRoute"/> call records (production / manual runs).
    /// </summary>
    internal static bool ShouldProfileApplyRoute() =>
        Volatile.Read(ref _overrideState) switch
        {
            2 => false,
            1 => TestRecordingDepth.Value > 0,
            _ => EnvEnabled
        };

    /// <summary>
    /// Marks the current async flow as allowed to increment profiling counters while <see cref="SetTestOverride"/> is <c>true</c>.
    /// Dispose to exit. Nests safely.
    /// </summary>
    public static IDisposable EnterTestRecordingScope() => new TestRecordingScope();

    private readonly struct TestRecordingScope : IDisposable
    {
        public TestRecordingScope() => TestRecordingDepth.Value = TestRecordingDepth.Value + 1;

        public void Dispose()
        {
            var d = TestRecordingDepth.Value - 1;
            TestRecordingDepth.Value = d < 0 ? 0 : d;
        }
    }

    /// <summary>Number of <see cref="ChannelMap.ApplyAdditive"/> calls (steady-state gain exactly 1, fast accumulators all declined).</summary>
    public static long ApplyAdditiveCalls => Volatile.Read(ref _applyAdditiveCalls);

    /// <summary>High-resolution timestamp ticks spent inside <see cref="ChannelMap.ApplyAdditive"/> (same units as <see cref="Stopwatch"/>).</summary>
    public static long ApplyAdditiveTicksTotal => Volatile.Read(ref _applyAdditiveTicksTotal);

    /// <summary>Scalar per-sample loops with uniform non-unity gain (same gain both ends of chunk).</summary>
    public static long ScalarUniformGainLoopCalls => Volatile.Read(ref _scalarUniformCalls);

    public static long ScalarUniformGainLoopTicksTotal => Volatile.Read(ref _scalarUniformTicksTotal);

    /// <summary>Scalar per-sample loops with linear gain ramp across the chunk.</summary>
    public static long ScalarRampLoopCalls => Volatile.Read(ref _scalarRampCalls);

    public static long ScalarRampLoopTicksTotal => Volatile.Read(ref _scalarRampTicksTotal);

    private static long _applyAdditiveCalls;
    private static long _applyAdditiveTicksTotal;
    private static long _scalarUniformCalls;
    private static long _scalarUniformTicksTotal;
    private static long _scalarRampCalls;
    private static long _scalarRampTicksTotal;

    private static bool ReadEnvFlag(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(v)) return false;
        return v.Equals("1", StringComparison.OrdinalIgnoreCase)
               || v.Equals("true", StringComparison.OrdinalIgnoreCase)
               || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Clears all counters (for benchmarks or tests).</summary>
    public static void ResetCounters()
    {
        Interlocked.Exchange(ref _applyAdditiveCalls, 0);
        Interlocked.Exchange(ref _applyAdditiveTicksTotal, 0);
        Interlocked.Exchange(ref _scalarUniformCalls, 0);
        Interlocked.Exchange(ref _scalarUniformTicksTotal, 0);
        Interlocked.Exchange(ref _scalarRampCalls, 0);
        Interlocked.Exchange(ref _scalarRampTicksTotal, 0);
    }

    /// <summary>
    /// When non-null, overrides env-based enablement (use <c>null</c> to restore env only).
    /// Intended for unit tests; not thread-safe with concurrent decoding.
    /// </summary>
    public static void SetTestOverride(bool? enabled) =>
        Volatile.Write(ref _overrideState, enabled is null ? 0 : (enabled.Value ? 1 : 2));

    internal static void RecordApplyAdditive(long ticks)
    {
        Interlocked.Increment(ref _applyAdditiveCalls);
        Interlocked.Add(ref _applyAdditiveTicksTotal, ticks);
    }

    internal static void RecordScalarUniformGain(long ticks)
    {
        Interlocked.Increment(ref _scalarUniformCalls);
        Interlocked.Add(ref _scalarUniformTicksTotal, ticks);
    }

    internal static void RecordScalarRamp(long ticks)
    {
        Interlocked.Increment(ref _scalarRampCalls);
        Interlocked.Add(ref _scalarRampTicksTotal, ticks);
    }
}
