namespace S.Media.PortAudio;

/// <summary>
/// Whether <see cref="PortAudioPlaybackHost"/> disposes the <see cref="S.Media.Core.Audio.AudioPlayer"/> it created,
/// or only tears down the PortAudio device while the caller disposes the player elsewhere (e.g. when the player is
/// also owned by an <see cref="S.Media.FFmpeg.MediaContainerPlaybackBundle"/>).
/// </summary>
public enum PortAudioPlaybackHostPlayerOwnership
{
    /// <summary>
    /// Default: <see cref="PortAudioPlaybackHost.Dispose"/> disposes <see cref="PortAudioPlaybackHost.Player"/>, then
    /// <see cref="PortAudioPlaybackHost.MainOutput"/>.
    /// </summary>
    HostDisposesPlayer,

    /// <summary>
    /// <see cref="PortAudioPlaybackHost.Dispose"/> disposes only <see cref="PortAudioPlaybackHost.MainOutput"/>.
    /// The caller must dispose <see cref="PortAudioPlaybackHost.Player"/> first (typically via the bundle that owns
    /// the player), then dispose this host so the hardware stream closes after the router stops submitting to it.
    /// </summary>
    CallerDisposesPlayer,
}
