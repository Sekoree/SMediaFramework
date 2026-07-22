using BenchmarkDotNet.Attributes;
using OSCLib;

namespace OSCLib.Benchmarks;

/// <summary>
/// Measures <see cref="OSCAddressMatcher.IsMatch"/> — the per-route address test. The current
/// implementation splits the address into a string[] and runs regexes even for purely literal
/// segments; a router with N routes pays this N times per incoming message. The mismatch case
/// matters as much as the match case (most routes reject most messages).
/// </summary>
[MemoryDiagnoser]
public class OscMatcherBenchmarks
{
    private const string Address = "/ch/01/mix/fader";

    [Benchmark]
    public bool LiteralMatch() => OSCAddressMatcher.IsMatch("/ch/01/mix/fader", Address);

    [Benchmark]
    public bool LiteralMismatch() => OSCAddressMatcher.IsMatch("/ch/02/mix/pan", Address);

    [Benchmark]
    public bool WildcardMatch() => OSCAddressMatcher.IsMatch("/ch/*/mix/fader", Address);

    [Benchmark]
    public bool WildcardMismatch() => OSCAddressMatcher.IsMatch("/bus/*/mix/fader", Address);

    /// <summary>32 literal routes + 4 wildcard routes, one incoming message — a realistic router pass.</summary>
    [Benchmark]
    public int RouterSweep36()
    {
        var hits = 0;
        for (var i = 0; i < 32; i++)
        {
            if (OSCAddressMatcher.IsMatch(RouterPatterns[i], Address))
                hits++;
        }

        for (var i = 32; i < RouterPatterns.Length; i++)
        {
            if (OSCAddressMatcher.IsMatch(RouterPatterns[i], Address))
                hits++;
        }

        return hits;
    }

    private static readonly string[] RouterPatterns = BuildPatterns();

    private static string[] BuildPatterns()
    {
        var patterns = new string[36];
        for (var i = 0; i < 32; i++)
            patterns[i] = $"/ch/{i + 1:00}/mix/fader";
        patterns[32] = "/ch/*/mix/fader";
        patterns[33] = "/bus/*/mix/fader";
        patterns[34] = "/ch/*/mix/pan";
        patterns[35] = "/dca/?/fader";
        return patterns;
    }
}
