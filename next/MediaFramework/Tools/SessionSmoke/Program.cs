// Phase 4 SessionSmoke — a full show runs headless (the Phase-4 exit gate). Builds a ShowDocument, round-
// trips it through JSON (D10 persistence), loads it into a ShowSession, then drives the dispatcher:
//   GO → cue 1 (audio) plays on the master output; seek jumps the playhead;
//   GO → cue 2 (video) plays onto a composition canvas → the CPU compositor composites it headless.
// No Avalonia, no GL — proves the cue → clip → audio-master and cue → clip → video-layer → composite paths.
using S.Media.Audio.PortAudio;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Decode.FFmpeg;
using S.Media.Interop;
using S.Media.Session;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: SessionSmoke <audio-file> [video-file]");
    return 1;
}

var audioFile = args[0];
var videoFile = args.Length > 1 ? args[1] : args[0];

// A sidecar SRT shown over the video cue's composition — a non-ASS format, so it exercises the full
// FFmpeg-decode → ASS events → libass path through the unified factory, end-to-end.
var subPath = Path.Combine(Path.GetTempPath(), "sessionsmoke-subs.srt");
File.WriteAllText(subPath, "1\n00:00:00,000 --> 02:46:39,000\nSessionSmoke subtitle layer\n");

// Verify the unified host factory (FFmpeg decode + libass render) independently: load the SRT, render a frame.
using (var subProbe = SubtitleOverlayFactory.FromFile(subPath, 1280, 720))
{
    var subFrame = subProbe?.RenderAt(TimeSpan.FromSeconds(1));
    Console.WriteLine($"subtitle factory: source={subProbe is not null}, renders={subFrame is not null} " +
                      $"({subFrame?.Format.Width}x{subFrame?.Format.Height})");
    if (subFrame is null)
    {
        Console.Error.WriteLine("FAIL: subtitle factory/renderer produced no overlay");
        return 8;
    }
}

var registry = MediaRegistry.Build(b => b.Use(new FFmpegModule()).Use(new PortAudioModule()));
var backend = registry.AudioBackends.FirstOrDefault();
if (backend is null)
{
    Console.Error.WriteLine("no audio backend registered");
    return 3;
}

// Author a two-cue show — an audio cue and a video cue on a composition — then prove D10: serialize → JSON
// → deserialize and drive the reloaded copy.
var document = new ShowDocument(
    Version: 1,
    Cues:
    [
        new CueDefinition("cue1", 1, "Audio Clip"),
        new CueDefinition("cue2", 2, "Video Clip"),
    ],
    Clips:
    [
        new ShowClipBinding("cue1", audioFile),
        new ShowClipBinding("cue2", videoFile, CompositionId: "screen", LayerIndex: 0, SubtitlePath: subPath),
    ],
    Compositions:
    [
        // The composition carries an affine output mapping (projector tiling / keystone) — the composited
        // canvas is cut into placed sections drawn onto the output. Affine sections composite headless on
        // the CPU backend (mesh warp is GL-only); here a single full-canvas section exercises the path.
        new ShowComposition("screen", "Main Screen", 1280, 720, 24, 1,
            OutputMapping: new ClipOutputMappingSpec(
                Sections:
                [
                    new ClipOutputMappingSection("full", Enabled: true,
                        SrcX: 0, SrcY: 0, SrcWidth: 1, SrcHeight: 1,
                        DestX: 0, DestY: 0, DestWidth: 1280, DestHeight: 720),
                ],
                OutputWidth: 1280, OutputHeight: 720)),
    ],
    Outputs: [],
    Routes: [],
    Devices: []);

var json = document.ToJson();
var reloaded = ShowDocument.FromJson(json);
Console.WriteLine($"decoders: {string.Join(", ", registry.Decoders.Select(d => d.Name))}; backend: {backend.Name}");
Console.WriteLine($"show: {reloaded.Cues.Count} cues, {reloaded.Clips.Count} clips, {reloaded.Compositions.Count} compositions (JSON {json.Length} B round-tripped)");

// A counting video output proves the composition fans composited frames out to a host-provided output —
// the IShowVideoOutputFactory seam the GUI uses to surface video onto its NDI/SDL/local lines.
var screenOutput = new RecordingVideoOutput();
await using var session = new ShowSession(
    registry,
    backend,
    (path, streamIndex, width, height) => SubtitleOverlayFactory.FromFileDeferred(path, width, height, streamIndex),
    (compId, name, _, _) => compId == "screen"
        ? new[] { new ClipCompositionOutputLease("screen_out", name, screenOutput, DisposeOutputOnRuntimeDispose: false) }
        : Array.Empty<ClipCompositionOutputLease>());
