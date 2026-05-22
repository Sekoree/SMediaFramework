using S.Media.Core.Audio;

namespace S.Media.PortAudio;

/// <summary>PortAudio helpers for <see cref="AudioRouter"/> hosts.</summary>
public static class AudioRouterPortAudioExtensions
{
    /// <summary>
    /// If <see cref="AudioRouter.PrimaryOutputId"/> resolves to a <see cref="PortAudioOutput"/>, fills its ring via
    /// <see cref="PortAudioOutput.PrefillFrom"/>.
    /// </summary>
    public static bool TryPrefillPrimaryPortAudio(
        this AudioRouter router,
        IAudioSource source,
        TimeSpan timeout,
        IAudioOutput? mirror = null,
        int? targetQueuedSamples = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(source);

        var id = router.PrimaryOutputId;
        if (string.IsNullOrEmpty(id))
            return false;
        if (!router.TryGetOutput(id, out var output) || output is not PortAudioOutput pa)
            return false;

        pa.PrefillFrom(source, timeout, router.ChunkSamples, mirror, targetQueuedSamples);
        return true;
    }
}
