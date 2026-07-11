using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Resources;

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

    /// <summary>Phase A - swaps the definition in place after the runtime is reconfigured (§9.6).
    /// Notifies derived UI bindings (kind label / summary / VM-derived booleans) so the line refreshes.
    /// Only the management VM calls this; everything else treats <see cref="Definition"/> as read-only.</summary>
    internal void ReplaceDefinition(OutputDefinition newDefinition)
    {
        Definition = newDefinition;
        OnPropertyChanged(nameof(Definition));
        OnPropertyChanged(nameof(EffectiveName));
        OnPropertyChanged(nameof(NameEdit));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsLocalVideo));
        OnPropertyChanged(nameof(IsNotLocalVideo));
        OnPropertyChanged(nameof(IsNDI));
        OnPropertyChanged(nameof(IsNotNDI));
        OnPropertyChanged(nameof(IsFileRecord));
        OnPropertyChanged(nameof(IsLiveStream));
        OnPropertyChanged(nameof(IsArmableOutput));
        OnPropertyChanged(nameof(RecordToggleLabel));
        OnPropertyChanged(nameof(IsClone));
        OnPropertyChanged(nameof(SupportsMediaPlayerRouting));
        OnPropertyChanged(nameof(IndentMargin));
        OnPropertyChanged(nameof(CloneParentLabel));
    }

    /// <summary>The name shown everywhere for this output: the operator alias when set, else the
    /// device-derived display name (UI rewrite P2, plan §5).</summary>
    public string EffectiveName => Definition.EffectiveName;

    /// <summary>
    /// Editable name cell. Shows <see cref="EffectiveName"/>; committing a different value stores it
    /// as the alias. Committing blank - or the original device name - clears the alias (falls back
    /// to <see cref="OutputDefinition.DisplayName"/>).
    /// </summary>
    public string NameEdit
    {
        get => Definition.EffectiveName;
        set
        {
            var trimmed = value?.Trim();
            var newAlias = string.IsNullOrEmpty(trimmed) || trimmed == Definition.DisplayName ? null : trimmed;
            if (newAlias == Definition.Alias)
                return;
            ReplaceDefinition(Definition with { Alias = newAlias });
            _host?.NotifyAliasChanged(this);
        }
    }

    /// <summary>True for top-level lines (non-clones). Per-player routing UI hides clones from the
    /// checkbox list because their selection is mirrored from the parent (§3.4 PlayerRoutingMirror).</summary>
    public bool SupportsMediaPlayerRouting => !IsClone;

    public bool IsLocalVideo => Definition is LocalVideoOutputDefinition;

    public bool IsNotLocalVideo => Definition is not LocalVideoOutputDefinition;

    public bool IsNDI => Definition is NDIOutputDefinition;

    public bool IsNotNDI => Definition is not NDIOutputDefinition;

    public bool IsFileRecord => Definition is FileOutputDefinition;

    public bool IsLiveStream => Definition is LiveStreamOutputDefinition;

    /// <summary>True for the armable encode kinds (file record / live stream) - shows the toggle button.</summary>
    public bool IsArmableOutput => IsFileRecord || IsLiveStream;

    /// <summary>True while the line's encode session is armed (recording a file / streaming live).
    /// Set by <see cref="OutputManagementViewModel"/> after arm/disarm completes.</summary>
    [ObservableProperty]
    private bool _isRecordArmed;

    /// <summary>The armed session's destination - the recording file path, or the LAN URLs while live.</summary>
    [ObservableProperty]
    private string? _recordFilePath;

    [RelayCommand(CanExecute = nameof(CanToggleRecord))]
    private Task ToggleRecord() =>
        _host?.SetFileRecordArmedAsync(this, !IsRecordArmed) ?? Task.CompletedTask;

    private bool CanToggleRecord() => IsArmableOutput && _host is not null;

    public string RecordToggleLabel => (IsLiveStream, IsRecordArmed) switch
    {
        (true, true) => Strings.StopStreamButton,
        (true, false) => Strings.GoLiveButton,
        (false, true) => Strings.RecordDisarmButton,
        _ => Strings.RecordArmButton,
    };

    partial void OnIsRecordArmedChanged(bool value)
    {
        _ = value;
        OnPropertyChanged(nameof(RecordToggleLabel));
    }

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
            return parent is null
                ? Strings.CloneParentMissingLabel
                : Strings.Format(nameof(Strings.CloneOfDisplayNameFormat), parent.Definition.DisplayName);
        }
    }

    [ObservableProperty]
    private bool _isPreviewRunning;

    [ObservableProperty]
    private OutputLineHealthState _health = OutputLineHealthState.Unknown;

    [ObservableProperty]
    private string? _healthDetail;

    /// <summary>Phase E (§8.1) - rolling ring of recent throughput samples (frames + audio chunks
    /// per refresh tick). 60 entries × 1 s ticks = 1 minute window. The view binds to this list to
    /// render an inline sparkline; <see cref="SparklinePeakSample"/> auto-scales the Y axis.</summary>
    public const int SparklineCapacity = 60;

    private readonly double[] _sparklineSamples = new double[SparklineCapacity];
    private int _sparklineCount;
    private int _sparklineHead;
    private long _lastVideoSubmittedTotal;
    private long _lastAudioEnqueuedTotal;

    /// <summary>Current sparkline snapshot in oldest→newest order. Cheap to materialize - the buffer
    /// is small and the view re-binds whenever <see cref="SparklineRevision"/> changes.</summary>
    public IReadOnlyList<double> SparklineSamples
    {
        get
        {
            if (_sparklineCount == 0) return Array.Empty<double>();
            var result = new double[_sparklineCount];
            var start = (_sparklineHead - _sparklineCount + SparklineCapacity) % SparklineCapacity;
            for (var i = 0; i < _sparklineCount; i++)
                result[i] = _sparklineSamples[(start + i) % SparklineCapacity];
            return result;
        }
    }

    /// <summary>Peak observed sample within the current window - used as the Y-axis scale.</summary>
    [ObservableProperty]
    private double _sparklinePeakSample;

    /// <summary>Increment that the view watches so the sparkline redraws on each new tick. Cheaper
    /// than rebinding the list itself for a 60-entry buffer.</summary>
    [ObservableProperty]
    private int _sparklineRevision;

    /// <summary>Throughput sample for the latest tick (frames + chunks per second).</summary>
    [ObservableProperty]
    private double _sparklineLastSample;

    /// <summary>Phase E (§8.1) - push one tick's worth of pump deltas into the ring. Caller passes
    /// the raw cumulative counters; the line VM subtracts the previous values so the ring stores
    /// per-second deltas (with a refresh-tick wall of 1 s the value is samples-per-second too).</summary>
    public void RecordSparklineSample(long videoSubmittedTotal, long audioEnqueuedTotal)
    {
        var videoDelta = Math.Max(0, videoSubmittedTotal - _lastVideoSubmittedTotal);
        var audioDelta = Math.Max(0, audioEnqueuedTotal - _lastAudioEnqueuedTotal);
        _lastVideoSubmittedTotal = videoSubmittedTotal;
        _lastAudioEnqueuedTotal = audioEnqueuedTotal;

        var sample = (double)(videoDelta + audioDelta);
        _sparklineSamples[_sparklineHead] = sample;
        _sparklineHead = (_sparklineHead + 1) % SparklineCapacity;
        if (_sparklineCount < SparklineCapacity)
            _sparklineCount++;

        // Recompute peak from the live ring (O(n) but n=60).
        var peak = 0.0;
        for (var i = 0; i < _sparklineCount; i++)
            if (_sparklineSamples[i] > peak)
                peak = _sparklineSamples[i];

        SparklineLastSample = sample;
        SparklinePeakSample = peak;
        SparklineRevision++;
        OnPropertyChanged(nameof(SparklineSamples));
    }

    /// <summary>Clear the ring (called when a session closes so the next session starts fresh).</summary>
    public void ResetSparkline()
    {
        Array.Clear(_sparklineSamples);
        _sparklineCount = 0;
        _sparklineHead = 0;
        _lastVideoSubmittedTotal = 0;
        _lastAudioEnqueuedTotal = 0;
        SparklineLastSample = 0;
        SparklinePeakSample = 0;
        SparklineRevision++;
        OnPropertyChanged(nameof(SparklineSamples));
    }

    public string HealthColor => Health switch
    {
        OutputLineHealthState.Healthy => "#4CAF50",
        OutputLineHealthState.Warning => "#FFC107",
        OutputLineHealthState.Error => "#E53935",
        _ => "#666666",
    };

    public string HealthToolTip => Health switch
    {
        OutputLineHealthState.Healthy => HealthDetail ?? Strings.OutputHealthyTooltip,
        OutputLineHealthState.Warning => HealthDetail ?? Strings.OutputWarningTooltip,
        OutputLineHealthState.Error => HealthDetail ?? Strings.OutputErrorTooltip,
        _ => HealthDetail ?? Strings.OutputIdleTooltip,
    };

    partial void OnHealthChanged(OutputLineHealthState value)
    {
        OnPropertyChanged(nameof(HealthColor));
        OnPropertyChanged(nameof(HealthToolTip));
    }

    partial void OnHealthDetailChanged(string? value) => OnPropertyChanged(nameof(HealthToolTip));

    /// <summary>UI rewrite P2 (plan §5): compact live throughput numbers next to the sparkline -
    /// cumulative video frames + audio chunks delivered through this line. Empty while unwired.</summary>
    [ObservableProperty]
    private string? _statsSummary;

    partial void OnIsPreviewRunningChanged(bool value)
    {
        _ = value;
        FullscreenPreviewCommand.NotifyCanExecuteChanged();
        WindowedPreviewCommand.NotifyCanExecuteChanged();
    }

    /// <summary>User-visible kind label (§12.3). Technical names (SDL3 / Avalonia / PortAudio) live in
    /// <see cref="KindTechnicalLabel"/> and surface as a tooltip / subtitle in the Outputs view.</summary>
    public string KindLabel => Definition.Kind switch
    {
        ManagedOutputKind.PortAudio => Strings.OutputKindLocalAudioLabel,
        ManagedOutputKind.NDI => Strings.OutputKindNDIProgramLabel,
        ManagedOutputKind.SDLOpenGlVideo => Strings.EngineStandaloneWindowLabel,
        ManagedOutputKind.AvaloniaOpenGlVideo => Strings.EngineInAppPreviewLabel,
        ManagedOutputKind.FileRecord => Strings.OutputKindFileRecordLabel,
        ManagedOutputKind.LiveStream => Strings.OutputKindLiveStreamLabel,
        _ => Definition.Kind.ToString(),
    };

    public string KindTechnicalLabel => Definition switch
    {
        PortAudioOutputDefinition p => p.EffectiveAudioBackendName,
        NDIOutputDefinition => Strings.OutputKindTechnicalNDI,
        LocalVideoOutputDefinition { Engine: VideoOutputEngine.SDLOpenGl } => Strings.OutputKindTechnicalSDLOpenGl,
        LocalVideoOutputDefinition => Strings.OutputKindTechnicalAvaloniaOpenGl,
        FileOutputDefinition => Strings.OutputKindTechnicalFileRecord,
        LiveStreamOutputDefinition => Strings.OutputKindTechnicalLiveStream,
        _ => Definition.Kind.ToString(),
    };

    public string Summary => Definition switch
        {
        PortAudioOutputDefinition p =>
            Strings.Format(nameof(Strings.OutputSummaryPortAudioFormat), p.DeviceName, p.ChannelCount, p.SampleRate, p.EffectiveAudioBackendName),
        LocalVideoOutputDefinition v =>
            Strings.Format(
                nameof(Strings.OutputSummaryLocalVideoBaseFormat),
                v.Engine,
                v.SurfaceMode == VideoSurfaceMode.FullScreen ? Strings.FullscreenLabel : Strings.WindowLabel,
                v.ScreenIndex)
            + (v.SurfaceMode == VideoSurfaceMode.Windowed && v.WindowWidth is { } w && v.WindowHeight is { } h
                ? Strings.Format(nameof(Strings.OutputSummaryWindowSizeFormat), w, h)
                : ""),
        NDIOutputDefinition n => n.StreamMode switch
        {
            NDIOutputStreamMode.VideoOnly => Strings.Format(nameof(Strings.OutputSummaryNDIVideoOnlyFormat), n.SourceName),
            NDIOutputStreamMode.AudioOnly =>
                Strings.Format(nameof(Strings.OutputSummaryNDIAudioOnlyFormat), n.SourceName, n.AudioChannelCount, n.AudioSampleRate),
            _ =>
                Strings.Format(nameof(Strings.OutputSummaryNDIVideoAudioFormat), n.SourceName, n.AudioChannelCount, n.AudioSampleRate),
        },
        FileOutputDefinition f =>
            Strings.Format(
                nameof(Strings.OutputSummaryFileRecordFormat),
                f.EffectiveEncode.Container,
                f.EffectiveEncode.VideoCodec,
                f.EffectiveEncode.AudioLegs.Count > 0 ? f.EffectiveEncode.AudioLegs[0].Codec : "-",
                f.DirectoryPath),
        LiveStreamOutputDefinition s =>
            Strings.Format(
                nameof(Strings.OutputSummaryLiveStreamFormat),
                s.PushTargets.Count,
                s.EffectiveLocalServer.Enabled ? $":{s.EffectiveLocalServer.Port}" : "off"),
        _ => Definition.DisplayName,
    };

    [RelayCommand(CanExecute = nameof(CanFullscreenPreview))]
    private void FullscreenPreview() => _host?.SetLocalPreviewFullscreen(this, true);

    private bool CanFullscreenPreview() => IsPreviewRunning && IsLocalVideo && _host is not null;

    [RelayCommand(CanExecute = nameof(CanWindowedPreview))]
    private void WindowedPreview() => _host?.SetLocalPreviewFullscreen(this, false);

    private bool CanWindowedPreview() => IsPreviewRunning && IsLocalVideo && _host is not null;

    [RelayCommand]
    private async Task RemoveAsync()
    {
        // Route through the management VM so it can warn + offer to stop when a player is actively playing
        // through this line (removing mid-playback otherwise races a live submit and crashes). The host-less
        // path (cue editor / tests) removes directly.
        if (_host is not null)
            await _host.RemoveLineAsync(this);
        else
            _requestRemove(this);
    }

    /// <summary>Phase B (§3.2) - open the Edit dialog. Delegates to the management VM so the dialog
    /// can be opened with the correct owner window and the right per-kind form.</summary>
    [RelayCommand]
    private Task EditAsync(CancellationToken cancellationToken) =>
        _host?.EditLineAsync(this, cancellationToken) ?? Task.CompletedTask;
}