session.LoadDocument(reloaded);

// GO → cue 1 fires (audio clip opens through the registry + plays on the master output).
var go1 = await session.GoAsync();
await Task.Delay(1000);
var afterFire = (await session.SnapshotAsync())[0];

// Seek the active clip forward; the playhead must jump.
await session.SeekAsync(TimeSpan.FromSeconds(5));
await Task.Delay(500);
var afterSeek = (await session.SnapshotAsync())[0];

// GO → cue 2 fires (video clip; its video is routed onto the "screen" composition and composited headless).
var go2 = await session.GoAsync();
await Task.Delay(1500);
var comp = await session.GetCompositionStatsAsync("screen");

var log = await session.GetCueExecutionLogAsync();
Console.WriteLine($"GO1={go1} pos={afterFire.ClipPosition.TotalSeconds:F2}s running={afterFire.IsRunning}");
Console.WriteLine($"SEEK pos={afterSeek.ClipPosition.TotalSeconds:F2}s");
Console.WriteLine($"GO2={go2} composition: submitted={comp?.FramesSubmitted} composited={comp?.FramesComposited} layers={comp?.LayerCount}");
Console.WriteLine($"cue log: {string.Join(", ", log.Select(e => $"{e.Number}:{e.Status}"))}");

// --- gate assertions ---------------------------------------------------------------------------------
if (go1 != CueExecutionStatus.Fired)
{
    Console.Error.WriteLine($"FAIL: GO1 did not fire (was {go1})");
    return 2;
}

if (afterFire.ClipPosition < TimeSpan.FromMilliseconds(200) || !afterFire.IsRunning)
{
    Console.Error.WriteLine("FAIL: audio clip did not play (position did not advance under real-time pacing)");
    return 3;
}

if (afterSeek.ClipPosition < afterFire.ClipPosition + TimeSpan.FromSeconds(3))
{
    Console.Error.WriteLine($"FAIL: seek did not jump the playhead ({afterFire.ClipPosition.TotalSeconds:F2}s → {afterSeek.ClipPosition.TotalSeconds:F2}s)");
    return 4;
}

// A seek while playing must KEEP playing. SeekCoordinated pauses+seeks (no resume), so ShowSession.SeekAsync
// must restore the pre-seek play state. Without it the clip is frozen after every seek and the media-player
// deck's poll reads the non-running clip as "ended" and tears the deck down — seek "stops playback".
if (!afterSeek.IsRunning)
{
    Console.Error.WriteLine("FAIL: clip stopped running after a seek (ShowSession.SeekAsync did not resume playback)");
    return 16;
}

if (go2 != CueExecutionStatus.Fired)
{
    Console.Error.WriteLine($"FAIL: GO2 did not fire (was {go2})");
    return 5;
}

if (comp is not { FramesSubmitted: > 0, FramesComposited: > 0 })
{
    Console.Error.WriteLine($"FAIL: video did not composite (submitted={comp?.FramesSubmitted}, composited={comp?.FramesComposited}) — pass a video file as arg 2");
    return 6;
}

// The composited frames must also reach the host factory's output — the IShowVideoOutputFactory seam the
// GUI uses to surface composited video onto a real NDI/SDL/local line.
if (screenOutput.Submitted == 0)
{
    Console.Error.WriteLine("FAIL: composition did not fan out to the host video-output factory output");
    return 15;
}
Console.WriteLine($"VIDEO-FACTORY output received {screenOutput.Submitted} composited frames");

// The composition must carry TWO layers — the clip's video + the auto-attached subtitle — and still composite.
if (comp.Value.LayerCount < 2)
{
    Console.Error.WriteLine($"FAIL: subtitle layer not attached (LayerCount={comp.Value.LayerCount}, expected 2: video + subtitle)");
    return 9;
}

// 8b live-edit: reposition the active video cue's placement while it plays (UpdateActiveCueVideoPlacement).
var livePlaced = await session.UpdateActivePlacementAsync(
    "cue2", "screen", 0, new ShowVideoPlacement(DestX: 0.25, DestY: 0.25, DestWidth: 0.5, DestHeight: 0.5, Opacity: 0.8));
