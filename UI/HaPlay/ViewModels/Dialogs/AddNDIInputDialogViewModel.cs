using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using HaPlay.Resources;
using NDILib;

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

    /// <summary>Dispose the underlying <see cref="NDIFinder"/> and stop polling. The dialog calls this
    /// from <c>finally</c> in <see cref="MediaPlayerViewModel.AddNDIInputAsync"/>.</summary>
    public void StopDiscovery()
    {
        _discoveryTimer?.Stop();
        _discoveryTimer = null;
        _finder?.Dispose();
        _finder = null;
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
        };
    }
}
