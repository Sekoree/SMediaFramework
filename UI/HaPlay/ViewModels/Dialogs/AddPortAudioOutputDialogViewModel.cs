using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using S.Media.PortAudio;

namespace HaPlay.ViewModels.Dialogs;

public partial class AddPortAudioOutputDialogViewModel : ViewModelBase
{
    /// <summary>
    /// When non-null, the dialog is editing an existing line (§3.2 — Edit reuses the same dialog as Add).
    /// On <see cref="TryCommit"/> we keep this Id so playback references survive the edit.
    /// </summary>
    private Guid? _existingId;

    [ObservableProperty] private string _displayName = "Main speakers";
    [ObservableProperty] private string? _validationMessage;

    public ObservableCollection<PortAudioHostApiEntry> HostApis { get; } = new();
    public ObservableCollection<PortAudioOutputDeviceEntry> Devices { get; } = new();

    [ObservableProperty] private PortAudioHostApiEntry? _selectedHostApi;
    [ObservableProperty] private PortAudioOutputDeviceEntry? _selectedDevice;
    [ObservableProperty] private int _channelCount = 2;
    [ObservableProperty] private int _sampleRate = 48000;

    /// <summary>True when the dialog is editing an existing line. Title bar uses this to show "Edit X" vs "Add X".</summary>
    public bool IsEditing => _existingId is not null;

    public string DialogTitle => IsEditing ? "Edit PortAudio output" : "Add PortAudio output";

    public string PrimaryButtonLabel => IsEditing ? "Save" : "Add";

    public void ReloadHostApis()
    {
        HostApis.Clear();
        foreach (var h in PortAudioDeviceCatalog.EnumerateHostApis())
            HostApis.Add(h);
        SelectedHostApi = HostApis.FirstOrDefault();
    }

    /// <summary>Pre-populate the dialog from <paramref name="existing"/> so the user sees current values.</summary>
    public void LoadFromExisting(PortAudioOutputDefinition existing)
    {
        _existingId = existing.Id;
        DisplayName = existing.DisplayName;

        ReloadHostApis();
        // PortAudioHostApiEntry / PortAudioOutputDeviceEntry are value-type structs, so FirstOrDefault
        // returns a zero-init struct on no-match (not null). Match by index, fall back to whatever's
        // current if the saved device is no longer enumerable.
        var hostMatch = HostApis.Where(h => h.Index == existing.HostApiIndex).Cast<PortAudioHostApiEntry?>().FirstOrDefault();
        if (hostMatch is not null)
            SelectedHostApi = hostMatch;

        // ReloadDevices is invoked by OnSelectedHostApiChanged; pick the matching device after that runs.
        var deviceMatch = Devices.Where(d => d.GlobalDeviceIndex == existing.GlobalDeviceIndex)
            .Cast<PortAudioOutputDeviceEntry?>().FirstOrDefault();
        if (deviceMatch is not null)
            SelectedDevice = deviceMatch;

        ChannelCount = existing.ChannelCount;
        SampleRate = existing.SampleRate;

        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(PrimaryButtonLabel));
    }

    partial void OnSelectedHostApiChanged(PortAudioHostApiEntry? value) => ReloadDevices();

    partial void OnSelectedDeviceChanged(PortAudioOutputDeviceEntry? value)
    {
        if (!value.HasValue)
            return;
        var d = value.GetValueOrDefault();
        ChannelCount = Math.Clamp(ChannelCount, 1, Math.Max(1, d.MaxOutputChannels));
        // Only auto-snap the sample rate during the Add flow — when editing, preserve the saved value
        // so a device swap doesn't silently reset the user's chosen rate.
        if (!IsEditing)
        {
            var sr = (int)Math.Round(d.DefaultSampleRate, MidpointRounding.AwayFromZero);
            if (sr > 0)
                SampleRate = sr;
        }
    }

    private void ReloadDevices()
    {
        Devices.Clear();
        var host = SelectedHostApi?.Index;
        foreach (var d in PortAudioDeviceCatalog.EnumerateOutputDevices(host))
            Devices.Add(d);
        SelectedDevice = Devices.FirstOrDefault();
    }

    public PortAudioOutputDefinition? TryCommit()
    {
        ValidationMessage = null;
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ValidationMessage = "Display name is required.";
            return null;
        }

        if (SelectedHostApi is null || SelectedDevice is null)
        {
            ValidationMessage = "Select a host API and output device.";
            return null;
        }

        if (ChannelCount < 1 || ChannelCount > SelectedDevice.Value.MaxOutputChannels)
        {
            ValidationMessage =
                $"Channel count must be between 1 and {SelectedDevice.Value.MaxOutputChannels} for this device.";
            return null;
        }

        if (SampleRate is < 8000 or > 192_000)
        {
            ValidationMessage = "Sample rate looks invalid (expected roughly 8 kHz–192 kHz).";
            return null;
        }

        return new PortAudioOutputDefinition(
            _existingId ?? Guid.NewGuid(),
            DisplayName.Trim(),
            SelectedHostApi.Value.Index,
            SelectedHostApi.Value.Name,
            SelectedDevice.Value.GlobalDeviceIndex,
            SelectedDevice.Value.Name,
            ChannelCount,
            SampleRate);
    }
}