await Task.Delay(200);
var compAfterPlace = await session.GetCompositionStatsAsync("screen");
Console.WriteLine($"LIVE-PLACE updated={livePlaced} stillComposites={compAfterPlace is { FramesComposited: > 0 }}");
if (!livePlaced || compAfterPlace is not { FramesComposited: > 0 })
{
    Console.Error.WriteLine($"FAIL: live placement update did not apply / broke compositing (updated={livePlaced})");
    return 14;
}

if (log.Count != 2 || log.Any(e => e.Status != CueExecutionStatus.Fired))
{
    Console.Error.WriteLine("FAIL: execution log did not record two fired cues");
    return 7;
}

// --- NXT-04 measured A/V sync gates -------------------------------------------------------------------
// Every composited frame the host output receives carries the SELECTED video frame's media PTS (the mixer
// canvas takes layer 0's master-aligned frame time), and the lock-free transport snapshot's ClipPosition is
// the same clip's audio-paced playhead — both in media time. skew = framePts − playhead therefore measures
// the video selection against the master clock quantitatively ("assert tolerances, not that frames
// appeared"). A constant bias (container start_time, buffering lead) is tolerated loosely; the TIGHT gates
// are jitter (selection instability) and the seek-induced SHIFT of the bias — the audio-ahead-after-seek
// regression class ships as a measured gate here.
var syncBefore = (await session.SnapshotAsync())[0];
if (syncBefore.IsActive && syncBefore.ClipDuration > syncBefore.ClipPosition + TimeSpan.FromSeconds(14))
{
    var steady = await screenOutput.CaptureSkewAsync(session, TimeSpan.FromSeconds(1.5));
    Console.WriteLine(
        $"SYNC steady: n={steady.Count} median={steady.MedianMs:F0}ms p95(|skew|)={steady.P95AbsMs:F0}ms jitter={steady.JitterMs:F0}ms");
    if (steady.Count < 10 || Math.Abs(steady.MedianMs) > 250 || steady.JitterMs > 120)
    {
        Console.Error.WriteLine(
            $"FAIL: steady-state A/V skew out of tolerance (n={steady.Count}, |median|={Math.Abs(steady.MedianMs):F0}ms > 250ms or jitter={steady.JitterMs:F0}ms > 120ms)");
        return 17;
    }

    // Coordinated A/V seek: the post-seek skew must MATCH the steady baseline (a shift = one side seeked
    // to a different effective position — the long-GOP HW-frame PTS desync class), and no stale pre-seek
    // frame may surface after the settle window.
    var target = (await session.SnapshotAsync())[0].ClipPosition + TimeSpan.FromSeconds(6);
    await session.SeekAsync(target);
    await Task.Delay(700); // settle: pipeline flush + refill + resume
    var postSeek = await screenOutput.CaptureSkewAsync(session, TimeSpan.FromSeconds(1.0));
    Console.WriteLine(
        $"SYNC post-seek: n={postSeek.Count} median={postSeek.MedianMs:F0}ms shift={postSeek.MedianMs - steady.MedianMs:F0}ms minPts={postSeek.MinPts.TotalSeconds:F2}s (target {target.TotalSeconds:F2}s)");
    if (postSeek.Count < 8 || Math.Abs(postSeek.MedianMs - steady.MedianMs) > 150 || postSeek.JitterMs > 150)
    {
        Console.Error.WriteLine(
            $"FAIL: post-seek A/V skew shifted/degraded (n={postSeek.Count}, shift={postSeek.MedianMs - steady.MedianMs:F0}ms > 150ms or jitter={postSeek.JitterMs:F0}ms > 150ms)");
        return 18;
    }

    if (postSeek.MinPts < target - TimeSpan.FromSeconds(1.5))
    {
        Console.Error.WriteLine(
            $"FAIL: stale pre-seek frame surfaced after the seek (minPts={postSeek.MinPts.TotalSeconds:F2}s « target {target.TotalSeconds:F2}s)");
        return 19;
    }

    // Pause: the playhead freezes (clock contract) and the presented frame must HOLD — either no new
    // submissions, or re-sent frames whose PTS no longer advances.
    await session.SetPausedAsync(true);
    await Task.Delay(300); // let the pause transient drain
    var paused = await screenOutput.CaptureSkewAsync(session, TimeSpan.FromMilliseconds(500));
    Console.WriteLine($"SYNC paused: n={paused.Count} ptsAdvance={paused.PtsSpreadMs:F0}ms");
    if (paused.Count > 0 && paused.PtsSpreadMs > 100)
    {
        Console.Error.WriteLine(
            $"FAIL: presented frames kept advancing while paused (ptsAdvance={paused.PtsSpreadMs:F0}ms > 100ms)");
        return 20;
    }

    // Resume: frames advance again and the skew returns to the steady baseline (no pause-induced shift —
    // the pause/resume desync class of the playback-clock freeze contract).
    await session.SetPausedAsync(false);
    await Task.Delay(400);
    var resumed = await screenOutput.CaptureSkewAsync(session, TimeSpan.FromSeconds(1.0));
    Console.WriteLine(
        $"SYNC resumed: n={resumed.Count} median={resumed.MedianMs:F0}ms shift={resumed.MedianMs - steady.MedianMs:F0}ms ptsAdvance={resumed.PtsSpreadMs:F0}ms");
    if (resumed.Count < 8 || Math.Abs(resumed.MedianMs - steady.MedianMs) > 150 || resumed.PtsSpreadMs < 500)
    {
        Console.Error.WriteLine(
            $"FAIL: post-resume playback degraded (n={resumed.Count}, shift={resumed.MedianMs - steady.MedianMs:F0}ms > 150ms or ptsAdvance={resumed.PtsSpreadMs:F0}ms < 500ms)");
        return 21;
    }
}
else
{
    Console.WriteLine("SYNC gates SKIPPED — clip too short (pass a ≥20s A/V file to exercise the measured sync gates)");
}

