using System.Diagnostics.CodeAnalysis;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;

namespace S.Media.Playback;

/// <summary>
/// Opens a shared-mux <see cref="MediaContainerDecoder"/> with a <see cref="VideoRouter"/> (always),
/// optional <see cref="AudioPlayer"/> wired to decoder audio (no PortAudio / SDL / NDI — add sinks from optional packages),
/// and <see cref="S.Media.FFmpeg.MediaContainerMegaPlaybackHost"/> for safe teardown.
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
    private readonly MediaContainerMegaPlaybackHost _bundle;
    private readonly string _videoRouterInputId;
    private readonly string? _audioSourceId;
    private readonly MediaClock? _freerun;
    private bool _disposed;

    private MediaPlayer(
        MediaContainerMegaPlaybackHost bundle,
        string videoRouterInputId,
        string? audioSourceId,
        MediaClock? freerun)
    {
        _bundle = bundle;
        _videoRouterInputId = videoRouterInputId;
        _audioSourceId = audioSourceId;
        _freerun = freerun;
    }

    public MediaContainerDecoder Decoder => _bundle.Decoder;

    public VideoRouter VideoRouter => _bundle.VideoRouter!;

    /// <summary>Input id returned by <see cref="VideoRouter.AddInput"/> — use with <see cref="VideoRouter.TryAddRoute"/>.</summary>
    public string VideoRouterInputId => _videoRouterInputId;

    public VideoPlayer Video => _bundle.Video;

    /// <summary>Present when <see cref="MediaPlayerOpenOptions.IncludeAudioRouter"/> was true at open time.</summary>
    public AudioPlayer? Audio => _bundle.Audio;

    /// <summary>Router source id for <see cref="MediaContainerDecoder.Audio"/> when <see cref="Audio"/> is non-null.</summary>
    public string? AudioSourceId => _audioSourceId;

    public IMediaClock PlayClock => _bundle.Clock;

    /// <summary>Non-null only when there was no <see cref="AudioPlayer"/> (video clocked from a freerun <see cref="MediaClock"/>).</summary>
    public MediaClock? FreerunClock => _freerun;

    public MediaContainerMegaPlaybackHost Bundle => _bundle;

    public AvRouter Av => _bundle.Router;

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
        MediaContainerMegaPlaybackHost? mega = null;

        void FailDispose()
        {
            if (mega is not null)
            {
                mega.Dispose();
                mega = null;
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
            if (options.IncludeAudioRouter)
            {
                audioPlayer = new AudioPlayer(media.Audio.Format.SampleRate, options.AudioChunkSamples);
                audioSourceId = audioPlayer.AddOwnedSource(media.Audio);
                playClock = audioPlayer.Clock;
            }
            else
            {
                freerun = new MediaClock();
                playClock = freerun;
            }

            router = new VideoRouter(null);
            string primaryOutputId;
            if (videoNegotiationLead is null)
            {
                var discard = new DiscardingVideoSink();
                primaryOutputId = router.AddOutput(discard, "_discard", disposeSinkOnRouterDispose: true);
            }
            else
            {
                primaryOutputId = router.AddOutput(
                    videoNegotiationLead,
                    "_primary",
                    disposeSinkOnRouterDispose: disposeNegotiationLead);
            }

            var vin = router.AddInput(primaryOutputId);
            videoPlayer = new VideoPlayer(media.Video, vin.Sink, playClock);

            var ownDecoder = decoderOwnership == MediaPlayerDecoderOwnership.BundleDisposesDecoder;
            var megaOwned = ComputeOwnedParts(ownDecoder: ownDecoder, hasFreerun: freerun is not null, hasAudio: audioPlayer is not null);

            mega = new MediaContainerMegaPlaybackHost(
                media,
                videoPlayer,
                playClock,
                audioPlayer,
                router,
                freerun,
                megaOwned);

            player = new MediaPlayer(mega, vin.Id, audioSourceId, audioPlayer is null ? freerun : null);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            FailDispose();
            return false;
        }
    }

    private static MediaContainerMegaPlaybackOwnedParts ComputeOwnedParts(bool ownDecoder, bool hasFreerun, bool hasAudio)
    {
        var o = MediaContainerMegaPlaybackOwnedParts.VideoPlayer | MediaContainerMegaPlaybackOwnedParts.VideoRouter;
        if (ownDecoder)
            o |= MediaContainerMegaPlaybackOwnedParts.Decoder;
        if (hasFreerun)
            o |= MediaContainerMegaPlaybackOwnedParts.FreerunMediaClock;
        if (hasAudio)
            o |= MediaContainerMegaPlaybackOwnedParts.AudioPlayer;
        return o;
    }
}
