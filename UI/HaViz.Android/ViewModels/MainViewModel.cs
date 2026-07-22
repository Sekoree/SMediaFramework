using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaViz.Android.Platform;
using HaViz.Android.Services;
using HaViz.Core;
using System.Collections.ObjectModel;

namespace HaViz.Android.ViewModels;

/// <summary>
/// The single-screen state: NDI/visualizer engine lifecycle, the mini player (playlist +
/// output routing), and system-audio capture. All engine PCM flows through <see cref="SubmitPcm"/>
/// (called on decoder/capture threads); everything else runs on the UI thread.
/// </summary>
public partial class MainViewModel : ObservableObject, IPcmSink, IDisposable
{
    private readonly PlatformServices _platform;
    private readonly PlaylistController _playlist = new();
    private readonly IMiniPlayer _player;
    private readonly IMediaFolderScanner _scanner;
    private readonly ISystemAudioCapture _capture;
    private readonly DispatcherTimer _statusTimer;
    private VizNdiEngine? _engine;
    // A session is loaded in the player (Resume is meaningful); false after Stop/playlist end.
    private bool _hasLoadedTrack;
    // Guard against a permanently broken playlist: error -> advance can loop forever.
    private const int MaxConsecutivePlaybackErrors = 3;
    private int _consecutivePlaybackErrors;
    // True while RefreshOutputDevices preselects: see OnSelectedOutputDeviceChanged.
    private bool _suppressOutputRouting;

    // --- NDI / visualizer settings (editable while stopped) ---
    [ObservableProperty] private string _ndiName = "HaViz";
    [ObservableProperty] private int _outputWidth = 1280;
    [ObservableProperty] private int _outputHeight = 720;
    [ObservableProperty] private int _outputFps = 30;
    [ObservableProperty] private double _presetDurationSeconds = 15;
    [ObservableProperty] private bool _shufflePresets = true;
    [ObservableProperty] private double _beatSensitivity = 0.5;

    // --- status ---
    [ObservableProperty] private bool _isEngineRunning;
    [ObservableProperty] private string _statusText = "stopped";
    [ObservableProperty] private string _presetText = "";

    // --- player ---
    [ObservableProperty] private string _currentTrackName = "(no track)";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _loopModeLabel = "Loop: All";
    [ObservableProperty] private string _playlistSummary = "no folder selected";
    [ObservableProperty] private AudioOutputDeviceInfo? _selectedOutputDevice;
    [ObservableProperty] private bool _playOnDevice; // default off: the box only feeds NDI

    // --- capture ---
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private string _captureStatus = "System-audio capture is off. Apps that block capture stay silent.";

    public ObservableCollection<AudioOutputDeviceInfo> OutputDevices { get; } = [];

    public MainViewModel(PlatformServices platform)
    {
        _platform = platform;
        _player = new MediaCodecMiniPlayer(platform.Activity, this);
        _scanner = new SafFolderScanner(platform.Activity);
        _capture = new SystemAudioCapture(platform.Activity, this);

        _player.TrackStarted += track => Dispatcher.UIThread.Post(() =>
        {
            // Extractor + codec opened, so the track is playable - the error streak is over.
            _consecutivePlaybackErrors = 0;
            CurrentTrackName = track.DisplayName;
            IsPlaying = true;
        });
        _player.PlaybackEnded += () => Dispatcher.UIThread.Post(OnTrackEnded);
        _player.PlaybackError += message => Dispatcher.UIThread.Post(() =>
        {
            if (++_consecutivePlaybackErrors >= MaxConsecutivePlaybackErrors)
            {
                // Loop One/All over broken files would error-skip forever - give up instead.
                _consecutivePlaybackErrors = 0;
                StopPlayback();
                StatusText = "playback stopped after repeated errors";
                return;
            }

            StatusText = $"player: {message}";
            OnTrackEnded(); // error-skip so one broken file doesn't stall the playlist
        });
        _capture.Stopped += () => Dispatcher.UIThread.Post(() =>
        {
            // Externally ended capture (projection revoked, system kill) must reach the UI; our
            // own Stop() paths update IsCapturing first, so this only fires for surprises.
            if (!IsCapturing)
                return;
            IsCapturing = false;
            CaptureStatus = "Capture stopped (projection revoked or service ended).";
        });

        RefreshOutputDevices();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshEngineStatus();
        _statusTimer.Start();
    }

    // --- IPcmSink (decoder/capture threads) ---
    public void SubmitPcm(ReadOnlySpan<float> interleaved, int sampleRate, int channels) =>
        _engine?.SubmitPcm(interleaved, sampleRate, channels);

    // --- engine ---
    [RelayCommand]
    private void ToggleEngine()
    {
        if (_engine is null)
            StartEngine();
        else
            StopEngine();
    }

