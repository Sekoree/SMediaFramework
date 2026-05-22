using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;

namespace S.Media.Playback;

/// <summary>
/// Opens a shared-mux <see cref="MediaContainerDecoder"/> with a <see cref="VideoRouter"/> (always),
/// optional <see cref="AudioPlayer"/> wired to decoder audio (no PortAudio / SDL / NDI — add outputs from optional packages),
/// and <see cref="S.Media.FFmpeg.MediaContainerPlaybackBundle"/> for safe teardown.
/// </summary>
/// <remarks>
/// <para>
/// When <paramref name="videoNegotiationLead"/> is <c>null</c>, a <see cref="DiscardingVideoOutput"/> is registered as the router
/// primary so decode and <see cref="VideoPlayer"/> can run with <strong>zero</strong> user video outputs; attach real
/// <see cref="IVideoOutput"/> outputs later via <see cref="VideoRouter.AddOutput"/> and <see cref="VideoRouter.TryAddRoute"/>.
/// </para>
/// <para>
/// Audio: when <see cref="MediaPlayerOpenOptions.IncludeAudioRouter"/> is true (default), an <see cref="AudioPlayer"/> owns
/// <see cref="MediaContainerDecoder.Audio"/> and drives <see cref="IMediaClock"/> for video. You can run with no audio outputs
/// (router consumes the mux audio stream every chunk). Add PortAudio, NDI, or other outputs from their respective assemblies.
/// </para>
/// </remarks>
public sealed class MediaPlayer : IDisposable
{
    private readonly MediaContainerPlaybackBundle? _bundle;
    private readonly MediaPlaybackSession? _liveSession;
    private readonly VideoRouter? _liveVideoRouter;
    private readonly VideoPlayer? _liveVideo;
    private readonly AudioPlayer? _liveAudio;
    private readonly IMediaClock? _liveClock;
    private readonly MediaClock? _liveFreerun;
    private readonly List<IDisposable> _ownedLiveDisposables = [];
    private readonly string _videoRouterInputId;
    private readonly IVideoOutput _videoInput;
    private readonly string? _audioSourceId;
    private readonly MediaClock? _freerun;
    private bool _disposed;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Playback.MediaPlayer");

    private MediaPlayer(
        MediaContainerPlaybackBundle bundle,
        string videoRouterInputId,
        IVideoOutput videoInput,
        string? audioSourceId,
        MediaClock? freerun)
    {
        _bundle = bundle;
        _videoRouterInputId = videoRouterInputId;
        _videoInput = videoInput;
        _audioSourceId = audioSourceId;
        _freerun = freerun;
    }

    private MediaPlayer(
        VideoRouter videoRouter,
        VideoPlayer video,
        IMediaClock clock,
        AudioPlayer? audio,
        MediaClock? freerun,
        string videoRouterInputId,
        IVideoOutput videoInput,
        string? audioSourceId,
        IEnumerable<IDisposable>? ownedLiveDisposables)
    {
        _liveVideoRouter = videoRouter;
        _liveVideo = video;
        _liveClock = clock;
        _liveAudio = audio;
        _liveFreerun = freerun;
        _liveSession = new MediaPlaybackSession(video, clock, audio);
        _videoRouterInputId = videoRouterInputId;
        _videoInput = videoInput;
        _audioSourceId = audioSourceId;
        if (ownedLiveDisposables is not null)
            _ownedLiveDisposables.AddRange(ownedLiveDisposables);
    }

    public bool IsLive => _bundle is null;

    public bool HasContainerDecoder => _bundle is not null;

    public MediaContainerDecoder Decoder =>
        _bundle?.Decoder
        ?? throw new InvalidOperationException("This MediaPlayer was opened from live sources and has no container decoder.");

    /// <inheritdoc cref="MediaContainerDecoder.Duration" />
    public TimeSpan Duration => _bundle?.Decoder.Duration ?? TimeSpan.Zero;

    public VideoRouter VideoRouter => _bundle?.VideoRouter ?? _liveVideoRouter!;

