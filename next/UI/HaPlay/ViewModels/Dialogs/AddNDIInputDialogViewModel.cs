using System.Collections.ObjectModel;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Models;
using HaPlay.Resources;
using NDILib;
using S.Media.NDI;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>
/// Phase C.5 (§6.3) — "Add NDI input" dialog VM. Two paths to identify the source:
/// <list type="bullet">
/// <item>Live-discovered list — <see cref="NDIFinder"/> scans the network and fills a refreshing
/// dropdown. Manual <see cref="RefreshAsync"/> rescans on demand; <see cref="StartDiscoveryAsync"/>
/// kicks off a 1-Hz background poll while the dialog is open.</item>
/// <item>Manual name — free text. Save an item that may not resolve until later (camera powers up at
/// showtime, NDI bridge connects on cue, sender behind a VLAN joins later).</item>
/// </list>
/// Both paths produce the same <see cref="NDIInputPlaylistItem"/> on commit. Callers must invoke
/// <see cref="StopDiscovery"/> when the dialog closes so the background <see cref="NDIFinder"/>
/// instance is disposed cleanly.
/// </summary>
public partial class AddNDIInputDialogViewModel : ViewModelBase, IDisposable
{
    private Guid? _existingId;
    private NDIFinder? _finder;
    private DispatcherTimer? _discoveryTimer;

    [ObservableProperty] private string _displayName = Strings.NdiInputDefaultName;
    [ObservableProperty] private string _sourceName = string.Empty;
    [ObservableProperty] private string? _validationMessage;
    [ObservableProperty] private bool _useDiscovery = true;
    [ObservableProperty] private bool _lowBandwidth;
    [ObservableProperty] private bool _audioOnly;
    [ObservableProperty] private bool _videoOnly;
    [ObservableProperty] private int _retrySeconds = 5;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string? _discoveryStatus;

    /// <summary>Manual audio jitter-buffer override in ms; null = framework default (~50 ms).</summary>
    [ObservableProperty] private int? _audioMinBufferedDurationMs;
    [ObservableProperty] private bool _isProbingBuffer;
    [ObservableProperty] private string? _bufferProbeStatus;
    private CancellationTokenSource? _probeCts;

    /// <summary>Discovered sources. Updated on the UI thread by the discovery timer.</summary>
    public ObservableCollection<string> DiscoveredSources { get; } = new();

    [ObservableProperty] private string? _selectedDiscoveredSource;

    public bool IsEditing => _existingId is not null;
    public string DialogTitle => IsEditing ? Strings.EditNdiInputDialogTitle : Strings.AddNdiInputDialogTitle;
    public string PrimaryButtonLabel => IsEditing ? Strings.SaveButton : Strings.AddButton;