    private void StartEngine()
    {
        try
        {
            var settings = new VizNdiSettings
            {
                NdiName = NdiName,
                Width = OutputWidth,
                Height = OutputHeight,
                Fps = OutputFps,
                PresetDirectory = _platform.PresetDirectory,
                PresetDurationSeconds = PresetDurationSeconds,
                ShufflePresets = ShufflePresets,
                BeatSensitivity = BeatSensitivity,
            };
            _platform.AcquireMulticastLock();
            _engine = new VizNdiEngine(settings, EglOffscreenGlContext.TryCreate, _platform.LoggerFactory);
            // Raised on the pump thread; marshal to the UI thread. A faulted engine is a zombie
            // (frames stop, IsRunning may still read true) - surface it instead of freezing the
            // status line on stale counters.
            _engine.Faulted += ex => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"engine faulted: {ex.Message} — stop and start again";
            });
            _engine.Start();
            IsEngineRunning = true;
            StatusText = $"sending '{settings.NdiName}' {settings.Width}x{settings.Height}@{settings.Fps}";
        }
        catch (Exception ex)
        {
            _engine?.Dispose();
            _engine = null;
            _platform.ReleaseMulticastLock();
            StatusText = $"start failed: {ex.Message}";
        }
    }

    private void StopEngine()
    {
        _engine?.Dispose();
        _engine = null;
        _platform.ReleaseMulticastLock();
        IsEngineRunning = false;
        StatusText = "stopped";
        PresetText = "";
    }

    private void RefreshEngineStatus()
    {
        if (_engine is not { } engine || !IsEngineRunning)
            return;
        var preset = engine.CurrentPresetName;
        // Mismatched-rate audio is dropped from the NDI stream (still drives visuals) - receivers
        // hear silence with no other hint, so the counter must be visible.
        var droppedAudio = engine.DroppedMismatchedRateFrames > 0
            ? $" · NDI audio muted (rate mismatch, {engine.DroppedMismatchedRateFrames} frames)"
            : "";
        PresetText = engine.VisualizerFailed
            ? "visualizer unavailable (GL/projectM failed)"
            : $"{engine.ConnectionCount} rx · f{engine.FramesSent} miss{engine.PollsWithoutFrame} tx{engine.AverageSubmitMs}ms y{engine.LastFrameLuma} · {preset ?? "(loading)"}{droppedAudio}";
    }

    [RelayCommand]
    private void NextPreset() => _engine?.RequestNextPreset();

    // --- player ---
    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var tracks = await _scanner.PickAndScanAsync();
        if (tracks.Count == 0)
            return;
        _playlist.SetTracks(tracks);
        PlaylistSummary = $"{tracks.Count} track(s)";
        PlayTrack(_playlist.Next());
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
            return;
        }

        if (_hasLoadedTrack)
        {
            StopCaptureForPlayback();
            _player.Resume();
            IsPlaying = true;
            return;
        }

        // Nothing loaded (fresh start or after Stop): Resume() on a dead session would only flip
        // the flag with no audio - start the current/first track instead.
        PlayTrack(_playlist.Current ?? _playlist.Next());
    }

    [RelayCommand]
    private void NextTrack() => PlayTrack(_playlist.Next());

    [RelayCommand]
    private void PreviousTrack() => PlayTrack(_playlist.Previous());

    [RelayCommand]
    private void StopPlayback()
    {
        _player.Stop();
        _hasLoadedTrack = false;
        IsPlaying = false;
        CurrentTrackName = "(no track)";
    }

    [RelayCommand]
    private void CycleLoopMode()
    {
        _playlist.LoopMode = _playlist.LoopMode switch
        {
            LoopMode.All => LoopMode.One,
            LoopMode.One => LoopMode.Off,
            _ => LoopMode.All,
        };
        LoopModeLabel = $"Loop: {_playlist.LoopMode}";
    }

    private void OnTrackEnded()
    {
        var next = _playlist.AdvanceAfterTrackEnd();
        if (next is null)
        {
            _hasLoadedTrack = false;
            IsPlaying = false;
            CurrentTrackName = "(no track)";
            return;
        }

        PlayTrack(next);
    }

    private void PlayTrack(TrackInfo? track)
    {
        if (track is null)
            return;
        StopCaptureForPlayback();
        _player.Play(track);
        _hasLoadedTrack = true;
        CurrentTrackName = track.DisplayName;
        IsPlaying = true;
    }

    private void RefreshOutputDevices()
    {
        OutputDevices.Clear();
        foreach (var device in _player.GetOutputDevices())
            OutputDevices.Add(device);
        // Display-only preselection: it must not call SetOutputDevice and override Android's
        // default routing before the user explicitly picks a device.
        _suppressOutputRouting = true;
        SelectedOutputDevice = OutputDevices.FirstOrDefault();
        _suppressOutputRouting = false;
    }

    partial void OnSelectedOutputDeviceChanged(AudioOutputDeviceInfo? value)
    {
        if (!_suppressOutputRouting)
            _player.SetOutputDevice(value?.Id);
    }

    partial void OnPlayOnDeviceChanged(bool value) => _player.SetLocalOutputEnabled(value);

    // --- capture ---
    [RelayCommand]
    private async Task ToggleCaptureAsync()
    {
        if (_capture.IsCapturing)
        {
            _capture.Stop();
            IsCapturing = false;
            CaptureStatus = "System-audio capture is off. Apps that block capture stay silent.";
            return;
        }

        // Capture and the player are mutually exclusive: both feed the same engine, and two
        // unrelated PCM streams garble the visuals and overrun the NDI audio clock.
        var playerStopped = IsPlaying || _hasLoadedTrack;
        if (playerStopped)
            StopPlayback();

        CaptureStatus = "requesting capture permission…";
        var started = await _capture.StartAsync();
        if (started && IsPlaying)
        {
            // The player was restarted while the consent dialog was up - playback wins.
            _capture.Stop();
            CaptureStatus = "Capture stopped: the player took over the engine feed.";
            return;
        }

        IsCapturing = started;
        CaptureStatus = started
            ? playerStopped
                ? "Capturing system audio → visualizer + NDI. Player stopped."
                : "Capturing system audio → visualizer + NDI."
            : "Capture not started (denied or unsupported on this device).";
    }

    /// <summary>Capture and the player are mutually exclusive engine feeds (see
    /// <see cref="ToggleCaptureAsync"/>); every playback start goes through here.</summary>
    private void StopCaptureForPlayback()
    {
        if (!_capture.IsCapturing)
            return;
        _capture.Stop();
        IsCapturing = false;
        CaptureStatus = "Capture stopped: the player took over the engine feed.";
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        StopEngine();
        _player.Dispose();
        _capture.Dispose();
    }
}
