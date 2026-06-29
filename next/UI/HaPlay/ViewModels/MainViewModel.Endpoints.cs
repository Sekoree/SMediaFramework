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
    private void AddOscEndpoint()
    {
        var n = ActionEndpoints.OfType<OscActionEndpoint>().Count() + 1;
        var endpoint = new OscActionEndpoint
        {
            Name = Strings.Format(nameof(Strings.OscEndpointNameFormat), n),
            Host = "127.0.0.1",
            Port = 9000,
        };
        ActionEndpoints.Add(endpoint);
        SelectedActionEndpoint = endpoint;
    }

    [RelayCommand(CanExecute = nameof(CanAddMidiEndpoint))]
    private void AddMidiEndpoint()
    {
        var n = ActionEndpoints.OfType<MidiActionEndpoint>().Count() + 1;
        var endpoint = new MidiActionEndpoint
        {
            Name = Strings.Format(nameof(Strings.MidiEndpointNameFormat), n),
            Channel = 0,
        };
        ActionEndpoints.Add(endpoint);
        SelectedActionEndpoint = endpoint;
    }

    private bool CanAddMidiEndpoint() => IsMidiAvailable;

    [RelayCommand(CanExecute = nameof(CanAddSelectedMidiOutputEndpoint))]
    private void AddSelectedMidiOutputEndpoint()
    {
        AddSelectedMidiOutputToProject();
    }

    private bool CanAddSelectedMidiOutputEndpoint() => IsMidiAvailable && SelectedMidiOutputOption is not null;

    [RelayCommand(CanExecute = nameof(CanAddSelectedMidiOutputToProject))]
    private void AddSelectedMidiOutputToProject()
    {
        if (SelectedMidiOutputOption is not { } output)
            return;

        Control.AddOrUpdateMidiOutputDevice(output.Id, output.Name);

        var existing = FindMidiEndpoint(output.Id, output.Name);
        if (existing is not null)
        {
            SelectedActionEndpoint = existing;
            EndpointTestStatus = $"Selected existing project MIDI output '{existing.Name}'.";
        }
        else
        {
            var endpoint = new MidiActionEndpoint
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

        RebuildProjectMidiDeviceRows();
        SelectedProjectMidiOutputRow = ProjectMidiOutputRows.FirstOrDefault(r =>
            MatchesMidiBinding(r.DeviceId, r.DeviceName, output.Id, output.Name));
    }

    private bool CanAddSelectedMidiOutputToProject() => IsMidiAvailable && SelectedMidiOutputOption is not null;

    private MidiActionEndpoint? FindMidiEndpoint(int deviceId, string deviceName) =>
        ActionEndpoints
            .OfType<MidiActionEndpoint>()
            .FirstOrDefault(e => MatchesMidiBinding(e.DeviceId, e.DeviceName ?? e.Name, deviceId, deviceName));

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
            OscActionEndpoint osc => osc with
            {
                Name = name,
                Host = string.IsNullOrWhiteSpace(OscEditHost) ? osc.Host : OscEditHost.Trim(),
                Port = OscEditPort is >= IPEndPoint.MinPort and <= IPEndPoint.MaxPort ? OscEditPort : osc.Port,
            },
            MidiActionEndpoint midi => midi with
            {
                Name = name,
                DeviceId = MidiEditDeviceId,
                DeviceName = string.IsNullOrWhiteSpace(MidiEditDeviceName) ? null : MidiEditDeviceName.Trim(),
                Channel = Math.Clamp(MidiEditChannel, 0, 15),
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

    [RelayCommand(CanExecute = nameof(CanRefreshMidiOutputs))]
    private void RefreshMidiOutputs()
    {
        RefreshMidiDeviceCatalog();
        if (SelectedActionEndpoint is MidiActionEndpoint midi)
            SelectedMidiOutputOption = MidiOutputOptions.FirstOrDefault(o => o.Id == midi.DeviceId);
    }

    private bool CanRefreshMidiOutputs() => IsMidiAvailable && SelectedActionEndpoint is MidiActionEndpoint;

    [RelayCommand(CanExecute = nameof(CanRefreshMidiDeviceCatalog))]
    private void RefreshMidiDeviceCatalog()
    {
        if (!IsMidiAvailable)
        {
            MidiInputOptions.Clear();
            MidiOutputOptions.Clear();
            SelectedMidiInputOption = null;
            SelectedMidiOutputOption = null;
            MidiDeviceStatus = MidiUnavailableStatus;
            return;
        }

        var previousInputId = SelectedMidiInputOption?.Id;
        var previousOutputId = SelectedMidiOutputOption?.Id;
        var initError = EnsureMidiInitialized();
        if (initError is not null)
        {
            MidiDeviceStatus = initError;
            return;
        }

        var inputs = PMUtil.GetInputDevices();
        MidiInputOptions.Clear();
        foreach (var dev in inputs)
            MidiInputOptions.Add(new MidiInputOption(dev.Id, dev.Name ?? Strings.Format(nameof(Strings.DeviceWithIdFormat), dev.Id)));

        var outputs = PMUtil.GetOutputDevices();
        MidiOutputOptions.Clear();
        foreach (var dev in outputs)
            MidiOutputOptions.Add(new MidiOutputOption(dev.Id, dev.Name ?? Strings.Format(nameof(Strings.DeviceWithIdFormat), dev.Id)));

        SelectedMidiInputOption = previousInputId is null
            ? MidiInputOptions.FirstOrDefault()
            : MidiInputOptions.FirstOrDefault(o => o.Id == previousInputId) ?? MidiInputOptions.FirstOrDefault();
        SelectedMidiOutputOption = previousOutputId is null
            ? MidiOutputOptions.FirstOrDefault()
            : MidiOutputOptions.FirstOrDefault(o => o.Id == previousOutputId) ?? MidiOutputOptions.FirstOrDefault();
        MidiDeviceStatus = Strings.Format(nameof(Strings.MidiDeviceCatalogStatusFormat), inputs.Count, outputs.Count);
    }

    private bool CanRefreshMidiDeviceCatalog() => IsMidiAvailable;

    [RelayCommand(CanExecute = nameof(CanUseSelectedMidiOutput))]
    private void UseSelectedMidiOutput()
    {
        if (SelectedMidiOutputOption is null)
            return;
        MidiEditDeviceId = SelectedMidiOutputOption.Id;
        MidiEditDeviceName = SelectedMidiOutputOption.Name;
    }

    private bool CanUseSelectedMidiOutput() => IsMidiAvailable && SelectedMidiOutputOption is not null;

    [RelayCommand(CanExecute = nameof(CanAddSelectedMidiInputToControl))]
    private void AddSelectedMidiInputToControl()
    {
        if (SelectedMidiInputOption is not { } input)
            return;
        Control.AddOrUpdateMidiInputDevice(input.Id, input.Name);
        RebuildProjectMidiDeviceRows();
        SelectedProjectMidiInputRow = ProjectMidiInputRows.FirstOrDefault(r =>
            MatchesMidiBinding(r.DeviceId, r.DeviceName, input.Id, input.Name));
        MidiDeviceStatus = $"Added project MIDI input '{input.Name}'.";
    }

    private bool CanAddSelectedMidiInputToControl() => IsMidiAvailable && SelectedMidiInputOption is not null;

    [RelayCommand(CanExecute = nameof(CanAddSelectedMidiOutputToControl))]
    private void AddSelectedMidiOutputToControl()
    {
        if (SelectedMidiOutputOption is not { } output)
            return;
        Control.AddOrUpdateMidiOutputDevice(output.Id, output.Name);
        RebuildProjectMidiDeviceRows();
        SelectedProjectMidiOutputRow = ProjectMidiOutputRows.FirstOrDefault(r =>
            MatchesMidiBinding(r.DeviceId, r.DeviceName, output.Id, output.Name));
        MidiDeviceStatus = $"Added project MIDI output '{output.Name}' to Control.";
    }

    private bool CanAddSelectedMidiOutputToControl() => IsMidiAvailable && SelectedMidiOutputOption is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedProjectMidiInput))]
    private void RemoveSelectedProjectMidiInput()
    {
        var row = SelectedProjectMidiInputRow;
        if (row is null)
            return;

        Control.RemoveMidiInputDevice(row.ControlDeviceId);
        MidiDeviceStatus = $"Removed project MIDI input '{row.Name}'.";
        RebuildProjectMidiDeviceRows();
    }

    private bool CanRemoveSelectedProjectMidiInput() => SelectedProjectMidiInputRow is not null;

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedProjectMidiOutput))]
    private void RemoveSelectedProjectMidiOutput()
    {
        var row = SelectedProjectMidiOutputRow;
        if (row is null)
            return;

        if (row.ControlDeviceId is { } controlDeviceId)
            Control.RemoveMidiOutputDevice(controlDeviceId);

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

        MidiDeviceStatus = $"Removed project MIDI output '{row.Name}'.";
        RebuildProjectMidiDeviceRows();
    }

    private bool CanRemoveSelectedProjectMidiOutput() => SelectedProjectMidiOutputRow is not null;

    [RelayCommand(CanExecute = nameof(CanTestSelectedProjectMidiOutput))]
    private async Task TestSelectedProjectMidiOutputAsync()
    {
        var row = SelectedProjectMidiOutputRow;
        if (row is null)
            return;

        EndpointTestStatus = Strings.TestingMidiStatus;
        var endpoint = row.CueEndpointId is { } endpointId
            ? ActionEndpoints.OfType<MidiActionEndpoint>().FirstOrDefault(e => e.Id == endpointId)
            : null;
        endpoint ??= new MidiActionEndpoint
        {
            Name = row.Name,
            DeviceId = row.DeviceId,
            DeviceName = row.DeviceName,
        };

        var (ok, detail) = await Task.Run(() => ActionEndpointProbe.TryProbeMidi(endpoint, EnsureMidiInitialized))
            .ConfigureAwait(true);
        EndpointTestStatus = ok
            ? Strings.Format(nameof(Strings.MidiTestOkStatusFormat), detail)
            : Strings.Format(nameof(Strings.MidiTestFailedStatusFormat), detail);
    }

    private bool CanTestSelectedProjectMidiOutput() => SelectedProjectMidiOutputRow is not null;
}
