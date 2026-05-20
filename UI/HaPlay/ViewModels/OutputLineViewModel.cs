using Avalonia;
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

    public OutputDefinition Definition { get; private set; }

    /// <summary>Phase A — swaps the definition in place after the runtime is reconfigured (§9.6).
    /// Notifies derived UI bindings (kind label / summary / VM-derived booleans) so the line refreshes.
    /// Only the management VM calls this; everything else treats <see cref="Definition"/> as read-only.</summary>
    internal void ReplaceDefinition(OutputDefinition newDefinition)
    {
        Definition = newDefinition;
        OnPropertyChanged(nameof(Definition));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsLocalVideo));
        OnPropertyChanged(nameof(IsNotLocalVideo));
        OnPropertyChanged(nameof(IsClone));
        OnPropertyChanged(nameof(SupportsMediaPlayerRouting));
        OnPropertyChanged(nameof(IndentMargin));
        OnPropertyChanged(nameof(CloneParentLabel));
    }

    /// <summary>True for top-level lines (non-clones). Per-player routing UI hides clones from the
    /// checkbox list because their selection is mirrored from the parent (§3.4 PlayerRoutingMirror).</summary>
    public bool SupportsMediaPlayerRouting => !IsClone;

    public bool IsLocalVideo => Definition is LocalVideoOutputDefinition;

    public bool IsNotLocalVideo => Definition is not LocalVideoOutputDefinition;

    /// <summary>True when this line is a clone of another local-video line (§3.4).</summary>
    public bool IsClone =>
        Definition is LocalVideoOutputDefinition lv && lv.CloneOfId is not null;

    /// <summary>Indent depth for the Outputs view tree. Phase B caps at 1 (no clone-of-clones).</summary>
    public Thickness IndentMargin => IsClone ? new Thickness(24, 4, 0, 4) : new Thickness(0, 4, 0, 4);

    /// <summary>Display name of this clone's parent, or null if not a clone or parent is gone.
    /// Used as a sub-label in the Outputs view to reinforce the nesting visually.</summary>
    public string? CloneParentLabel
    {
        get
        {
            if (Definition is not LocalVideoOutputDefinition { CloneOfId: { } parentId } || _host is null)
                return null;
            var parent = _host.Outputs.FirstOrDefault(o => o.Definition.Id == parentId);
            return parent is null ? "(parent missing)" : $"clone of {parent.Definition.DisplayName}";
        }
    }

    [ObservableProperty]
    private bool _isPreviewRunning;

    partial void OnIsPreviewRunningChanged(bool value)
    {
        StartPreviewCommand.NotifyCanExecuteChanged();
        StopPreviewCommand.NotifyCanExecuteChanged();
        FullscreenPreviewCommand.NotifyCanExecuteChanged();
        WindowedPreviewCommand.NotifyCanExecuteChanged();
    }

    /// <summary>User-visible kind label (§12.3). Technical names (SDL3 / Avalonia / PortAudio) live in
    /// <see cref="KindTechnicalLabel"/> and surface as a tooltip / subtitle in the Outputs view.</summary>
    public string KindLabel => Definition.Kind switch
    {
        ManagedOutputKind.PortAudio => "Local audio",
        ManagedOutputKind.NDI => "NDI program",
        ManagedOutputKind.SdlOpenGlVideo => "Standalone window",
        ManagedOutputKind.AvaloniaOpenGlVideo => "In-app preview",
        _ => Definition.Kind.ToString(),
    };

    public string KindTechnicalLabel => Definition.Kind switch
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

    /// <summary>Phase B (§3.2) — open the Edit dialog. Delegates to the management VM so the dialog
    /// can be opened with the correct owner window and the right per-kind form.</summary>
    [RelayCommand]
    private Task EditAsync(CancellationToken cancellationToken) =>
        _host?.EditLineAsync(this, cancellationToken) ?? Task.CompletedTask;
}
