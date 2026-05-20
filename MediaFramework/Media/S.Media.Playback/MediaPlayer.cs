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
/// optional <see cref="AudioPlayer"/> wired to decoder audio (no PortAudio / SDL / NDI — add sinks from optional packages),
/// and <see cref="S.Media.FFmpeg.MediaContainerPlaybackBundle"/> for safe teardown.
/// </summary>
/// <remarks>
/// <para>
/// When <paramref name="videoNegotiationLead"/> is <c>null</c>, a <see cref="DiscardingVideoSink"/> is registered as the router
/// primary so decode and <see cref="VideoPlayer"/> can run with <strong>zero</strong> user video sinks; attach real
/// <see cref="IVideoSink"/> outputs later via <see cref="VideoRouter.AddOutput"/> and <see cref="VideoRouter.TryAddRoute"/>.
/// </para>
/// <para>
/// Audio: when <see cref="MediaPlayerOpenOptions.IncludeAudioRouter"/> is true (default), an <see cref="AudioPlayer"/> owns
/// <see cref="MediaContainerDecoder.Audio"/> and drives <see cref="IMediaClock"/> for video. You can run with no audio sinks
/// (router consumes the mux audio stream every chunk). Add PortAudio, NDI, or other sinks from their respective assemblies.
/// </para>
/// </remarks>
public sealed class MediaPlayer : IDisposable
{
    private readonly MediaContainerPlaybackBundle _bundle;
    private readonly string _videoRouterInputId;
    private readonly IVideoSink _videoInputSink;
    private readonly string? _audioSourceId;
    private readonly MediaClock? _freerun;
    private bool _disposed;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Playback.MediaPlayer");

    private MediaPlayer(
        MediaContainerPlaybackBundle bundle,
        string videoRouterInputId,
        IVideoSink videoInputSink,
        string? audioSourceId,
        MediaClock? freerun)
    {
        _bundle = bundle;
        _videoRouterInputId = videoRouterInputId;
        _videoInputSink = videoInputSink;
        _audioSourceId = audioSourceId;
        _freerun = freerun;
    }

    public MediaContainerDecoder Decoder => _bundle.Decoder;

    public VideoRouter VideoRouter => _bundle.VideoRouter!;

    /// <summary>Input id returned by <see cref="VideoRouter.AddInput"/> — use with <see cref="VideoRouter.TryAddRoute"/>.</summary>
    public string VideoRouterInputId => _videoRouterInputId;

    /// <summary>
    /// The router input sink the decoder feeds into. Submit pre-Play priming or warmup frames here
    /// rather than directly to per-branch leaf sinks so the router's pixel-format converters run for
    /// every branch (Avalonia keeps the source format, NDI gets the post-conversion format, etc.).
    /// </summary>
    public IVideoSink VideoInputSink => _videoInputSink;

    public VideoPlayer Video => _bundle.Video;

    /// <summary>Present when <see cref="MediaPlayerOpenOptions.IncludeAudioRouter"/> was true at open time.</summary>
    public AudioPlayer? Audio => _bundle.Audio;

    /// <summary>Router source id for <see cref="MediaContainerDecoder.Audio"/> when <see cref="Audio"/> is non-null.</summary>
    public string? AudioSourceId => _audioSourceId;

    public IMediaClock PlayClock => _bundle.Clock;

    /// <summary>Non-null only when there was no <see cref="AudioPlayer"/> (video clocked from a freerun <see cref="MediaClock"/>).</summary>
    public MediaClock? FreerunClock => _freerun;

    public MediaContainerPlaybackBundle Bundle => _bundle;

    public MediaContainerSession Session => _bundle.Session;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _bundle.Dispose();
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

    /// <summary>Opens from a media file (decoder owned by the bundle).</summary>
    public static bool TryOpen(
        string mediaPath,
        in MediaPlayerOpenOptions options,
        IVideoSink? videoNegotiationLead,
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

    /// <summary>Opens from a media file with explicit decoder ownership.</summary>
    public static bool TryOpen(
        string mediaPath,
        in MediaPlayerOpenOptions options,
        IVideoSink? videoNegotiationLead,
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

    /// <summary>Uses an already-opened decoder (caller keeps ownership unless <paramref name="decoderOwnership"/> requests otherwise).</summary>
    public static bool TryOpen(
        MediaContainerDecoder decoder,
        in MediaPlayerOpenOptions options,
        IVideoSink? videoNegotiationLead,
        bool disposeNegotiationLead,
        MediaPlayerDecoderOwnership decoderOwnership,
        [NotNullWhen(true)] out MediaPlayer? player,
        out string? errorMessage)
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

    private static bool TryOpenCore(
        MediaContainerDecoder media,
        in MediaPlayerOpenOptions options,
        IVideoSink? videoNegotiationLead,
        bool disposeNegotiationLead,
        MediaPlayerDecoderOwnership decoderOwnership,
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
                // DiscardingVideoSink drops the frame immediately — no Submit work to offload to a pump,
                // so register synchronously and avoid spinning up a drainer thread for nothing.
                var discard = new DiscardingVideoSink();
                primaryOutputId = router.AddOutput(discard, "_discard",
                    disposeSinkOnRouterDispose: true, synchronous: true);
            }
            else
            {
                // End-user video sinks (Avalonia / SDL3 GL surfaces, encoders, …) get the Phase 2
                // pump-by-default treatment so a stutter on Submit can't back-pressure the clock thread.
                primaryOutputId = router.AddOutput(
                    videoNegotiationLead,
                    "_primary",
                    disposeSinkOnRouterDispose: disposeNegotiationLead);
            }

            var vin = router.AddInput(primaryOutputId);
            videoPlayer = new VideoPlayer(media.Video, vin.Sink, playClock);

            var ownDecoder = decoderOwnership == MediaPlayerDecoderOwnership.BundleDisposesDecoder;
            var bundleOwned = ComputeOwnedParts(ownDecoder: ownDecoder, hasFreerun: freerun is not null, hasAudio: audioPlayer is not null);

            bundle = new MediaContainerPlaybackBundle(
                media,
                videoPlayer,
                playClock,
                audioPlayer,
                router,
                freerun,
                bundleOwned);

            player = new MediaPlayer(bundle, vin.Id, vin.Sink, audioSourceId, audioPlayer is null ? freerun : null);
            Trace.LogInformation("TryOpenCore: opened (hasAudio={HasAudio} hasVideo={HasVideo} audioRate={AudioRate}Hz videoFmt={VideoFmt} clockType={Clock} negotiationLead={Lead})",
                media.HasAudio, media.HasVideo,
                media.HasAudio ? media.Audio.Format.SampleRate : 0,
                media.HasVideo ? videoPlayer.Format.ToString() : "(none)",
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

    private static MediaContainerPlaybackBundleOwnedParts ComputeOwnedParts(bool ownDecoder, bool hasFreerun, bool hasAudio)
    {
        var o = MediaContainerPlaybackBundleOwnedParts.VideoPlayer | MediaContainerPlaybackBundleOwnedParts.VideoRouter;
        if (ownDecoder)
            o |= MediaContainerPlaybackBundleOwnedParts.Decoder;
        if (hasFreerun)
            o |= MediaContainerPlaybackBundleOwnedParts.FreerunMediaClock;
        if (hasAudio)
            o |= MediaContainerPlaybackBundleOwnedParts.AudioPlayer;
        return o;
    }
}
