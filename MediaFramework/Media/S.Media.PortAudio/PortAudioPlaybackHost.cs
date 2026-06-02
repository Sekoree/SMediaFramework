using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg;

namespace S.Media.PortAudio;

/// <summary>
/// Wires <see cref="MediaContainerDecoder"/> shared-mux audio into an <see cref="AudioRouter"/> + <see cref="MediaClock"/>
/// with a primary <see cref="PortAudioOutput"/>.
/// </summary>
public sealed class PortAudioPlaybackHost : IDisposable
{
    private readonly PortAudioPlaybackHostPlayerOwnership _playerOwnership;
    private bool _disposed;

    private PortAudioPlaybackHost(
        MediaContainerDecoder container,
        AudioRouter router,
        MediaClock clock,
        string sourceId,
        PortAudioOutput mainOutput,
        string primarySinkId,
        PortAudioPlaybackHostPlayerOwnership playerOwnership)
    {
        Container = container;
        Router = router;
        Clock = clock;
        SourceId = sourceId;
        MainOutput = mainOutput;
        PrimaryOutputId = primarySinkId;
        _playerOwnership = playerOwnership;
    }

    public MediaContainerDecoder Container { get; }

    public AudioRouter Router { get; }

    public MediaClock Clock { get; }

    public string SourceId { get; }

    public PortAudioOutput MainOutput { get; }

    public string PrimaryOutputId { get; }

    public AudioFormat AudioFormat => Container.Audio.Format;

    public static PortAudioPlaybackHost? TryCreatePortAudioMain(
        MediaContainerDecoder container,
        int chunkSamples = 480,
        int? deviceLatencyMs = null,
        Action<string>? onWireFailedMessage = null,
        PortAudioPlaybackHostPlayerOwnership playerOwnership = PortAudioPlaybackHostPlayerOwnership.HostDisposesPlayer)
    {
        ArgumentNullException.ThrowIfNull(container);
        AudioRouter? router = null;
        PortAudioOutput? output = null;
        string? sourceId = null;
        string? sinkMain = null;
        try
        {
            var audioSource = container.Audio;
            var clock = new MediaClock();
            router = new AudioRouter(audioSource.Format.SampleRate, chunkSamples);
            router.AttachMasterClock(clock);

            double? latencySec = deviceLatencyMs is > 0 ? deviceLatencyMs.Value / 1000.0 : null;
            output = new PortAudioOutput(
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

            sourceId = router.AddSource(audioSource);
            sinkMain = router.AddOutput(output);
            router.Connect(sourceId, sinkMain);

            return new PortAudioPlaybackHost(container, router, clock, sourceId, output, sinkMain, playerOwnership);
        }
        catch (Exception ex)
        {
            RollbackPartialWire(router, output, sourceId, sinkMain, disposeRouter: true, "TryCreatePortAudioMain");
            onWireFailedMessage?.Invoke(ex.Message);
            return null;
        }
    }

    public static PortAudioPlaybackHost? TryWirePortAudioMainForRouter(
        MediaContainerDecoder container,
        AudioRouter router,
        MediaClock clock,
        string decoderMuxAudioSourceId,
        int chunkSamples = 480,
        int? deviceLatencyMs = null,
        Action<string>? onWireFailedMessage = null,
        PortAudioPlaybackHostPlayerOwnership playerOwnership = PortAudioPlaybackHostPlayerOwnership.HostDisposesPlayer,
        int? deviceIndex = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrEmpty(decoderMuxAudioSourceId);
        PortAudioOutput? output = null;
        string? sinkMain = null;
        try
        {
            if (!router.SourceIds.Contains(decoderMuxAudioSourceId))
            {
                onWireFailedMessage?.Invoke($"AudioRouter has no source '{decoderMuxAudioSourceId}'.");
                return null;
            }

            var audioSource = container.Audio;
            double? latencySec = deviceLatencyMs is > 0 ? deviceLatencyMs.Value / 1000.0 : null;
            output = new PortAudioOutput(
                audioSource.Format,
                deviceIndex: deviceIndex,
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

            sinkMain = router.AddOutput(output);
            router.Connect(decoderMuxAudioSourceId, sinkMain);

            return new PortAudioPlaybackHost(container, router, clock, decoderMuxAudioSourceId, output, sinkMain, playerOwnership);
        }
        catch (Exception ex)
        {
            // Setup failed partway. Undo any router registration and dispose the native
            // PortAudio stream so a partial-wire failure can't leak the device. The router
            // owns only the pump wrapper (on success the returned host owns the output), so
            // RemoveOutput tears down the pump and we still dispose the output ourselves.
            RollbackPartialWire(router, output, sourceId: null, sinkMain, disposeRouter: false, "TryWirePortAudioMainForRouter");
            onWireFailedMessage?.Invoke(ex.Message);
            return null;
        }
    }

    internal static void RollbackPartialWireForTests(
        AudioRouter? router,
        IDisposable? output,
        string? sourceId,
        string? sinkMain,
        bool disposeRouter) =>
        RollbackPartialWire(router, output, sourceId, sinkMain, disposeRouter, "PortAudioPlaybackHost.Tests");

    private static void RollbackPartialWire(
        AudioRouter? router,
        IDisposable? output,
        string? sourceId,
        string? sinkMain,
        bool disposeRouter,
        string label)
    {
        if (router is not null)
        {
            if (sinkMain is not null)
                MediaDiagnostics.SwallowDisposeErrors(() => router.RemoveOutput(sinkMain), $"{label}: rollback RemoveOutput");
            if (sourceId is not null)
                MediaDiagnostics.SwallowDisposeErrors(() => router.RemoveSource(sourceId), $"{label}: rollback RemoveSource");
            if (disposeRouter)
                MediaDiagnostics.SwallowDisposeErrors(router.Dispose, $"{label}: rollback router dispose");
        }

        if (output is not null)
            MediaDiagnostics.SwallowDisposeErrors(output.Dispose, $"{label}: rollback output dispose");
    }

    public void PrefillMainOutputDirectFromDecoder(TimeSpan timeout, IAudioOutput? mirrorPackedFloats = null)
    {
        if (!Router.TryPrefillPrimaryPortAudio(Container.Audio, timeout, mirrorPackedFloats))
            throw new InvalidOperationException("Primary output must be PortAudio for hardware prefill.");
    }

    public void StartHardwareOutput() => MainOutput.Start();

    public MediaContainerSession CreateContainerSession(VideoPlayer video, IMediaClock playClock) =>
        MediaContainerSession.Create(Container, video, playClock, Router, Clock, SourceId);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_playerOwnership == PortAudioPlaybackHostPlayerOwnership.HostDisposesPlayer)
        {
            TryDisposeOwned(() => Router.Dispose(), "PortAudioPlaybackHost.Dispose: AudioRouter");
            TryDisposeOwned(() => Clock.Dispose(), "PortAudioPlaybackHost.Dispose: MediaClock");
        }

        TryDisposeOwned(() => MainOutput.Dispose(), "PortAudioPlaybackHost.Dispose: MainOutput");
    }

    private static void TryDisposeOwned(Action dispose, string debugLabel) =>
        MediaDiagnostics.SwallowDisposeErrors(dispose, debugLabel);
}
