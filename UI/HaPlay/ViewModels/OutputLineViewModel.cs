using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Models;

namespace HaPlay.ViewModels;

public partial class OutputLineViewModel : ViewModelBase
{
    private readonly Action<OutputLineViewModel> _requestRemove;
    private readonly OutputManagementViewModel? _host;

    public OutputLineViewModel(
        OutputDefinition definition,
        Action<OutputLineViewModel> requestRemove,
        OutputManagementViewModel? host = null)
    {
        Definition = definition;
        _requestRemove = requestRemove;
        _host = host;
    }

    public OutputDefinition Definition { get; }

    public bool SupportsMediaPlayerRouting =>
        Definition is not LocalVideoOutputDefinition v || v.Engine == VideoOutputEngine.SdlOpenGl;

    public bool IsLocalVideo => Definition is LocalVideoOutputDefinition;

    public bool IsNotLocalVideo => Definition is not LocalVideoOutputDefinition;

    [ObservableProperty]
    private bool _isPreviewRunning;

    partial void OnIsPreviewRunningChanged(bool value)
    {
        StartPreviewCommand.NotifyCanExecuteChanged();
        StopPreviewCommand.NotifyCanExecuteChanged();
        FullscreenPreviewCommand.NotifyCanExecuteChanged();
        WindowedPreviewCommand.NotifyCanExecuteChanged();
    }

    public string KindLabel => Definition.Kind switch
    {
        ManagedOutputKind.PortAudio => "PortAudio",
        ManagedOutputKind.NDI => "NDI",
        ManagedOutputKind.SdlOpenGlVideo => "SDL3 OpenGL",
        ManagedOutputKind.AvaloniaOpenGlVideo => "Avalonia OpenGL",
        _ => Definition.Kind.ToString(),
    };

    public string Summary => Definition switch
    {
        PortAudioOutputDefinition p =>
            $"{p.DeviceName} · {p.ChannelCount} ch @ {p.SampleRate} Hz · API {p.HostApiName}",
        LocalVideoOutputDefinition v =>
            $"{v.Engine} · {(v.SurfaceMode == VideoSurfaceMode.FullScreen ? "Fullscreen" : "Window")} · screen {v.ScreenIndex}"
            + (v.SurfaceMode == VideoSurfaceMode.Windowed && v.WindowWidth is { } w && v.WindowHeight is { } h
                ? $" · {w}×{h}"
                : ""),
        NDIOutputDefinition n => n.StreamMode switch
        {
            NDIOutputStreamMode.VideoOnly => $"“{n.SourceName}” · video only",
            NDIOutputStreamMode.AudioOnly =>
                $"“{n.SourceName}” · audio only · {n.AudioChannelCount} ch @ {n.AudioSampleRate} Hz",
            _ =>
                $"“{n.SourceName}” · video+audio · NDI audio {n.AudioChannelCount} ch @ {n.AudioSampleRate} Hz",
        },
        _ => Definition.DisplayName,
    };

    [RelayCommand(CanExecute = nameof(CanStartPreview))]
    private Task StartPreviewAsync(CancellationToken cancellationToken) =>
        IsLocalVideo && _host is not null
            ? _host.StartLocalPreviewAsync(this, cancellationToken)
            : Task.CompletedTask;

    private bool CanStartPreview() =>
        !IsPreviewRunning && IsLocalVideo && _host is not null;

    [RelayCommand(CanExecute = nameof(CanStopPreview))]
    private void StopPreview() => _host?.StopLocalPreview(this);

    private bool CanStopPreview() => IsPreviewRunning && IsLocalVideo && _host is not null;

    [RelayCommand(CanExecute = nameof(CanFullscreenPreview))]
    private void FullscreenPreview() => _host?.SetLocalPreviewFullscreen(this, true);

    private bool CanFullscreenPreview() => IsPreviewRunning && IsLocalVideo && _host is not null;

    [RelayCommand(CanExecute = nameof(CanWindowedPreview))]
    private void WindowedPreview() => _host?.SetLocalPreviewFullscreen(this, false);

    private bool CanWindowedPreview() => IsPreviewRunning && IsLocalVideo && _host is not null;

    [RelayCommand]
    private void Remove() => _requestRemove(this);
}
