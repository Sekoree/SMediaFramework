using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.Playback;
using S.Media.PortAudio;
using S.Media.SDL3;
using S.Media.SkiaSharp;

namespace S.Media.Quick;

/// <summary>
/// One-call "open and play" facade for the soundboard / cue-player product surface. Given a path,
/// figures out whether to drive a full media pipeline (FFmpeg + PortAudio + SDL3 GL window) or a
/// still-image pipeline (SkiaSharp → GL window, hold-last-frame), and hands back a
/// <see cref="QuickPlayback"/> handle with <c>Play</c> / <c>Pause</c> / <c>Stop</c> / <c>Dispose</c>.
/// </summary>
/// <remarks>
/// <para>
/// Sensible defaults: audio auto-resamples to match the decoder rate (Phase 2 ergonomics), video
/// sinks are pump-wrapped automatically (Phase 2 default), and image cues hold the final frame on
/// screen indefinitely (Phase 3 ergonomics). Hides the router / clock / ownership ceremony that
/// <see cref="MediaPlayer.TryOpen"/> exposes for power users.
/// </para>
/// <para>
/// Display: an SDL3 GL window owned by the facade. Avalonia / WPF / WinForms hosts should use
/// <see cref="MediaPlayer.TryOpen"/> directly with the relevant embedded GL control.
/// </para>
/// </remarks>
public static class QuickPlayer
{
    /// <summary>
    /// File extensions <see cref="QuickPlayer"/> treats as still images. Match is case-insensitive.
    /// Anything else routes through FFmpeg.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ImageExtensions = new[]
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif",
    };

    /// <summary>Open a file and prepare playback. Call <see cref="QuickPlayback.Play"/> on the result.</summary>
    /// <param name="path">Path to a media file (audio/video) or an image (PNG/JPEG/WebP/BMP/GIF).</param>
    /// <param name="windowTitle">Override for the SDL3 window title. Defaults to the file name.</param>
    /// <param name="initialWidth">Window width before any frame negotiation resizes it.</param>
    /// <param name="initialHeight">Window height before any frame negotiation resizes it.</param>
    public static QuickPlayback Open(string path, string? windowTitle = null,
        int initialWidth = 1280, int initialHeight = 720)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("media file not found", path);

        var title = string.IsNullOrWhiteSpace(windowTitle) ? Path.GetFileName(path) : windowTitle!;
        var ext = Path.GetExtension(path);
        var isImage = ext is not null && ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);

        return isImage
            ? OpenImage(path, title, initialWidth, initialHeight)
            : OpenMedia(path, title, initialWidth, initialHeight);
    }

    private static QuickPlayback OpenImage(string path, string title, int initialWidth, int initialHeight)
    {
        var image = ImageFileSource.OpenFromFile(path);
        SDL3GLVideoSink? sink = null;
        VideoPlayer? video = null;
        MediaClock? clock = null;
        try
        {
            sink = new SDL3GLVideoSink(title,
                initialWidth: Math.Min(image.Format.Width, initialWidth),
                initialHeight: Math.Min(image.Format.Height, initialHeight));
            clock = new MediaClock();
            video = new VideoPlayer(image, sink, clock) { HoldLastFrameAtEnd = true };
            return new QuickPlayback(QuickPlaybackKind.Image, mediaPlayer: null, audioHost: null,
                sink, video, clock, ownedDisposable: image);
        }
        catch
        {
            video?.Dispose();
            clock?.Dispose();
            sink?.Dispose();
            image.Dispose();
            throw;
        }
    }

    private static QuickPlayback OpenMedia(string path, string title, int initialWidth, int initialHeight)
    {
        FFmpegRuntime.EnsureInitialized();
        SDL3GLVideoSink? sink = null;
        MediaPlayer? player = null;
        PortAudioPlaybackHost? audioHost = null;
        try
        {
            sink = new SDL3GLVideoSink(title, initialWidth, initialHeight);

            var openOptions = new MediaPlayerOpenOptions(
                TryHardwareAcceleration: true,
                IncludeAudioRouter: true);

            if (!MediaPlayer.TryOpen(
                    path,
                    openOptions,
                    sink,
                    disposeNegotiationLead: true,
                    out player,
                    out var errorMessage))
            {
                sink = null;
                throw new InvalidOperationException(
                    $"QuickPlayer.OpenMedia: MediaPlayer.TryOpen failed for '{path}' — {errorMessage}");
            }

            if (player!.Audio is not null && player.Decoder.HasAudio && player.AudioSourceId is not null)
            {
                audioHost = PortAudioPlaybackHost.TryWirePortAudioMainForPlayer(
                    player.Decoder,
                    player.Audio,
                    player.AudioSourceId,
                    openOptions.AudioChunkSamples,
                    deviceLatencyMs: null,
                    onWireFailedMessage: msg => MediaDiagnostics.LogWarning(
                        "QuickPlayer: PortAudio wire-up failed — {0}", msg),
                    PortAudioPlaybackHostPlayerOwnership.CallerDisposesPlayer);
            }

            return new QuickPlayback(QuickPlaybackKind.Media, player, audioHost, sink,
                video: null, clock: null, ownedDisposable: null);
        }
        catch
        {
            audioHost?.Dispose();
            player?.Dispose();
            sink?.Dispose();
            throw;
        }
    }
}