    public void LoadFromExisting(NDIInputPlaylistItem existing)
    {
        _existingId = existing.Id;
        DisplayName = existing.CustomDisplayName ?? existing.SourceName;
        SourceName = existing.SourceName;
        LowBandwidth = existing.LowBandwidth;
        AudioOnly = existing.AudioOnly;
        VideoOnly = existing.VideoOnly;
        RetrySeconds = existing.RetrySeconds;
        AudioMinBufferedDurationMs = existing.AudioMinBufferedDurationMs;
        // Default to manual-name when editing — the saved name is authoritative and shouldn't be silently
        // replaced by a discovery match with the same human label but a different transport address.
        UseDiscovery = false;

        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(PrimaryButtonLabel));
    }

    /// <summary>Open the NDI finder and start polling for sources. Safe to call multiple times — the
    /// timer is idempotent.</summary>
    public Task StartDiscoveryAsync()
    {
        if (_finder is null)
        {
            var rc = NDIFinder.Create(out var f, new NDIFinderSettings { ShowLocalSources = true });
            if (rc != 0 || f is null)
            {
                DiscoveryStatus = string.Format(
                    System.Globalization.CultureInfo.CurrentUICulture,
                    Strings.NdiDiscoveryUnavailableStatus,
                    rc);
                UseDiscovery = false;
                return Task.CompletedTask;
            }
            _finder = f;
        }

        // Snap an initial source list immediately so the combobox isn't empty for ~1 s.
        RefreshSnapshot();

        if (_discoveryTimer is null)
        {
            _discoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _discoveryTimer.Tick += OnDiscoveryTick;
            _discoveryTimer.Start();
        }

        return Task.CompletedTask;
    }

    /// <summary>Manual rescan — equivalent to a tick of the discovery timer.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task RefreshAsync()
    {
        IsScanning = true;
        DiscoveryStatus = Strings.NdiDiscoveryScanningStatus;
        try
        {
            await StartDiscoveryAsync().ConfigureAwait(false);
            RefreshSnapshot();
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>
    /// Probes the live network for the lowest glitch-free audio jitter-buffer size for the selected source
    /// (ramps from a safe reserve down to the floor — see <see cref="NdiAudioBufferProbe"/>), reports the
    /// lowest/balanced/safe presets, and sets the override to the measured floor. Runs off the UI thread with
    /// per-step progress; cancellable.
    /// </summary>
    [RelayCommand]
    private async Task ProbeBufferAsync()
    {
        var name = (UseDiscovery ? SelectedDiscoveredSource : SourceName)?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            BufferProbeStatus = "Pick or enter an NDI source first.";
            return;
        }
        if (IsProbingBuffer)
            return;

        IsProbingBuffer = true;
        BufferProbeStatus = "Probing… ramping buffer sizes down to your network's floor (a few seconds).";
        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;
        try
        {
            var presets = await Task.Run(() =>
            {
                var match = NDISource.Find(TimeSpan.FromSeconds(3))
                    .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
                if (match.Name is null)
                    return (NdiAudioBufferPresets?)null;

                return NdiAudioBufferProbe.Probe(
                    match,
                    onStep: (buf, underruns) => Dispatcher.UIThread.Post(() =>
                        BufferProbeStatus = $"{buf.TotalMilliseconds:0} ms → {(underruns == 0 ? "OK" : $"{underruns} underrun chunk(s)")}"),
                    cancellationToken: ct);
            }, ct).ConfigureAwait(true);

            if (presets is null)
            {
                BufferProbeStatus = $"Source '{name}' was not found on the network.";
                return;
            }
            if (!presets.Value.HasAudio)
            {
                BufferProbeStatus = "Source carries no audio — a buffer override isn't needed.";
                return;
            }

            var p = presets.Value;
            AudioMinBufferedDurationMs = (int)Math.Round(p.Lowest.TotalMilliseconds);
            BufferProbeStatus =
                $"Lowest {p.Lowest.TotalMilliseconds:0} ms · balanced {p.Balanced.TotalMilliseconds:0} ms · " +
                $"safe {p.Safe.TotalMilliseconds:0} ms — override set to lowest (raise it if you hear dropouts).";
        }
        catch (OperationCanceledException)
        {
            BufferProbeStatus = "Probe cancelled.";
        }
        catch (Exception ex)
        {
            BufferProbeStatus = $"Probe failed: {ex.Message}";
        }
        finally
        {
            IsProbingBuffer = false;
            _probeCts?.Dispose();
            _probeCts = null;
        }
    }

    [RelayCommand]
    private void CancelBufferProbe() => _probeCts?.Cancel();

    /// <summary>NumericUpDown-friendly (decimal?) view of the nullable-int override. Empty = framework default.</summary>
    public decimal? AudioBufferOverrideMs
    {
        get => AudioMinBufferedDurationMs;
        set => AudioMinBufferedDurationMs = value is null ? null : (int)Math.Round(value.Value);
    }

    partial void OnAudioMinBufferedDurationMsChanged(int? value) => OnPropertyChanged(nameof(AudioBufferOverrideMs));

    /// <summary>Dispose the underlying <see cref="NDIFinder"/> and stop polling. The dialog calls this
    /// from <c>finally</c> in <see cref="MediaPlayerViewModel.AddNDIInputAsync"/>.</summary>
    public void StopDiscovery()
    {
        _discoveryTimer?.Stop();
        _discoveryTimer = null;
        _finder?.Dispose();
        _finder = null;
        try { _probeCts?.Cancel(); } catch { /* best effort */ }
    }

    public void Dispose() => StopDiscovery();

    private void OnDiscoveryTick(object? sender, EventArgs e) => RefreshSnapshot();

    private void RefreshSnapshot()
    {
        if (_finder is null) return;
        try
        {
            var sources = _finder.GetCurrentSources();
            // Stable: only rebuild the collection when the visible set actually changes, so a user
            // mid-selection isn't bounced off the combobox by an identical re-fill.
            var newNames = sources.Select(s => s.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            var currentNames = DiscoveredSources.ToList();
            if (newNames.Count == currentNames.Count && newNames.SequenceEqual(currentNames, StringComparer.Ordinal))
                return;

            var prev = SelectedDiscoveredSource;
            DiscoveredSources.Clear();
            foreach (var n in newNames)
                DiscoveredSources.Add(n);
            SelectedDiscoveredSource = prev is { } p && newNames.Contains(p, StringComparer.Ordinal)
                ? p
                : DiscoveredSources.FirstOrDefault();
            DiscoveryStatus = newNames.Count == 0
                ? Strings.NdiDiscoveryNoSourcesStatus
                : string.Format(
                    System.Globalization.CultureInfo.CurrentUICulture,
                    Strings.NdiDiscoverySourceCountStatus,
                    newNames.Count);
        }
        catch (Exception ex)
        {
            DiscoveryStatus = string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                Strings.NdiDiscoveryScanFailedStatus,
                ex.Message);
        }
    }

    partial void OnSelectedDiscoveredSourceChanged(string? value)
    {
        if (UseDiscovery && !string.IsNullOrEmpty(value))
            SourceName = value;
    }

    partial void OnUseDiscoveryChanged(bool value)
    {
        if (value && SelectedDiscoveredSource is { } s && string.IsNullOrEmpty(SourceName))
            SourceName = s;
    }

    public NDIInputPlaylistItem? TryCommit()
    {
        ValidationMessage = null;
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ValidationMessage = Strings.ValidationDisplayNameRequired;
            return null;
        }

        var name = (UseDiscovery ? SelectedDiscoveredSource : SourceName)?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ValidationMessage = UseDiscovery
                ? Strings.ValidationDiscoveryPickOrManual
                : Strings.ValidationNdiSourceNameExample;
            return null;
        }

        if (AudioOnly && VideoOnly)
        {
            ValidationMessage = Strings.ValidationAudioOnlyAndVideoOnlyMutuallyExclusive;
            return null;
        }

        if (RetrySeconds is < 0 or > 600)
        {
            ValidationMessage = Strings.ValidationRetryIntervalRange;
            return null;
        }

        if (AudioMinBufferedDurationMs is < 0 or > 2000)
        {
            ValidationMessage = "Audio buffer override must be between 0 and 2000 ms.";
            return null;
        }

        return new NDIInputPlaylistItem(name)
        {
            Id = _existingId ?? Guid.NewGuid(),
            CustomDisplayName = string.Equals(DisplayName.Trim(), name, StringComparison.Ordinal)
                ? null
                : DisplayName.Trim(),
            LowBandwidth = LowBandwidth,
            AudioOnly = AudioOnly,
            VideoOnly = VideoOnly,
            RetrySeconds = RetrySeconds,
            AudioMinBufferedDurationMs = AudioMinBufferedDurationMs,
        };
    }
}
