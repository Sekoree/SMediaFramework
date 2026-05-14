using System.Linq;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using S.Media.FFmpeg;

namespace S.Media.PortAudio;

/// <summary>
/// Wires <see cref="MediaContainerDecoder"/> shared-mux audio into an <see cref="AudioPlayer"/> with a primary
/// <see cref="PortAudioOutput"/> (borrowed <see cref="MediaContainerDecoder.Audio"/> source, same prefill / stream-open
/// ordering as <c>VideoPlaybackSmoke</c>). Does <strong>not</strong> own the decoder — the caller keeps its <c>using</c> on
/// <see cref="MediaContainerDecoder"/> and disposes this host (which disposes the player and the PortAudio device,
/// unless <see cref="MediaContainerPlaybackHostPlayerOwnership.CallerDisposesPlayer"/> was selected at creation).
/// </summary>
/// <remarks>
/// <para>
/// Video routing (<see cref="S.Media.FFmpeg.Video.VideoRouter"/>), GL sinks, and NDI remain host-owned; use
/// <see cref="CreateAvRouter"/> with a <see cref="MediaPlaybackSession"/> built from the same <see cref="MediaContainerDecoder"/>
/// and <see cref="AudioPlayer.Clock"/> graph. For optional single-<see cref="IDisposable.Dispose"/> of the decoder plus
/// <see cref="VideoPlayer"/> / optional <see cref="VideoRouter"/> / freerun <see cref="MediaClock"/> when you inject sinks yourself, see
/// <see cref="S.Media.FFmpeg.MediaContainerMegaPlaybackHost"/>.
/// </para>
/// <para>
/// When wiring the same <see cref="AudioPlayer"/> into <see cref="S.Media.FFmpeg.MediaContainerMegaPlaybackHost"/>, use
/// <see cref="MediaContainerPlaybackHostPlayerOwnership.CallerDisposesPlayer"/>, dispose the mega host first (so the player and router stop),
/// then dispose this host to close <see cref="MainOutput"/> only.
/// </para>
/// <para>
/// <see cref="Dispose"/> tears down in fixed order (player when owned, then <see cref="MainOutput"/>). In <c>DEBUG</c> builds,
/// a failure from one step is logged via <see cref="MediaDiagnostics"/> and teardown continues; <c>Release</c> remains best-effort silent.
/// </para>
/// </remarks>
public sealed class MediaContainerPlaybackHost : IDisposable
{
    private readonly MediaContainerPlaybackHostPlayerOwnership _playerOwnership;
    private bool _disposed;

    private MediaContainerPlaybackHost(
        MediaContainerDecoder container,
        AudioPlayer player,
        string sourceId,
        PortAudioOutput mainOutput,
        string primarySinkId,
        MediaContainerPlaybackHostPlayerOwnership playerOwnership)
    {
        Container = container;
        Player = player;
        SourceId = sourceId;
        MainOutput = mainOutput;
        PrimarySinkId = primarySinkId;
        _playerOwnership = playerOwnership;
    }

    /// <summary>Shared demux — same instance the host was constructed with.</summary>
    public MediaContainerDecoder Container { get; }

    public AudioPlayer Player { get; }

    /// <summary>Router id returned by <see cref="AudioRouter.AddSource"/> for <see cref="MediaContainerDecoder.Audio"/>.</summary>
    public string SourceId { get; }

    public PortAudioOutput MainOutput { get; }

    /// <summary>Router id of <see cref="MainOutput"/> (for <see cref="AudioRouter.GetPumpStats"/>).</summary>
    public string PrimarySinkId { get; }

    public AudioFormat AudioFormat => Container.Audio.Format;

