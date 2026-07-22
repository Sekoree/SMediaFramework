using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaViz.Core;
using HaViz.Desktop.Capture;
using HaViz.Desktop.Playback;
using Microsoft.Extensions.Logging;
using S.Media.Audio.PortAudio;
using S.Media.Core.Registry;
using S.Media.Decode.FFmpeg;
using S.Media.Present.SDL3;
using System.Collections.ObjectModel;

namespace HaViz.Desktop.ViewModels;

/// <summary>Output-device combo entry; Id null = backend default.</summary>
public sealed record OutputDeviceChoice(string? Id, string Display)
{
    public override string ToString() => Display;
}

/// <summary>Input-device combo entry wrapping the PortAudio catalog row.</summary>
public sealed record InputDeviceChoice(PortAudioInputDeviceEntry Entry)
{
    public override string ToString() => $"{Entry.Name} ({Entry.MaxInputChannels} in)";
}

/// <summary>One selectable input channel (1-based label, 0-based index into the frame).</summary>
public sealed partial class ChannelChoice(int index) : ObservableObject
{
    public int Index { get; } = index;

    public string Label { get; } = $"{index + 1}";

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Desktop head: the "GUI NDIVisualizer" - engine lifecycle plus the two PCM feeds, the mini
/// player (framework MediaPlayer: FFmpeg decode -> audible output + viz tap) and line-in capture
/// (PortAudio input with per-channel selection). The two feeds are mutually exclusive: both drive
/// the same engine, and two unrelated PCM streams garble the visuals and the NDI audio clock.
/// All engine/player/capture control runs on the UI thread; PCM lands on decoder/capture threads.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".wma", ".mka",
        ".mp4", ".mkv", ".webm", ".mov",
    };

    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(static b => b.AddConsole());
    private readonly DispatcherTimer _statusTimer;
    private readonly PlaylistController _playlist = new();
    private readonly IMediaRegistry? _registry;
    private readonly DesktopMiniPlayer? _player;
    private readonly LineInCapture _capture = new();
    private VizNdiEngine? _engine;
    private bool _disposed;
    // A session is loaded in the player (Resume is meaningful); false after Stop/playlist end.
    private bool _hasLoadedTrack;
    // Guard against a permanently broken playlist: error -> advance can loop forever.
    private const int MaxConsecutivePlaybackErrors = 3;
    private int _consecutivePlaybackErrors;

    public MainViewModel()
    {
        PresetDirectory = ResolvePresetDirectory() ?? "";

        try
        {
            _registry = MediaRegistry.Build(b => b
                .Use(new FFmpegModule())
                .Use(new PortAudioModule()));
            var backend = _registry.AudioBackends.FirstOrDefault();
            if (backend is not null)
            {
                _player = new DesktopMiniPlayer(_registry, backend, SubmitPcm);
                _player.TrackStarted += track =>
                {
                    // The file opened and the stream started - the error streak is over.
                    _consecutivePlaybackErrors = 0;
                    CurrentTrackName = track.DisplayName;
                    IsPlaying = true;
                };
                _player.PlaybackEnded += OnTrackEnded;
                _player.PlaybackError += OnPlaybackError;
            }
            else
            {
                PlayerStatus = "player unavailable: no audio backend (PortAudio missing?)";
            }
        }
        catch (Exception ex)
        {
            PlayerStatus = $"player unavailable: {ex.Message}";
        }

        _capture.Faulted += message => Dispatcher.UIThread.Post(() =>
        {
            _capture.Stop();
            IsCapturing = false;
            CaptureStatus = $"capture failed: {message}";
        });

        RefreshOutputDevices();
        RefreshInputDevices();

        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => OnStatusTick());
        _statusTimer.Start();
    }

    // --- NDI / visualizer settings (editable while stopped) ---
    [ObservableProperty] private string _ndiName = "HaViz";
    [ObservableProperty] private int _outputWidth = 1280;
    [ObservableProperty] private int _outputHeight = 720;
    [ObservableProperty] private int _outputFps = 60;
    [ObservableProperty] private int _presetDurationSeconds = 30;
    [ObservableProperty] private bool _shufflePresets = true;
    [ObservableProperty] private double _beatSensitivity = 1.0;
    [ObservableProperty] private string _presetDirectory = "";

    // --- engine status ---
    [ObservableProperty] private bool _isEngineRunning;
    [ObservableProperty] private string _statusText = "stopped";
    [ObservableProperty] private string _presetText = "";

    // --- player ---
    [ObservableProperty] private string _currentTrackName = "(no track)";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private string _loopModeLabel = "Loop: All";
    [ObservableProperty] private string _playlistSummary = "no folder selected";
    [ObservableProperty] private string _playerStatus = "";
    [ObservableProperty] private OutputDeviceChoice? _selectedOutputDevice;

    // --- line-in capture ---
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private string _captureStatus = "Line-in capture is off.";
    [ObservableProperty] private InputDeviceChoice? _selectedInputDevice;

    public ObservableCollection<OutputDeviceChoice> OutputDevices { get; } = [];

    public ObservableCollection<InputDeviceChoice> InputDevices { get; } = [];

    public ObservableCollection<ChannelChoice> InputChannels { get; } = [];

    /// <summary>App-local preset bundle first (deployed copies are self-contained; the csproj
    /// stages External/projectm/linux-x64/presets next to the executable), then the repo dev
    /// tree for uncopied IDE runs. Null = no pack found (projectM idles on its builtin preset).</summary>
    internal static string? ResolvePresetDirectory()
    {
        var appLocal = Path.Combine(AppContext.BaseDirectory, "presets");
        if (Directory.Exists(appLocal))
            return appLocal;
        var dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../External/projectm/linux-x64/presets"));
        return Directory.Exists(dev) ? dev : null;
    }

    // --- PCM sink (decoder/capture threads) ---
    private void SubmitPcm(ReadOnlySpan<float> interleaved, int sampleRate, int channels) =>
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
                PresetDirectory = string.IsNullOrWhiteSpace(PresetDirectory) ? null : PresetDirectory,
                PresetDurationSeconds = PresetDurationSeconds,
                ShufflePresets = ShufflePresets,
                BeatSensitivity = BeatSensitivity,
            };
            _engine = new VizNdiEngine(settings, SDL3OffscreenGlContext.TryCreate, _loggerFactory);
            _engine.Faulted += ex => Dispatcher.UIThread.Post(() =>
                StatusText = $"engine faulted: {ex.Message} — stop and start again");
            _engine.Start();
            IsEngineRunning = true;
            StatusText = $"sending '{settings.NdiName}' {settings.Width}x{settings.Height}@{settings.Fps}";
        }
        catch (Exception ex)
        {
            _engine?.Dispose();
            _engine = null;
            StatusText = $"start failed: {ex.Message}";
        }
    }

    private void StopEngine()
    {
        _engine?.Dispose();
        _engine = null;
        IsEngineRunning = false;
        StatusText = "stopped";
        PresetText = "";
    }

    private void OnStatusTick()
    {
        _player?.Poll();
        RefreshEngineStatus();
    }

    private void RefreshEngineStatus()
    {
        if (_engine is not { } engine || !IsEngineRunning)
            return;
        var droppedAudio = engine.DroppedMismatchedRateFrames > 0
            ? $" · NDI audio muted (rate mismatch, {engine.DroppedMismatchedRateFrames} frames)"
            : "";
        PresetText = engine.VisualizerFailed
            ? "visualizer unavailable (GL/projectM failed)"
            : $"{engine.ConnectionCount} rx · f{engine.FramesSent} tx{engine.AverageSubmitMs}ms y{engine.LastFrameLuma} · {engine.CurrentPresetName ?? "(loading)"}{droppedAudio}";
    }

    [RelayCommand]
    private void NextPreset() => _engine?.RequestNextPreset();

    // --- player ---
    public void LoadMusicFolder(string folder)
    {
        List<TrackInfo> tracks;
        try
        {
            tracks = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => MediaExtensions.Contains(Path.GetExtension(f)))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(f => new TrackInfo(f, Path.GetFileNameWithoutExtension(f)))
                .ToList();
        }
        catch (Exception ex)
        {
            PlayerStatus = $"scan failed: {ex.Message}";
            return;
        }

        if (tracks.Count == 0)
        {
            PlaylistSummary = "no media files in folder";
            return;
        }

        _playlist.SetTracks(tracks);
        PlaylistSummary = $"{tracks.Count} track(s)";
        PlayTrack(_playlist.Next());
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_player is null)
            return;
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

        // Nothing loaded (fresh start or after Stop): start the current/first track instead.
        PlayTrack(_playlist.Current ?? _playlist.Next());
    }

    [RelayCommand]
    private void NextTrack() => PlayTrack(_playlist.Next());

    [RelayCommand]
    private void PreviousTrack() => PlayTrack(_playlist.Previous());

    [RelayCommand]
    private void StopPlayback()
    {
        _player?.Stop();
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
            _player?.Stop();
            _hasLoadedTrack = false;
            IsPlaying = false;
            CurrentTrackName = "(no track)";
            return;
        }

        PlayTrack(next);
    }

    private void OnPlaybackError(string message)
    {
        if (++_consecutivePlaybackErrors >= MaxConsecutivePlaybackErrors)
        {
            // Loop One/All over broken files would error-skip forever - give up instead.
            _consecutivePlaybackErrors = 0;
            StopPlayback();
            PlayerStatus = "playback stopped after repeated errors";
            return;
        }

        PlayerStatus = $"player: {message}";
        OnTrackEnded(); // error-skip so one broken file doesn't stall the playlist
    }

    private void PlayTrack(TrackInfo? track)
    {
        if (_player is null || track is null)
            return;
        StopCaptureForPlayback();
        PlayerStatus = "";
        _player.Play(track);
        // TrackStarted set the display state; a synchronous PlaybackError left the player empty.
        _hasLoadedTrack = _player.HasTrack;
        IsPlaying = _player.HasTrack;
    }

    private void RefreshOutputDevices()
    {
        OutputDevices.Clear();
        OutputDevices.Add(new OutputDeviceChoice(null, "System default"));
        if (_player is not null)
        {
            foreach (var device in _player.GetOutputDevices())
                OutputDevices.Add(new OutputDeviceChoice(device.Id, device.Name));
        }

        SelectedOutputDevice = OutputDevices[0];
    }

    partial void OnSelectedOutputDeviceChanged(OutputDeviceChoice? value) =>
        _player?.SetOutputDevice(value?.Id); // applies from the next track

    // --- line-in capture ---
    private void RefreshInputDevices()
    {
        InputDevices.Clear();
        foreach (var entry in LineInCapture.EnumerateDevices())
            InputDevices.Add(new InputDeviceChoice(entry));
        SelectedInputDevice = InputDevices.FirstOrDefault(d => d.Entry.IsDefault) ?? InputDevices.FirstOrDefault();
        if (InputDevices.Count == 0)
            CaptureStatus = "No input devices found (PortAudio missing or no hardware).";
    }

    partial void OnSelectedInputDeviceChanged(InputDeviceChoice? value)
    {
        InputChannels.Clear();
        if (value is null)
            return;
        for (var i = 0; i < value.Entry.MaxInputChannels; i++)
            InputChannels.Add(new ChannelChoice(i) { IsSelected = i < 2 }); // default: first stereo pair
    }

    [RelayCommand]
    private void ToggleCapture()
    {
        if (_capture.IsCapturing)
        {
            _capture.Stop();
            IsCapturing = false;
            CaptureStatus = "Line-in capture is off.";
            return;
        }

        if (SelectedInputDevice is not { } device)
            return;
        var channels = InputChannels.Where(c => c.IsSelected).Select(c => c.Index).ToArray();
        if (channels.Length == 0)
        {
            CaptureStatus = "Select at least one input channel.";
            return;
        }

        // Capture and the player are mutually exclusive engine feeds.
        if (IsPlaying || _hasLoadedTrack)
            StopPlayback();

        try
        {
            _capture.Start(device.Entry, channels, SubmitPcm);
            IsCapturing = true;
            CaptureStatus = $"Capturing ch {string.Join("+", channels.Select(c => c + 1))} of '{device.Entry.Name}' → visualizer + NDI.";
        }
        catch (Exception ex)
        {
            CaptureStatus = $"capture failed: {ex.Message}";
        }
    }

    /// <summary>Every playback start goes through here (the feeds are mutually exclusive).</summary>
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
        if (_disposed)
            return;
        _disposed = true;
        _statusTimer.Stop();
        _capture.Dispose();
        _player?.Dispose();
        (_registry as IDisposable)?.Dispose();
        StopEngine();
        _loggerFactory.Dispose();
    }
}
