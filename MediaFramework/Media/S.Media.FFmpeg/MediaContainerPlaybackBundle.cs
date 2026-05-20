using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg.Video;

namespace S.Media.FFmpeg;

/// <summary>
/// Flags for <see cref="MediaContainerPlaybackBundle"/> — which child objects the host should
/// <see cref="IDisposable.Dispose"/> in <see cref="MediaContainerPlaybackBundle.Dispose"/>.
/// </summary>
[Flags]
public enum MediaContainerPlaybackBundleOwnedParts
{
    None = 0,

    /// <summary>
    /// Dispose <see cref="MediaContainerPlaybackBundle.Decoder"/> after players and optional <see cref="VideoRouter"/>.
    /// </summary>
    Decoder = 1 << 0,

    /// <summary>Dispose <see cref="MediaContainerPlaybackBundle.Video"/>.</summary>
    VideoPlayer = 1 << 1,

    /// <summary>Dispose <see cref="MediaContainerPlaybackBundle.Audio"/> when non-null.</summary>
    AudioPlayer = 1 << 2,

    /// <summary>Dispose <see cref="MediaContainerPlaybackBundle.VideoRouter"/> when non-null.</summary>
    VideoRouter = 1 << 3,

    /// <summary>
    /// Dispose a freerun <see cref="MediaClock"/> supplied as <c>freerunClockToDispose</c> (must not be
    /// <see cref="AudioPlayer.Clock"/> when <see cref="AudioPlayer"/> is owned here — dispose audio first).
    /// </summary>
    FreerunMediaClock = 1 << 4,
}

/// <summary>
/// One process-local owner for a shared <see cref="MediaContainerDecoder"/>, dependent <see cref="VideoPlayer"/> /
/// optional <see cref="AudioPlayer"/>, optional <see cref="VideoRouter"/>, with a fixed <see cref="Dispose"/>
/// order so mux-backed decode stops before the container closes.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MediaContainerSession"/> on its own only groups references — callers keep separate <c>using</c>
/// scopes. This type is for hosts that want a single <c>using</c> (or one explicit <see cref="Dispose"/>) while still
/// injecting GL, PortAudio, NDI, or test sinks into <see cref="VideoPlayer"/> and optional PortAudio wiring via
/// <c>S.Media.PortAudio.PortAudioPlaybackHost.TryCreatePortAudioMain</c>.
/// </para>
/// <para>
/// Pre-baked ownership profiles: <see cref="SmokeToolDefaultOwnership"/> matches the <c>Tools/VideoPlaybackSmoke</c>
/// wiring; <see cref="DefaultBundledHostOwnership"/> matches <see cref="S.Media.Playback.MediaPlayer"/>. For a
/// host-platform-free single-file playback path use
/// <see cref="S.Media.Playback.MediaPlayer.TryOpen(string,S.Media.Playback.MediaPlayerOpenOptions,S.Media.Core.Video.IVideoSink?,bool,out S.Media.Playback.MediaPlayer?,out string?)"/>.
/// Worked example (GL + NDI + PortAudio fan-out, ownership flags, `finally`-order) in
/// <c>Doc/MediaFramework-Architecture.md</c>.
/// </para>
/// <para>
/// Default teardown order: <see cref="VideoPlayer"/> → optional <see cref="AudioPlayer"/> → optional
/// <see cref="VideoRouter"/> → optional owned freerun <see cref="MediaClock"/> → <see cref="MediaContainerDecoder"/>.
/// Each owned step is best-effort: failures are swallowed in release builds so later disposals still run; in
/// <c>DEBUG</c>, failures are logged via <see cref="MediaDiagnostics"/>.
/// </para>
/// </remarks>
public sealed class MediaContainerPlaybackBundle : IDisposable
{
    private readonly MediaContainerPlaybackBundleOwnedParts _owned;
    private readonly MediaClock? _freerunClockToDispose;
    private bool _disposed;

