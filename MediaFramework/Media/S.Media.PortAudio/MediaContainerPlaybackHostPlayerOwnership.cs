namespace S.Media.PortAudio;

/// <summary>
/// Whether <see cref="MediaContainerPlaybackHost"/> disposes the <see cref="S.Media.Core.Audio.AudioPlayer"/> it created,
/// or only tears down the PortAudio device while the caller disposes the player elsewhere (e.g. <see cref="S.Media.FFmpeg.MediaContainerMegaPlaybackHost"/>).
/// </summary>
public enum MediaContainerPlaybackHostPlayerOwnership
{
    /// <summary>
    /// Default: <see cref="MediaContainerPlaybackHost.Dispose"/> disposes <see cref="MediaContainerPlaybackHost.Player"/>, then
    /// <see cref="MediaContainerPlaybackHost.MainOutput"/>.
    /// </summary>
    HostDisposesPlayer,

    /// <summary>
    /// <see cref="MediaContainerPlaybackHost.Dispose"/> disposes only <see cref="MediaContainerPlaybackHost.MainOutput"/>.
    /// The caller must dispose <see cref="MediaContainerPlaybackHost.Player"/> first (typically via a mega-host that owns the player),
    /// then dispose this host so the hardware stream closes after the router stops submitting to it.
    /// </summary>
    CallerDisposesPlayer,
}
