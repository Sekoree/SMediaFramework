using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Control;
using HaPlay.Resources;
using HaPlay.ViewModels.Dialogs;
using HaPlay.Views.Dialogs;
using OSCLib;

namespace HaPlay.ViewModels;

public sealed class ControlMonitorEntryViewModel
{
    public ControlMonitorEntryViewModel(ControlMonitorRecord record)
    {
        Time = record.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        Direction = record.Direction.ToString();
        Protocol = record.Protocol.ToString();
        Result = record.Result.ToString();
        IsError = record.Direction == ControlMonitorDirection.Error || record.Result == ControlMonitorResult.Failed;
        Summary = BuildSummary(record);
    }

    public string Time { get; }

    public string Direction { get; }

    public string Protocol { get; }

    public string Result { get; }

    public string Summary { get; }

    public bool IsError { get; }

    private static string BuildSummary(ControlMonitorRecord record)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(record.Address))
            parts.Add(record.Address!);
        if (record.OSCArguments.Count > 0)
            parts.Add("[" + string.Join(", ", record.OSCArguments.Select(FormatOSCArgument)) + "]");
        if (record.MIDIController is { } controller)
            parts.Add($"cc{controller}");
        if (record.MIDINote is { } note)
            parts.Add($"note{note}");
        if (record.MIDIValue is { } value)
            parts.Add($"={value}");
        if (!string.IsNullOrWhiteSpace(record.DeviceKey))
            parts.Add($"dev:{record.DeviceKey}");
        else if (!string.IsNullOrWhiteSpace(record.Endpoint))
            parts.Add(record.Endpoint!);
        if (!string.IsNullOrWhiteSpace(record.Message))
            parts.Add(record.Message!);
        if (!string.IsNullOrWhiteSpace(record.ErrorMessage))
            parts.Add($"⚠ {record.ErrorMessage}");

        return parts.Count > 0 ? string.Join("  ", parts) : "(no detail)";
    }

    private static string FormatOSCArgument(ControlMonitorOSCArgumentRecord argument)
    {
        if (argument.FloatValue is { } f)
            return f.ToString("0.###", CultureInfo.InvariantCulture);
        if (argument.IntegerValue is { } i)
            return i.ToString(CultureInfo.InvariantCulture);
        if (argument.BoolValue is { } b)
            return b ? "true" : "false";
        if (argument.StringValue is { } s)
            return $"\"{s}\"";
        return argument.Kind;
    }
}

internal sealed record ControlMonitorFilterSettings(
    bool ErrorsOnly,
    string Text,
    string Direction,
    string Protocol,
    string DeviceText);

/// <summary>Context-menu callbacks shared by every structure row, supplied by the workspace VM.</summary>
internal sealed record ControlStructureRowCommands(
    Action AddProjectScript,
    Action AddHelperFile,
    Action<ControlStructureRowViewModel> AddDeviceScript,
    Action<ControlStructureRowViewModel> AddEndpointScript,
    Action<ControlStructureRowViewModel> AddLayerScript,
    Action<ControlStructureRowViewModel> ActivateLayer,
    Action AddLayer,
    Action<ControlStructureRowViewModel> EditLayer,
    Action<ControlStructureRowViewModel> RemoveLayer,
    Action<ControlStructureRowViewModel> AddPeriodicSend,
    Action<ControlStructureRowViewModel> EditPeriodicSend,
    Action<ControlStructureRowViewModel> RemovePeriodicSend,
    Action<ControlStructureRowViewModel> EditOSCDevice,
    Action<ControlStructureRowViewModel> RemoveOSCDevice,
    Action<ControlStructureRowViewModel> TestOSC,
    Action<ControlStructureRowViewModel> TestMIDI,
    Action AddOSCListener,
    Action<ControlStructureRowViewModel> EditOSCListener,
    Action<ControlStructureRowViewModel> RemoveOSCListener,
    Action<ControlStructureRowViewModel> EditMIDIDevice,
    Action<ControlStructureRowViewModel> ExportLayer);

