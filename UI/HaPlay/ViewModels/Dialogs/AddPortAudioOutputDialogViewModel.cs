using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using HaPlay.Resources;
using S.Media.Core.Audio;
using S.Media.PortAudio;

namespace HaPlay.ViewModels.Dialogs;

public sealed record AudioBackendChoice(string Name, string DisplayName, IAudioBackend Backend);

public sealed record AudioOutputDeviceChoice(
    string? DeviceId,
    string Name,
    int MaxOutputChannels,
    double DefaultSampleRate,
    bool IsDefault,
    PortAudioOutputDeviceEntry? PortAudioDevice = null)
{
    public string DisplayName => IsDefault ? $"{Name} (default)" : Name;

    public int EffectiveMaxOutputChannels => Math.Max(1, MaxOutputChannels);
}

public partial class AddPortAudioOutputDialogViewModel : ViewModelBase
{
    /// <summary>
    /// When non-null, the dialog is editing an existing line (§3.2 — Edit reuses the same dialog as Add).
    /// On <see cref="TryCommit"/> we keep this Id so playback references survive the edit.
    /// </summary>
    private Guid? _existingId;
    private bool _suppressBackendReload;
    private IReadOnlyCollection<string> _existingOutputNames = Array.Empty<string>();

    [ObservableProperty] private string _displayName = Strings.MainSpeakersDefaultName;
    [ObservableProperty] private string? _validationMessage;

    public ObservableCollection<AudioBackendChoice> Backends { get; } = new();
    public ObservableCollection<PortAudioHostApiEntry> HostApis { get; } = new();
    public ObservableCollection<AudioOutputDeviceChoice> Devices { get; } = new();

    [ObservableProperty] private AudioBackendChoice? _selectedBackend;
    [ObservableProperty] private PortAudioHostApiEntry? _selectedHostApi;
    [ObservableProperty] private AudioOutputDeviceChoice? _selectedDevice;
    [ObservableProperty] private int _channelCount = 2;
    [ObservableProperty] private int _sampleRate = 48000;

    /// <summary>True when the dialog is editing an existing line. Title bar uses this to show "Edit X" vs "Add X".</summary>
    public bool IsEditing => _existingId is not null;

    public bool IsPortAudioBackend =>
        SelectedBackend is null || IsPortAudioBackendName(SelectedBackend.Name);

    public string DialogTitle => IsEditing ? Strings.EditPortAudioOutputDialogTitle : Strings.AddPortAudioOutputDialogTitle;

    public string PrimaryButtonLabel => IsEditing ? Strings.SaveButton : Strings.AddButton;

    public void InitializeExistingOutputNames(IEnumerable<string> names)
    {
        var set = OutputNameUniqueness.CreateNameSet(names);
        _existingOutputNames = set;
        if (!IsEditing)
            DisplayName = OutputNameUniqueness.MakeUniqueDefaultName(DisplayName, set);
    }

    public void ReloadBackends()
    {
        var selectedName = SelectedBackend?.Name;
        Backends.Clear();
        foreach (var backend in DiscoverBackends())
            Backends.Add(new AudioBackendChoice(backend.Name, BackendDisplayName(backend), backend));

        var selected =
            Backends.FirstOrDefault(b => string.Equals(b.Name, selectedName, StringComparison.OrdinalIgnoreCase))
            ?? Backends.FirstOrDefault(b => IsPortAudioBackendName(b.Name))
            ?? Backends.FirstOrDefault();

        _suppressBackendReload = true;
        SelectedBackend = selected;
        _suppressBackendReload = false;
        OnPropertyChanged(nameof(IsPortAudioBackend));
    }

    public void ReloadHostApis()
    {
        if (Backends.Count == 0)
            ReloadBackends();

        ReloadDevicesForSelectedBackend();
    }

