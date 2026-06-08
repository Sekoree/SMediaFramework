using System.Diagnostics;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using S.Media.FFmpeg.Audio;
using S.Media.Playback;

if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
{
    Console.Error.WriteLine("usage: TransportSyncProbe <media-file> [--no-hw] [--cycles N] [--hold-ms N] [--targets s1,s2,...] [--audio-out-rate N] [--verify-content]");
    Console.Error.WriteLine("  --verify-content   decode each seek target with HW and with SW and compare the actual");
    Console.Error.WriteLine("                     displayed pixels (not just the PTS label) so an A/V content desync is caught.");
    return args.Length == 0 ? 2 : 0;
}

var path = args[0];
var noHw = args.Contains("--no-hw", StringComparer.Ordinal);
var verifyContent = args.Contains("--verify-content", StringComparer.Ordinal);
var cycles = ReadInt("--cycles", 1);
var holdMs = ReadInt("--hold-ms", 1500);
var audioOutRate = ReadInt("--audio-out-rate", 0);
var targets = ReadTargets("--targets") ?? [3, 17, 30, 60, 90];

if (!File.Exists(path))
{
    Console.Error.WriteLine($"file not found: {path}");
    return 2;
}

if (verifyContent)
    return RunContentVerification(path, targets, holdMs, audioOutRate);

var videoOut = new RecordingVideoOutput();
var options = new MediaPlayerOpenOptions(
    TryHardwareAcceleration: !noHw,
    IncludeAudioRouter: true,
    AudioPacketQueueDepth: 720,
    VideoPacketQueueDepth: 512,
    FileReadBufferBytes: 4 * 1024 * 1024,
    FileVideoDecodeQueueCapacity: 16);

if (!MediaPlayer.OpenFile(path)
        .WithOptions(options)
        .WithVideoLead(videoOut, disposeOnPlayerDispose: false)
        .TryBuild(out var player, out var error))
{
    Console.Error.WriteLine(error);
    return 3;
}

using (player)
{
    if (player!.AudioRouter is null || player.AudioSourceId is null || !player.Decoder.HasAudio)
    {
        Console.Error.WriteLine("probe requires a media file with audio");
        return 3;
    }

    var audioFormat = player.Decoder.Audio.Format;
    if (audioOutRate > 0)
        audioFormat = new AudioFormat(audioOutRate, audioFormat.Channels);
    var audioOut = new VirtualClockedAudioOutput(audioFormat);
    IAudioOutput routerAudioOut = audioOut.Format == player.Decoder.Audio.Format
        ? audioOut
        : ResamplingAudioOutput.Wrap(audioOut, player.Decoder.Audio.Format);
    var audioOutputId = player.AudioRouter.AddOutput(routerAudioOut, "probe_clocked_audio", pumpCapacityChunks: 4);
    player.AudioRouter.Connect(player.AudioSourceId, audioOutputId);

    Console.WriteLine($"file={Path.GetFileName(path)} hw={!noHw}");
    Console.WriteLine($"duration={player.Duration} video={player.Video.Format} audio={player.Decoder.Audio.Format} outputAudio={audioOut.Format}");
    Console.WriteLine($"audioMaster={(player.AudioClock?.Master?.GetType().Name ?? "(none)")}");
    Console.WriteLine();

    for (var cycle = 1; cycle <= cycles; cycle++)
    {
        Console.WriteLine($"cycle {cycle}");
        videoOut.Reset();
        PlayAndHold(player, audioOut, videoOut, $"start", holdMs);
        Pause(player, "pause-start");

        videoOut.Reset();
        PlayAndHold(player, audioOut, videoOut, "resume-no-seek", holdMs);
        Pause(player, "pause-resume");

        foreach (var t in targets)
        {
            var target = TimeSpan.FromSeconds(t);
            if (target >= player.Duration - TimeSpan.FromSeconds(1))
                continue;

            videoOut.Reset();
            var sw = Stopwatch.StartNew();
            player.SeekCoordinated(target, CancellationToken.None, PauseFlushPolicy.SkipFlush);
            sw.Stop();
            Console.WriteLine(
                $"  seek target={target:c} elapsedMs={sw.Elapsed.TotalMilliseconds:F1} decoderA={SourcePosition(player.Decoder.Audio):c} decoderV={SourcePosition(player.Decoder.Video):c} clock={player.PlayClock.CurrentPosition:c}");

            player.PrewarmVideoAfterSeek();
            PrintSnapshot(player, audioOut, videoOut, "prewarm");
            PlayAndHold(player, audioOut, videoOut, $"play-after-seek-{target.TotalSeconds:F0}", holdMs);
            Pause(player, "pause-seek");
        }
    }
}

