using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using S.Control;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>A current MIDI port shown as a selectable option in the resolution dialog.</summary>
public sealed record ControlMIDIPortOption(ControlMIDIPortInfo Port)
{
    public string Display => string.IsNullOrWhiteSpace(Port.Name)
        ? $"#{Port.Id.ToString(CultureInfo.InvariantCulture)}"
        : $"{Port.Name} (#{Port.Id.ToString(CultureInfo.InvariantCulture)})";
}

public sealed partial class ControlMIDIDeviceResolutionRowViewModel : ObservableObject
{
    public ControlMIDIDeviceResolutionRowViewModel(ControlMIDIResolutionRequest request)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        foreach (var port in request.AvailablePorts)
            Options.Add(new ControlMIDIPortOption(port));

        var preferredId = request.Candidates.Count > 0 ? request.Candidates[0].Id : (int?)null;
        SelectedOption = (preferredId is { } id ? Options.FirstOrDefault(o => o.Port.Id == id) : null)
            ?? Options.FirstOrDefault();

        Title = $"{request.DeviceName} — {request.Direction}";
        StatusText = string.IsNullOrWhiteSpace(request.Message) ? request.Status.ToString() : request.Message;
    }

    public ControlMIDIResolutionRequest Request { get; }

    public string Title { get; }

    public string StatusText { get; }

    public ObservableCollection<ControlMIDIPortOption> Options { get; } = new();

    [ObservableProperty]
    private ControlMIDIPortOption? _selectedOption;
}

/// <summary>
/// Fallback MIDI device-selection dialog VM. Lists each configured MIDI binding that could not be
/// confidently matched to a current port and lets the user pick the live port for it. Mirrors the
/// existing rebind-missing dialogs (Skip leaves bindings untouched; Apply returns the selections).
/// </summary>
public sealed partial class RebindMissingControlMIDIDevicesDialogViewModel : ViewModelBase
{
    public RebindMissingControlMIDIDevicesDialogViewModel(IReadOnlyList<ControlMIDIResolutionRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        foreach (var request in requests)
            Rows.Add(new ControlMIDIDeviceResolutionRowViewModel(request));
    }

    public ObservableCollection<ControlMIDIDeviceResolutionRowViewModel> Rows { get; } = new();

    public IReadOnlyDictionary<ControlMIDIResolutionKey, ControlMIDIPortInfo> BuildSelections()
    {
        var map = new Dictionary<ControlMIDIResolutionKey, ControlMIDIPortInfo>();
        foreach (var row in Rows)
        {
            if (row.SelectedOption is null)
                continue;

            map[new ControlMIDIResolutionKey(row.Request.DeviceInstanceId, row.Request.Direction)] = row.SelectedOption.Port;
        }

        return map;
    }
}
