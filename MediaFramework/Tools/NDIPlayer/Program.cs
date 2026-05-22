using System.Diagnostics;
using System.Text;
using System.Threading;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using S.Media.NDI;
using S.Media.NDI.Audio;
using S.Media.NDI.Video;

Console.OutputEncoding = Encoding.UTF8;

if (!TryParseArgs(args, out var opt, out var mediaPath, out var ndiName))
{
    WriteUsage();
    return 2;
}

if (!File.Exists(mediaPath))
{
    Console.Error.WriteLine($"file not found: {mediaPath}");
    return 3;
}

FFmpegRuntime.EnsureInitialized();

var videoOpts = new VideoDecoderOpenOptions
{
    TryHardwareAcceleration = !opt.NoHw,
};

using var dec = MediaContainerDecoder.Open(mediaPath, videoOpts);
dec.SeekPresentation(TimeSpan.Zero);

using var ndi = new NDIOutput(ndiName, clockVideo: false, clockAudio: true,
    minimumVideoSubmitSpacing: null,
    videoTimecodeMode: NDIVideoTimecodeMode.MuxerPresentationTicks);

if (opt.NDIWaitFirstReceiverMs > 0)
{
    var ms = (uint)opt.NDIWaitFirstReceiverMs;
    var n = ndi.GetReceiverConnectionCount(ms);
    if (opt.Verbose)
        Console.Error.WriteLine($"[ndi-debug] waited up to {ms} ms for first receiver — count={n}");
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Single-threaded mux pump when both streams — never call Audio+Video TryReadNextFrame concurrently.
var pumpCount = (!opt.AudioOnly || !opt.VideoOnly) ? 1 : 0;
using var pumpsFinished = pumpCount > 0 ? new CountdownEvent(pumpCount) : null;
var dbg = opt.Verbose ? new NDIDebugState() : null;
Task? hudTask = null;
if (dbg is not null && pumpsFinished is not null)
{
    hudTask = Task.Run(() => DebugHudLoop(ndi, dec, dbg, pumpsFinished, opt.DebugIntervalMs, cts.Token),
        CancellationToken.None);
}

if (opt.Verbose)
    WriteStartupDiagnostics(mediaPath, ndiName, dec, ndi, opt);

long sessionStartTs = 0;
NDIAudioOutput? ndAudio = null;

if (!opt.AudioOnly)
{
    VideoFormatNegotiator.Connect(dec.Video, ndi.VideoOutput);
    sessionStartTs = Stopwatch.GetTimestamp();

    if (!opt.VideoOnly && dec.HasAudio)
        ndAudio = ndi.EnableAudio(dec.Audio.Format);

    if (opt.VideoOnly || !dec.HasAudio)
        PumpVideo(dec.Video, ndi.VideoOutput, dbg, pumpsFinished, ref sessionStartTs, opt.WallPace, opt.WallDriftCorrect,
            cts.Token);
    else if (ndAudio is not null)
        PumpMuxOrdered(dec, ndi.VideoOutput, ndAudio, dbg, pumpsFinished, ref sessionStartTs, opt.WallPace,
            opt.WallDriftCorrect, cts.Token);
}
else if (!opt.VideoOnly)
{
    if (!dec.HasAudio)
    {
        Console.Error.WriteLine("audio-only requested but media has no audio stream");
        return 4;
    }
    ndAudio = ndi.EnableAudio(dec.Audio.Format);
    sessionStartTs = Stopwatch.GetTimestamp();
    PumpAudio(dec.Audio, ndAudio, dbg, pumpsFinished, ref sessionStartTs, opt.WallPace, opt.WallDriftCorrect,
        cts.Token);
}

cts.Cancel();
if (hudTask is not null)
{
    try { hudTask.Wait(TimeSpan.FromSeconds(2)); }
    catch (Exception ex)
    {
#if DEBUG
        MediaDiagnostics.LogError(ex, "NDIPlayer: HUD task wait");
#else
        if (opt.Verbose)
            Console.Error.WriteLine($"[ndi-debug] HUD task wait failed: {ex.Message}");
#endif
    }
}

return 0;

/// <summary>
/// Sleep until mux <paramref name="presentationTime"/> matches wall time since <paramref name="wallAnchorTicks"/>.
/// Optionally nudges <paramref name="wallAnchorTicks"/> so cumulative <see cref="Thread.Sleep"/> overshoot
/// does not walk seconds ahead of real time (helps NDI Monitor stream health over long runs).
/// </summary>
static void PaceToPresentationTime(
    TimeSpan presentationTime,
    ref long wallAnchorTicks,
    bool wallPaceEnabled,
    bool driftCorrect,
    CancellationToken ct)
{
    if (!wallPaceEnabled)
        return;

    var freq = Stopwatch.Frequency;
    var targetEnd = wallAnchorTicks + (long)(presentationTime.TotalSeconds * freq);

    while (!ct.IsCancellationRequested)
    {
        var now = Stopwatch.GetTimestamp();
        if (now >= targetEnd)
            break;

        var remainingTicks = targetEnd - now;
        var remainingMs = remainingTicks * 1000.0 / freq;
        if (remainingMs >= 2.0)
            Thread.Sleep(1);
        else if (remainingMs >= 0.35)
            Thread.Sleep(0);
        else
        {
            var spins = 0;
            while (Stopwatch.GetTimestamp() < targetEnd && !ct.IsCancellationRequested && spins++ < 4000)
                Thread.SpinWait(16);
            break;
        }
    }

    if (!driftCorrect || ct.IsCancellationRequested)
        return;

    var late = Stopwatch.GetTimestamp() - targetEnd;
    const double leak = 0.16;
    var maxStep = (long)(freq * 0.004);
    var step = (long)(late * leak);
    if (step > maxStep)
        step = maxStep;
    else if (step < -maxStep)
        step = -maxStep;

    wallAnchorTicks -= step;
}

static void WriteUsage()
{
    Console.WriteLine("Usage: NDIPlayer [options] <media-file> <ndi-source-name>");
    Console.WriteLine("  Decodes the file and sends video and/or audio to a new NDI source (default: both).");
    Console.WriteLine("  --video-only     Video stream only.");
    Console.WriteLine("  --audio-only     Audio stream only.");
    Console.WriteLine("  --no-hw          Force software video decode.");
    Console.WriteLine("  --verbose, -v    Periodic debug lines on stderr (PTS vs audio, FPS, NDI connections).");
    Console.WriteLine("  --debug-ms=n     Interval for --verbose HUD (default 500, min 100).");
    Console.WriteLine("  --no-wall-pace   Disable realtime wall pacing (decode/send as fast as CPU allows).");
    Console.WriteLine("  --no-wall-drift-correct  Disable wall-anchor nudging (default: on with wall pace).");
    Console.WriteLine("  --ndi-wait-first-receiver-ms=n  Block up to n ms for first NDI receiver (0=off, max 300000).");
    Console.WriteLine("  Lab (dotnet test, not runtime): RUN_NDI_EGRESS_SOAK=1, RUN_NDI_EGRESS_SOAK_ROUNDS=<n>, RUN_NDI_EGRESS_SOAK_STRESS=1 — NDIEgressPresentationTimelineTests; RUN_NDI_MUX_SOAK=1 — NDIEgressMuxPlayheadClockTests; RUN_NDI_MEMORY_PRESSURE=1, optional RUN_NDI_MEMORY_PRESSURE_ROUNDS=<n> (200–100k, or 200–2M with RUN_NDI_MEMORY_PRESSURE_LONG=1), optional RUN_NDI_MEMORY_PRESSURE_HEAP=1, optional RUN_NDI_MEMORY_PRESSURE_HEAP_STRICT=1 (requires HEAP=1) — NDIOutputLifecycleMemoryTests; RUN_MEDIA_SOAK=1, optional RUN_MEDIA_SOAK_ROUNDS=<n> (8–10000) — MediaContainerDecoderSoakTests.");
    Console.WriteLine("  Shared-mux pause (app hosts): after stopping pumps, call MediaContainerDecoder.FlushCodecPipelines()");
    Console.WriteLine("    or pass decoder.FlushCodecPipelines to AvPlaybackCoordinator.Pause(..., flushSharedMuxAfterPause).");
}

static void WriteStartupDiagnostics(string mediaPath, string ndiName, MediaContainerDecoder dec, NDIOutput ndi,
    NDIPlayerCliOptions opt)
{
    var v = dec.Video.Format;
    var a = dec.HasAudio ? dec.Audio.Format : default;
    Console.Error.WriteLine(
        $"[ndi-debug] media={mediaPath} ndiName={ndiName} ndiConnections={ndi.ConnectionCount}");
    Console.Error.WriteLine(
        $"[ndi-debug] video {v.Width}x{v.Height} {v.PixelFormat} @{v.FrameRate}  audio "
        + (dec.HasAudio ? $"{a.SampleRate}Hz ch={a.Channels}" : "(none)"));
    Console.Error.WriteLine(
        "[ndi-debug] SeekPresentation(0); A+V uses one-thread mux-ordered TryReadNextFrame (PTS); " +
        "NDI video MuxerPresentationTicks; audio timecodes from mux PTS; wallPace=" +
        (opt.WallPace ? "on" : "off") + "; wallDriftCorrect=" + (opt.WallDriftCorrect ? "on" : "off") + ".");
}

static void DebugHudLoop(NDIOutput ndi, MediaContainerDecoder dec, NDIDebugState dbg, CountdownEvent pumpsFinished,
    int intervalMs, CancellationToken ct)
{
    var intervalMsClamped = (int)Math.Clamp(intervalMs, 100, 10_000);
    long prevVf = 0, prevAf = 0;
    var wall = Stopwatch.StartNew();

    while (true)
    {
        var pumped = pumpsFinished.WaitHandle.WaitOne(intervalMsClamped);
        var vf = dbg.VideoFramesSubmitted;
        var af = dbg.AudioChunksSubmitted;
        var dvf = vf - prevVf;
        var daf = af - prevAf;
        prevVf = vf;
        prevAf = af;
        var dt = intervalMsClamped / 1000.0;
        var vFps = dt > 1e-6 ? dvf / dt : 0;
        var aChunksPerSec = dt > 1e-6 ? daf / dt : 0;

        var vPts = TimeSpan.FromTicks(dbg.LastVideoPresentationTicks);
        var aPts = TimeSpan.FromTicks(dbg.LastAudioPresentationTicks);
        var driftMs = (vPts - aPts).TotalMilliseconds;
        var lastChunk = dbg.LastAudioSamplesPerChannel;
        var nomFps = dec.Video.Format.FrameRate.ToDouble();
        var nomFpsStr = nomFps > 0 && !double.IsNaN(nomFps) ? $"{nomFps:0.##}" : "?";

        var fus = ndi.TryPollMonitorReceiverPumpFusion(0, false, 0, 0, 0, 0, 0);

        Console.Error.WriteLine(
            $"[ndi-debug] t={wall.Elapsed:mm\\:ss\\.fff}  ndiRx={ndi.ConnectionCount}  " +
            $"tallyP={fus.ReceiverTally.OnProgram} tallyV={fus.ReceiverTally.OnPreview} tallyΔ={(fus.TallyChangedInThisPoll ? 1 : 0)}  " +
            $"vFrames={vf} vFps~{vFps:0.#} nomFps={nomFpsStr}  vPTS={vPts:hh\\:mm\\:ss\\.fff}  " +
            $"aPTS={aPts:hh\\:mm\\:ss\\.fff}  driftMs={driftMs:0}  " +
            $"aChunks={af} (~{aChunksPerSec:0.#}/s) lastAuSpc={lastChunk}" +
            (pumped ? "  (pumps finished)" : ct.IsCancellationRequested ? "  (cancelled)" : ""));

        if (pumped || ct.IsCancellationRequested)
            break;
    }

    Console.Error.WriteLine(
        $"[ndi-debug] summary  vFrames={dbg.VideoFramesSubmitted} aChunks={dbg.AudioChunksSubmitted}  " +
        $"last vPTS={TimeSpan.FromTicks(dbg.LastVideoPresentationTicks):hh\\:mm\\:ss\\.fff}  " +
        $"last aPTS={TimeSpan.FromTicks(dbg.LastAudioPresentationTicks):hh\\:mm\\:ss\\.fff}  " +
        $"driftMs={(TimeSpan.FromTicks(dbg.LastVideoPresentationTicks) - TimeSpan.FromTicks(dbg.LastAudioPresentationTicks)).TotalMilliseconds:0}");
}

/// <summary>
/// Decode and send A/V in presentation-timestamp order on one thread so
/// <see cref="MediaContainerDecoder"/>'s shared demux is never polled concurrently for discrete frames.
/// </summary>
static void PumpMuxOrdered(
    MediaContainerDecoder dec,
    IVideoOutput videoSink,
    NDIAudioOutput audioSink,
    NDIDebugState? dbg,
    CountdownEvent? pumpsFinished,
    ref long wallAnchorTicks,
    bool wallPace,
    bool wallDriftCorrect,
    CancellationToken ct)
{
    VideoFrame? pendingV = null;
    AudioFrame? pendingA = null;
    var vExhausted = false;
    var aExhausted = false;
    try
    {
        while (!ct.IsCancellationRequested)
        {
            if (pendingV is null && !vExhausted)
            {
                if (!dec.Video.TryReadNextFrame(out var vf))
                    vExhausted = dec.Video.IsExhausted;
                else
                    pendingV = vf;
            }

            if (pendingA is null && !aExhausted)
            {
                if (!dec.Audio.TryReadNextFrame(out var af))
                    aExhausted = dec.Audio.IsExhausted;
                else
                    pendingA = af;
            }

            if (pendingV is null && pendingA is null)
            {
                if (vExhausted && aExhausted)
                    break;
                Thread.Sleep(2);
                continue;
            }

            var emitVideo = pendingV is not null
                && (pendingA is null || pendingV.PresentationTime <= pendingA.Value.PresentationTime);

            if (emitVideo)
            {
                var f = pendingV!;
                pendingV = null;
                try
                {
                    dbg?.OnVideoFrame(f);
                    PaceToPresentationTime(f.PresentationTime, ref wallAnchorTicks, wallPace, wallDriftCorrect, ct);
                    videoSink.Submit(f);
                }
                catch
                {
                    f.Dispose();
                    throw;
                }

                continue;
            }

            if (pendingA is { } aFrame)
            {
                pendingA = null;
                try
                {
                    dbg?.OnAudioFrame(in aFrame);
                    PaceToPresentationTime(aFrame.PresentationTime, ref wallAnchorTicks, wallPace, wallDriftCorrect, ct);
                    audioSink.Submit(in aFrame);
                }
                finally
                {
                    aFrame.Dispose();
                }
            }
        }
    }
    finally
    {
        if (pendingV is { } pv)
            pv.Dispose();
        if (pendingA is { } pa)
            pa.Dispose();
        pumpsFinished?.Signal();
    }
}

static void PumpVideo(IVideoSource src, IVideoOutput output, NDIDebugState? dbg, CountdownEvent? pumpsFinished,
    ref long wallAnchorTicks, bool wallPace, bool wallDriftCorrect, CancellationToken ct)
{
    try
    {
        while (!ct.IsCancellationRequested)
        {
            if (!src.TryReadNextFrame(out var frame))
                break;
            try
            {
                dbg?.OnVideoFrame(frame);
                PaceToPresentationTime(frame.PresentationTime, ref wallAnchorTicks, wallPace, wallDriftCorrect, ct);
                output.Submit(frame); // output takes ownership; do not dispose again
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }
    }
    finally
    {
        pumpsFinished?.Signal();
    }
}

static void PumpAudio(IAudioSource src, NDIAudioOutput output, NDIDebugState? dbg, CountdownEvent? pumpsFinished,
    ref long wallAnchorTicks, bool wallPace, bool wallDriftCorrect, CancellationToken ct)
{
    try
    {
        while (!ct.IsCancellationRequested)
        {
            if (!src.TryReadNextFrame(out var frame))
                break;

            try
            {
                dbg?.OnAudioFrame(in frame);
                PaceToPresentationTime(frame.PresentationTime, ref wallAnchorTicks, wallPace, wallDriftCorrect, ct);
                output.Submit(in frame);
            }
            finally
            {
                frame.Dispose();
            }
        }
    }
    finally
    {
        pumpsFinished?.Signal();
    }
}

static bool TryParseArgs(string[] args, out NDIPlayerCliOptions opt, out string mediaPath, out string ndiName)
{
    opt = default;
    mediaPath = "";
    ndiName = "";
    var videoOnly = false;
    var audioOnly = false;
    var noHw = false;
    var verbose = false;
    var debugMs = 500;
    var wallPace = true;
    var wallDriftCorrect = true;
    var ndiWaitFirstRxMs = 0;
    var rest = new List<string>();
    foreach (var a in args)
    {
        switch (a)
        {
            case "--video-only":
                videoOnly = true;
                break;
            case "--audio-only":
                audioOnly = true;
                break;
            case "--no-hw":
                noHw = true;
                break;
            case "--verbose":
            case "-v":
                verbose = true;
                break;
            case "--no-wall-pace":
                wallPace = false;
                break;
            case "--no-wall-drift-correct":
                wallDriftCorrect = false;
                break;
            default:
                if (TryParseKeyedInt(a, "--debug-ms=", out var dm) && dm >= 100)
                {
                    debugMs = dm;
                    break;
                }

                if (TryParseKeyedInt(a, "--ndi-wait-first-receiver-ms=", out var nwr) && nwr >= 0)
                {
                    ndiWaitFirstRxMs = Math.Clamp(nwr, 0, 300_000);
                    break;
                }

                rest.Add(a);
                break;
        }
    }

    if (videoOnly && audioOnly)
        return false;
    if (rest.Count != 2)
        return false;
    opt = new NDIPlayerCliOptions(videoOnly, audioOnly, noHw, verbose, debugMs, wallPace, wallDriftCorrect,
        ndiWaitFirstRxMs);
    mediaPath = rest[0];
    ndiName = rest[1];
    return true;
}

static bool TryParseKeyedInt(string arg, string prefix, out int value)
{
    value = 0;
    if (!arg.StartsWith(prefix, StringComparison.Ordinal))
        return false;
    return int.TryParse(arg.AsSpan(prefix.Length), out value);
}

file readonly record struct NDIPlayerCliOptions(
    bool VideoOnly,
    bool AudioOnly,
    bool NoHw,
    bool Verbose,
    int DebugIntervalMs,
    bool WallPace,
    bool WallDriftCorrect,
    int NDIWaitFirstReceiverMs);

/// <summary>Cross-thread counters for <see cref="DebugHudLoop"/>.</summary>
file sealed class NDIDebugState
{
    private long _videoFramesSubmitted;
    private long _lastVideoPresentationTicks;
    private long _audioChunksSubmitted;
    private long _lastAudioPresentationTicks;
    private int _lastAudioSamplesPerChannel;

    public void OnVideoFrame(VideoFrame frame)
    {
        Interlocked.Increment(ref _videoFramesSubmitted);
        Interlocked.Exchange(ref _lastVideoPresentationTicks, frame.PresentationTime.Ticks);
    }

    public void OnAudioFrame(in AudioFrame frame)
    {
        Interlocked.Increment(ref _audioChunksSubmitted);
        Interlocked.Exchange(ref _lastAudioSamplesPerChannel, frame.SamplesPerChannel);
        Interlocked.Exchange(ref _lastAudioPresentationTicks, frame.PresentationTime.Ticks);
    }

    public long VideoFramesSubmitted => Interlocked.Read(ref _videoFramesSubmitted);
    public long LastVideoPresentationTicks => Interlocked.Read(ref _lastVideoPresentationTicks);
    public long AudioChunksSubmitted => Interlocked.Read(ref _audioChunksSubmitted);
    public long LastAudioPresentationTicks => Interlocked.Read(ref _lastAudioPresentationTicks);
    public int LastAudioSamplesPerChannel => Volatile.Read(ref _lastAudioSamplesPerChannel);
}
