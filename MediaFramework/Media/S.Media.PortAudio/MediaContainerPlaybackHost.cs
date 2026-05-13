using S.Media.Core.Audio;
using S.Media.Core.Playback;
using S.Media.Core.Video;
using S.Media.FFmpeg;

namespace S.Media.PortAudio;

/// <summary>
/// Wires <see cref="MediaContainerDecoder"/> shared-mux audio into an <see cref="AudioPlayer"/> with a primary
/// <see cref="PortAudioOutput"/> (borrowed <see cref="MediaContainerDecoder.Audio"/> source, same prefill / stream-open
/// ordering as <c>VideoPlaybackSmoke</c>). Does <strong>not</strong> own the decoder — the caller keeps its <c>using</c> on
/// <see cref="MediaContainerDecoder"/> and disposes this host (which disposes the player and the PortAudio device).
/// </summary>
/// <remarks>
/// <para>
/// Video routing (<see cref="S.Media.FFmpeg.Video.VideoRouter"/>), GL sinks, and NDI remain host-owned; use
/// <see cref="CreateAvRouter"/> with a <see cref="MediaPlaybackSession"/> built from the same <see cref="MediaContainerDecoder"/>
/// and <see cref="AudioPlayer.Clock"/> graph.
/// </para>
/// </remarks>
public sealed class MediaContainerPlaybackHost : IDisposable
{
    private MediaContainerPlaybackHost(
        MediaContainerDecoder container,
        AudioPlayer player,
        string sourceId,
        PortAudioOutput mainOutput,
        string primarySinkId)
    {
        Container = container;
        Player = player;
        SourceId = sourceId;
        MainOutput = mainOutput;
        PrimarySinkId = primarySinkId;
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
    public static MediaContainerPlaybackHost? TryCreatePortAudioMain(
        MediaContainerDecoder container,
        int chunkSamples = 480,
        int? deviceLatencyMs = null,
        Action<string>? onWireFailedMessage = null)
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

            return new MediaContainerPlaybackHost(container, player, sourceId, output, sinkMain);
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
        Player.Dispose();
        MainOutput.Dispose();
    }
}
