using System.Diagnostics;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using S.Media.FFmpeg.Audio;
using S.Media.Playback;

if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
{
    Console.Error.WriteLine("usage: TransportSyncProbe <media-file> [--no-hw] [--cycles N] [--hold-ms N] [--targets s1,s2,...] [--audio-out-rate N]");
    return args.Length == 0 ? 2 : 0;
}

var path = args[0];
var noHw = args.Contains("--no-hw", StringComparer.Ordinal);
var cycles = ReadInt("--cycles", 1);
var holdMs = ReadInt("--hold-ms", 1500);
var audioOutRate = ReadInt("--audio-out-rate", 0);
var targets = ReadTargets("--targets") ?? [3, 17, 30, 60, 90];

if (!File.Exists(path))
{
    Console.Error.WriteLine($"file not found: {path}");
    return 2;
}

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

    public VideoFormat Format => _format;
    public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [];
    public TimeSpan? LastSubmittedPts { get { lock (_gate) return _lastSubmittedPts; } }

    public void Configure(VideoFormat format) => _format = format;

    public void Submit(VideoFrame frame)
    {
        lock (_gate)
            _lastSubmittedPts = frame.PresentationTime;
        frame.Dispose();
    }

    public void Reset()
    {
        lock (_gate)
            _lastSubmittedPts = null;
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
