using System.Diagnostics;
using NDILib;
using S.Media.NDI;
using S.Media.Time;

// Phase 5 live-convergence probe. Receives a real NDI source and drives its video onto a live-led session
// master (SessionClock over a MonotonicWallClock) through a SourceTimeline (RebaseToLatest) via
// LiveTimelineDriver — the P7 model: "live is a source scheduled against the master", not a master-less
// path. Reports frame rate, the per-frame schedule lead (DueMaster - master-now; bounded = healthy), and
// RebaseToLatest re-anchors. No display needed.

var nameFilter = args.Length > 0 ? args[0] : null;
var runFor = TimeSpan.FromSeconds(args.Length > 1 && int.TryParse(args[1], out var s) ? s : 20);

var rc = NDIRuntime.Create(out var runtime);
if (rc != 0 || runtime is null)
{
    Console.Error.WriteLine($"NDI runtime init failed ({rc}).");
    return 3;
}

Console.WriteLine("discovering NDI sources…");
var sources = NDISource.Find(TimeSpan.FromSeconds(3));
if (sources.Count == 0)
{
    Console.Error.WriteLine("no NDI sources on the network.");
    return 4;
}

var chosen = sources.FirstOrDefault(x => nameFilter is null || x.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
if (chosen.Name is null)
{
    Console.Error.WriteLine($"no NDI source matching '{nameFilter}'. Available: {string.Join(", ", sources.Select(x => x.Name))}");
    return 4;
}
Console.WriteLine($"receiving '{chosen.Name}' for {runFor.TotalSeconds:0}s…");

using var source = NDISource.Open(chosen, new NDISourceOptions { ReceiveVideo = true, ReceiveAudio = true });

// Live-led master + a RebaseToLatest video timeline (the classic NDI policy), driven against the master.
var master = SessionClock.LiveWallClock();
var driver = new LiveTimelineDriver(new SourceTimeline(RebasePolicy.RebaseToLatest));
var video = source.Video;

long frames = 0;
var leadMin = TimeSpan.MaxValue;
var leadMax = TimeSpan.MinValue;
var leadSum = TimeSpan.Zero;
var start = Stopwatch.StartNew();
var nextReport = TimeSpan.FromSeconds(2);

// One frame per iteration: TryReadNextFrame blocks ~one frame interval waiting for live data, which
// paces the loop and lets us re-check the run deadline + emit periodic reports between frames.
while (start.Elapsed < runFor)
{
    if (video.TryReadNextFrame(out var frame))
    {
        var masterNow = master.Now;
        var placement = driver.Place(frame.PresentationTime, masterNow);
        var lead = placement.DueMaster - masterNow;   // how far ahead of "now" the frame is scheduled
        if (lead < leadMin) leadMin = lead;
        if (lead > leadMax) leadMax = lead;
        leadSum += lead;
        frames++;
        frame.Dispose();
    }

    if (start.Elapsed >= nextReport)
    {
        var fps = frames / start.Elapsed.TotalSeconds;
        source.TryGetVideoFormat(out var vf);
        Console.WriteLine(
            $"  t={start.Elapsed.TotalSeconds,4:0}s  frames={frames,5}  fps={fps,5:0.0}  " +
            $"lead mean/min/max={(frames > 0 ? (leadSum / frames).TotalMilliseconds : 0),6:0.0}/{leadMin.TotalMilliseconds,6:0.0}/{leadMax.TotalMilliseconds,6:0.0}ms  " +
            $"reanchors={driver.ReAnchorCount}  {vf.Width}x{vf.Height}");
        Console.Out.Flush();
        nextReport += TimeSpan.FromSeconds(2);
    }
}

source.TryGetVideoFormat(out var vfmt);
var hasAudio = source.TryGetAudioFormat(out var afmt);
Console.WriteLine();
Console.WriteLine($"video : {vfmt.Width}x{vfmt.Height} @ {frames / start.Elapsed.TotalSeconds:0.0} fps over {start.Elapsed.TotalSeconds:0}s ({frames} frames)");
Console.WriteLine($"audio : {(hasAudio ? $"{afmt.SampleRate} Hz, {afmt.Channels} ch" : "not delivered")}");
Console.WriteLine($"sched : lead {(frames > 0 ? (leadSum / frames).TotalMilliseconds : 0):0.0}ms mean, [{leadMin.TotalMilliseconds:0.0}, {leadMax.TotalMilliseconds:0.0}]ms, {driver.ReAnchorCount} re-anchor(s)");
Console.WriteLine(frames > 0
    ? "LiveReceiveProbe OK — live NDI video scheduled against the session master with bounded lead."
    : "LiveReceiveProbe: no video frames received (sender video off?).");
return frames > 0 ? 0 : 5;
