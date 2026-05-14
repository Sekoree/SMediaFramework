using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg.Video;

namespace S.Media.FFmpeg;

/// <summary>
/// Flags for <see cref="MediaContainerMegaPlaybackHost"/> — which child objects the host should
/// <see cref="IDisposable.Dispose"/> in <see cref="MediaContainerMegaPlaybackHost.Dispose"/>.
/// </summary>
[Flags]
public enum MediaContainerMegaPlaybackOwnedParts
{
    None = 0,

    /// <summary>
    /// Dispose <see cref="MediaContainerMegaPlaybackHost.Decoder"/> after players and optional <see cref="VideoRouter"/>.
    /// </summary>
    Decoder = 1 << 0,

    /// <summary>Dispose <see cref="MediaContainerMegaPlaybackHost.Video"/>.</summary>
    VideoPlayer = 1 << 1,

    /// <summary>Dispose <see cref="MediaContainerMegaPlaybackHost.Audio"/> when non-null.</summary>
    AudioPlayer = 1 << 2,

    /// <summary>Dispose <see cref="MediaContainerMegaPlaybackHost.VideoRouter"/> when non-null.</summary>
    VideoRouter = 1 << 3,

    /// <summary>
    /// Dispose a freerun <see cref="MediaClock"/> supplied as <c>freerunClockToDispose</c> (must not be
    /// <see cref="AudioPlayer.Clock"/> when <see cref="AudioPlayer"/> is owned here — dispose audio first).
    /// </summary>
    FreerunMediaClock = 1 << 4,
}

/// <summary>
/// Tier F row 24 stepping stone: one process-local owner for a shared <see cref="MediaContainerDecoder"/>,
/// dependent <see cref="VideoPlayer"/> / optional <see cref="AudioPlayer"/>, optional <see cref="VideoRouter"/>,
/// and a fixed <see cref="Dispose"/> order so mux-backed decode stops before the container closes.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MediaContainerPlaybackGraph"/> and <see cref="AvRouter"/> only group references — callers keep
/// separate <c>using</c> scopes. This type is for hosts that want a single <c>using</c> (or one explicit
/// <see cref="Dispose"/>) while still injecting GL, PortAudio, NDI, or test sinks into <see cref="VideoPlayer"/>
/// and optional PortAudio wiring via <c>S.Media.PortAudio.MediaContainerPlaybackHost.TryCreatePortAudioMain</c>.
/// </para>
/// <para>
/// <strong>Example:</strong> <c>Tools/VideoPlaybackSmoke</c> wires GL / NDI / PortAudio sinks, then wraps decoder +
/// <see cref="VideoPlayer"/> + optional <see cref="VideoRouter"/> + freerun <see cref="MediaClock"/> in this host so
/// <c>finally</c> can dispose this mega host (mux + optional <see cref="AudioPlayer"/>) before <see cref="S.Media.PortAudio.MediaContainerPlaybackHost"/>
/// closes the PortAudio device when using <see cref="S.Media.PortAudio.MediaContainerPlaybackHostPlayerOwnership.CallerDisposesPlayer"/>.
/// Use <see cref="SmokeToolDefaultOwnership"/> or <see cref="DefaultBundledHostOwnership"/> for the ownership flags that match <c>VideoPlaybackSmoke</c> / <see cref="S.Media.Playback.MediaPlayer"/> wiring.
/// For the smoke tool’s SDL + NDI + PortAudio bundle, see <c>VideoPlaybackSmoke.VideoPlaybackSmokeSession</c> (<c>Doc/Todo.md</c> PO-05).
/// For a single media path with router + optional sinks only, <see cref="S.Media.Playback.MediaPlayer.TryOpen(string,S.Media.Playback.MediaPlayerOpenOptions,S.Media.Core.Video.IVideoSink?,bool,out S.Media.Playback.MediaPlayer?,out string?)"/> is the library entry (no SDL/NDI/PortAudio on the playback assembly itself).
/// </para>
/// <para>
/// Default teardown order: <see cref="VideoPlayer"/> → optional <see cref="AudioPlayer"/> → optional
/// <see cref="VideoRouter"/> → optional owned freerun <see cref="MediaClock"/> → <see cref="MediaContainerDecoder"/>.
/// Each owned step is best-effort: failures are swallowed in release builds so later disposals still run; in
/// <c>DEBUG</c>, failures are logged via <see cref="MediaDiagnostics"/>.
/// </para>
/// </remarks>
public sealed class MediaContainerMegaPlaybackHost : IDisposable
{
    private readonly MediaContainerMegaPlaybackOwnedParts _owned;
    private readonly MediaClock? _freerunClockToDispose;
    private bool _disposed;

