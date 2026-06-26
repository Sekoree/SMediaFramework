using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Registry;
using S.Media.Core.Triggers;
using S.Media.Core.Video;
using S.Media.Time;

namespace S.Media.Players;

/// <summary>
/// Plays one media object — a file/URI opened through the media registry (P2 — no globals) or caller-provided
/// live sources — by wiring a <see cref="VideoRouter"/> (always) plus an optional <see cref="AudioRouter"/> +
/// <see cref="MediaClock"/> to the source audio. No PortAudio / SDL / NDI here — attach those outputs from the
/// optional packages via <see cref="AttachVideoOutput"/> / <see cref="AttachAudioOutput"/>.
/// </summary>
/// <remarks>
/// <para>
/// When <paramref name="videoNegotiationLead"/> is <c>null</c>, a <see cref="DiscardingVideoOutput"/> is registered as the router
/// primary so decode and <see cref="VideoPlayer"/> can run with <strong>zero</strong> user video outputs; attach real
/// <see cref="IVideoOutput"/> outputs later via <see cref="VideoRouter.AddOutput"/> and <see cref="VideoRouter.TryAddRoute"/>.
/// </para>
/// <para>
/// Audio: when <see cref="MediaPlayerOpenOptions.IncludeAudioRouter"/> is true (default), an <see cref="AudioRouter"/> owns
/// <see cref="MediaContainerDecoder.Audio"/> and drives <see cref="IMediaClock"/> for video. You can run with no audio outputs
/// (router consumes the mux audio stream every chunk). Add PortAudio, NDI, or other outputs from their respective assemblies.
/// </para>
/// </remarks>
public sealed class MediaPlayer : IDisposable
{
    private readonly MediaPlaybackSession? _liveSession;
    private readonly VideoRouter? _liveVideoRouter;
    private readonly VideoPlayer? _liveVideo;
    private readonly AudioRouter? _liveAudioRouter;
    private readonly MediaClock? _liveAudioClock;
    private readonly IMediaClock? _liveClock;
    private readonly MediaClock? _liveFreerun;
    private readonly List<IDisposable> _ownedLiveDisposables = [];
    // Companion resources (e.g. a PortAudio host wired via WithPortAudio) the player owns and disposes
    // on Dispose — disposed AFTER the bundle/live graph so the router has stopped submitting before a
    // companion closes its hardware output. Keeps the simple "open with audio" path from leaking.
    private readonly List<IDisposable> _ownedCompanions = [];
    private readonly string _videoRouterInputId;
    private readonly IVideoOutput _videoInput;
    private readonly string? _audioSourceId;
    private readonly TriggerBus _triggerBus = new();
    private IAudioOutputPlaybackStats? _portAudioPlaybackStats;
    private bool _disposed;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Playback.MediaPlayer");

    // Set when the video source is live (NDI/capture): the player re-anchors it to the master at Play so it
    // presents Scheduled against the session clock (Doc 03), not master-less.
    private readonly ILiveVideoSource? _liveVideoSource;

    private MediaPlayer(
        VideoRouter videoRouter,
        VideoPlayer video,
        IMediaClock clock,
        AudioRouter? audioRouter,
        MediaClock? audioClock,
        MediaClock? freerun,
        string videoRouterInputId,
        IVideoOutput videoInput,
        string? audioSourceId,
        IEnumerable<IDisposable>? ownedLiveDisposables,
        ILiveVideoSource? liveVideoSource = null)
    {
        _liveVideoRouter = videoRouter;
        _liveVideo = video;
        _liveVideoSource = liveVideoSource;
        _liveClock = clock;
        _liveAudioRouter = audioRouter;
        _liveAudioClock = audioClock;
        _liveFreerun = freerun;
        _liveSession = new MediaPlaybackSession(video, clock, audioRouter, audioClock, audioSourceId);
        _videoRouterInputId = videoRouterInputId;
        _videoInput = videoInput;
        _audioSourceId = audioSourceId;
        if (ownedLiveDisposables is not null)
            _ownedLiveDisposables.AddRange(ownedLiveDisposables);
    }

