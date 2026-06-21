using System.IO;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
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
/// The managed object a C-ABI player handle points at. It owns a <see cref="MediaPlayer"/> (and, on the
/// convenience open path, a PortAudio host + SDL window) and serializes transport calls so a host program can
/// drive it from any thread. The graph-open path leaves the player's <see cref="VideoRouter"/> /
/// <see cref="AudioRouter"/> bare so the host wires its own outputs. Instances are pinned by a
/// <c>GCHandle</c> in <see cref="NativeApi"/>; the runtime GC only ever sees this graph, never the host's
/// memory.
/// </summary>
internal sealed class PlayerInstance : IDisposable
{
    private readonly object _gate = new();
    private readonly MediaPlayer _player;
    private readonly PortAudioPlaybackHost? _audioHost;
    private readonly TimeSpan _duration;
    private PlayerState _state = PlayerState.Idle;
    private bool _disposed;

    // Event delivery (C function-pointer callback). Pinned router handles handed to the host are freed here
    // on close — they borrow the player-owned routers, so the targets are disposed by the player, not us.
    private sealed record EventSink(IntPtr Self, IntPtr Callback, IntPtr UserData);
    private EventSink? _eventSink;
    private int _endedFired;
    private MediaClock? _eventClock;
    private VideoPlayer? _eventVideo;
    private IntPtr _videoRouterHandle;
    private IntPtr _audioRouterHandle;

    private PlayerInstance(MediaPlayer player, PortAudioPlaybackHost? audioHost, TimeSpan duration)
    {
        _player = player;
        _audioHost = audioHost;
        _duration = duration;
        WireEvents();
    }

    // --- open paths ------------------------------------------------------------------------------