public sealed class ControlStructureRowViewModel
{
    internal ControlStructureRowViewModel(
        string kind,
        string name,
        string detail,
        string state,
        int Level,
        bool IsGroup = false,
        Guid? deviceInstanceId = null,
        Guid? layerId = null,
        Guid? oscListenerId = null,
        Guid? periodicSendId = null,
        ControlDeviceProtocol? protocol = null,
        ControlStructureRowCommands? commands = null)
    {
        Kind = kind;
        Name = name;
        Detail = detail;
        State = state;
        this.Level = Level;
        this.IsGroup = IsGroup;
        DeviceInstanceId = deviceInstanceId;
        LayerId = layerId;
        OSCListenerId = oscListenerId;
        PeriodicSendId = periodicSendId;
        Protocol = protocol;

        if (commands is not null)
        {
            AddProjectScriptCommand = new RelayCommand(commands.AddProjectScript);
            AddHelperFileCommand = new RelayCommand(commands.AddHelperFile);
            AddDeviceScriptCommand = new RelayCommand(() => commands.AddDeviceScript(this), () => CanAddDeviceScript);
            AddEndpointScriptCommand = new RelayCommand(() => commands.AddEndpointScript(this), () => CanAddEndpointScript);
            AddLayerScriptCommand = new RelayCommand(() => commands.AddLayerScript(this), () => CanAddLayerScript);
            ActivateLayerCommand = new RelayCommand(() => commands.ActivateLayer(this), () => CanActivateLayer);
            AddLayerCommand = new RelayCommand(commands.AddLayer);
            EditLayerCommand = new RelayCommand(() => commands.EditLayer(this), () => CanEditLayer);
            RemoveLayerCommand = new RelayCommand(() => commands.RemoveLayer(this), () => CanEditLayer);
            AddOSCListenerCommand = new RelayCommand(commands.AddOSCListener);
            EditOSCListenerCommand = new RelayCommand(() => commands.EditOSCListener(this), () => CanEditOSCListener);
            RemoveOSCListenerCommand = new RelayCommand(() => commands.RemoveOSCListener(this), () => CanEditOSCListener);
            AddPeriodicSendCommand = new RelayCommand(() => commands.AddPeriodicSend(this), () => CanAddPeriodicSend);
            EditPeriodicSendCommand = new RelayCommand(() => commands.EditPeriodicSend(this), () => CanEditPeriodicSend);
            RemovePeriodicSendCommand = new RelayCommand(() => commands.RemovePeriodicSend(this), () => CanEditPeriodicSend);
            EditOSCDeviceCommand = new RelayCommand(() => commands.EditOSCDevice(this), () => CanEditOSCDevice);
            RemoveOSCDeviceCommand = new RelayCommand(() => commands.RemoveOSCDevice(this), () => CanEditOSCDevice);
            TestOSCCommand = new RelayCommand(() => commands.TestOSC(this), () => CanTestOSC);
            TestMIDICommand = new RelayCommand(() => commands.TestMIDI(this), () => CanTestMIDI);
            EditMIDIDeviceCommand = new RelayCommand(() => commands.EditMIDIDevice(this), () => CanEditMIDIDevice);
            ExportLayerCommand = new RelayCommand(() => commands.ExportLayer(this), () => CanEditLayer);
        }
    }

    public string Kind { get; }

    public string Name { get; }

    public string Detail { get; }

    public string State { get; }

    public int Level { get; }

    public bool IsGroup { get; }

    public Guid? DeviceInstanceId { get; }

    public Guid? LayerId { get; }

    public Guid? OSCListenerId { get; }

    public Guid? PeriodicSendId { get; }

    public ControlDeviceProtocol? Protocol { get; }

    public double IndentWidth => Level * 16;

    public bool CanAddDeviceScript => DeviceInstanceId is not null && PeriodicSendId is null;

    public bool CanEditPeriodicSend => PeriodicSendId is not null && DeviceInstanceId is not null;

    public bool CanAddEndpointScript => OSCListenerId is not null;

    public bool CanEditOSCListener => OSCListenerId is not null;

    public bool CanAddLayerScript => LayerId is not null;

    public bool CanActivateLayer => LayerId is not null;

    public bool CanEditLayer => LayerId is not null;

    public bool CanAddPeriodicSend => Protocol == ControlDeviceProtocol.OSC && DeviceInstanceId is not null && PeriodicSendId is null;

    public bool CanEditOSCDevice => Protocol == ControlDeviceProtocol.OSC && DeviceInstanceId is not null && PeriodicSendId is null;

    public bool CanTestOSC => Protocol == ControlDeviceProtocol.OSC && DeviceInstanceId is not null && PeriodicSendId is null;

    public bool CanTestMIDI => Protocol == ControlDeviceProtocol.MIDI && DeviceInstanceId is not null;

    public bool CanEditMIDIDevice => Protocol == ControlDeviceProtocol.MIDI && DeviceInstanceId is not null;

    public ICommand? AddProjectScriptCommand { get; }

    public ICommand? AddHelperFileCommand { get; }

    public ICommand? AddDeviceScriptCommand { get; }

    public ICommand? AddEndpointScriptCommand { get; }

    public ICommand? AddLayerScriptCommand { get; }

    public ICommand? ActivateLayerCommand { get; }

    public ICommand? AddLayerCommand { get; }

    public ICommand? EditLayerCommand { get; }

    public IRelayCommand? ExportLayerCommand { get; }

    public ICommand? RemoveLayerCommand { get; }

    public ICommand? AddOSCListenerCommand { get; }

    public ICommand? EditOSCListenerCommand { get; }

    public ICommand? RemoveOSCListenerCommand { get; }

    public ICommand? AddPeriodicSendCommand { get; }

    public ICommand? EditPeriodicSendCommand { get; }

    public ICommand? RemovePeriodicSendCommand { get; }

    public ICommand? EditOSCDeviceCommand { get; }

    public ICommand? RemoveOSCDeviceCommand { get; }

    public ICommand? TestOSCCommand { get; }

    public ICommand? TestMIDICommand { get; }

    public ICommand? EditMIDIDeviceCommand { get; }
}

public sealed record ControlX32CommandRowViewModel(
    Guid DeviceInstanceId,
    string DeviceName,
    string DeviceKey,
    string Host,
    int? Port,
    string Group,
    string CommandName,
    string Address,
    string ValueKind,
    string Access,
    string CacheValue,
    bool CanRequest);

public sealed record ControlProfileRowViewModel(
    string DisplayName,
    string Id,
    string Protocol,
    string Source,
    string Summary,
    bool IsProjectOverride);

/// <summary>A selectable layer in the script editor's layer picker. <see cref="ToString"/> drives display.</summary>
public sealed record ControlLayerOption(Guid Id, string Name)
{
    public override string ToString() => Name;
}