return 0;

int ReadInt(string name, int fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name && int.TryParse(args[i + 1], out var value))
            return value;
    }
    return fallback;
}

double[]? ReadTargets(string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] != name)
            continue;
        return args[i + 1]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(double.Parse)
            .ToArray();
    }
    return null;
}

// --- content verification ------------------------------------------------------
//
// The label-based snapshot (video-clock-ms / audio-clock-ms) compares each side against the clock, but a
// frame's reported PresentationTime is its LABEL, not proof that the right pixels were decoded. On the
// hardware path a frame counter can re-label a post-seek keyframe as the target, so the labels line up
// while the picture is a whole GOP behind the (correctly timestamped) audio. To catch that we decode each
// seek target twice — once with HW, once with SW (which always carries the true container PTS) — and compare
// the ACTUAL displayed luma a fixed distance past the seek. If the same logical time shows different pixels,
// video content is desynced from its label (and therefore from audio).
static int RunContentVerification(string path, double[] targets, int holdMs, int audioOutRate)
{
    Console.WriteLine($"content verification: file={Path.GetFileName(path)} targets=[{string.Join(",", targets)}]");
    Console.WriteLine("decoding each target with HW then SW and comparing displayed pixels 0.4s past the seek...");
    Console.WriteLine();

    var hw = CaptureSeekSignatures(path, useHw: true, targets, holdMs, audioOutRate, out var hwError);
    if (hwError is not null) { Console.Error.WriteLine($"HW capture failed: {hwError}"); return 3; }
    var sw = CaptureSeekSignatures(path, useHw: false, targets, holdMs, audioOutRate, out var swError);
    if (swError is not null) { Console.Error.WriteLine($"SW capture failed: {swError}"); return 3; }

    var desyncs = 0;
    foreach (var t in targets)
    {
        if (!hw.TryGetValue(t, out var h) || !sw.TryGetValue(t, out var s) || h.Sig is null || s.Sig is null)
        {
            Console.WriteLine($"  target={t,6:F1}s  (skipped — beyond duration or no frame captured)");
            continue;
        }

        var diff = SignatureDistance(h.Sig, s.Sig);
        // hw vs sw decode of the SAME frame differs by only a level or two of luma; a 2-3 s content gap is a
        // wholly different image and scores far higher. 10/255 average per-block difference splits them cleanly.
        var desync = diff > 10.0;
        if (desync) desyncs++;
        Console.WriteLine(
            $"  target={t,6:F1}s  hwLabel={h.Pts:c} swLabel={s.Pts:c}  lumaDiff={diff,6:F1}  {(desync ? "*** CONTENT DESYNC ***" : "ok")}");
    }

    Console.WriteLine();
    if (desyncs > 0)
    {
        Console.WriteLine($"FAIL: {desyncs} target(s) show hardware video content that does not match the software reference.");
        return 4;
    }
    Console.WriteLine("PASS: hardware and software show the same picture at every seek target.");
    return 0;
}

static Dictionary<double, (TimeSpan Pts, byte[]? Sig)> CaptureSeekSignatures(
    string path, bool useHw, double[] targets, int holdMs, int audioOutRate, out string? error)
{
    error = null;
    var result = new Dictionary<double, (TimeSpan, byte[]?)>();
    var videoOut = new RecordingVideoOutput { CaptureSignatures = true };
    var options = new MediaPlayerOpenOptions(
        TryHardwareAcceleration: useHw,
        IncludeAudioRouter: true,
        AudioPacketQueueDepth: 720,
        VideoPacketQueueDepth: 512,
        FileReadBufferBytes: 4 * 1024 * 1024,
        FileVideoDecodeQueueCapacity: 16);

    if (!MediaPlayer.OpenFile(path).WithOptions(options)
            .WithVideoLead(videoOut, disposeOnPlayerDispose: false)
            .TryBuild(out var player, out var buildError))
    {
        error = buildError;
        return result;
    }

    using (player)
    {
        if (player!.AudioRouter is null || player.AudioSourceId is null || !player.Decoder.HasAudio)
        {
            error = "probe requires a media file with audio";
            return result;
        }

        var audioFormat = player.Decoder.Audio.Format;
        if (audioOutRate > 0) audioFormat = new AudioFormat(audioOutRate, audioFormat.Channels);
        var audioOut = new VirtualClockedAudioOutput(audioFormat);
        IAudioOutput routerAudioOut = audioOut.Format == player.Decoder.Audio.Format
            ? audioOut
            : ResamplingAudioOutput.Wrap(audioOut, player.Decoder.Audio.Format);
        var audioOutputId = player.AudioRouter.AddOutput(routerAudioOut, "verify_audio", pumpCapacityChunks: 4);
        player.AudioRouter.Connect(player.AudioSourceId, audioOutputId);

        const double probeOffsetSec = 0.4;
        foreach (var t in targets)
        {
            var target = TimeSpan.FromSeconds(t);
            if (target >= player.Duration - TimeSpan.FromSeconds(1))
            {
                result[t] = (TimeSpan.Zero, null);
                continue;
            }

            videoOut.Reset();
            player.SeekCoordinated(target, CancellationToken.None, PauseFlushPolicy.SkipFlush);
            player.PrewarmVideoAfterSeek();
            player.Play();
            Thread.Sleep(holdMs);
            player.Pause(CancellationToken.None, PauseFlushPolicy.SkipFlush);

            var probePts = target + TimeSpan.FromSeconds(probeOffsetSec);
            result[t] = videoOut.SignatureNearest(probePts);
        }
    }

    return result;
}