    /// <summary>Input id returned by <see cref="VideoRouter.AddInput"/> — use with <see cref="VideoRouter.TryAddRoute"/>.</summary>
    public string VideoRouterInputId => _videoRouterInputId;

    /// <summary>
    /// The router input output the decoder feeds into. Submit pre-Play priming or warmup frames here
    /// rather than directly to per-branch leaf outputs so the router's pixel-format converters run for
    /// every branch (Avalonia keeps the source format, NDI gets the post-conversion format, etc.).
    /// </summary>
    public IVideoOutput VideoInput => _videoInput;

    public VideoPlayer Video => _bundle?.Video ?? _liveVideo!;

    /// <summary>Present when <see cref="MediaPlayerOpenOptions.IncludeAudioRouter"/> was true at open time.</summary>
    public AudioPlayer? Audio => _bundle?.Audio ?? _liveAudio;

    /// <summary>Router source id for <see cref="MediaContainerDecoder.Audio"/> when <see cref="Audio"/> is non-null.</summary>
    public string? AudioSourceId => _audioSourceId;

    public IMediaClock PlayClock => _bundle?.Clock ?? _liveClock!;

    /// <summary>Non-null only when there was no <see cref="AudioPlayer"/> (video clocked from a freerun <see cref="MediaClock"/>).</summary>
    public MediaClock? FreerunClock => _freerun ?? _liveFreerun;

    public MediaContainerPlaybackBundle Bundle =>
        _bundle ?? throw new InvalidOperationException("This MediaPlayer was opened from live sources and has no MediaContainerPlaybackBundle.");

    public MediaContainerSession Session =>
        _bundle?.Session
        ?? throw new InvalidOperationException("This MediaPlayer was opened from live sources; use PlaybackSession.");

