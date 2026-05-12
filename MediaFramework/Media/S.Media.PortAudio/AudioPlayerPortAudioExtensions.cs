using S.Media.Core.Audio;

namespace S.Media.PortAudio;

/// <summary>PortAudio helpers for <see cref="AudioPlayer"/> hosts.</summary>
public static class AudioPlayerPortAudioExtensions
{
    /// <summary>
    /// If the player's <see cref="AudioPlayer.PrimarySinkId"/> resolves to a
    /// <see cref="PortAudioOutput"/>, fills its ring via
    /// <see cref="PortAudioOutput.PrefillFrom"/> using <see cref="AudioRouter.ChunkSamples"/>.
    /// </summary>
    /// <returns><c>true</c> when prefill ran; <c>false</c> when there is no primary or it is not PortAudio.</returns>
    public static bool TryPrefillPrimaryPortAudio(
        this AudioPlayer player,
        IAudioSource source,
        TimeSpan timeout,
        IAudioSink? mirror = null,
        int? targetQueuedSamples = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(source);

        var id = player.PrimarySinkId;
        if (string.IsNullOrEmpty(id))
            return false;
        if (!player.Router.TryGetSink(id, out var sink) || sink is not PortAudioOutput pa)
            return false;

        pa.PrefillFrom(source, timeout, player.Router.ChunkSamples, mirror, targetQueuedSamples);
        return true;
    }
}
