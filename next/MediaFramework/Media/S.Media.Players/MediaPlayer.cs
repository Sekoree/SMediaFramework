using System.Linq;

namespace S.Media.Players;

/// <summary>
/// Phase 2 audio-first single-media player: opens a URI through the <see cref="IMediaRegistry"/>, mixes
/// its audio through an <see cref="AudioRouter"/>, and plays it on one master output whose
/// <see cref="IPlaybackClock"/> drives a <see cref="MediaClock"/> (D11 — the master output is the clock
/// source; the source resamples to the device's rate on ingress). No on-screen video yet — video display
/// and the full A/V <c>AvPlaybackCoordinator</c> arrive in Phase 3 with the GPU/present tier.
/// </summary>
/// <remarks>Built entirely on registry contracts: it never references a concrete backend (FFmpeg /
/// PortAudio) — those are supplied by the caller's registry + audio backend at the composition root.</remarks>
public sealed class MediaPlayer : IDisposable
{
    private readonly AudioRouter _router;
    private readonly MediaClock _clock;
    private readonly IAudioOutput _output;
    private readonly IAudioSource _audio;
    private bool _disposed;

    private MediaPlayer(AudioRouter router, MediaClock clock, IAudioOutput output, IAudioSource audio)
    {
        _router = router;
        _clock = clock;
        _output = output;
        _audio = audio;
    }

    /// <summary>The master playhead position (derived from the master output's clock).</summary>
    public TimeSpan Position => _clock.CurrentPosition;

    /// <summary>True while audio is advancing.</summary>
    public bool IsRunning => _router.IsRunning;

    /// <summary>The sample rate the session mixes at (the master device's rate — D11).</summary>
    public int SampleRate => _router.SampleRate;

    /// <summary>
    /// Opens the audio of <paramref name="uri"/> via <paramref name="registry"/> and wires it to a master
    /// output created on <paramref name="audioBackend"/>. Mixing happens at the chosen device's native
    /// rate; the source auto-resamples to it via the registry's resampler.
    /// </summary>
    public static MediaPlayer OpenAudio(
        IMediaRegistry registry,
        IAudioBackend audioBackend,
        string uri,
        string? deviceId = null,
        int channels = 2)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(audioBackend);
        ArgumentException.ThrowIfNullOrEmpty(uri);

        var devices = audioBackend.EnumerateOutputDevices();
        var device = deviceId is null
            ? devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault()
            : devices.FirstOrDefault(d => d.Id == deviceId)
              ?? throw new ArgumentException($"audio output device '{deviceId}' not found", nameof(deviceId));
        var sampleRate = device is { DefaultSampleRate: > 0 } ? (int)device.DefaultSampleRate : 48000;

        if (!registry.TryOpenAudio(uri, null, out var audio))
            throw new InvalidOperationException(
                $"No registered decoder can open '{uri}' for audio (registered: {string.Join(", ", registry.Decoders.Select(d => d.Name))}).");

        AudioRouter? router = null;
        IAudioOutput? output = null;
        try
        {
            router = new AudioRouter(sampleRate)
            {
                // D11: source resamples to the master rate on ingress, via the registry's resampler.
                ResamplerFactory = (inner, rate) => registry.CreateResampler(inner, rate)
                    ?? throw new InvalidOperationException("autoResample needs a resampler — register an FFmpeg (or other) module."),
            };
            var clock = new MediaClock();
            router.AttachMasterClock(clock);                 // slaves to the primary output's IPlaybackClock
            var sourceId = router.AddSource(audio, autoResample: true);
            output = audioBackend.CreateOutput(device?.Id, new AudioFormat(sampleRate, channels));
            var outputId = router.AddOutput(output);          // clocked output → primary/master (D11)
            router.ApplyMatrix(sourceId, outputId, DefaultPlaybackMatrix(audio.Format.Channels, channels));
            return new MediaPlayer(router, clock, output, audio);
        }
        catch
        {
            router?.Dispose();
            (output as IDisposable)?.Dispose();
            (audio as IDisposable)?.Dispose();
            throw;
        }
    }

    private static float[,] DefaultPlaybackMatrix(int sourceChannels, int outputChannels)
    {
        if (AudioChannelLayoutPresets.TryGetDownmix(sourceChannels, outputChannels, out var matrix))
            return matrix;

        matrix = new float[sourceChannels, outputChannels];
        for (var ch = 0; ch < Math.Min(sourceChannels, outputChannels); ch++)
            matrix[ch, ch] = 1f;
        return matrix;
    }

    /// <summary>Start (or resume) playback.</summary>
    public void Play()
    {
        _clock.Start();
        _router.Start();
    }

    /// <summary>Pause playback; <see cref="Position"/> holds.</summary>
    public void Pause()
    {
        _router.Pause();
        _clock.Pause();
    }

    /// <summary>Seek to <paramref name="position"/> on the master timeline.</summary>
    public void Seek(TimeSpan position)
    {
        _router.Seek(position);
        _clock.Seek(position);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        MediaDiagnostics.SwallowDisposeErrors(() => _router.Stop(), "MediaPlayer: router stop");
        MediaDiagnostics.SwallowDisposeErrors(_router.Dispose, "MediaPlayer: router dispose");
        MediaDiagnostics.SwallowDisposeErrors(() => (_output as IDisposable)?.Dispose(), "MediaPlayer: output dispose");
        MediaDiagnostics.SwallowDisposeErrors(() => (_audio as IDisposable)?.Dispose(), "MediaPlayer: audio dispose");
    }
}