    public IAvPlaybackSession PlaybackSession => _bundle?.Session.Session ?? _liveSession!;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_bundle is not null)
        {
            _bundle.Dispose();
            return;
        }

        TryDispose(() => _liveVideo?.Dispose(), "MediaPlayer.Dispose: live VideoPlayer");
        TryDispose(() => _liveAudio?.Dispose(), "MediaPlayer.Dispose: live AudioPlayer");
        TryDispose(() => _liveVideoRouter?.Dispose(), "MediaPlayer.Dispose: live VideoRouter");
        TryDispose(() => _liveFreerun?.Dispose(), "MediaPlayer.Dispose: live MediaClock");
        foreach (var d in _ownedLiveDisposables)
            TryDispose(d.Dispose, "MediaPlayer.Dispose: live source");
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

    /// <summary>Opens from a media file path (decoder owned by the bundle).</summary>
    public static bool TryOpen(
        string mediaPath,
        in MediaPlayerOpenOptions options,
        IVideoOutput? videoNegotiationLead,
        bool disposeNegotiationLead,
        [NotNullWhen(true)] out MediaPlayer? player,
        out string? errorMessage) =>
        TryOpen(
            mediaPath,
            options,
            videoNegotiationLead,
            disposeNegotiationLead,
            MediaPlayerDecoderOwnership.BundleDisposesDecoder,
            out player,
            out errorMessage);

    /// <summary>
    /// Opens from a local media file path (decoder owned by the bundle). Prefer this explicit helper
    /// when user input is known to be a file path; use <see cref="TryOpenUri"/> for network/protocol URLs.
    /// </summary>
    public static bool TryOpenFile(
        string mediaPath,
        in MediaPlayerOpenOptions options,
        IVideoOutput? videoNegotiationLead,
        bool disposeNegotiationLead,
        [NotNullWhen(true)] out MediaPlayer? player,
        out string? errorMessage) =>
        TryOpen(mediaPath, options, videoNegotiationLead, disposeNegotiationLead, out player, out errorMessage);

    /// <summary>Opens from a media file path with explicit decoder ownership.</summary>
    public static bool TryOpen(
        string mediaPath,
        in MediaPlayerOpenOptions options,
        IVideoOutput? videoNegotiationLead,
        bool disposeNegotiationLead,
        MediaPlayerDecoderOwnership decoderOwnership,
        [NotNullWhen(true)] out MediaPlayer? player,
        out string? errorMessage)
    {
        player = null;
        errorMessage = null;

        if (!options.ValidateWin32Nv12Flags(out errorMessage))
            return false;

        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            errorMessage = "media path is required.";
            return false;
        }

        if (!File.Exists(mediaPath))
        {
            errorMessage = $"file not found: {mediaPath}";
            return false;
        }

        FFmpegRuntime.EnsureInitialized();

        MediaContainerDecoder? media = null;
        try
        {
            media = MediaContainerDecoder.Open(mediaPath, options.ToVideoDecoderOpenOptions());
            media.SeekPresentation(TimeSpan.Zero);
            return TryOpenCore(
                media,
                options,
                videoNegotiationLead,
                disposeNegotiationLead,
                decoderOwnership,
                videoSourceOverride: null,
                out player,
                out errorMessage);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            media?.Dispose();
            player = null;
            return false;
        }
    }

    /// <summary>
    /// Opens from a media URI. <c>file:</c> URIs are validated as local files; non-file absolute URIs
    /// are passed through to FFmpeg protocol I/O (for example <c>http:</c>, <c>https:</c>, or <c>rtsp:</c>).
    /// </summary>
    public static bool TryOpenUri(
        Uri mediaUri,
        in MediaPlayerOpenOptions options,
        IVideoOutput? videoNegotiationLead,
        bool disposeNegotiationLead,
        [NotNullWhen(true)] out MediaPlayer? player,
        out string? errorMessage)
    {
        var openOptions = options;
        return TryOpenFromDecoderFactory(
            () => MediaContainerDecoder.OpenUri(mediaUri, openOptions.ToVideoDecoderOpenOptions()),
            openOptions,
            videoNegotiationLead,
            disposeNegotiationLead,
            MediaPlayerDecoderOwnership.BundleDisposesDecoder,
            out player,
            out errorMessage);
    }

    /// <summary>
    /// Opens from a finite readable media stream by spooling it to a temporary file owned by the decoder.
    /// For live/network streams, prefer <see cref="TryOpenUri"/> so FFmpeg can use protocol-native I/O.
    /// </summary>
    public static bool TryOpenStream(
        Stream mediaStream,
        string? inputName,
        in MediaPlayerOpenOptions options,
        IVideoOutput? videoNegotiationLead,
        bool disposeNegotiationLead,
        [NotNullWhen(true)] out MediaPlayer? player,
        out string? errorMessage)
    {
        var openOptions = options;
        return TryOpenFromDecoderFactory(
            () => MediaContainerDecoder.OpenStream(mediaStream, inputName, openOptions.ToVideoDecoderOpenOptions()),
            openOptions,
            videoNegotiationLead,
            disposeNegotiationLead,
            MediaPlayerDecoderOwnership.BundleDisposesDecoder,
            out player,
            out errorMessage);
    }

    /// <summary>
    /// Opens from a finite readable media stream by spooling it to a temporary file owned by the decoder.
    /// </summary>
    public static bool TryOpenStream(
        Stream mediaStream,
        in MediaPlayerOpenOptions options,
        IVideoOutput? videoNegotiationLead,
        bool disposeNegotiationLead,
        [NotNullWhen(true)] out MediaPlayer? player,
        out string? errorMessage) =>
        TryOpenStream(
            mediaStream,
            null,
            options,
            videoNegotiationLead,
            disposeNegotiationLead,
            out player,
            out errorMessage);

    /// <summary>
    /// Opens a player graph from already-decoded live sources, bypassing
    /// <see cref="MediaContainerDecoder"/>. Use for capture devices, NDI
    /// receivers, and other inputs that already expose <see cref="IAudioSource"/>
    /// / <see cref="IVideoSource"/>.
    /// </summary>
    public static bool TryOpenLive(
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
    /// <see cref="AudioPlayer"/> when it is wired.
    /// </summary>
    public static bool TryOpenLive(
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

        FFmpegRuntime.EnsureInitialized();

        AudioPlayer? audioPlayer = null;
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
                audioPlayer = new AudioPlayer(audioSource.Format.SampleRate, options.AudioChunkSamples);
                audioSourceId = disposeSourcesOnDispose
                    ? audioPlayer.AddOwnedSource(audioSource)
                    : audioPlayer.Router.AddSource(audioSource);
                playClock = audioPlayer.Clock;
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
            var liveQueueCap = options.LiveVideoDecodeQueueCapacity > 0
                ? options.LiveVideoDecodeQueueCapacity
                : 4;
            videoPlayer = new VideoPlayer(
                effectiveVideoSource,
                vin.Output,
                playClock,
                queueCapacity: liveQueueCap,
                presentationMode: options.LiveVideoPresentation);

            player = new MediaPlayer(
                router,
                videoPlayer,
                playClock,
                audioPlayer,
                audioPlayer is null ? freerun : null,
                vin.Id,
                vin.Output,
                audioSourceId,
                ownedDisposables);

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
            TryDispose(() => audioPlayer?.Dispose(), "MediaPlayer.TryOpenLive: AudioPlayer");
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
    public static bool TryOpen(
        MediaContainerDecoder decoder,
        in MediaPlayerOpenOptions options,
        IVideoOutput? videoNegotiationLead,
        bool disposeNegotiationLead,
        MediaPlayerDecoderOwnership decoderOwnership,
        [NotNullWhen(true)] out MediaPlayer? player,
        out string? errorMessage,
        IVideoSource? videoSourceOverride = null)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        player = null;
        errorMessage = null;
        if (!options.ValidateWin32Nv12Flags(out errorMessage))
            return false;
        FFmpegRuntime.EnsureInitialized();
        try
        {
            return TryOpenCore(
                decoder,
                options,
                videoNegotiationLead,
                disposeNegotiationLead,
                decoderOwnership,
                videoSourceOverride,
                out player,
                out errorMessage);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            player = null;
            return false;
        }
    }

    private static bool TryOpenFromDecoderFactory(
        Func<MediaContainerDecoder> decoderFactory,
        in MediaPlayerOpenOptions options,
        IVideoOutput? videoNegotiationLead,
        bool disposeNegotiationLead,
        MediaPlayerDecoderOwnership decoderOwnership,
        [NotNullWhen(true)] out MediaPlayer? player,
        out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(decoderFactory);
        player = null;
        errorMessage = null;

        if (!options.ValidateWin32Nv12Flags(out errorMessage))
            return false;

        FFmpegRuntime.EnsureInitialized();

        MediaContainerDecoder? media = null;
        try
        {
            media = decoderFactory();
            media.SeekPresentation(TimeSpan.Zero);
            return TryOpenCore(
                media,
                options,
                videoNegotiationLead,
                disposeNegotiationLead,
                decoderOwnership,
                videoSourceOverride: null,
                out player,
                out errorMessage);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            media?.Dispose();
            player = null;
            return false;
        }
    }

    private static bool TryOpenCore(
        MediaContainerDecoder media,
        in MediaPlayerOpenOptions options,
        IVideoOutput? videoNegotiationLead,
        bool disposeNegotiationLead,
        MediaPlayerDecoderOwnership decoderOwnership,
        IVideoSource? videoSourceOverride,
        [NotNullWhen(true)] out MediaPlayer? player,
        out string? errorMessage)
    {
        player = null;
        errorMessage = null;

        MediaClock? freerun = null;
        AudioPlayer? audioPlayer = null;
        string? audioSourceId = null;
        IMediaClock playClock;
        VideoRouter? router = null;
        VideoPlayer? videoPlayer = null;
        MediaContainerPlaybackBundle? bundle = null;

        void FailDispose()
        {
            if (bundle is not null)
            {
                bundle.Dispose();
                bundle = null;
            }
            else
            {
                videoPlayer?.Dispose();
                videoPlayer = null;
                router?.Dispose();
                router = null;
            }

            if (decoderOwnership == MediaPlayerDecoderOwnership.BundleDisposesDecoder)
                media.Dispose();

            freerun?.Dispose();
            freerun = null;
        }

        try
        {
            if (options.IncludeAudioRouter && media.HasAudio)
            {
                audioPlayer = new AudioPlayer(media.Audio.Format.SampleRate, options.AudioChunkSamples);
                audioSourceId = audioPlayer.AddOwnedSource(media.Audio);
                playClock = audioPlayer.Clock;
            }
            else
            {
                // No AudioPlayer either because the caller asked for video-only routing or because the
                // container has no audio stream. Drive the visible clock from a freerun MediaClock that
                // <see cref="AvPlaybackCoordinator.Play"/> starts manually when there's no audio master.
                freerun = new MediaClock();
                playClock = freerun;
            }

            router = new VideoRouter(null);
            string primaryOutputId;
            if (videoNegotiationLead is null)
            {
                // DiscardingVideoOutput drops the frame immediately — no Submit work to offload to a pump,
                // so register synchronously and avoid spinning up a drainer thread for nothing.
                var discard = new DiscardingVideoOutput();
                primaryOutputId = router.AddOutput(discard, "_discard",
                    disposeOutputOnRouterDispose: true, synchronous: true);
            }
            else
            {
                // End-user video outputs (Avalonia / SDL3 GL surfaces, encoders, …) get the Phase 2
                // pump-by-default treatment so a stutter on Submit can't back-pressure the clock thread.
                primaryOutputId = router.AddOutput(
                    videoNegotiationLead,
                    "_primary",
                    disposeOutputOnRouterDispose: disposeNegotiationLead);
            }

            var vin = router.AddInput(primaryOutputId);
            var videoForPlayer = videoSourceOverride ?? media.Video;
            videoPlayer = new VideoPlayer(videoForPlayer, vin.Output, playClock);

            var ownDecoder = decoderOwnership == MediaPlayerDecoderOwnership.BundleDisposesDecoder;
            var bundleOwned = ComputeOwnedParts(
                ownDecoder: ownDecoder,
                hasFreerun: freerun is not null,
                hasAudio: audioPlayer is not null,
                hasVideo: true);

            bundle = new MediaContainerPlaybackBundle(
                media,
                videoPlayer,
                playClock,
                audioPlayer,
                router,
                freerun,
                bundleOwned);

            player = new MediaPlayer(bundle, vin.Id, vin.Output, audioSourceId, audioPlayer is null ? freerun : null);
            Trace.LogInformation("TryOpenCore: opened (hasAudio={HasAudio} hasVideo={HasVideo} audioRate={AudioRate}Hz videoFmt={VideoFmt} clockType={Clock} negotiationLead={Lead})",
                media.HasAudio, media.HasVideo,
                media.HasAudio ? media.Audio.Format.SampleRate : 0,
                videoPlayer is not null ? videoPlayer.Format.ToString() : "(none)",
                playClock.GetType().Name,
                videoNegotiationLead?.GetType().Name ?? "(discard)");
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            Trace.LogError(ex, "TryOpenCore failed");
            FailDispose();
            return false;
        }
    }

    private static MediaContainerPlaybackBundleOwnedParts ComputeOwnedParts(
        bool ownDecoder, bool hasFreerun, bool hasAudio, bool hasVideo)
    {
        var o = MediaContainerPlaybackBundleOwnedParts.VideoRouter;
        if (hasVideo)
            o |= MediaContainerPlaybackBundleOwnedParts.VideoPlayer;
        if (ownDecoder)
            o |= MediaContainerPlaybackBundleOwnedParts.Decoder;
        if (hasFreerun)
            o |= MediaContainerPlaybackBundleOwnedParts.FreerunMediaClock;
        if (hasAudio)
            o |= MediaContainerPlaybackBundleOwnedParts.AudioPlayer;
        return o;
    }

    private sealed class EmptyLiveVideoSource : IVideoSource
    {
        private VideoFormat _format = new(16, 16, PixelFormat.Bgra32, new Rational(30, 1));

        public VideoFormat Format => _format;

        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];

        public bool IsExhausted => true;

        public void SelectOutputFormat(PixelFormat format) => _format = _format with { PixelFormat = format };

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            frame = null!;
            return false;
        }
    }
}