    /// <summary>
    /// Creates the host and an <see cref="AvRouter"/> with <see cref="MediaContainerAvRouter.Create"/> wiring.
    /// </summary>
    /// <param name="freerunClockToDispose">
    /// When <see cref="MediaContainerMegaPlaybackOwnedParts.FreerunMediaClock"/> is set, this instance is disposed
    /// after <see cref="VideoPlayer"/> (typically the same object as <paramref name="clock"/> when it is a <see cref="MediaClock"/>).
    /// </param>
    public MediaContainerMegaPlaybackHost(
        MediaContainerDecoder decoder,
        VideoPlayer video,
        IMediaClock clock,
        AudioPlayer? audio,
        VideoRouter? videoRouter,
        MediaClock? freerunClockToDispose,
        MediaContainerMegaPlaybackOwnedParts ownedParts)
    {
        Decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        Video = video ?? throw new ArgumentNullException(nameof(video));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Audio = audio;
        VideoRouter = videoRouter;
        _owned = ownedParts;
        _freerunClockToDispose = freerunClockToDispose;

        if (_owned.HasFlag(MediaContainerMegaPlaybackOwnedParts.VideoRouter) && VideoRouter is null)
            throw new ArgumentException("VideoRouter ownership requested but videoRouter is null.", nameof(videoRouter));

        if (_owned.HasFlag(MediaContainerMegaPlaybackOwnedParts.FreerunMediaClock) && _freerunClockToDispose is null)
            throw new ArgumentException("FreerunMediaClock ownership requested but freerunClockToDispose is null.", nameof(freerunClockToDispose));

        if (_owned.HasFlag(MediaContainerMegaPlaybackOwnedParts.AudioPlayer) && Audio is null)
            throw new ArgumentException("AudioPlayer ownership requested but audio is null.", nameof(audio));

        Router = MediaContainerAvRouter.Create(Decoder, Video, Clock, Audio);
    }

    public MediaContainerDecoder Decoder { get; }

    public VideoPlayer Video { get; }

    public AudioPlayer? Audio { get; }

    public VideoRouter? VideoRouter { get; }

    public IMediaClock Clock { get; }

    public AvRouter Router { get; }

    /// <summary>
    /// Ownership flags for <see cref="VideoPlaybackSmoke"/> and similar hosts: shared mux decoder + video player,
    /// optional <see cref="VideoRouter"/>, optional freerun <see cref="MediaClock"/> when audio is not wired into the mega host,
    /// optional <see cref="AudioPlayer"/> when PortAudio wiring is bundled here (dispose before <see cref="S.Media.PortAudio.MediaContainerPlaybackHost"/> closes hardware).
    /// </summary>
    public static MediaContainerMegaPlaybackOwnedParts SmokeToolDefaultOwnership(
        bool hasVideoRouter,
        bool hasFreerunMediaClock,
        bool hasAudioPlayer = false)
    {
        var o = MediaContainerMegaPlaybackOwnedParts.Decoder | MediaContainerMegaPlaybackOwnedParts.VideoPlayer;
        if (hasVideoRouter)
            o |= MediaContainerMegaPlaybackOwnedParts.VideoRouter;
        if (hasFreerunMediaClock)
            o |= MediaContainerMegaPlaybackOwnedParts.FreerunMediaClock;
        if (hasAudioPlayer)
            o |= MediaContainerMegaPlaybackOwnedParts.AudioPlayer;
        return o;
    }

    /// <summary>
    /// Same bit-vector as <see cref="SmokeToolDefaultOwnership"/> — preferred name for bundled <c>VideoPlaybackSmoke.VideoPlaybackSmokeSession</c> hosts and other non-CLI callers.
    /// </summary>
    public static MediaContainerMegaPlaybackOwnedParts DefaultBundledHostOwnership(
        bool hasVideoRouter,
        bool hasFreerunMediaClock,
        bool hasAudioPlayer = false) =>
        SmokeToolDefaultOwnership(hasVideoRouter, hasFreerunMediaClock, hasAudioPlayer);

    /// <summary>
    /// Same grouping as <see cref="MediaContainerPlaybackGraph"/> — does not add ownership; use this for helpers
    /// that accept a graph type.
    /// </summary>
    public MediaContainerPlaybackGraph AsPlaybackGraph() => new(Decoder, Video, Clock, Audio);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_owned.HasFlag(MediaContainerMegaPlaybackOwnedParts.VideoPlayer))
            TryDisposeOwned(() => Video.Dispose(), "MediaContainerMegaPlaybackHost.Dispose: VideoPlayer");

        if (_owned.HasFlag(MediaContainerMegaPlaybackOwnedParts.AudioPlayer) && Audio is not null)
            TryDisposeOwned(() => Audio.Dispose(), "MediaContainerMegaPlaybackHost.Dispose: AudioPlayer");

        if (_owned.HasFlag(MediaContainerMegaPlaybackOwnedParts.VideoRouter) && VideoRouter is not null)
            TryDisposeOwned(() => VideoRouter.Dispose(), "MediaContainerMegaPlaybackHost.Dispose: VideoRouter");

        if (_owned.HasFlag(MediaContainerMegaPlaybackOwnedParts.FreerunMediaClock) && _freerunClockToDispose is not null)
            TryDisposeOwned(() => _freerunClockToDispose.Dispose(), "MediaContainerMegaPlaybackHost.Dispose: FreerunMediaClock");

        if (_owned.HasFlag(MediaContainerMegaPlaybackOwnedParts.Decoder))
            TryDisposeOwned(() => Decoder.Dispose(), "MediaContainerMegaPlaybackHost.Dispose: Decoder");
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
            // best effort — continue mega-host teardown
        }
#endif
    }
}