// Average absolute per-cell difference between two 8x8 luma signatures (0..255).
static double SignatureDistance(byte[] a, byte[] b)
{
    var n = Math.Min(a.Length, b.Length);
    if (n == 0) return double.MaxValue;
    long sum = 0;
    for (var i = 0; i < n; i++) sum += Math.Abs(a[i] - b[i]);
    return sum / (double)n;
}

static void PlayAndHold(
    MediaPlayer player,
    VirtualClockedAudioOutput audioOut,
    RecordingVideoOutput videoOut,
    string label,
    int holdMs)
{
    player.Play();
    Thread.Sleep(holdMs);
    PrintSnapshot(player, audioOut, videoOut, label);
}

static void Pause(MediaPlayer player, string label)
{
    player.Pause(CancellationToken.None, PauseFlushPolicy.SkipFlush);
    Console.WriteLine(
        $"  {label} clock={player.PlayClock.CurrentPosition:c} decoderA={SourcePosition(player.Decoder.Audio):c} decoderV={SourcePosition(player.Decoder.Video):c}");
}

static void PrintSnapshot(
    MediaPlayer player,
    VirtualClockedAudioOutput audioOut,
    RecordingVideoOutput videoOut,
    string label)
{
    var clock = player.PlayClock.CurrentPosition;
    var lastVideo = videoOut.LastSubmittedPts;
    var videoMinusClockMs = lastVideo is { } v ? (v - clock).TotalMilliseconds : double.NaN;
    var audioMinusClockMs = (SourcePosition(player.Decoder.Audio) - clock).TotalMilliseconds;
    Console.WriteLine(
        $"  {label} clock={clock:c} master={audioOut.ElapsedSinceStart:c} audioPos={SourcePosition(player.Decoder.Audio):c} videoSrc={SourcePosition(player.Decoder.Video):c} videoLast={(lastVideo?.ToString("c") ?? "(none)")} video-clock-ms={videoMinusClockMs:F1} audio-clock-ms={audioMinusClockMs:F1} displayed={player.Video.DisplayedCount} decoded={player.Video.DecodedCount} queued={player.Video.QueuedFrameCount} droppedLate={player.Video.DroppedLate}");
}

static TimeSpan SourcePosition(object source) =>
    source is ISeekableSource seekable ? seekable.Position : TimeSpan.Zero;

internal sealed class RecordingVideoOutput : IVideoOutput
{
    private readonly object _gate = new();
    private VideoFormat _format;
    private TimeSpan? _lastSubmittedPts;
    // Bounded ring of (pts, lumaSignature) for the content-verification mode. Only populated when
    // CaptureSignatures is set so the normal label-based run pays nothing for it.
    private readonly List<(TimeSpan Pts, byte[] Sig)> _signatures = [];

    public VideoFormat Format => _format;
    public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [];
    public TimeSpan? LastSubmittedPts { get { lock (_gate) return _lastSubmittedPts; } }

    /// <summary>When true, Submit computes an 8x8 luma signature per frame so SignatureNearest can return it.</summary>
    public bool CaptureSignatures { get; init; }

    public void Configure(VideoFormat format) => _format = format;

