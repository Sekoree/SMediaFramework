using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using S.Media.PortAudio;
using S.Media.Playback;
using S.Media.SDL3;

namespace S.Media.Interop;

/// <summary>
/// Playback state reported across the C ABI by <c>mfp_get_state</c>. Values are part of the ABI — append
/// only, never renumber.
/// </summary>
internal enum PlayerState
{
    Idle = 0,
    Playing = 1,
    Paused = 2,
    Ended = 3,
    Error = 4,
}

/// <summary>
/// The managed object a C-ABI player handle points at. It owns a <see cref="MediaPlayer"/> plus (optionally)
/// a PortAudio hardware host and an SDL video window, and serializes transport calls so a host program can
/// drive it from any thread. Instances are pinned by a <c>GCHandle</c> in <see cref="NativeApi"/>; the
/// runtime GC only ever sees this graph, never the host program's memory.
/// </summary>
internal sealed class PlayerInstance : IDisposable
{
    private readonly object _gate = new();
    private readonly MediaPlayer _player;
    private readonly PortAudioPlaybackHost? _audioHost;
    private readonly TimeSpan _duration;
    private PlayerState _state = PlayerState.Idle;
    private bool _disposed;

    private PlayerInstance(MediaPlayer player, PortAudioPlaybackHost? audioHost, TimeSpan duration)
    {
        _player = player;
        _audioHost = audioHost;
        _duration = duration;
    }

    /// <summary>
    /// Opens <paramref name="utf8PathManaged"/> and wires the requested outputs. <paramref name="audioDeviceIndex"/>
    /// is a global PortAudio device index, <see cref="NativeApi.DefaultAudioDevice"/> for the system default, or
    /// <see cref="NativeApi.NoAudioDevice"/> to leave audio unwired. Audio/video sides absent from the file are
    /// silently skipped (no hard failure), so a host can always request both.
    /// </summary>
    public static bool TryOpen(
        string path,
        bool withVideoWindow,
        int audioDeviceIndex,
        out PlayerInstance? instance,
        out string? error)
    {
        instance = null;
        error = null;

        MediaContainerDecoder? decoder = null;
        SDL3GLVideoOutput? videoWindow = null;
        try
        {
            var options = MediaPlayerOpenOptions.Default;
            decoder = MediaContainerDecoder.Open(path, options.ToVideoDecoderOpenOptions());
            var hasAudio = decoder.HasAudio;
            var hasVideo = decoder.HasVideo;

            var builder = MediaPlayer.Open(decoder)
                .WithOptions(options)
                .WithDecoderOwnership(MediaPlayerDecoderOwnership.BundleDisposesDecoder);

            if (withVideoWindow && hasVideo)
            {
                videoWindow = new SDL3GLVideoOutput(
                    title: System.IO.Path.GetFileName(path),
                    initialWidth: 1280,
                    initialHeight: 720);
                builder = builder.WithVideoLead(videoWindow, disposeOnPlayerDispose: true);
            }

            if (audioDeviceIndex != NativeApi.NoAudioDevice && hasAudio)
            {
                var device = audioDeviceIndex == NativeApi.DefaultAudioDevice ? (int?)null : audioDeviceIndex;
                builder = builder.WithPortAudio(deviceIndex: device);
            }

            if (!builder.TryBuild(out var player, out error))
            {
                // BundleDisposesDecoder owns the decoder on failure; the SDL window is ours to close.
                videoWindow?.Dispose();
                return false;
            }

            // WithPortAudio is a no-op when audio wasn't requested / present, so the host stays null then.
            var host = builder.GetWiredPortAudioHost();
            instance = new PlayerInstance(player, host, decoder.Duration);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            videoWindow?.Dispose();
            // If the player never took ownership, dispose the decoder we opened.
            decoder?.Dispose();
            MediaDiagnostics.LogError(ex, "S.Media.Interop.PlayerInstance.TryOpen");
            return false;
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_audioHost is { } host)
            {
                _player.Play(
                    prefillBeforeHardware: () => host.PrefillMainOutputDirectFromDecoder(TimeSpan.FromMilliseconds(500)),
                    startHardware: host.StartHardwareOutput);
            }
            else
            {
                _player.Play();
            }

            _state = PlayerState.Playing;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _player.Pause();
            _state = PlayerState.Paused;
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _player.SeekCoordinated(position);
        }
    }

    public long PositionTicks
    {
        get
        {
            lock (_gate)
                return _disposed ? 0 : _player.PlayClock.CurrentPosition.Ticks;
        }
    }

    public long DurationTicks => _duration.Ticks;

    public PlayerState State
    {
        get
        {
            lock (_gate)
            {
                if (_disposed)
                    return PlayerState.Error;
                if (_state == PlayerState.Playing && IsEndedUnlocked())
                    _state = PlayerState.Ended;
                return _state;
            }
        }
    }

    public bool IsEnded
    {
        get { lock (_gate) return !_disposed && IsEndedUnlocked(); }
    }

    /// <summary>End-of-media for a finite file: the playhead reached the duration, or the audio router
    /// drained its source to natural EOF. Live sources never end on their own.</summary>
    private bool IsEndedUnlocked()
    {
        if (_player.IsLive)
            return false;
        if (_player.AudioRouter is { CompletedNaturally: true })
            return true;
        return _duration > TimeSpan.Zero
            && _player.PlayClock.CurrentPosition >= _duration - TimeSpan.FromMilliseconds(150);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            MediaDiagnostics.SwallowDisposeErrors(_player.Dispose, "S.Media.Interop.PlayerInstance.Dispose");
        }
    }
}
