using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using S.Media.PortAudio;

namespace HaPlay.ViewModels.Dialogs;

public partial class AddPortAudioOutputDialogViewModel : ViewModelBase
{
    [ObservableProperty] private string _displayName = "Main speakers";
    [ObservableProperty] private string? _validationMessage;

    public ObservableCollection<PortAudioHostApiEntry> HostApis { get; } = new();
    public ObservableCollection<PortAudioOutputDeviceEntry> Devices { get; } = new();

    [ObservableProperty] private PortAudioHostApiEntry? _selectedHostApi;
    [ObservableProperty] private PortAudioOutputDeviceEntry? _selectedDevice;
    [ObservableProperty] private int _channelCount = 2;
    [ObservableProperty] private int _sampleRate = 48000;

    public void ReloadHostApis()
    {
        HostApis.Clear();
        foreach (var h in PortAudioDeviceCatalog.EnumerateHostApis())
            HostApis.Add(h);
        SelectedHostApi = HostApis.FirstOrDefault();
    }

    partial void OnSelectedHostApiChanged(PortAudioHostApiEntry? value) => ReloadDevices();

    partial void OnSelectedDeviceChanged(PortAudioOutputDeviceEntry? value)
    {
        if (!value.HasValue)
            return;
        var d = value.GetValueOrDefault();
        ChannelCount = Math.Clamp(ChannelCount, 1, Math.Max(1, d.MaxOutputChannels));
        var sr = (int)Math.Round(d.DefaultSampleRate, MidpointRounding.AwayFromZero);
        if (sr > 0)
            SampleRate = sr;
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
            Guid.NewGuid(),
            DisplayName.Trim(),
            SelectedHostApi.Value.Index,
            SelectedHostApi.Value.Name,
            SelectedDevice.Value.GlobalDeviceIndex,
            SelectedDevice.Value.Name,
            ChannelCount,
            SampleRate);
    }
}