    /// <summary>True once <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>Total media duration when the source reports it; 0 for live / unknown.</summary>
    public TimeSpan Duration { get; internal set; } = TimeSpan.Zero;

    public VideoRouter VideoRouter => _liveVideoRouter!;

    /// <summary>Input id returned by <see cref="VideoRouter.AddInput"/> — use with <see cref="VideoRouter.TryAddRoute"/>.</summary>
    public string VideoRouterInputId => _videoRouterInputId;

    /// <summary>
    /// The router input output the decoder feeds into. Submit pre-Play priming or warmup frames here
    /// rather than directly to per-branch leaf outputs so the router's pixel-format converters run for
    /// every branch (Avalonia keeps the source format, NDI gets the post-conversion format, etc.).
    /// </summary>
    public IVideoOutput VideoInput => _videoInput;

    public VideoPlayer Video => _liveVideo!;

    /// <summary>Present when <see cref="MediaPlayerOpenOptions.IncludeAudioRouter"/> was true at open time.</summary>
    public AudioRouter? AudioRouter => _liveAudioRouter;

    /// <summary>Media clock paired with <see cref="AudioRouter"/> when audio is wired.</summary>
    public MediaClock? AudioClock => _liveAudioClock;

    /// <summary>Router source id for <see cref="MediaContainerDecoder.Audio"/> when <see cref="AudioRouter"/> is non-null.</summary>
    public string? AudioSourceId => _audioSourceId;

    public IMediaClock PlayClock => _liveClock!;

    /// <summary>Non-null only when there was no audio router (video clocked from a freerun <see cref="MediaClock"/>).</summary>
    public MediaClock? FreerunClock => _liveFreerun;

    /// <summary>Scriptable trigger surface for OSC/MIDI adapters and host bindings.</summary>
    public TriggerBus Triggers => _triggerBus;

    /// <summary>
    /// Registers a companion resource the player owns and disposes when it is disposed (after the
    /// playback graph is torn down). Used by builder wire-ups (e.g. <c>WithPortAudio</c>) so the
    /// caller can dispose just the player and not leak the companion host.
    /// </summary>
    internal void RegisterOwnedCompanion(IDisposable companion)
    {
        ArgumentNullException.ThrowIfNull(companion);
        _ownedCompanions.Add(companion);
    }

    internal void SetPortAudioPlaybackStats(IAudioOutputPlaybackStats stats) =>
        _portAudioPlaybackStats = stats ?? throw new ArgumentNullException(nameof(stats));

    /// <summary>One-shot operational snapshot for HUDs and logging.</summary>
    public MediaPlayerMetrics GetMetrics()
    {
        var clock = PlayClock;
        var masterName = clock is MediaClock mc
            ? mc.Master?.GetType().Name ?? "(freerun)"
            : "(external)";
        var clockSnap = new MediaClockMetricsSnapshot(clock.CurrentPosition, masterName);

        VideoPlayerMetricsSnapshot? videoSnap = null;
        var vp = Video;
        videoSnap = new VideoPlayerMetricsSnapshot(
            vp.DecodedCount,
            vp.DisplayedCount,
            vp.DroppedLate,
            vp.DroppedDrain);

        AudioRouterMetricsSnapshot? audioRouterSnap = null;
        IReadOnlyList<AudioOutputPumpMetricsEntry> audioOutputs = [];
        var ar = AudioRouter;
        if (ar is not null)
        {
            var agg = ar.GetAggregatePumpStats();
            audioRouterSnap = new AudioRouterMetricsSnapshot(
                ar.ChunksProduced,
                agg.TotalEnqueued,
                agg.TotalProcessed,
                agg.TotalDropped,
                agg.OutputCount);
            var ids = ar.GetRegisteredOutputIds();
            var list = new AudioOutputPumpMetricsEntry[ids.Count];
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                list[i] = new AudioOutputPumpMetricsEntry(id, ar.GetPumpStats(id));
            }

            audioOutputs = list;
        }