/// <summary>Kind of source <see cref="QuickPlayer.Open"/> resolved to.</summary>
public enum QuickPlaybackKind
{
    Media,
    Image,
}

/// <summary>
/// Handle returned by <see cref="QuickPlayer.Open"/>. Disposing tears down every owned resource
/// (sink, clock, decoder, audio host, image source) in the right order.
/// </summary>
public sealed class QuickPlayback : IDisposable
{
    private readonly MediaPlayer? _mediaPlayer;
    private readonly PortAudioPlaybackHost? _audioHost;
    private readonly SDL3GLVideoSink _sink;
    private readonly VideoPlayer? _imageVideoPlayer;
    private readonly MediaClock? _imageClock;
    private readonly IDisposable? _ownedDisposable;
    private bool _disposed;

    internal QuickPlayback(QuickPlaybackKind kind, MediaPlayer? mediaPlayer,
        PortAudioPlaybackHost? audioHost, SDL3GLVideoSink sink, VideoPlayer? video, MediaClock? clock,
        IDisposable? ownedDisposable)
    {
        Kind = kind;
        _mediaPlayer = mediaPlayer;
        _audioHost = audioHost;
        _sink = sink;
        _imageVideoPlayer = video;
        _imageClock = clock;
        _ownedDisposable = ownedDisposable;
    }

    public QuickPlaybackKind Kind { get; }
    public SDL3GLVideoSink WindowSink => _sink;
    public MediaPlayer? MediaPlayer => _mediaPlayer;
    public VideoPlayer? ImageVideo => _imageVideoPlayer;

    /// <summary>Start playback. For media, drives the clock + audio + video. For images, holds the frame on screen via <see cref="VideoPlayer.HoldLastFrameAtEnd"/>.</summary>
    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Audio?.Play();
            _mediaPlayer.Video.Play();
        }
        if (_imageClock is not null)
            _imageClock.Start();
        _imageVideoPlayer?.Play();
    }

    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mediaPlayer?.Audio?.Pause();
        _mediaPlayer?.Video.Pause();
        _imageVideoPlayer?.Pause();
        _imageClock?.Pause();
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mediaPlayer?.Audio?.Stop();
        _mediaPlayer?.Video.Stop();
        _imageVideoPlayer?.Stop();
        _imageClock?.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _imageVideoPlayer?.Dispose(); } catch { /* best effort */ }
        try { _audioHost?.Dispose(); } catch { /* best effort */ }
        try { _mediaPlayer?.Dispose(); } catch { /* best effort */ }
        try { _imageClock?.Dispose(); } catch { /* best effort */ }
        try { _sink.Dispose(); } catch { /* best effort */ }
        try { _ownedDisposable?.Dispose(); } catch { /* best effort */ }
    }
}