    /// <summary>
    /// Attempts PortAudio-backed wiring for <paramref name="container"/>. On failure invokes
    /// <paramref name="onWireFailedMessage"/> with a short reason (when not <c>null</c>) and returns <c>null</c>.
    /// </summary>
    /// <param name="playerOwnership">
    /// Use <see cref="MediaContainerPlaybackHostPlayerOwnership.CallerDisposesPlayer"/> when bundling <see cref="Player"/> into
    /// <see cref="S.Media.FFmpeg.MediaContainerMegaPlaybackHost"/>; dispose that mega host before calling <see cref="Dispose"/> here.
    /// </param>
    public static MediaContainerPlaybackHost? TryCreatePortAudioMain(
        MediaContainerDecoder container,
        int chunkSamples = 480,
        int? deviceLatencyMs = null,
        Action<string>? onWireFailedMessage = null,
        MediaContainerPlaybackHostPlayerOwnership playerOwnership = MediaContainerPlaybackHostPlayerOwnership.HostDisposesPlayer)
    {
        ArgumentNullException.ThrowIfNull(container);
        try
        {
            var audioSource = container.Audio;
            var player = new AudioPlayer(audioSource.Format.SampleRate, chunkSamples);

            double? latencySec = deviceLatencyMs is > 0 ? deviceLatencyMs.Value / 1000.0 : null;
            var output = new PortAudioOutput(
                audioSource.Format,
                deviceIndex: null,
                suggestedLatency: latencySec,
                framesPerBuffer: chunkSamples,
                ringCapacityFrames: audioSource.Format.SampleRate);

            var cap = output.CapacitySamples;
            var floor = chunkSamples * 4;
            var target = Math.Max(floor, Math.Min(chunkSamples * 16, cap / 3));
            if (latencySec is { } s && s > 0)
            {
                var latencySamples = (int)(audioSource.Format.SampleRate * s);
                target = Math.Max(target, Math.Min(latencySamples * 2, cap / 2));
            }

            output.TargetQueueSamples = target;

            string sourceId = player.Router.AddSource(audioSource);
            string sinkMain = player.AddOutput(output);
            player.Connect(sourceId, sinkMain);

            return new MediaContainerPlaybackHost(container, player, sourceId, output, sinkMain, playerOwnership);
        }
        catch (Exception ex)
        {
            onWireFailedMessage?.Invoke(ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Wires a default <see cref="PortAudioOutput"/> into an <see cref="AudioPlayer"/> that already has
    /// <see cref="MediaContainerDecoder.Audio"/> registered as <paramref name="decoderMuxAudioSourceId"/> (same pattern as
    /// <see cref="TryCreatePortAudioMain"/>, without creating a new player).
    /// </summary>
    public static MediaContainerPlaybackHost? TryWirePortAudioMainForPlayer(
        MediaContainerDecoder container,
        AudioPlayer player,
        string decoderMuxAudioSourceId,
        int chunkSamples = 480,
        int? deviceLatencyMs = null,
        Action<string>? onWireFailedMessage = null,
        MediaContainerPlaybackHostPlayerOwnership playerOwnership = MediaContainerPlaybackHostPlayerOwnership.HostDisposesPlayer)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentException.ThrowIfNullOrEmpty(decoderMuxAudioSourceId);
        try
        {
            if (!player.Router.SourceIds.Contains(decoderMuxAudioSourceId))
            {
                onWireFailedMessage?.Invoke($"AudioPlayer has no source '{decoderMuxAudioSourceId}'.");
                return null;
            }

            var audioSource = container.Audio;
            double? latencySec = deviceLatencyMs is > 0 ? deviceLatencyMs.Value / 1000.0 : null;
            var output = new PortAudioOutput(
                audioSource.Format,
                deviceIndex: null,
                suggestedLatency: latencySec,
                framesPerBuffer: chunkSamples,
                ringCapacityFrames: audioSource.Format.SampleRate);

            var cap = output.CapacitySamples;
            var floor = chunkSamples * 4;
            var target = Math.Max(floor, Math.Min(chunkSamples * 16, cap / 3));
            if (latencySec is { } s && s > 0)
            {
                var latencySamples = (int)(audioSource.Format.SampleRate * s);
                target = Math.Max(target, Math.Min(latencySamples * 2, cap / 2));
            }

            output.TargetQueueSamples = target;

            string sinkMain = player.AddOutput(output);
            player.Connect(decoderMuxAudioSourceId, sinkMain);

            return new MediaContainerPlaybackHost(container, player, decoderMuxAudioSourceId, output, sinkMain, playerOwnership);
        }
        catch (Exception ex)
        {
            onWireFailedMessage?.Invoke(ex.Message);
            return null;
        }
    }

    /// <inheritdoc cref="AudioPlayerPortAudioExtensions.TryPrefillPrimaryPortAudio"/>
    public void PrefillMainOutputDirectFromDecoder(TimeSpan timeout, IAudioSink? mirrorPackedFloats = null)
    {
        if (!Player.TryPrefillPrimaryPortAudio(Container.Audio, timeout, mirrorPackedFloats))
            throw new InvalidOperationException("Primary sink must be PortAudio for hardware prefill.");
    }

    /// <summary>Opens the native PortAudio stream after <see cref="PrefillMainOutputDirectFromDecoder"/>.</summary>
    public void StartHardwareOutput() => MainOutput.Start();

    /// <summary>Builds an <see cref="AvRouter"/> for this container — <paramref name="session"/> must use the same clock graph.</summary>
    public AvRouter CreateAvRouter(IAvPlaybackSession session) => new(Container, session);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_playerOwnership == MediaContainerPlaybackHostPlayerOwnership.HostDisposesPlayer)
            TryDisposeOwned(() => Player.Dispose(), "MediaContainerPlaybackHost.Dispose: AudioPlayer");
        TryDisposeOwned(() => MainOutput.Dispose(), "MediaContainerPlaybackHost.Dispose: MainOutput");
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
            // best effort — continue host teardown
        }
#endif
    }
}
