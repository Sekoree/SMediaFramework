using System.Diagnostics;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Decode.FFmpeg;
using S.Media.Players;
using S.Media.Present.SDL3;

// Phase 5 multi-output smoke + the NXT-04 hardware-tier MEASURED cross-output skew gate. Opens one source
// through the registry (FFmpeg) and fans its video to TWO independent outputs via
// MediaPlayer.AttachVideoOutput → VideoRouter (per-branch negotiation/pumps). Each output is instrumented:
// every Submit records (frame PTS → monotonic wall instant), and at the end the same-PTS pairs reduce to a
// cross-output presentation-skew distribution that is GATED at one frame period (p95) - the measurable
// software proxy for "both displays show the same frame at the same time".
//
//   MultiOutputSmoke <media> [seconds=30] [--headless]
//
// Default (windows): two SDL3 GL windows - run on real displays for the hardware check (put one window on
// each physical output; the printed skew is the router+present-path skew, and eyes/camera confirm the
// glass). --headless: two discarding sinks (no display needed) - gates the router/fan-out skew on any box,
// the scheduled/CI tier of the same measurement.

var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var headless = args.Contains("--headless", StringComparer.OrdinalIgnoreCase);
if (positional.Length < 1)
{
    Console.Error.WriteLine("usage: MultiOutputSmoke <media-file-or-uri> [seconds=30] [--headless]");
    return 2;
}

var uri = positional[0];
var seconds = positional.Length > 1 && double.TryParse(positional[1], out var s) ? s : 30.0;

var registry = MediaRegistry.Build(b => b.Use(new FFmpegModule()));

// Software decode so the source publishes its pixel format up front (a hardware decoder only does so after
// its first frame, which the player's up-front negotiation needs primed). The router then fans CPU frames
// to two INDEPENDENT outputs (each does its own GL upload - no shared-texture constraint).
using var player = MediaPlayer.Open(registry, uri, new MediaPlayerOpenOptions { TryHardwareAcceleration = false });
Console.WriteLine($"opened '{uri}' - fanning to two {(headless ? "headless sinks" : "phase-locked windows")} for {seconds:0}s or until closed…");

var closed = false;
IVideoOutput sink1, sink2;
IDisposable? win1 = null, win2 = null;
if (headless)
{
    sink1 = new DiscardingVideoOutput();
    sink2 = new DiscardingVideoOutput();
}
else
{
    var w1 = new SDL3GLVideoOutput("Multi-Output 1/2", 960, 540);
    var w2 = new SDL3GLVideoOutput("Multi-Output 2/2", 960, 540);
    w1.CloseRequested += (_, _) => closed = true;
    w2.CloseRequested += (_, _) => closed = true;
    (sink1, win1) = (w1, w1);
    (sink2, win2) = (w2, w2);
}

using var _1 = win1;
using var _2 = win2;

var probe1 = new SkewProbeOutput(sink1);
var probe2 = new SkewProbeOutput(sink2);
player.AttachVideoOutput(probe1, "win1");
player.AttachVideoOutput(probe2, "win2");
player.Play();

var sw = Stopwatch.StartNew();
var nextReport = TimeSpan.FromSeconds(2);
while (sw.Elapsed.TotalSeconds < seconds && !closed && !player.Video.IsSourceExhausted)
{
    if (sw.Elapsed >= nextReport)
    {
        Console.WriteLine($"  t={sw.Elapsed.TotalSeconds,4:0}s  pos={player.Position:mm\\:ss\\.ff}  displayed={player.Video.DisplayedCount}  dropped(late)={player.Video.DroppedLate}");
        nextReport += TimeSpan.FromSeconds(2);
    }
    Thread.Sleep(100);
}

Console.WriteLine();
Console.WriteLine($"MultiOutputSmoke done - two outputs fanned {player.Video.DisplayedCount} frames " +
    $"({(closed ? "window closed" : player.Video.IsSourceExhausted ? "source ended" : "time elapsed")}).");

// --- NXT-04 measured cross-output skew gate ------------------------------------------------------------
// Same-PTS pairs across the two probes → |Δt| distribution. A frame that reached only one output (a late
// drop on one branch) is a divergence and counts against the budget as a miss.
var samples1 = probe1.Drain();
var samples2 = probe2.Drain();
var byPts = new Dictionary<TimeSpan, long>(samples1.Count);
foreach (var (pts, ticks) in samples1)
    byPts[pts] = ticks;

var skewsMs = new List<double>(samples2.Count);
var misses = 0;
foreach (var (pts, ticks) in samples2)
{
    if (byPts.Remove(pts, out var other))
        skewsMs.Add(Math.Abs(Stopwatch.GetElapsedTime(Math.Min(ticks, other), Math.Max(ticks, other)).TotalMilliseconds));
    else
        misses++;
}
misses += byPts.Count; // frames only output 1 saw

if (skewsMs.Count < 10)
{
    Console.Error.WriteLine($"FAIL: too few matched cross-output frames to measure skew (n={skewsMs.Count}, misses={misses})");
    return 17;
}

skewsMs.Sort();
var median = skewsMs[skewsMs.Count / 2];
var p95 = skewsMs[(int)(skewsMs.Count * 0.95)];
var max = skewsMs[^1];
var missRatio = misses / (double)(skewsMs.Count + misses);

// Budget: one frame period of the fanned stream (both outputs must show the same frame within one frame
// time to read as "in sync" on adjacent displays), floored at 20 ms for very-high-fps sources.
var frameRate = probe1.Format.FrameRate.ToDouble() is > 0 and var fps ? fps : 30.0;
var budgetMs = Math.Max(1000.0 / frameRate, 20.0);

Console.WriteLine(
    $"SKEW cross-output: n={skewsMs.Count} median={median:F2}ms p95={p95:F2}ms max={max:F2}ms " +
    $"misses={misses} ({missRatio:P1}) budget(p95)={budgetMs:F1}ms");
if (p95 > budgetMs || missRatio > 0.05)
{
    var clauses = new List<string>();
    if (p95 > budgetMs) clauses.Add($"p95={p95:F2}ms > {budgetMs:F1}ms");
    if (missRatio > 0.05) clauses.Add($"one-sided frames {missRatio:P1} > 5%");
    Console.Error.WriteLine($"FAIL: cross-output skew gate - {string.Join(" and ", clauses)}");
    return 18;
}

Console.WriteLine("MultiOutputSmoke skew gate OK - both outputs presented the same frames within one frame period.");
return 0;

/// <summary>Wraps a real output, recording (frame PTS → Stopwatch timestamp) at every Submit before
/// forwarding - the per-output presentation instant used by the cross-output skew gate.</summary>
sealed class SkewProbeOutput(IVideoOutput inner) : IVideoOutput
{
    private readonly Lock _gate = new();
    private readonly List<(TimeSpan Pts, long Ticks)> _samples = [];

    public VideoFormat Format => inner.Format;
    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => inner.AcceptedPixelFormats;
    public void Configure(VideoFormat format) => inner.Configure(format);

    public void Submit(VideoFrame frame)
    {
        lock (_gate)
            _samples.Add((frame.PresentationTime, Stopwatch.GetTimestamp()));
        inner.Submit(frame);
    }

    public List<(TimeSpan Pts, long Ticks)> Drain()
    {
        lock (_gate)
        {
            var copy = new List<(TimeSpan, long)>(_samples);
            _samples.Clear();
            return copy;
        }
    }
}