// --- 8b trim-in: a clip with a StartOffset starts at the trim point (forward play) -----------------
await using (var trimSession = new ShowSession(registry, backend))
{
    var trimOffset = TimeSpan.FromSeconds(3);
    trimSession.LoadDocument(new ShowDocument(
        1,
        [new CueDefinition("trim", 1, "Trim-in")],
        [new ShowClipBinding("trim", audioFile) { StartOffset = trimOffset }],
        [], [], [], []));
    await trimSession.GoAsync();
    await Task.Delay(800);
    var trim = (await trimSession.SnapshotAsync())[0];
    Console.WriteLine($"TRIM-IN pos={trim.ClipPosition.TotalSeconds:F2}s dur={trim.ClipDuration.TotalSeconds:F2}s running={trim.IsRunning} (offset {trimOffset.TotalSeconds:F0}s)");

    if (!trim.IsRunning)
    {
        Console.Error.WriteLine("FAIL: trimmed clip did not play");
        return 10;
    }

    // Only assert the offset landed when the clip is long enough to honour it (a short CI tone can't).
    if (trim.ClipDuration > trimOffset + TimeSpan.FromSeconds(1) && trim.ClipPosition < trimOffset)
    {
        Console.Error.WriteLine($"FAIL: trim-in did not seek to StartOffset (pos={trim.ClipPosition.TotalSeconds:F2}s < {trimOffset.TotalSeconds:F0}s)");
        return 11;
    }
}

// --- 8b end/loop: a clip with Loop + a trimmed out-point wraps back to the start ------------------
await using (var loopSession = new ShowSession(registry, backend))
{
    loopSession.LoadDocument(new ShowDocument(
        1,
        [new CueDefinition("loop", 1, "Loop")],
        [new ShowClipBinding("loop", audioFile) { Loop = true, EndOffset = TimeSpan.FromSeconds(4) }],
        [], [], [], []));
    await loopSession.GoAsync();
    await Task.Delay(300);
    var dur = (await loopSession.SnapshotAsync())[0].ClipDuration;
    await Task.Delay(2800); // past the out-point — a non-looping clip would sit beyond it (or have ended)
    var looped = (await loopSession.SnapshotAsync())[0];
    var outPoint = dur - TimeSpan.FromSeconds(4);
    Console.WriteLine($"LOOP pos={looped.ClipPosition.TotalSeconds:F2}s dur={dur.TotalSeconds:F2}s running={looped.IsRunning} (out-point {outPoint.TotalSeconds:F2}s)");

    // Only assert the wrap when the clip is long enough to give a >1s loop window (a short CI tone can't).
    if (outPoint > TimeSpan.FromSeconds(1) && (!looped.IsRunning || looped.ClipPosition >= outPoint))
    {
        Console.Error.WriteLine($"FAIL: clip did not loop at the out-point (pos={looped.ClipPosition.TotalSeconds:F2}s, expected wrapped < {outPoint.TotalSeconds:F2}s, running)");
        return 12;
    }
}