        var videoRouter = VideoRouter;
        var vIds = videoRouter.GetRegisteredOutputIds();
        var vList = new VideoOutputPumpMetricsEntry[vIds.Count];
        for (var i = 0; i < vIds.Count; i++)
        {
            var id = vIds[i];
            videoRouter.TryGetVideoOutputPumpMetrics(id, out var m);
            vList[i] = new VideoOutputPumpMetricsEntry(id, m);
        }

        PortAudioMetricsSnapshot? paSnap = null;
        if (_portAudioPlaybackStats is { } pa)
            paSnap = new PortAudioMetricsSnapshot(pa.PlayedSamples, pa.UnderrunSamples, pa.DroppedSamples);

        NdiIngestMetricsSnapshot? ndiSnap = null;
        foreach (var d in _ownedLiveDisposables)
        {
            if (d is INdiOverflowReporter ndi)
            {
                ndiSnap = new NdiIngestMetricsSnapshot(ndi.AudioOverflowFloats, ndi.VideoOverflowFrames);
                break;
            }
        }

        return new MediaPlayerMetrics(
            clockSnap,
            videoSnap,
            audioRouterSnap,
            vList,
            audioOutputs,
            paSnap,
            ndiSnap);
    }

    // --- one-call output wiring (A1) ---------------------------------------

    /// <summary>
    /// Adds <paramref name="output"/> to <see cref="VideoRouter"/> and routes this player's video
    /// input to it in one call. Returns the registered output id (pass it to
    /// <see cref="VideoRouter.RemoveOutput"/> to detach). On a negotiation failure the output
    /// registration is rolled back and <see cref="InvalidOperationException"/> is thrown —
    /// the player keeps presenting on its existing routes either way.
    /// </summary>
    /// <param name="synchronous">Forwarded to <see cref="VideoRouter.AddOutput"/> — pass
    /// <see langword="true"/> only for outputs whose <see cref="IVideoOutput.Submit"/> returns promptly.</param>
    public string AttachVideoOutput(IVideoOutput output, string? id = null, bool synchronous = false)
    {
        return TryAttachVideoOutput(output, out var outputId, out var error, id, synchronous)
            ? outputId
            : throw new InvalidOperationException(error);
    }

    /// <summary>Non-throwing form of <see cref="AttachVideoOutput"/>.</summary>
    public bool TryAttachVideoOutput(
        IVideoOutput output,
        [NotNullWhen(true)] out string? outputId,
        [NotNullWhen(false)] out string? error,
        string? id = null,
        bool synchronous = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        error = null;
        outputId = VideoRouter.AddOutput(output, id, synchronous: synchronous);
        if (VideoRouter.TryAddRoute(_videoRouterInputId, outputId, out var routeError))
            return true;

        VideoRouter.RemoveOutput(outputId);
        outputId = null;
        error = routeError;
        return false;
    }

    /// <summary>
    /// Adds <paramref name="output"/> to <see cref="AudioRouter"/> and routes the decoder's audio
    /// source to it in one call. Returns the registered output id. <paramref name="map"/> defaults
    /// to the identity map for the output's channel count; on a routing failure the output
    /// registration is rolled back before the exception propagates.
    /// </summary>
    /// <exception cref="InvalidOperationException">The player has no <see cref="AudioRouter"/>
    /// (opened with <c>IncludeAudioRouter: false</c>) or no decodable audio stream.</exception>
    public string AttachAudioOutput(IAudioOutput output, string? id = null, ChannelMap? map = null, float gain = 1.0f)
    {
        ArgumentNullException.ThrowIfNull(output);
        var router = AudioRouter
            ?? throw new InvalidOperationException(
                "AttachAudioOutput: this player has no AudioRouter (opened with IncludeAudioRouter: false).");
        var sourceId = AudioSourceId
            ?? throw new InvalidOperationException(
                "AttachAudioOutput: this player has no routed audio source (no decodable audio stream).");

        var outputId = router.AddOutput(output, id);
        try
        {
            if (map is { } m)
                router.AddRoute(sourceId, outputId, m, gain);
            else
                router.Route(sourceId, outputId, gain);
        }
        catch
        {
            router.RemoveOutput(outputId);
            throw;
        }

        return outputId;
    }

    public void Play(
        Action? prefillBeforeHardware = null,
        Action? startHardware = null,
        IPlaybackClock? videoOnlyMaster = null,
        Func<bool>? verifyPrebufferAfterPrefill = null)
    {
        // Live video: re-anchor the source's synthesized PTS to the master so prebuffered + subsequent frames
        // present Scheduled against the session clock (Doc 03 §2), not master-less. No-op for file sources.
        _liveVideoSource?.RebaseToLatest(_liveClock?.CurrentPosition ?? TimeSpan.Zero);
        _liveSession!.Play(prefillBeforeHardware, startHardware, videoOnlyMaster, verifyPrebufferAfterPrefill);
    }

    public void Pause(CancellationToken cancellationToken = default,
        PauseFlushPolicy flushPolicy = PauseFlushPolicy.FlushCodecPipelines)
    {
        var flush = ResolveFlushAction(flushPolicy);
        _liveSession!.Pause(cancellationToken, flush);
    }

    public void PauseWithFlushAction(Action flushAction, CancellationToken cancellationToken = default)
    {
        _liveSession!.Pause(cancellationToken, flushAction);
    }

    public void Seek(TimeSpan position)
    {
        _liveSession!.Seek(position);
    }

    public void SeekCoordinated(TimeSpan position, CancellationToken cancellationToken = default,
        PauseFlushPolicy flushPolicy = PauseFlushPolicy.FlushCodecPipelines)
    {
        var flush = ResolveFlushAction(flushPolicy);
        _liveSession!.SeekCoordinated(position, cancellationToken, flush);
    }

    /// <summary>
    /// Spin up video decode briefly after a coordinated seek, before <see cref="Play"/>.
    /// </summary>
    public void PrewarmVideoAfterSeek()
    {
        // Registry/live sources prewarm through the VideoPlayer; there is no container decoder to spin up.
    }

    private static Action? ResolveFlushAction(PauseFlushPolicy policy) => null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TryDispose(() => _liveVideo?.Dispose(), "MediaPlayer.Dispose: VideoPlayer");
        TryDispose(() => _liveAudioRouter?.Dispose(), "MediaPlayer.Dispose: AudioRouter");
        TryDispose(() => _liveAudioClock?.Dispose(), "MediaPlayer.Dispose: AudioClock");
        TryDispose(() => _liveVideoRouter?.Dispose(), "MediaPlayer.Dispose: VideoRouter");
        TryDispose(() => _liveFreerun?.Dispose(), "MediaPlayer.Dispose: MediaClock");
        foreach (var d in _ownedLiveDisposables)
            TryDispose(d.Dispose, "MediaPlayer.Dispose: source");

        // After the playback graph is down (router stopped) so a companion can close its hardware
        // output without the router still submitting into it.
        foreach (var c in _ownedCompanions)
            TryDispose(c.Dispose, "MediaPlayer.Dispose: owned companion");
    }

    private static void TryDispose(Action? dispose, string debugLabel)
    {
        if (dispose is null)
            return;
        MediaDiagnostics.SwallowDisposeErrors(dispose, debugLabel);
    }

    /// <summary>Registers <see cref="Console.CancelKeyPress"/> to cancel <paramref name="cts"/> while swallowing process exit.</summary>
    public static void AttachConsoleCancelKeyPress(CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(cts);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
    }

    // --- registry-driven open (P2 — opens through IMediaRegistry, no globals) -------------------------

    /// <summary>The mix sample rate (the audio router's rate), or 0 when there is no audio router.</summary>
    public int SampleRate => _liveAudioRouter?.SampleRate ?? 0;

    /// <summary>The master playhead position.</summary>
    public TimeSpan Position => PlayClock.CurrentPosition;

    /// <summary>True while the active playback clock is advancing.</summary>
    public bool IsRunning => _liveClock?.IsRunning ?? false;

    /// <summary>
    /// Opens <paramref name="uri"/> through <paramref name="registry"/>: the highest-confidence decoder
    /// provides the video and/or audio sources (whichever it can open — audio-only and video-only both
    /// work), wired into the live playback graph. The opened sources are owned by the player.
    /// </summary>
    public static bool TryOpen(
        IMediaRegistry registry,
        string uri,
        in MediaPlayerOpenOptions options,
        IVideoOutput? videoNegotiationLead,
        [NotNullWhen(true)] out MediaPlayer? player,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrEmpty(uri);
        player = null;
        error = null;

        // Open both kinds opportunistically: a confidence-matched decoder still throws when the source lacks
        // that stream (an audio-only file has no video, and vice-versa). Tolerate that here so the dual-open
        // falls back to whichever kind exists; a genuinely unopenable source surfaces as the "no decoder"
        // error below (both null).
        IVideoSource? video = null;
        if (options.VideoStreamIndex != MediaPlayerOpenOptions.DisabledStreamIndex)
            try { registry.TryOpenVideo(uri, options.ToVideoSourceOpenOptions(), out video); }
            catch { video = null; }
        IAudioSource? audio = null;
        if (options.IncludeAudioRouter && options.AudioStreamIndex != MediaPlayerOpenOptions.DisabledStreamIndex)
            try { registry.TryOpenAudio(uri, options.ToAudioSourceOpenOptions(), out audio); }
            catch { audio = null; }

        if (video is null && audio is null)
        {
            error = $"no registered decoder could open '{uri}' for audio or video " +
                $"(registered: {string.Join(", ", registry.Decoders.Select(d => d.Name))}).";
            return false;
        }

        if (TryOpenLive(audio, video, options, videoNegotiationLead, disposeNegotiationLead: false,
                disposeSourcesOnDispose: true, out player, out error))
        {
            WireAdaptiveRateFromRegistry(registry, player);
            return true;
        }

        (video as IDisposable)?.Dispose();
        (audio as IDisposable)?.Dispose();
        return false;
    }

    /// <summary>
    /// Enables adaptive-rate drift correction on the player's non-master audio outputs when the registry
    /// provides a wrapper factory (FFmpeg). The router's per-output pump-pressure signal drives a small
    /// resample-rate bias on each non-master output; the master device stays the clock. No-op for
    /// single-output players (the only output is the master) and when no factory is registered.
    /// </summary>
    private static void WireAdaptiveRateFromRegistry(IMediaRegistry registry, MediaPlayer player)
    {
        if (!registry.SupportsAdaptiveRateOutput || player.AudioRouter is not { } router)
            return;

        router.AdaptiveRateWrapper = (r, inner, outputId, maxRateDeltaHz) =>
        {
            // The monitor subscribes to the router's PumpPressure for this output; it is disposed with the
            // wrapped output (passed as biasSource), so the subscription is released on RemoveOutput.
            var monitor = new PumpPressurePlaybackHintMonitor(r, outputId);
            return registry.CreateAdaptiveRateOutput(inner, () => monitor.HintPpmBias, maxRateDeltaHz, monitor) ?? inner;
        };
        router.EnableAdaptiveRateOnNonMasterOutputs();
    }

    /// <summary>Fluent open: <c>OpenFile(registry, path).WithOptions(...).Build()</c>. The provider is
    /// selected by the URI scheme (a bare path opens via the default file provider — D2).</summary>
    public static MediaPlayerOpenFileBuilder OpenFile(IMediaRegistry registry, string filePathOrUri) =>
        new(registry, filePathOrUri);

    /// <summary>Fluent open for an absolute URI (<c>http:</c> / <c>rtsp:</c> / <c>ndi:</c> / …).</summary>
    public static MediaPlayerOpenFileBuilder OpenUri(IMediaRegistry registry, Uri mediaUri) =>
        new(registry, (mediaUri ?? throw new ArgumentNullException(nameof(mediaUri))).ToString());

    /// <summary>Throwing form of <see cref="TryOpen"/>.</summary>
    public static MediaPlayer Open(IMediaRegistry registry, string uri, MediaPlayerOpenOptions? options = null) =>
        TryOpen(registry, uri, options ?? MediaPlayerOpenOptions.Default, videoNegotiationLead: null, out var player, out var error)
            ? player
            : throw new InvalidOperationException(error);

    /// <summary>
    /// Convenience for the audio-first path: opens the audio of <paramref name="uri"/> through the registry
    /// and wires it to a master output created on <paramref name="audioBackend"/> (the clocked device).
    /// </summary>
    public static MediaPlayer OpenAudio(
        IMediaRegistry registry,
        IAudioBackend audioBackend,
        string uri,
        string? deviceId = null,
        int channels = 2)
    {
        ArgumentNullException.ThrowIfNull(audioBackend);
        var player = Open(registry, uri, MediaPlayerOpenOptions.Default);
        try
        {
            var devices = audioBackend.EnumerateOutputDevices();
            var device = deviceId is null
                ? devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault()
                : devices.FirstOrDefault(d => d.Id == deviceId)
                  ?? throw new ArgumentException($"audio output device '{deviceId}' not found", nameof(deviceId));
            var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
            var output = audioBackend.CreateOutput(device?.Id, new AudioFormat(rate, channels));
            if (output is IDisposable d)
                player.RegisterOwnedCompanion(d);
            player.AttachAudioOutput(output, "_master");
            return player;
        }
        catch
        {
            player.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One-call open for a <em>live</em> source (e.g. <c>ndi://&lt;name&gt;</c>). Opens it through the
    /// registry with Scheduled presentation; the source is warmed and re-anchored to the master at
    /// <see cref="Play"/> so live video presents scheduled against the session clock (Doc 03 §2), not
    /// master-less. With no <paramref name="audioBackend"/> the group is video-led on a free-running clock and
    /// no audio is opened; with one (and a source that has audio) a master output is created on it (the device
    /// becomes the clock reference). Attach video outputs with <see cref="AttachVideoOutput"/>, then
    /// <see cref="Play"/>.
    /// </summary>
    public static MediaPlayer OpenLive(
        IMediaRegistry registry,
        string uri,
        IAudioBackend? audioBackend = null,
        string? deviceId = null,
        MediaPlayerOpenOptions? options = null)
    {
        var opts = options ?? MediaPlayerOpenOptions.Default;
        opts = opts with
        {
            LiveVideoPresentation = VideoPresentationMode.Scheduled,
            // Without an audio backend, don't open a (separate) live audio connection at all.
            IncludeAudioRouter = audioBackend is not null && opts.IncludeAudioRouter,
        };

        var player = Open(registry, uri, opts);
        if (audioBackend is null || player.AudioRouter is null)
            return player;

        try
        {
            var devices = audioBackend.EnumerateOutputDevices();
            var device = deviceId is null
                ? devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault()
                : devices.FirstOrDefault(d => d.Id == deviceId)
                  ?? throw new ArgumentException($"audio output device '{deviceId}' not found", nameof(deviceId));
            var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
            var output = audioBackend.CreateOutput(device?.Id, new AudioFormat(rate, 2));
            if (output is IDisposable d)
                player.RegisterOwnedCompanion(d);
            player.AttachAudioOutput(output, "_master");
            return player;
        }
        catch
        {
            player.Dispose();
            throw;
        }
    }

    /// <summary>Opens a local media file path (not a URI string — use <see cref="OpenUri"/> for <c>http:</c> / <c>rtsp:</c>).</summary>
    internal static bool TryOpenLive(
        IAudioSource? audioSource,
        IVideoSource? videoSource,
        in MediaPlayerOpenOptions options,
        IVideoOutput? videoNegotiationLead,
        bool disposeNegotiationLead,
        [NotNullWhen(true)] out MediaPlayer? player,
        out string? errorMessage) =>
        TryOpenLive(
            audioSource,
            videoSource,
            options,
            videoNegotiationLead,
            disposeNegotiationLead,
            disposeSourcesOnDispose: true,
            out player,
            out errorMessage);

    /// <summary>
    /// Opens a player graph from live sources. When
    /// <paramref name="disposeSourcesOnDispose"/> is true, the live video source
    /// is disposed with the player; the live audio source is owned by
    /// <see cref="AudioRouter"/> when it is wired.
    /// </summary>
    internal static bool TryOpenLive(
        IAudioSource? audioSource,
        IVideoSource? videoSource,
        in MediaPlayerOpenOptions options,
        IVideoOutput? videoNegotiationLead,
        bool disposeNegotiationLead,
        bool disposeSourcesOnDispose,
        [NotNullWhen(true)] out MediaPlayer? player,
        out string? errorMessage)
    {
        player = null;
        errorMessage = null;

        if (!options.ValidateWin32Nv12Flags(out errorMessage))
            return false;

        if (audioSource is null && videoSource is null)
        {
            errorMessage = "at least one live audio or video source is required.";
            return false;
        }

        if (audioSource is not null && videoSource is null && !options.IncludeAudioRouter)
        {
            errorMessage = "audio-only live sources require IncludeAudioRouter.";
            return false;
        }


        AudioRouter? audioRouter = null;
        MediaClock? audioClock = null;
        string? audioSourceId = null;
        MediaClock? freerun = null;
        IMediaClock playClock;
        VideoRouter? router = null;
        VideoPlayer? videoPlayer = null;
        var ownedDisposables = new List<IDisposable>();
        var effectiveVideoSource = videoSource ?? new EmptyLiveVideoSource();

        try
        {
            if (options.IncludeAudioRouter && audioSource is not null)
            {
                audioClock = new MediaClock();
                audioRouter = new AudioRouter(audioSource.Format.SampleRate, options.AudioChunkSamples);
                audioRouter.AttachMasterClock(audioClock);
                audioSourceId = disposeSourcesOnDispose
                    ? audioRouter.AddOwnedSource(audioSource)
                    : audioRouter.AddSource(audioSource);
                // Route the audio source to a discard sink so it is actually consumed — advancing the
                // master clock and reaching EOF for "play to completion". Sources are only pulled when
                // routed; a real clocked output (e.g. PortAudio) attached later still becomes the pacing
                // primary and plays the audio — this sink just drops its copy.
                var audioDiscardId = audioRouter.AddOutput(new DiscardingAudioOutput(audioSource.Format), "_audio_discard");
                audioRouter.Connect(audioSourceId, audioDiscardId);
                playClock = audioClock;
            }
            else
            {
                freerun = new MediaClock();
                playClock = freerun;
                if (disposeSourcesOnDispose && audioSource is IDisposable audioDisposable)
                    ownedDisposables.Add(audioDisposable);
            }

            if (disposeSourcesOnDispose && videoSource is IDisposable videoDisposable)
                ownedDisposables.Add(videoDisposable);

            // A live video source (NDI/capture) publishes its NativePixelFormats only after its first frame,
            // which VideoPlayer's up-front negotiation needs. Block for that first frame and discard it; the
            // player re-anchors the source to the master at Play (Doc 03 §2).
            var liveVideo = videoSource as ILiveVideoSource;
            if (liveVideo is not null && videoSource!.TryReadNextFrame(out var warmFrame))
                warmFrame.Dispose();

            router = new VideoRouter(null);
            string primaryOutputId;
            if (videoNegotiationLead is null)
            {
                var discard = new DiscardingVideoOutput();
                primaryOutputId = router.AddOutput(discard, "_discard",
                    disposeOutputOnRouterDispose: true, synchronous: true);
            }
            else
            {
                primaryOutputId = router.AddOutput(
                    videoNegotiationLead,
                    "_primary",
                    disposeOutputOnRouterDispose: disposeNegotiationLead);
            }

            var vin = router.AddInput(primaryOutputId);
            var decodeQueueCap = options.FileVideoDecodeQueueCapacity > 0
                ? options.FileVideoDecodeQueueCapacity
                : options.LiveVideoDecodeQueueCapacity > 0
                    ? options.LiveVideoDecodeQueueCapacity
                    : 4;
            videoPlayer = new VideoPlayer(
                effectiveVideoSource,
                vin.Output,
                playClock,
                queueCapacity: decodeQueueCap,
                presentationMode: options.LiveVideoPresentation);

            player = new MediaPlayer(
                router,
                videoPlayer,
                playClock,
                audioRouter,
                audioClock,
                audioRouter is null ? freerun : null,
                vin.Id,
                vin.Output,
                audioSourceId,
                ownedDisposables,
                liveVideo);

            Trace.LogInformation("TryOpenLive: opened (hasAudio={HasAudio} hasVideo={HasVideo} audioRate={AudioRate}Hz videoFmt={VideoFmt} clockType={Clock} negotiationLead={Lead})",
                audioSource is not null,
                videoSource is not null,
                audioSource?.Format.SampleRate ?? 0,
                videoPlayer.Format,
                playClock.GetType().Name,
                videoNegotiationLead?.GetType().Name ?? "(discard)");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            Trace.LogError(ex, "TryOpenLive failed");
            TryDispose(() => videoPlayer?.Dispose(), "MediaPlayer.TryOpenLive: VideoPlayer");
            TryDispose(() => audioRouter?.Dispose(), "MediaPlayer.TryOpenLive: AudioRouter");
            TryDispose(() => audioClock?.Dispose(), "MediaPlayer.TryOpenLive: AudioClock");
            TryDispose(() => router?.Dispose(), "MediaPlayer.TryOpenLive: VideoRouter");
            TryDispose(() => freerun?.Dispose(), "MediaPlayer.TryOpenLive: MediaClock");
            foreach (var d in ownedDisposables)
                TryDispose(d.Dispose, "MediaPlayer.TryOpenLive: live source");
            player = null;
            return false;
        }
    }

    /// <summary>Uses an already-opened decoder (caller keeps ownership unless <paramref name="decoderOwnership"/> requests otherwise).</summary>
    /// <param name="videoSourceOverride">
    /// When non-null, drives <see cref="VideoPlayer"/> instead of <see cref="MediaContainerDecoder.Video"/>
    /// (preset scaling, fade wrappers, etc.). The decoder is still used for audio and container metadata.
    /// </param>
    private sealed class EmptyLiveVideoSource : IVideoSource, ISeekableSource
    {
        private VideoFormat _format = new(16, 16, PixelFormat.Bgra32, new Rational(30, 1));

        public VideoFormat Format => _format;

        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];

        public bool IsExhausted => true;

        // ISeekableSource: the stub carries no frames, so duration/position are zero and seek is a no-op —
        // this lets audio-only registry clips honour a coordinated A/V seek (the audio source does the work).
        public TimeSpan Duration => TimeSpan.Zero;

        public TimeSpan Position => TimeSpan.Zero;

        public void Seek(TimeSpan position)
        {
        }

        public void SelectOutputFormat(PixelFormat format) => _format = _format with { PixelFormat = format };

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            frame = null!;
            return false;
        }
    }
}
