using S.Media.Core.Audio;

namespace S.Media.Core.Triggers;

/// <summary>Registers standard <see cref="AudioClipPlayer"/> triggers on a <see cref="TriggerBus"/>.</summary>
public static class AudioTriggerRegistration
{
    /// <summary>
    /// Binds <c>{id}.fire</c>, <c>{id}.stop</c>, <c>{id}.stopAll</c>, and <c>{id}.loop</c> for one pad player.
    /// </summary>
    public static void RegisterAudioClipPlayer(
        TriggerBus bus,
        string id,
        AudioClipPlayer player,
        AudioRouter router,
        string outputId,
        ChannelMap? map = null,
        float gain = 1f)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentException.ThrowIfNullOrEmpty(outputId);

        bus.Register($"{id}.fire", (in TriggerPayload _) =>
        {
            player.Fire(router, outputId, map, gain);
        });
        bus.Register($"{id}.stop", (in TriggerPayload _) => player.StopAll());
        bus.Register($"{id}.stopAll", (in TriggerPayload _) => player.StopAll());
        bus.Register($"{id}.loop", (in TriggerPayload _) =>
        {
            player.Mode = AudioClipPlayerMode.LatchedLoop;
            player.Fire(router, outputId, map, gain);
        });
    }
}
