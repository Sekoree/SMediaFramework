using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

/// <summary>List row for OSC/MIDI workspaces — wraps a persisted endpoint with live health.</summary>
public sealed partial class ActionEndpointRowViewModel : ObservableObject
{
    public ActionEndpointRowViewModel(ActionEndpoint endpoint) => Endpoint = endpoint;

    public ActionEndpoint Endpoint { get; private set; }

    [ObservableProperty]
    private ActionEndpointHealthState _health = ActionEndpointHealthState.Unknown;

    [ObservableProperty]
    private string? _healthDetail;

    public bool IsOsc => Endpoint is OscActionEndpoint;

    public bool IsMidi => Endpoint is MidiActionEndpoint;

    /// <summary>Fill color for the list-row status LED.</summary>
    public string HealthColor => Health switch
    {
        ActionEndpointHealthState.Ok => "#4CAF50",
        ActionEndpointHealthState.Failed => "#E53935",
        ActionEndpointHealthState.Checking => "#FFC107",
        _ => "#666666",
    };

    public string HealthToolTip => Health switch
    {
        ActionEndpointHealthState.Ok => HealthDetail ?? Strings.EndpointReachableTooltip,
        ActionEndpointHealthState.Failed => HealthDetail ?? Strings.EndpointUnreachableTooltip,
        ActionEndpointHealthState.Checking => Strings.EndpointCheckingTooltip,
        _ => Strings.EndpointNotCheckedTooltip,
    };

    public void ReplaceEndpoint(ActionEndpoint endpoint) => Endpoint = endpoint;

    partial void OnHealthChanged(ActionEndpointHealthState value)
    {
        OnPropertyChanged(nameof(HealthColor));
        OnPropertyChanged(nameof(HealthToolTip));
    }

    partial void OnHealthDetailChanged(string? value) => OnPropertyChanged(nameof(HealthToolTip));
}