    /// <summary>
    /// Convenience open: wires the requested outputs itself. <paramref name="audioDeviceIndex"/> is a global
    /// PortAudio device index, <see cref="NativeApi.DefaultAudioDevice"/> for the default, or
    /// <see cref="NativeApi.NoAudioDevice"/> to leave audio unwired. Sides absent from the file are skipped.
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
                    title: Path.GetFileName(path),
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
            decoder?.Dispose();
            MediaDiagnostics.LogError(ex, "S.Media.Interop.PlayerInstance.TryOpen");
            return false;
        }
    }

    /// <summary>Graph open from a local file: no outputs wired — the host attaches its own via the routers.</summary>
    public static bool TryOpenFile(string path, out PlayerInstance? instance, out string? error) =>
        TryOpenGraph(MediaPlayer.OpenFile(path).WithOptions(MediaPlayerOpenOptions.Default), out instance, out error);

    /// <summary>Graph open from a URI (file:/http:/https:/rtsp: …).</summary>
    public static bool TryOpenUri(string uri, out PlayerInstance? instance, out string? error)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            instance = null;
            error = $"invalid absolute URI: {uri}";
            return false;
        }

        return TryOpenGraph(MediaPlayer.OpenUri(parsed).WithOptions(MediaPlayerOpenOptions.Default), out instance, out error);
    }

    /// <summary>Graph open from an in-memory byte buffer (copied; the host keeps ownership of its memory).</summary>
    public static bool TryOpenStream(ReadOnlySpan<byte> bytes, out PlayerInstance? instance, out string? error)
    {
        // Copy into a managed buffer so the decoder's AVIO can read it for the player's whole lifetime,
        // independent of the host's pointer.
        var stream = new MemoryStream(bytes.ToArray(), writable: false);
        if (TryOpenGraph(MediaPlayer.OpenStream(stream).WithOptions(MediaPlayerOpenOptions.Default), out instance, out error))
            return true;
        stream.Dispose();
        return false;
    }

    private static bool TryOpenGraph(MediaPlayerOpenBuilder builder, out PlayerInstance? instance, out string? error)
    {
        instance = null;
        try
        {
            if (!builder.TryBuild(out var player, out error))
                return false;
            instance = new PlayerInstance(player, audioHost: null, player.Duration);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            MediaDiagnostics.LogError(ex, "S.Media.Interop.PlayerInstance.TryOpenGraph");
            return false;
        }
    }

    // --- graph accessors (handles cached + freed on close) ---------------------------------------

    /// <summary>Pinned handle for the player's video router (borrowed — disposed by the player). Cached.</summary>
    public IntPtr GetVideoRouterHandle()
    {
        lock (_gate)
        {
            if (_disposed)
                return IntPtr.Zero;
            if (_videoRouterHandle == IntPtr.Zero)
                _videoRouterHandle = Handles.Alloc(_player.VideoRouter);
            return _videoRouterHandle;
        }
    }

    /// <summary>Pinned handle for the player's audio router, or zero when the player has none. Cached.</summary>
    public IntPtr GetAudioRouterHandle()
    {
        lock (_gate)
        {
            if (_disposed || _player.AudioRouter is null)
                return IntPtr.Zero;
            if (_audioRouterHandle == IntPtr.Zero)
                _audioRouterHandle = Handles.Alloc(_player.AudioRouter);
            return _audioRouterHandle;
        }
    }

    public string VideoRouterInputId => _player.VideoRouterInputId;
    public string? AudioSourceId => _player.AudioSourceId;
    public bool HasAudioRouter => _player.AudioRouter is not null;

    // --- events ----------------------------------------------------------------------------------

    /// <summary>Registers (or clears, when <paramref name="callback"/> is zero) the host event callback.
    /// <paramref name="self"/> is the player handle passed back to the callback.</summary>
    public void SetEventCallback(IntPtr self, IntPtr callback, IntPtr userData) =>
        Volatile.Write(ref _eventSink, callback == IntPtr.Zero ? null : new EventSink(self, callback, userData));

    private void WireEvents()
    {
        if (_player.PlayClock is MediaClock clock)
        {
            _eventClock = clock;
            clock.PositionChanged += OnClockPosition;
        }

        try
        {
            _eventVideo = _player.Video;
            _eventVideo.Faulted += OnVideoFaulted;
        }
        catch
        {
            _eventVideo = null;
        }
    }

    private unsafe void FireEvent(int type, long arg)
    {
        var sink = Volatile.Read(ref _eventSink);
        if (sink is null || sink.Callback == IntPtr.Zero)
            return;
        try
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, int, long, IntPtr, void>)sink.Callback)(
                sink.Self, type, arg, sink.UserData);
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "S.Media.Interop: host event callback threw");
        }
    }

    private void OnClockPosition(object? sender, TimeSpan pos)
    {
        FireEvent(NativeApi.EventPosition, pos.Ticks);
        if (Volatile.Read(ref _endedFired) != 0)
            return;
        var ended = !_player.IsLive
            && (_player.AudioRouter is { CompletedNaturally: true }
                || (_duration > TimeSpan.Zero && pos >= _duration - TimeSpan.FromMilliseconds(150)));
        if (ended && Interlocked.Exchange(ref _endedFired, 1) == 0)
            FireEvent(NativeApi.EventEnded, 0);
    }

    private void OnVideoFaulted(object? sender, VideoPlayerFaultedEventArgs e) =>
        FireEvent(NativeApi.EventFaulted, 0);

    // --- transport -------------------------------------------------------------------------------

    public void Play()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Exchange(ref _endedFired, 0);
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
            Interlocked.Exchange(ref _endedFired, 0);
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
        IntPtr videoRouterHandle, audioRouterHandle;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            Volatile.Write(ref _eventSink, null);
            if (_eventClock is { } clock)
                clock.PositionChanged -= OnClockPosition;
            if (_eventVideo is { } video)
                video.Faulted -= OnVideoFaulted;
            videoRouterHandle = _videoRouterHandle;
            audioRouterHandle = _audioRouterHandle;
            _videoRouterHandle = IntPtr.Zero;
            _audioRouterHandle = IntPtr.Zero;
            MediaDiagnostics.SwallowDisposeErrors(_player.Dispose, "S.Media.Interop.PlayerInstance.Dispose");
        }

        // Free the borrowed router handles (their targets are owned + disposed by the player above).
        Handles.Free(videoRouterHandle, dispose: false);
        Handles.Free(audioRouterHandle, dispose: false);
    }
}