// --- 8b fade-in: a clip with FadeIn plays through the gain ramp without stalling ------------------
await using (var fadeSession = new ShowSession(registry, backend))
{
    fadeSession.LoadDocument(new ShowDocument(
        1,
        [new CueDefinition("fade", 1, "Fade")],
        [new ShowClipBinding("fade", audioFile) { FadeIn = TimeSpan.FromSeconds(1) }],
        [], [], [], []));
    await fadeSession.GoAsync();
    await Task.Delay(1500); // past the 1s fade ramp
    var faded = (await fadeSession.SnapshotAsync())[0];
    Console.WriteLine($"FADE-IN pos={faded.ClipPosition.TotalSeconds:F2}s running={faded.IsRunning} (1s gain ramp — verify audibly on HW)");

    // The ramp only touches gain, not transport — so position must still advance; a stall = the background
    // ramp broke the session. (The audible 0→1 fade itself isn't observable headless; that's a HW check.)
    if (faded.ClipPosition < TimeSpan.FromSeconds(0.3))
    {
        Console.Error.WriteLine($"FAIL: playback stalled during the fade-in ramp (pos={faded.ClipPosition.TotalSeconds:F2}s)");
        return 13;
    }
}

Console.WriteLine("SessionSmoke OK — a full show ran headless (audio cue + seek + video cue composited with a subtitle layer + trim-in + loop + fade-in + host video-output fan-out).");
return 0;

// Counts the composited frames a composition fans out to a host-provided video-output lease, and — for the
// NXT-04 measured sync gates — captures (frame media PTS, playhead at submit) pairs over a sample window.
sealed class RecordingVideoOutput : IVideoOutput
{
    private VideoFormat _format;
    private readonly object _gate = new();
    private List<(TimeSpan Pts, TimeSpan Clock)>? _capture;
    private Func<TimeSpan>? _clockProbe;
    public int Submitted { get; private set; }
    public VideoFormat Format => _format;
    public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = Array.Empty<PixelFormat>();
    public void Configure(VideoFormat format) => _format = format;

    public void Submit(VideoFrame frame)
    {
        Submitted++;
        lock (_gate)
        {
            if (_capture is not null && _clockProbe is not null)
                _capture.Add((frame.PresentationTime, _clockProbe()));
        }

        frame.Dispose();
    }

    /// <summary>Samples every submitted frame's (media PTS, lock-free snapshot playhead) for
    /// <paramref name="window"/> and reduces them to skew statistics. The probe reads
    /// <see cref="ShowSession.Snapshot"/> — no dispatcher marshaling on the submit path.</summary>
    public async Task<SkewStats> CaptureSkewAsync(ShowSession session, TimeSpan window)
    {
        lock (_gate)
        {
            _capture = new List<(TimeSpan, TimeSpan)>();
            _clockProbe = () => session.Snapshot() is [{ } s, ..] ? s.ClipPosition : TimeSpan.Zero;
        }

        await Task.Delay(window);
        List<(TimeSpan Pts, TimeSpan Clock)> samples;
        lock (_gate)
        {
            samples = _capture!;
            _capture = null;
            _clockProbe = null;
        }

        return SkewStats.From(samples);
    }
}

/// <summary>Reduced skew samples: median signed skew (bias — start_time offsets/buffer lead land here),
/// p95 of |skew|, jitter (p95 of |skew − median|, the selection-instability signal), the smallest PTS seen
/// (stale-frame detection after a seek), and the PTS span (advance/hold detection around pause).</summary>
readonly record struct SkewStats(int Count, double MedianMs, double P95AbsMs, double JitterMs, TimeSpan MinPts, double PtsSpreadMs)
{
    public static SkewStats From(List<(TimeSpan Pts, TimeSpan Clock)> samples)
    {
        if (samples.Count == 0)
            return new SkewStats(0, 0, 0, 0, TimeSpan.Zero, 0);
        var skews = samples.Select(s => (s.Pts - s.Clock).TotalMilliseconds).OrderBy(x => x).ToArray();
        var median = skews[skews.Length / 2];
        var p95Abs = skews.Select(Math.Abs).OrderBy(x => x).ToArray()[(int)(skews.Length * 0.95)];
        var jitter = skews.Select(x => Math.Abs(x - median)).OrderBy(x => x).ToArray()[(int)(skews.Length * 0.95)];
        var minPts = samples.Min(s => s.Pts);
        var spread = (samples.Max(s => s.Pts) - minPts).TotalMilliseconds;
        return new SkewStats(samples.Count, median, p95Abs, jitter, minPts, spread);
    }
}
