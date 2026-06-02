using S.Media.Playback;

namespace S.Media.PortAudio;

/// <summary>Fluent PortAudio wiring for <see cref="MediaPlayerOpenBuilder"/>.</summary>
public static class MediaPlayerOpenBuilderPortAudioExtensions
{
    /// <summary>
    /// After the player is built, wires the decoder mux audio to the default PortAudio device.
    /// </summary>
    /// <param name="transferHostOwnershipToPlayer">
    /// When <c>true</c> (default), the built <see cref="MediaPlayer"/> owns the PortAudio host and
    /// disposes it (after its router stops) when the player is disposed — so the simple "open with
    /// audio" path can't leak the host. The host is still reachable via
    /// <see cref="GetWiredPortAudioHost"/> for stats/inspection, but the caller must NOT dispose it.
    /// Pass <c>false</c> to manage the host lifetime yourself (dispose the player first, then the host).
    /// </param>
    public static MediaPlayerOpenFileBuilder WithPortAudio(
        this MediaPlayerOpenFileBuilder builder,
        int? deviceLatencyMs = null,
        int? chunkSamples = null,
        bool transferHostOwnershipToPlayer = true,
        int? deviceIndex = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        RegisterPortAudioCompanion(builder, deviceLatencyMs, chunkSamples, transferHostOwnershipToPlayer, deviceIndex);
        return builder;
    }

    /// <inheritdoc cref="WithPortAudio(MediaPlayerOpenFileBuilder,int?,int?,bool,int?)"/>
    public static MediaPlayerOpenDecoderBuilder WithPortAudio(
        this MediaPlayerOpenDecoderBuilder builder,
        int? deviceLatencyMs = null,
        int? chunkSamples = null,
        bool transferHostOwnershipToPlayer = true,
        int? deviceIndex = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        RegisterPortAudioCompanion(builder, deviceLatencyMs, chunkSamples, transferHostOwnershipToPlayer, deviceIndex);
        return builder;
    }

    /// <summary>Non-null after a successful <see cref="MediaPlayerOpenBuilder.TryBuild"/> when <see cref="WithPortAudio"/> was used.</summary>
    public static PortAudioPlaybackHost? GetWiredPortAudioHost(this MediaPlayerOpenBuilder builder) =>
        builder.WiredPortAudioHost as PortAudioPlaybackHost;

    private static void RegisterPortAudioCompanion(
        MediaPlayerOpenBuilder builder,
        int? deviceLatencyMs,
        int? chunkSamples,
        bool transferHostOwnershipToPlayer,
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

            // Always CallerDisposesPlayer: the host's Dispose then closes only its MainOutput (never the
            // router/clock the player owns), so the player can own + dispose the host without recursion.
            var host = PortAudioPlaybackHost.TryWirePortAudioMainForRouter(
                player.Decoder,
                player.AudioRouter,
                player.AudioClock,
                player.AudioSourceId,
                samples,
                deviceLatencyMs,
                msg => builder.CompanionFailureMessage = msg,
                PortAudioPlaybackHostPlayerOwnership.CallerDisposesPlayer,
                deviceIndex);

            if (host is null)
            {
                builder.CompanionFailureMessage ??= "PortAudio wire-up failed.";
                return false;
            }

            builder.WiredPortAudioHost = host;
            player.SetPortAudioPlaybackStats(host.MainOutput);
            // Default: hand the host to the player so disposing the player alone tears down the hardware
            // output (after the router stops). Opt out to manage the host lifetime yourself.
            if (transferHostOwnershipToPlayer)
                player.RegisterOwnedCompanion(host);
            return true;
        });
    }
}
