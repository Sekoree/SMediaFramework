using System.Diagnostics;

namespace S.Media.Core;

/// <summary>Ad-hoc diagnostics helpers for HaPlay / framework profiling.</summary>
public static class SDebug
{
    /// <summary>Legacy per-key timestamps. Prefer <see cref="ChangeTrace"/> for transport profiling.</summary>
    public static Dictionary<string, long> TraceTime { get; } = new();

    /// <summary>
    /// Segment timer for playlist / transport traces. Logs <c>+Δms (total Tms) label</c> per step.
    /// Enable with <c>MF_HAPLAY_CHANGE_TRACE=1</c> (default on when unset). Set <c>0</c> to disable.
    /// </summary>
    public static class ChangeTrace
    {
        private static long _originTicks;
        private static long _lastTicks;
        private static int _depth;

        public static bool IsActive => _depth > 0;

        public static bool Enabled { get; } = ReadEnabledFromEnvironment();

        public static void Begin(string origin)
        {
            if (!Enabled)
                return;

            if (_depth == 0)
            {
                var now = Stopwatch.GetTimestamp();
                _originTicks = now;
                _lastTicks = now;
            }

            _depth++;
            Console.WriteLine($"[ChangeTrace] BEGIN #{_depth} {origin}");
        }

        public static void Step(string label)
        {
            if (!Enabled || _depth == 0)
                return;

            var now = Stopwatch.GetTimestamp();
            var deltaMs = TicksToMs(now - _lastTicks);
            var totalMs = TicksToMs(now - _originTicks);
            _lastTicks = now;
            Console.WriteLine($"[ChangeTrace] +{deltaMs:F1}ms (total {totalMs:F1}ms) {label}");
        }

        public static void End(string? label = null)
        {
            if (!Enabled || _depth == 0)
                return;

            if (!string.IsNullOrEmpty(label))
                Step(label);

            var totalMs = TicksToMs(Stopwatch.GetTimestamp() - _originTicks);
            Console.WriteLine($"[ChangeTrace] DONE (total {totalMs:F1}ms, depth was {_depth})");
            _depth = 0;
        }

        /// <summary>Milliseconds between two <see cref="Stopwatch"/> timestamps.</summary>
        public static double TicksToMs(long tickDelta) =>
            tickDelta * 1000.0 / Stopwatch.Frequency;

        private static bool ReadEnabledFromEnvironment()
        {
            var v = Environment.GetEnvironmentVariable("MF_HAPLAY_CHANGE_TRACE");
            if (string.IsNullOrWhiteSpace(v))
                return true;
            return v is not ("0" or "false" or "False" or "FALSE" or "no" or "No" or "NO");
        }
    }
}
