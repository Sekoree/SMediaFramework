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

public sealed class ProjectMidiInputRowViewModel
{
    public ProjectMidiInputRowViewModel(Guid controlDeviceId, int? deviceId, string? deviceName)
    {
        ControlDeviceId = controlDeviceId;
        DeviceId = deviceId;
        DeviceName = deviceName?.Trim() ?? string.Empty;
    }

    public Guid ControlDeviceId { get; }

    public int? DeviceId { get; }

    public string DeviceName { get; }

    public string Name => string.IsNullOrWhiteSpace(DeviceName) ? "(unnamed input)" : DeviceName;

    public string DeviceIdText => DeviceId is { } id ? id.ToString(CultureInfo.InvariantCulture) : "name";

    public string UsageText => "Control input";

    public string MatchKey => MidiRowMatchKey(DeviceId, DeviceName);

    internal static string MidiRowMatchKey(int? deviceId, string? deviceName) =>
        deviceId is { } id
            ? $"id:{id.ToString(CultureInfo.InvariantCulture)}"
            : $"name:{(deviceName ?? string.Empty).Trim().ToUpperInvariant()}";
}

public sealed class ProjectMidiOutputRowViewModel
{
    public ProjectMidiOutputRowViewModel(Guid? controlDeviceId, Guid? cueEndpointId, int? deviceId, string? deviceName)
    {
        ControlDeviceId = controlDeviceId;
        CueEndpointId = cueEndpointId;
        DeviceId = deviceId;
        DeviceName = deviceName?.Trim() ?? string.Empty;
    }

    public Guid? ControlDeviceId { get; }

    public Guid? CueEndpointId { get; }

    public int? DeviceId { get; }

    public string DeviceName { get; }

    public bool UsedByControl => ControlDeviceId is not null;

    public bool UsedByCue => CueEndpointId is not null;

    public string Name => string.IsNullOrWhiteSpace(DeviceName) ? "(unnamed output)" : DeviceName;

    public string DeviceIdText => DeviceId is { } id ? id.ToString(CultureInfo.InvariantCulture) : "name";

    public string UsageText => (UsedByControl, UsedByCue) switch
    {
        (true, true) => "Cue + Control output",
        (true, false) => "Control output",
        (false, true) => "Cue output",
        _ => "Unused output",
    };

    public string MatchKey => ProjectMidiInputRowViewModel.MidiRowMatchKey(DeviceId, DeviceName);
}
