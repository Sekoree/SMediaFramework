using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaViz.Core;
using Microsoft.Extensions.Logging;
using S.Media.Present.SDL3;

namespace HaViz.Desktop.ViewModels;

/// <summary>
/// Desktop head v1: the "GUI NDIVisualizer" - visualizer→NDI only. The engine/playlist logic all
/// lives in HaViz.Core; this head supplies the SDL3 offscreen GL factory and the UI. The mini
/// player (framework MediaPlayer) and PortAudio line-in capture the csproj describes are the next
/// increments - until then audio reactivity comes from whatever the host pushes via NDI receivers
/// probing, i.e. the visuals idle-animate without PCM.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(static b => b.AddConsole());
    private readonly DispatcherTimer _statusTimer;
    private VizNdiEngine? _engine;
    private bool _disposed;

    public MainViewModel()
    {
        PresetDirectory = ResolvePresetDirectory() ?? "";
        _statusTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => RefreshEngineStatus());
    }

    [ObservableProperty]
    private string _ndiName = "HaViz";

    [ObservableProperty]
    private int _outputWidth = 1280;

    [ObservableProperty]
    private int _outputHeight = 720;

    [ObservableProperty]
    private int _outputFps = 60;

    [ObservableProperty]
    private int _presetDurationSeconds = 30;

    [ObservableProperty]
    private bool _shufflePresets = true;

    [ObservableProperty]
    private double _beatSensitivity = 1.0;

    [ObservableProperty]
    private string _presetDirectory = "";

    [ObservableProperty]
    private bool _isEngineRunning;

    [ObservableProperty]
    private string _statusText = "stopped";

    [ObservableProperty]
    private string _presetText = "";

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
            _statusTimer.Start();
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
        _statusTimer.Stop();
        _engine?.Dispose();
        _engine = null;
        IsEngineRunning = false;
        StatusText = "stopped";
        PresetText = "";
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopEngine();
        _loggerFactory.Dispose();
    }
}
