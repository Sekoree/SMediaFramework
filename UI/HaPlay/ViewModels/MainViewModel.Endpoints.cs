using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using HaPlay.Resources;
using S.Control;
using OSCLib;
using PMLib;
using PMLib.Devices;
using PMLib.MessageTypes;
using PMLib.Types;

namespace HaPlay.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void AddOSCEndpoint()
    {
        var n = ActionEndpoints.OfType<OSCActionEndpoint>().Count() + 1;
        var endpoint = new OSCActionEndpoint
        {
            Name = Strings.Format(nameof(Strings.OSCEndpointNameFormat), n),
            Host = "127.0.0.1",
            Port = 9000,
        };
        ActionEndpoints.Add(endpoint);
        SelectedActionEndpoint = endpoint;
    }

    [RelayCommand(CanExecute = nameof(CanAddMIDIEndpoint))]
    private void AddMIDIEndpoint()
    {
        var n = ActionEndpoints.OfType<MIDIActionEndpoint>().Count() + 1;
        var endpoint = new MIDIActionEndpoint
        {
            Name = Strings.Format(nameof(Strings.MIDIEndpointNameFormat), n),
            Channel = 0,
        };
        ActionEndpoints.Add(endpoint);
        SelectedActionEndpoint = endpoint;
    }

    private bool CanAddMIDIEndpoint() => IsMIDIAvailable;

    [RelayCommand(CanExecute = nameof(CanAddSelectedMIDIOutputEndpoint))]
    private void AddSelectedMIDIOutputEndpoint()
    {
        AddSelectedMIDIOutputToProject();
    }

    private bool CanAddSelectedMIDIOutputEndpoint() => IsMIDIAvailable && SelectedMIDIOutputOption is not null;

    [RelayCommand(CanExecute = nameof(CanAddSelectedMIDIOutputToProject))]
    private void AddSelectedMIDIOutputToProject()
    {
        if (SelectedMIDIOutputOption is not { } output)
            return;

        Control.AddOrUpdateMIDIOutputDevice(output.Id, output.Name);

        var existing = FindMIDIEndpoint(output.Id, output.Name);
        if (existing is not null)
        {
            SelectedActionEndpoint = existing;
            EndpointTestStatus = $"Selected existing project MIDI output '{existing.Name}'.";
        }
        else
        {
            var endpoint = new MIDIActionEndpoint
            {
                Name = output.Name,
                DeviceId = output.Id,
                DeviceName = output.Name,
                Channel = 0,
            };
            ActionEndpoints.Add(endpoint);
            SelectedActionEndpoint = endpoint;
            EndpointTestStatus = $"Added project MIDI output '{endpoint.Name}'.";
        }

        RebuildProjectMIDIDeviceRows();
        SelectedProjectMIDIOutputRow = ProjectMIDIOutputRows.FirstOrDefault(r =>
            MatchesMIDIBinding(r.DeviceId, r.DeviceName, output.Id, output.Name));
    }

    private bool CanAddSelectedMIDIOutputToProject() => IsMIDIAvailable && SelectedMIDIOutputOption is not null;

    private MIDIActionEndpoint? FindMIDIEndpoint(int deviceId, string deviceName) =>
        ActionEndpoints
            .OfType<MIDIActionEndpoint>()
            .FirstOrDefault(e => MatchesMIDIBinding(e.DeviceId, e.DeviceName ?? e.Name, deviceId, deviceName));

    [RelayCommand(CanExecute = nameof(CanRemoveActionEndpoint))]
    private void RemoveActionEndpoint()
    {
        if (SelectedActionEndpoint is null)
            return;
        ActionEndpoints.Remove(SelectedActionEndpoint);
        SelectedActionEndpoint = ActionEndpoints.FirstOrDefault();
    }

    private bool CanRemoveActionEndpoint() => SelectedActionEndpoint is not null;

    [RelayCommand(CanExecute = nameof(CanSaveActionEndpointEdits))]
    private void SaveActionEndpointEdits()
    {
        if (SelectedActionEndpoint is null)
            return;
        var index = ActionEndpoints.IndexOf(SelectedActionEndpoint);
        if (index < 0)
            return;

        var name = string.IsNullOrWhiteSpace(EndpointEditName)
            ? SelectedActionEndpoint.Name
            : EndpointEditName.Trim();

        var replacement = SelectedActionEndpoint switch
        {
            OSCActionEndpoint osc => osc with
            {
                Name = name,
                Host = string.IsNullOrWhiteSpace(OSCEditHost) ? osc.Host : OSCEditHost.Trim(),
                Port = OSCEditPort is >= IPEndPoint.MinPort and <= IPEndPoint.MaxPort ? OSCEditPort : osc.Port,
            },
            MIDIActionEndpoint midi => midi with
            {
                Name = name,
                DeviceId = MIDIEditDeviceId,
                DeviceName = string.IsNullOrWhiteSpace(MIDIEditDeviceName) ? null : MIDIEditDeviceName.Trim(),
                Channel = Math.Clamp(MIDIEditChannel, 0, 15),
            },
            _ => SelectedActionEndpoint,
        };

        ActionEndpoints[index] = replacement;
        SelectedActionEndpoint = replacement;
        FindEndpointRow(replacement.Id)?.ReplaceEndpoint(replacement);
        var row = FindEndpointRow(replacement.Id);
        if (row is not null)
            _ = ProbeEndpointRowAsync(row, CancellationToken.None);
    }

    private bool CanSaveActionEndpointEdits() => SelectedActionEndpoint is not null;

    [RelayCommand(CanExecute = nameof(CanRefreshMIDIOutputs))]
    private void RefreshMIDIOutputs()
    {
        RefreshMIDIDeviceCatalog();
        if (SelectedActionEndpoint is MIDIActionEndpoint midi)
            SelectedMIDIOutputOption = MIDIOutputOptions.FirstOrDefault(o => o.Id == midi.DeviceId);
    }

    private bool CanRefreshMIDIOutputs() => IsMIDIAvailable && SelectedActionEndpoint is MIDIActionEndpoint;

    [RelayCommand(CanExecute = nameof(CanRefreshMIDIDeviceCatalog))]
    private void RefreshMIDIDeviceCatalog()
    {
        if (!IsMIDIAvailable)
        {
            MIDIInputOptions.Clear();
            MIDIOutputOptions.Clear();
            SelectedMIDIInputOption = null;
            SelectedMIDIOutputOption = null;
            MIDIDeviceStatus = MIDIUnavailableStatus;
            return;
        }

        var previousInputId = SelectedMIDIInputOption?.Id;
        var previousOutputId = SelectedMIDIOutputOption?.Id;
        var initError = EnsureMIDIInitialized();
        if (initError is not null)
        {
            MIDIDeviceStatus = initError;
            return;
        }

        var inputs = PMUtil.GetInputDevices();
        MIDIInputOptions.Clear();
        foreach (var dev in inputs)
            MIDIInputOptions.Add(new MIDIInputOption(dev.Id, dev.Name ?? Strings.Format(nameof(Strings.DeviceWithIdFormat), dev.Id)));

        var outputs = PMUtil.GetOutputDevices();
        MIDIOutputOptions.Clear();
        foreach (var dev in outputs)
            MIDIOutputOptions.Add(new MIDIOutputOption(dev.Id, dev.Name ?? Strings.Format(nameof(Strings.DeviceWithIdFormat), dev.Id)));

        SelectedMIDIInputOption = previousInputId is null
            ? MIDIInputOptions.FirstOrDefault()
            : MIDIInputOptions.FirstOrDefault(o => o.Id == previousInputId) ?? MIDIInputOptions.FirstOrDefault();
        SelectedMIDIOutputOption = previousOutputId is null
            ? MIDIOutputOptions.FirstOrDefault()
            : MIDIOutputOptions.FirstOrDefault(o => o.Id == previousOutputId) ?? MIDIOutputOptions.FirstOrDefault();
        MIDIDeviceStatus = Strings.Format(nameof(Strings.MIDIDeviceCatalogStatusFormat), inputs.Count, outputs.Count);
    }

    private bool CanRefreshMIDIDeviceCatalog() => IsMIDIAvailable;

    [RelayCommand(CanExecute = nameof(CanUseSelectedMIDIOutput))]
    private void UseSelectedMIDIOutput()
    {
        if (SelectedMIDIOutputOption is null)
            return;
        MIDIEditDeviceId = SelectedMIDIOutputOption.Id;
        MIDIEditDeviceName = SelectedMIDIOutputOption.Name;
    }

    private bool CanUseSelectedMIDIOutput() => IsMIDIAvailable && SelectedMIDIOutputOption is not null;

    [RelayCommand(CanExecute = nameof(CanAddSelectedMIDIInputToControl))]
    private void AddSelectedMIDIInputToControl()
    {
        if (SelectedMIDIInputOption is not { } input)
            return;
        Control.AddOrUpdateMIDIInputDevice(input.Id, input.Name);
        RebuildProjectMIDIDeviceRows();
        SelectedProjectMIDIInputRow = ProjectMIDIInputRows.FirstOrDefault(r =>
            MatchesMIDIBinding(r.DeviceId, r.DeviceName, input.Id, input.Name));
        MIDIDeviceStatus = $"Added project MIDI input '{input.Name}'.";
    }

    private bool CanAddSelectedMIDIInputToControl() => IsMIDIAvailable && SelectedMIDIInputOption is not null;

    [RelayCommand(CanExecute = nameof(CanAddSelectedMIDIOutputToControl))]
    private void AddSelectedMIDIOutputToControl()
    {
        if (SelectedMIDIOutputOption is not { } output)
            return;
        Control.AddOrUpdateMIDIOutputDevice(output.Id, output.Name);
        RebuildProjectMIDIDeviceRows();
        SelectedProjectMIDIOutputRow = ProjectMIDIOutputRows.FirstOrDefault(r =>
            MatchesMIDIBinding(r.DeviceId, r.DeviceName, output.Id, output.Name));
        MIDIDeviceStatus = $"Added project MIDI output '{output.Name}' to Control.";
    }

    private bool CanAddSelectedMIDIOutputToControl() => IsMIDIAvailable && SelectedMIDIOutputOption is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedProjectMIDIInput))]
    private void RemoveSelectedProjectMIDIInput()
    {
        var row = SelectedProjectMIDIInputRow;
        if (row is null)
            return;

        Control.RemoveMIDIInputDevice(row.ControlDeviceId);
        MIDIDeviceStatus = $"Removed project MIDI input '{row.Name}'.";
        RebuildProjectMIDIDeviceRows();
    }

    private bool CanRemoveSelectedProjectMIDIInput() => SelectedProjectMIDIInputRow is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedProjectMIDIOutput))]
    private void RemoveSelectedProjectMIDIOutput()
    {
        var row = SelectedProjectMIDIOutputRow;
        if (row is null)
            return;

        if (row.ControlDeviceId is { } controlDeviceId)
            Control.RemoveMIDIOutputDevice(controlDeviceId);

        if (row.CueEndpointId is { } endpointId)
        {
            var endpoint = ActionEndpoints.FirstOrDefault(e => e.Id == endpointId);
            if (endpoint is not null)
            {
                ActionEndpoints.Remove(endpoint);
                if (ReferenceEquals(SelectedActionEndpoint, endpoint))
                    SelectedActionEndpoint = ActionEndpoints.FirstOrDefault();
            }
        }

        MIDIDeviceStatus = $"Removed project MIDI output '{row.Name}'.";
        RebuildProjectMIDIDeviceRows();
    }

    private bool CanRemoveSelectedProjectMIDIOutput() => SelectedProjectMIDIOutputRow is not null;

    [RelayCommand(CanExecute = nameof(CanTestSelectedProjectMIDIOutput))]
    private async Task TestSelectedProjectMIDIOutputAsync()
    {
        var row = SelectedProjectMIDIOutputRow;
        if (row is null)
            return;

        EndpointTestStatus = Strings.TestingMIDIStatus;
        var endpoint = row.CueEndpointId is { } endpointId
            ? ActionEndpoints.OfType<MIDIActionEndpoint>().FirstOrDefault(e => e.Id == endpointId)
            : null;
        endpoint ??= new MIDIActionEndpoint
        {
            Name = row.Name,
            DeviceId = row.DeviceId,
            DeviceName = row.DeviceName,
        };

        var (ok, detail) = await Task.Run(() => ActionEndpointProbe.TryProbeMIDI(endpoint, EnsureMIDIInitialized))
            .ConfigureAwait(true);
        EndpointTestStatus = ok
            ? Strings.Format(nameof(Strings.MIDITestOkStatusFormat), detail)
            : Strings.Format(nameof(Strings.MIDITestFailedStatusFormat), detail);
    }

    private bool CanTestSelectedProjectMIDIOutput() => SelectedProjectMIDIOutputRow is not null;
}
