using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;

namespace HaPlay.ViewModels.Dialogs;

public sealed partial class RebindMissingActionEndpointRowViewModel : ObservableObject
{
    public RebindMissingActionEndpointRowViewModel(
        Guid missingEndpointId,
        int affectedCueCount,
        CueActionKind kind,
        IReadOnlyList<ActionEndpoint> replacements)
    {
        MissingEndpointId = missingEndpointId;
        AffectedCueCount = affectedCueCount;
        Kind = kind;
        foreach (var ep in replacements.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            ReplacementOptions.Add(ep);
        SelectedReplacement = ReplacementOptions.FirstOrDefault();
    }

    public Guid MissingEndpointId { get; }

    public int AffectedCueCount { get; }

    public CueActionKind Kind { get; }

    public string MissingLabel => $"{MissingEndpointId:N} ({AffectedCueCount} cue{(AffectedCueCount == 1 ? "" : "s")})";

    public ObservableCollection<ActionEndpoint> ReplacementOptions { get; } = new();

    [ObservableProperty]
    private ActionEndpoint? _selectedReplacement;

    public bool HasReplacement => SelectedReplacement is not null;
}

public partial class RebindMissingActionEndpointsDialogViewModel : ViewModelBase
{
    public RebindMissingActionEndpointsDialogViewModel(
        IReadOnlyList<(Guid MissingId, int CueCount, CueActionKind Kind)> missingGroups,
        IReadOnlyList<ActionEndpoint> allEndpoints)
    {
        foreach (var group in missingGroups)
        {
            var options = allEndpoints
                .Where(e => e switch
                {
                    OscActionEndpoint when group.Kind == CueActionKind.OscOut => true,
                    MidiActionEndpoint when group.Kind == CueActionKind.MidiOut => true,
                    _ => false,
                })
                .ToList();
            Rows.Add(new RebindMissingActionEndpointRowViewModel(
                group.MissingId, group.CueCount, group.Kind, options));
        }
    }

    public ObservableCollection<RebindMissingActionEndpointRowViewModel> Rows { get; } = new();

    public IReadOnlyDictionary<Guid, Guid> BuildReplacementMap()
    {
        var map = new Dictionary<Guid, Guid>();
        foreach (var row in Rows)
        {
            if (!row.HasReplacement || row.SelectedReplacement is null)
                continue;
            map[row.MissingEndpointId] = row.SelectedReplacement.Id;
        }
        return map;
    }
}