    /// <summary>
    /// Creates the bundle and a <see cref="MediaContainerSession"/> via <see cref="MediaContainerSession.Create"/>.
    /// </summary>
    /// <param name="freerunClockToDispose">
    /// When <see cref="MediaContainerPlaybackBundleOwnedParts.FreerunMediaClock"/> is set, this instance is disposed
    /// after <see cref="VideoPlayer"/> (typically the same object as <paramref name="clock"/> when it is a <see cref="MediaClock"/>).
    /// </param>
    public MediaContainerPlaybackBundle(
        MediaContainerDecoder decoder,
        VideoPlayer video,
        IMediaClock clock,
        AudioPlayer? audio,
        VideoRouter? videoRouter,
        MediaClock? freerunClockToDispose,
        MediaContainerPlaybackBundleOwnedParts ownedParts)
    {
        Decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        Video = video ?? throw new ArgumentNullException(nameof(video));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Audio = audio;
        VideoRouter = videoRouter;
        _owned = ownedParts;
        _freerunClockToDispose = freerunClockToDispose;

        if (_owned.HasFlag(MediaContainerPlaybackBundleOwnedParts.VideoRouter) && VideoRouter is null)
            throw new ArgumentException("VideoRouter ownership requested but videoRouter is null.", nameof(videoRouter));

        if (_owned.HasFlag(MediaContainerPlaybackBundleOwnedParts.FreerunMediaClock) && _freerunClockToDispose is null)
            throw new ArgumentException("FreerunMediaClock ownership requested but freerunClockToDispose is null.", nameof(freerunClockToDispose));

        if (_owned.HasFlag(MediaContainerPlaybackBundleOwnedParts.AudioPlayer) && Audio is null)
            throw new ArgumentException("AudioPlayer ownership requested but audio is null.", nameof(audio));

        Session = MediaContainerSession.Create(Decoder, Video, Clock, Audio);
    }

    public MediaContainerDecoder Decoder { get; }

    public VideoPlayer Video { get; }

    public AudioPlayer? Audio { get; }

    public VideoRouter? VideoRouter { get; }

    public IMediaClock Clock { get; }

    public MediaContainerSession Session { get; }

    /// <summary>
    /// Ownership flags for <see cref="VideoPlaybackSmoke"/> and similar hosts: shared mux decoder + video player,
    /// optional <see cref="VideoRouter"/>, optional freerun <see cref="MediaClock"/> when audio is not wired into the bundle,
    /// optional <see cref="AudioPlayer"/> when PortAudio wiring is bundled here (dispose before <c>S.Media.PortAudio.PortAudioPlaybackHost</c> closes hardware).
    /// </summary>
    public static MediaContainerPlaybackBundleOwnedParts SmokeToolDefaultOwnership(
        bool hasVideoRouter,
        bool hasFreerunMediaClock,
        bool hasAudioPlayer = false)
    {
        var o = MediaContainerPlaybackBundleOwnedParts.Decoder | MediaContainerPlaybackBundleOwnedParts.VideoPlayer;
        if (hasVideoRouter)
            o |= MediaContainerPlaybackBundleOwnedParts.VideoRouter;
        if (hasFreerunMediaClock)
            o |= MediaContainerPlaybackBundleOwnedParts.FreerunMediaClock;
        if (hasAudioPlayer)
            o |= MediaContainerPlaybackBundleOwnedParts.AudioPlayer;
        return o;
    }

    /// <summary>
    /// Same bit-vector as <see cref="SmokeToolDefaultOwnership"/> — preferred name for bundled <c>VideoPlaybackSmoke.VideoPlaybackSmokeSession</c> hosts and other non-CLI callers.
    /// </summary>
    public static MediaContainerPlaybackBundleOwnedParts DefaultBundledHostOwnership(
        bool hasVideoRouter,
        bool hasFreerunMediaClock,
        bool hasAudioPlayer = false) =>
        SmokeToolDefaultOwnership(hasVideoRouter, hasFreerunMediaClock, hasAudioPlayer);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_owned.HasFlag(MediaContainerPlaybackBundleOwnedParts.VideoPlayer))
            TryDisposeOwned(() => Video.Dispose(), "MediaContainerPlaybackBundle.Dispose: VideoPlayer");

        if (_owned.HasFlag(MediaContainerPlaybackBundleOwnedParts.AudioPlayer) && Audio is not null)
            TryDisposeOwned(() => Audio.Dispose(), "MediaContainerPlaybackBundle.Dispose: AudioPlayer");

        if (_owned.HasFlag(MediaContainerPlaybackBundleOwnedParts.VideoRouter) && VideoRouter is not null)
            TryDisposeOwned(() => VideoRouter.Dispose(), "MediaContainerPlaybackBundle.Dispose: VideoRouter");

        if (_owned.HasFlag(MediaContainerPlaybackBundleOwnedParts.FreerunMediaClock) && _freerunClockToDispose is not null)
            TryDisposeOwned(() => _freerunClockToDispose.Dispose(), "MediaContainerPlaybackBundle.Dispose: FreerunMediaClock");

        if (_owned.HasFlag(MediaContainerPlaybackBundleOwnedParts.Decoder))
            TryDisposeOwned(() => Decoder.Dispose(), "MediaContainerPlaybackBundle.Dispose: Decoder");
    }

    private static void TryDisposeOwned(Action dispose, string debugLabel)
    {
        try
        {
            dispose();
        }
#if DEBUG
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, debugLabel);
        }
#else
        catch
        {
            // best effort — continue bundle teardown
        }
#endif
    }
}