    public void Submit(VideoFrame frame)
    {
        lock (_gate)
        {
            _lastSubmittedPts = frame.PresentationTime;
            if (CaptureSignatures)
            {
                _signatures.Add((frame.PresentationTime, LumaSignature(frame)));
                if (_signatures.Count > 4096) _signatures.RemoveAt(0);
            }
        }
        frame.Dispose();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _lastSubmittedPts = null;
            _signatures.Clear();
        }
    }

    /// <summary>Signature (and label) of the captured frame whose PTS is closest to <paramref name="probe"/>.</summary>
    public (TimeSpan Pts, byte[]? Sig) SignatureNearest(TimeSpan probe)
    {
        lock (_gate)
        {
            (TimeSpan Pts, byte[] Sig)? best = null;
            var bestDelta = TimeSpan.MaxValue;
            foreach (var e in _signatures)
            {
                var d = (e.Pts - probe).Duration();
                if (d < bestDelta) { bestDelta = d; best = e; }
            }
            return best is { } b ? (b.Pts, b.Sig) : (TimeSpan.Zero, null);
        }
    }

    // Downsampled 8x8 mean-luma grid of plane 0. Plane 0 is the luma plane for NV12 (HW) and I420 (SW)
    // alike, so the signature is comparable across decoders even though their pixel formats differ. Robust
    // to the tiny per-pixel differences between HW and SW decode, sensitive to a wholly different image.
    private static byte[] LumaSignature(VideoFrame frame)
    {
        const int grid = 8;
        var sig = new byte[grid * grid];
        if (frame.PlaneCount == 0) return sig;

        var plane = frame.Planes[0].Span;
        var stride = frame.Strides[0];
        var width = frame.Format.Width;
        var height = frame.Format.Height;
        if (stride <= 0 || width <= 0 || height <= 0 || plane.IsEmpty) return sig;

        for (var gy = 0; gy < grid; gy++)
        for (var gx = 0; gx < grid; gx++)
        {
            long sum = 0;
            var count = 0;
            var y0 = (int)((long)gy * height / grid);
            var y1 = (int)((long)(gy + 1) * height / grid);
            var x0 = (int)((long)gx * width / grid);
            var x1 = (int)((long)(gx + 1) * width / grid);
            // Sample a few points per cell rather than every pixel — enough to distinguish frames cheaply.
            var yStep = Math.Max(1, (y1 - y0) / 4);
            var xStep = Math.Max(1, (x1 - x0) / 4);
            for (var y = y0; y < y1; y += yStep)
            {
                var row = y * stride;
                for (var x = x0; x < x1; x += xStep)
                {
                    var idx = row + x;
                    if (idx >= 0 && idx < plane.Length) { sum += plane[idx]; count++; }
                }
            }
            sig[gy * grid + gx] = count > 0 ? (byte)(sum / count) : (byte)0;
        }

        return sig;
    }
}

internal sealed class VirtualClockedAudioOutput : IAudioOutput, IClockedOutput, IFlushableOutput, IPlaybackClock
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastWallSeconds;
    private double _queuedFrames;
    private double _playedFrames;
    private double _epochFrames;

    public VirtualClockedAudioOutput(AudioFormat format)
    {
        Format = format;
        TargetQueueSamples = Math.Max(format.SampleRate / 10, 480 * 4);
    }

    public AudioFormat Format { get; }
    public int TargetQueueSamples { get; set; }
    public bool IsAdvancing => true;

    public TimeSpan ElapsedSinceStart
    {
        get
        {
            lock (_gate)
            {
                UpdatePlaybackLocked();
                return TimeSpan.FromSeconds(Math.Max(0, (_playedFrames - _epochFrames) / Format.SampleRate));
            }
        }
    }

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        lock (_gate)
        {
            UpdatePlaybackLocked();
            _queuedFrames += packedSamples.Length / Format.Channels;
        }
    }

    public bool WaitForCapacity(int chunkSamples, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            int waitMs;
            lock (_gate)
            {
                UpdatePlaybackLocked();
                if (_queuedFrames + chunkSamples <= TargetQueueSamples)
                    return true;
                var excess = _queuedFrames + chunkSamples - TargetQueueSamples;
                waitMs = Math.Max(1, (int)Math.Ceiling(1000.0 * excess / Format.SampleRate));
            }

            if (token.WaitHandle.WaitOne(waitMs))
                return false;
        }

        return false;
    }

    public void Flush()
    {
        lock (_gate)
        {
            UpdatePlaybackLocked();
            _queuedFrames = 0;
            _epochFrames = _playedFrames;
        }
    }

    private void UpdatePlaybackLocked()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var delta = now - _lastWallSeconds;
        if (delta <= 0)
            return;

        var frames = delta * Format.SampleRate;
        var audible = Math.Min(frames, _queuedFrames);
        _queuedFrames -= audible;
        _playedFrames += audible;
        _lastWallSeconds = now;
    }
}
