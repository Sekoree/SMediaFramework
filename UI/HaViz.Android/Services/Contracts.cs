using HaViz.Core;

namespace HaViz.Android.Services;

/// <summary>Destination for decoded/captured PCM (the engine, behind the view model).</summary>
public interface IPcmSink
{
    void SubmitPcm(ReadOnlySpan<float> interleaved, int sampleRate, int channels);
}

public sealed record AudioOutputDeviceInfo(int Id, string Name);

/// <summary>
/// The mini media player: decodes one track at a time (platform decoders), plays it on the
/// selected output device, and pushes the decoded PCM to the <see cref="IPcmSink"/>. Track
/// sequencing lives in <see cref="PlaylistController"/> - the player only knows the current track.
/// Events fire on a background thread; marshal to the UI thread in handlers.
/// </summary>
public interface IMiniPlayer : IDisposable
{
    event Action<TrackInfo>? TrackStarted;

    /// <summary>The current track finished on its own (not via Stop/skip).</summary>
    event Action? PlaybackEnded;

    event Action<string>? PlaybackError;

    bool IsPlaying { get; }

    void Play(TrackInfo track);

    void Pause();

    void Resume();

    void Stop();

    IReadOnlyList<AudioOutputDeviceInfo> GetOutputDevices();

    /// <summary>Routes playback to the given device id (<see cref="AudioOutputDeviceInfo.Id"/>);
    /// null returns to the system default. Applies to the current and subsequent tracks.</summary>
    void SetOutputDevice(int? deviceId);

    /// <summary>Whether decoded audio is audible on the device (default false: the box only feeds
    /// the visualizer/NDI). Decode pacing and the PCM tap are unaffected either way - the
    /// AudioTrack keeps clocking the pipeline, it just plays silence while disabled.</summary>
    void SetLocalOutputEnabled(bool enabled);
}

/// <summary>SAF folder pick + recursive scan for playable audio (by extension/MIME).</summary>
public interface IMediaFolderScanner
{
    /// <summary>Launches the system folder picker and scans it recursively. Empty on cancel.</summary>
    Task<IReadOnlyList<TrackInfo>> PickAndScanAsync();
}

/// <summary>
/// System-audio capture via MediaProjection playback capture (Android 10+). Start shows the
/// system consent dialog and spins up the required foreground service; capture pushes PCM into
/// the <see cref="IPcmSink"/> until stopped. Apps that opt out of capture stay silent by design.
/// </summary>
public interface ISystemAudioCapture : IDisposable
{
    bool IsCapturing { get; }

    /// <summary>Capture ended for any reason, including externally (system killed the service,
    /// user revoked the projection). May fire on any thread; marshal to the UI thread.</summary>
    event Action? Stopped;

    /// <summary>False when the user declined the consent dialog or the device cannot capture.</summary>
    Task<bool> StartAsync();

    void Stop();
}