    /// <summary>Pre-populate the dialog from <paramref name="existing"/> so the user sees current values.</summary>
    public void LoadFromExisting(PortAudioOutputDefinition existing)
    {
        _existingId = existing.Id;
        DisplayName = existing.DisplayName;

        ReloadBackends();
        var backend = Backends.FirstOrDefault(b =>
            string.Equals(b.Name, existing.EffectiveAudioBackendName, StringComparison.OrdinalIgnoreCase))
            ?? Backends.FirstOrDefault(b => IsPortAudioBackendName(b.Name))
            ?? Backends.FirstOrDefault();

        _suppressBackendReload = true;
        SelectedBackend = backend;
        _suppressBackendReload = false;
        OnPropertyChanged(nameof(IsPortAudioBackend));
        ReloadDevicesForSelectedBackend();

        if (existing.UsesPortAudioBackend)
        {
            var hostMatch = HostApis
                .Where(h => string.Equals(h.Name, existing.HostApiName, StringComparison.OrdinalIgnoreCase))
                .Cast<PortAudioHostApiEntry?>()
                .FirstOrDefault()
                ?? HostApis.Where(h => h.Index == existing.HostApiIndex)
                    .Cast<PortAudioHostApiEntry?>()
                    .FirstOrDefault();
            if (hostMatch is not null)
                SelectedHostApi = hostMatch;

            var deviceMatch = Devices
                .Where(d => string.Equals(d.Name, existing.DeviceName, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault()
                ?? Devices.FirstOrDefault(d => d.PortAudioDevice?.GlobalDeviceIndex == existing.GlobalDeviceIndex);
            if (deviceMatch is not null)
                SelectedDevice = deviceMatch;
        }
        else
        {
            var deviceMatch = Devices.FirstOrDefault(d =>
                    string.Equals(d.DeviceId, existing.AudioBackendDeviceId, StringComparison.OrdinalIgnoreCase))
                ?? Devices.FirstOrDefault(d =>
                    string.Equals(d.Name, existing.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (deviceMatch is not null)
            {
                SelectedDevice = deviceMatch;
            }
            else if (!string.IsNullOrWhiteSpace(existing.DeviceName) || !string.IsNullOrWhiteSpace(existing.AudioBackendDeviceId))
            {
                var saved = new AudioOutputDeviceChoice(
                    existing.AudioBackendDeviceId,
                    string.IsNullOrWhiteSpace(existing.DeviceName) ? "Saved device" : existing.DeviceName,
                    Math.Max(1, existing.ChannelCount),
                    existing.SampleRate,
                    IsDefault: false);
                Devices.Add(saved);
                SelectedDevice = saved;
            }
        }

        ChannelCount = existing.ChannelCount;
        SampleRate = existing.SampleRate;

        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(PrimaryButtonLabel));
    }

    partial void OnSelectedBackendChanged(AudioBackendChoice? value)
    {
        OnPropertyChanged(nameof(IsPortAudioBackend));
        if (!_suppressBackendReload)
            ReloadDevicesForSelectedBackend();
    }

    partial void OnSelectedHostApiChanged(PortAudioHostApiEntry? value)
    {
        if (IsPortAudioBackend)
            ReloadDevices();
    }

    partial void OnSelectedDeviceChanged(AudioOutputDeviceChoice? value)
    {
        if (value is null)
            return;
        ChannelCount = Math.Clamp(ChannelCount, 1, value.EffectiveMaxOutputChannels);
        // Only auto-snap the sample rate during the Add flow — when editing, preserve the saved value
        // so a device swap doesn't silently reset the user's chosen rate.
        if (!IsEditing)
        {
            var sr = (int)Math.Round(value.DefaultSampleRate, MidpointRounding.AwayFromZero);
            if (sr > 0)
                SampleRate = sr;
        }
    }

    private void ReloadDevicesForSelectedBackend()
    {
        if (SelectedBackend is null)
        {
            HostApis.Clear();
            Devices.Clear();
            SelectedHostApi = null;
            SelectedDevice = null;
            return;
        }

        if (IsPortAudioBackend)
            ReloadPortAudioHostApis();
        else
        {
            HostApis.Clear();
            SelectedHostApi = null;
            ReloadDevices();
        }
    }

    private void ReloadPortAudioHostApis()
    {
        var previousHost = SelectedHostApi?.Index;
        HostApis.Clear();
        foreach (var h in PortAudioDeviceCatalog.EnumerateHostApis())
            HostApis.Add(h);
        SelectedHostApi =
            HostApis.Cast<PortAudioHostApiEntry?>().FirstOrDefault(h => h?.Index == previousHost)
            ?? HostApis.Cast<PortAudioHostApiEntry?>().FirstOrDefault();
        ReloadDevices();
    }

    private void ReloadDevices()
    {
        ValidationMessage = null;
        var previousDeviceId = SelectedDevice?.DeviceId;
        Devices.Clear();

        if (SelectedBackend is null)
        {
            SelectedDevice = null;
            return;
        }

        if (IsPortAudioBackend)
        {
            var host = SelectedHostApi?.Index;
            foreach (var d in PortAudioDeviceCatalog.EnumerateOutputDevices(host))
            {
                Devices.Add(new AudioOutputDeviceChoice(
                    d.GlobalDeviceIndex.ToString(CultureInfo.InvariantCulture),
                    d.Name,
                    d.MaxOutputChannels,
                    d.DefaultSampleRate,
                    IsDefault: false,
                    PortAudioDevice: d));
            }
        }
        else
        {
            Devices.Add(new AudioOutputDeviceChoice(
                null,
                "System default",
                MaxOutputChannels: 64,
                DefaultSampleRate: 48000,
                IsDefault: true));

            try
            {
                foreach (var d in SelectedBackend.Backend.EnumerateOutputDevices())
                {
                    Devices.Add(new AudioOutputDeviceChoice(
                        d.Id,
                        d.Name,
                        d.MaxChannels,
                        d.DefaultSampleRate,
                        d.IsDefault));
                }
            }
            catch (Exception ex)
            {
                ValidationMessage = ex.Message;
            }
        }

        SelectedDevice =
            Devices.FirstOrDefault(d => string.Equals(d.DeviceId, previousDeviceId, StringComparison.OrdinalIgnoreCase))
            ?? Devices.FirstOrDefault(d => d.IsDefault)
            ?? Devices.FirstOrDefault();
    }

    public PortAudioOutputDefinition? TryCommit()
    {
        ValidationMessage = null;
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ValidationMessage = Strings.ValidationDisplayNameRequired;
            return null;
        }
        var displayName = DisplayName.Trim();
        if (OutputNameUniqueness.TryFindDuplicate(displayName, _existingOutputNames, out var duplicateName))
        {
            ValidationMessage = Strings.Format(nameof(Strings.ValidationOutputNameAlreadyExistsFormat), duplicateName);
            return null;
        }

        if (SelectedBackend is null || SelectedDevice is null)
        {
            ValidationMessage = Strings.ValidationSelectHostApiAndOutputDevice;
            return null;
        }

        if (ChannelCount < 1 || ChannelCount > SelectedDevice.EffectiveMaxOutputChannels)
        {
            ValidationMessage = string.Format(
                CultureInfo.CurrentUICulture,
                Strings.ValidationChannelCountRangeForDevice,
                SelectedDevice.EffectiveMaxOutputChannels);
            return null;
        }

        if (SampleRate is < 8000 or > 192_000)
        {
            ValidationMessage = Strings.ValidationSampleRateInvalid;
            return null;
        }

        if (IsPortAudioBackend)
        {
            if (SelectedHostApi is null || SelectedDevice.PortAudioDevice is not { } portDevice)
            {
                ValidationMessage = Strings.ValidationSelectHostApiAndOutputDevice;
                return null;
            }

            return new PortAudioOutputDefinition(
                _existingId ?? Guid.NewGuid(),
                displayName,
                SelectedHostApi.Value.Index,
                SelectedHostApi.Value.Name,
                portDevice.GlobalDeviceIndex,
                portDevice.Name,
                ChannelCount,
                SampleRate,
                PortAudioOutputDefinition.PortAudioBackendName,
                portDevice.GlobalDeviceIndex.ToString(CultureInfo.InvariantCulture));
        }

        return new PortAudioOutputDefinition(
            _existingId ?? Guid.NewGuid(),
            displayName,
            HostApiIndex: -1,
            HostApiName: SelectedBackend.Name,
            GlobalDeviceIndex: -1,
            DeviceName: SelectedDevice.Name,
            ChannelCount,
            SampleRate,
            AudioBackendName: SelectedBackend.Name,
            AudioBackendDeviceId: SelectedDevice.DeviceId);
    }

    private static IReadOnlyList<IAudioBackend> DiscoverBackends()
    {
        var registered = AudioBackends.All;
        if (registered.Count > 0)
            return registered;

        // Unit tests instantiate the dialog without running App.InitializeMediaFramework. Keep the legacy
        // PortAudio path visible in that case; real app startup registers every available backend.
        return [new PortAudioBackend()];
    }

    private static bool IsPortAudioBackendName(string name) =>
        string.Equals(name, PortAudioOutputDefinition.PortAudioBackendName, StringComparison.OrdinalIgnoreCase);

    private static string BackendDisplayName(IAudioBackend backend) =>
        IsPortAudioBackendName(backend.Name) ? PortAudioOutputDefinition.PortAudioBackendName : backend.Name;
}
