using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using HaPlay.Resources;
using S.Media.Audio.PortAudio;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>
/// Phase C.5 (§6.4) — "Add PortAudio input" dialog VM. Mirrors
/// <see cref="AddPortAudioOutputDialogViewModel"/> but enumerates capture devices and produces a
/// <see cref="PortAudioInputPlaylistItem"/> on commit. Edit-existing is supported so a saved item's
/// device choice can be re-bound after a USB swap (§6.4 device-by-name + index-fallback rule).
/// </summary>
public partial class AddPortAudioInputDialogViewModel : ViewModelBase
{
    private Guid? _existingId;
    private PlaylistItem? _existingItem;

    [ObservableProperty] private string _displayName = Strings.MicrophoneDefaultName;
    [ObservableProperty] private string? _validationMessage;

    public ObservableCollection<PortAudioHostApiEntry> HostApis { get; } = new();
    public ObservableCollection<PortAudioInputDeviceEntry> Devices { get; } = new();

    [ObservableProperty] private PortAudioHostApiEntry? _selectedHostApi;
    [ObservableProperty] private PortAudioInputDeviceEntry? _selectedDevice;
    [ObservableProperty] private int _channelCount = 2;
    [ObservableProperty] private int _sampleRate = 48000;

    public bool IsEditing => _existingId is not null;
    public string DialogTitle => IsEditing ? Strings.EditPortAudioInputDialogTitle : Strings.AddPortAudioInputDialogTitle;
    public string PrimaryButtonLabel => IsEditing ? Strings.SaveButton : Strings.AddButton;

    public void ReloadHostApis()
    {
        HostApis.Clear();
        if (!RuntimeModules.IsPortAudioAvailable)
        {
            // Opening the dialog on a machine without the portaudio native library must not crash;
            // leave the pickers empty and say why.
            ValidationMessage = RuntimeModules.PortAudioUnavailableReason;
            SelectedHostApi = null;
            return;
        }
        foreach (var h in PortAudioDeviceCatalog.EnumerateHostApis())
            HostApis.Add(h);
        SelectedHostApi = HostApis.FirstOrDefault();
    }

    /// <summary>Pre-populate the dialog from an existing <see cref="PortAudioInputPlaylistItem"/>.</summary>
    public void LoadFromExisting(PortAudioInputPlaylistItem existing)
    {
        _existingId = existing.Id;
        _existingItem = existing;
        DisplayName = existing.CustomDisplayName ?? existing.DeviceName;

        ReloadHostApis();
        if (existing.HostApiIndex is { } hostIdx)
        {
            var hostMatch = HostApis.Where(h => h.Index == hostIdx).Cast<PortAudioHostApiEntry?>().FirstOrDefault();
            if (hostMatch is not null)
                SelectedHostApi = hostMatch;
        }

        // Match by device name first (§6.4 — name match survives USB port renumbering); then fall back to
        // the global index if the name is no longer enumerable.
        PortAudioInputDeviceEntry? deviceMatch =
            Devices.Where(d => string.Equals(d.Name, existing.DeviceName, StringComparison.OrdinalIgnoreCase))
                .Cast<PortAudioInputDeviceEntry?>().FirstOrDefault()
            ?? (existing.GlobalDeviceIndex is { } gi
                ? Devices.Where(d => d.GlobalDeviceIndex == gi).Cast<PortAudioInputDeviceEntry?>().FirstOrDefault()
                : null);
        if (deviceMatch is not null)
            SelectedDevice = deviceMatch;

        ChannelCount = existing.Channels;
        SampleRate = existing.SampleRate;

        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(PrimaryButtonLabel));
    }

    partial void OnSelectedHostApiChanged(PortAudioHostApiEntry? value) => ReloadDevices();

    partial void OnSelectedDeviceChanged(PortAudioInputDeviceEntry? value)
    {
        if (!value.HasValue) return;
        var d = value.GetValueOrDefault();
        ChannelCount = Math.Clamp(ChannelCount, 1, Math.Max(1, d.MaxInputChannels));
        if (!IsEditing)
        {
            var sr = (int)Math.Round(d.DefaultSampleRate, MidpointRounding.AwayFromZero);
            if (sr > 0) SampleRate = sr;
        }
    }

    private void ReloadDevices()
    {
        Devices.Clear();
        if (!RuntimeModules.IsPortAudioAvailable)
        {
            SelectedDevice = null;
            return;
        }
        var host = SelectedHostApi?.Index;
        foreach (var d in PortAudioDeviceCatalog.EnumerateInputDevices(host))
            Devices.Add(d);
        SelectedDevice = Devices.FirstOrDefault();
    }

    public PortAudioInputPlaylistItem? TryCommit()
    {
        ValidationMessage = null;
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ValidationMessage = Strings.ValidationDisplayNameRequired;
            return null;
        }

        if (SelectedHostApi is null || SelectedDevice is null)
        {
            ValidationMessage = Strings.ValidationSelectHostApiAndInputDevice;
            return null;
        }

        var dev = SelectedDevice.Value;
        if (ChannelCount < 1 || ChannelCount > dev.MaxInputChannels)
        {
            ValidationMessage = string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                Strings.ValidationChannelCountRangeForDevice,
                dev.MaxInputChannels);
            return null;
        }

        if (SampleRate is < 8000 or > 192_000)
        {
            ValidationMessage = Strings.ValidationSampleRateInvalid;
            return null;
        }

        return new PortAudioInputPlaylistItem(dev.Name)
        {
            Id = _existingId ?? Guid.NewGuid(),
            CustomDisplayName = string.Equals(DisplayName.Trim(), dev.Name, StringComparison.Ordinal)
                ? null
                : DisplayName.Trim(),
            HostApiName = SelectedHostApi.Value.Name,
            HostApiIndex = SelectedHostApi.Value.Index,
            GlobalDeviceIndex = dev.GlobalDeviceIndex,
            Channels = ChannelCount,
            SampleRate = SampleRate,
            SuggestedLatency = dev.DefaultLowInputLatency,
        };
    }
}
