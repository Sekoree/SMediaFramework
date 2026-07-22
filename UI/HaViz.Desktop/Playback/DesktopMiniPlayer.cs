using HaViz.Core;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Players;

namespace HaViz.Desktop.Playback;

/// <summary>Interleaved float PCM hand-off (same shape as VizNdiEngine.SubmitPcm).</summary>
public delegate void PcmSubmit(ReadOnlySpan<float> interleaved, int sampleRate, int channels);

/// <summary>
/// Router output that forwards the mixed PCM to the visualizer/NDI engine. Attached alongside the
/// audible master output, so the tap hears exactly what the device plays. Submit runs on the
/// audio thread - the sink (VizNdiEngine.SubmitPcm) is thread-safe by contract.
/// </summary>
internal sealed class VizTapAudioOutput(AudioFormat format, PcmSubmit sink) : IAudioOutput
{
    public AudioFormat Format => format;

    public void Submit(ReadOnlySpan<float> packedSamples) =>
        sink(packedSamples, format.SampleRate, format.Channels);
}

/// <summary>
/// The desktop counterpart of the Android head's IMiniPlayer: one framework MediaPlayer per track
/// (FFmpeg decode -> AudioRouter -> audible backend output + viz tap). MediaPlayer has no
/// end-of-track event - natural end is IsRunning flipping false - so the owner must call
/// <see cref="Poll"/> from a UI timer to get <see cref="PlaybackEnded"/>. All members are
/// UI-thread only; only the tap's Submit runs on the audio thread.
/// </summary>
public sealed class DesktopMiniPlayer(IMediaRegistry registry, IAudioBackend backend, PcmSubmit sink)
    : IDisposable
{
    // IsRunning can briefly read false right after Play() while the hardware stream spins up;
    // without a grace window Poll() would declare the track over before it started.
    private const long StartGraceMs = 2000;

    // The audible output OpenAudio attaches; also the playback clock, so "local output off" must
    // MUTE its route (click-free gain ramp), never detach it - without a clocked output decode
    // pacing dies and the viz/NDI tap starves too.
    private const string MasterOutputId = "_master";

    private MediaPlayer? _player;
    private bool _paused;
    private bool _endedRaised;
    private long _startedAtMs;
    private string? _deviceId;
    private bool _localOutputEnabled = true;

    public event Action<TrackInfo>? TrackStarted;
    public event Action? PlaybackEnded;
    public event Action<string>? PlaybackError;

    public bool HasTrack => _player is not null;

    public TimeSpan Position => _player?.Position ?? TimeSpan.Zero;

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        try
        {
            return backend.EnumerateOutputDevices();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Null = backend default. Takes effect from the NEXT track (the live player keeps
    /// its already-opened hardware stream).</summary>
    public void SetOutputDevice(string? deviceId) => _deviceId = deviceId;

    /// <summary>False = decode keeps running and the viz/NDI tap keeps full-level audio, but the
    /// local device plays silence (the box only feeds NDI - the Android head's PlayOnDevice).
    /// Applies immediately to the current track and to every following one.</summary>
    public void SetLocalOutputEnabled(bool enabled)
    {
        _localOutputEnabled = enabled;
        ApplyLocalOutputGain();
    }

    private void ApplyLocalOutputGain()
    {
        if (_player is not { AudioRouter: { } router, AudioSourceId: { } sourceId })
            return;
        try
        {
            router.SetRouteGain(sourceId, MasterOutputId, _localOutputEnabled ? 1f : 0f);
        }
        catch (InvalidOperationException)
        {
            // No audible route on this track - nothing to mute.
        }
    }

    public void Play(TrackInfo track)
    {
        Stop();
        MediaPlayer? player = null;
        try
        {
            player = MediaPlayer.OpenAudio(registry, backend, track.Uri, _deviceId);
            var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
            player.AttachAudioOutput(new VizTapAudioOutput(new AudioFormat(rate, 2), sink), "viz-tap");
            _player = player;
            ApplyLocalOutputGain(); // before Play() so a muted box never blips the first chunk
            player.Play();
            _paused = false;
            _endedRaised = false;
            _startedAtMs = Environment.TickCount64;
            TrackStarted?.Invoke(track);
        }
        catch (Exception ex)
        {
            player?.Dispose();
            _player = null;
            PlaybackError?.Invoke(ex.Message);
        }
    }

    public void Pause()
    {
        if (_player is not { } player || _paused)
            return;
        try
        {
            player.Pause();
            _paused = true;
        }
        catch (Exception ex)
        {
            PlaybackError?.Invoke(ex.Message);
        }
    }

    public void Resume()
    {
        if (_player is not { } player || !_paused)
            return;
        try
        {
            player.Play();
            _paused = false;
            _startedAtMs = Environment.TickCount64;
        }
        catch (Exception ex)
        {
            PlaybackError?.Invoke(ex.Message);
        }
    }

    public void Stop()
    {
        _player?.Dispose();
        _player = null;
        _paused = false;
    }

    /// <summary>Call periodically from the UI thread; raises <see cref="PlaybackEnded"/> once when
    /// the current track finished on its own.</summary>
    public void Poll()
    {
        if (_player is not { } player || _paused || _endedRaised)
            return;
        if (Environment.TickCount64 - _startedAtMs < StartGraceMs)
            return;
        if (player.IsRunning)
            return;
        _endedRaised = true;
        PlaybackEnded?.Invoke();
    }

    public void Dispose() => Stop();
}
