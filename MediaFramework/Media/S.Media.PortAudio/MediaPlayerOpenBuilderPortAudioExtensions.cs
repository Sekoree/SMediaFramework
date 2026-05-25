using S.Media.Playback;

namespace S.Media.PortAudio;

/// <summary>Fluent PortAudio wiring for <see cref="MediaPlayerOpenBuilder"/>.</summary>
public static class MediaPlayerOpenBuilderPortAudioExtensions
{
    /// <summary>
    /// After the player is built, wires the decoder mux audio to the default PortAudio device.
    /// The returned host is stored on the builder (<see cref="WiredPortAudioHost"/>); dispose it before or with the player.
    /// </summary>
    public static MediaPlayerOpenFileBuilder WithPortAudio(
        this MediaPlayerOpenFileBuilder builder,
        int? deviceLatencyMs = null,
        int? chunkSamples = null,
        PortAudioPlaybackHostPlayerOwnership ownership = PortAudioPlaybackHostPlayerOwnership.CallerDisposesPlayer,
        int? deviceIndex = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        RegisterPortAudioCompanion(builder, deviceLatencyMs, chunkSamples, ownership, deviceIndex);
        return builder;
    }

    public static MediaPlayerOpenDecoderBuilder WithPortAudio(
        this MediaPlayerOpenDecoderBuilder builder,
        int? deviceLatencyMs = null,
        int? chunkSamples = null,
        PortAudioPlaybackHostPlayerOwnership ownership = PortAudioPlaybackHostPlayerOwnership.CallerDisposesPlayer,
        int? deviceIndex = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        RegisterPortAudioCompanion(builder, deviceLatencyMs, chunkSamples, ownership, deviceIndex);
        return builder;
    }

    /// <summary>Non-null after a successful <see cref="MediaPlayerOpenBuilder.TryBuild"/> when <see cref="WithPortAudio"/> was used.</summary>
    public static PortAudioPlaybackHost? GetWiredPortAudioHost(this MediaPlayerOpenBuilder builder) =>
        builder.WiredPortAudioHost as PortAudioPlaybackHost;

    private static void RegisterPortAudioCompanion(
        MediaPlayerOpenBuilder builder,
        int? deviceLatencyMs,
        int? chunkSamples,
        PortAudioPlaybackHostPlayerOwnership ownership,
        int? deviceIndex = null)
    {
        builder.CompanionSteps.Add(player =>
        {
            if (!player.HasContainerDecoder)
            {
                builder.CompanionFailureMessage = "WithPortAudio requires a container decoder.";
                return false;
            }

            if (player.AudioRouter is null || player.AudioClock is null || player.AudioSourceId is null)
            {
                builder.CompanionFailureMessage = "WithPortAudio requires an audio router on the player.";
                return false;
            }

            var samples = chunkSamples ?? builder.Options.AudioChunkSamples;

            var host = PortAudioPlaybackHost.TryWirePortAudioMainForRouter(
                player.Decoder,
                player.AudioRouter,
                player.AudioClock,
                player.AudioSourceId,
                samples,
                deviceLatencyMs,
                msg => builder.CompanionFailureMessage = msg,
                ownership,
                deviceIndex);

            if (host is null)
            {
                builder.CompanionFailureMessage ??= "PortAudio wire-up failed.";
                return false;
            }

            builder.WiredPortAudioHost = host;
            player.SetPortAudioPlaybackStats(host.MainOutput);
            return true;
        });
    }
}
