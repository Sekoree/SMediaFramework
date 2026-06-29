using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using HaPlay.Playback;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>
/// "Subtitles…" dialog for the media-player deck: lists a file's embedded subtitle tracks (none/one/many) plus
/// cue-style font/size/alignment overrides, and produces the <see cref="CueSubtitleSelection"/> list stored on
/// the played <see cref="FilePlaylistItem"/>. Mirrors the cue-editor Subtitles tab for the standalone player.
/// </summary>
public partial class SubtitleSelectionDialogViewModel : ViewModelBase
{
    /// <summary>Embedded subtitle tracks, each with its own none/one/many toggle.</summary>
    public ObservableCollection<CueSubtitleTrackChoice> Tracks { get; } = new();

    public bool HasTracks => Tracks.Count > 0;

    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string? _fontFamily;
    [ObservableProperty] private double? _fontScale;

    /// <summary>NumericUpDown-friendly (decimal?) view of <see cref="FontScale"/>.</summary>
    public decimal? FontScaleValue
    {
        get => FontScale is { } s ? (decimal)s : null;
        set => FontScale = value is null ? null : (double)value;
    }

    partial void OnFontScaleChanged(double? value) => OnPropertyChanged(nameof(FontScaleValue));

    public IReadOnlyList<SubtitleAlignmentChoice> AlignmentChoices { get; } =
    [
        new(null, "Default"),
        new(1, "Bottom-left"), new(2, "Bottom-center"), new(3, "Bottom-right"),
        new(4, "Middle-left"), new(5, "Middle-center"), new(6, "Middle-right"),
        new(7, "Top-left"), new(8, "Top-center"), new(9, "Top-right"),
    ];

    [ObservableProperty] private SubtitleAlignmentChoice? _selectedAlignment;

    /// <summary>Probes <paramref name="mediaPath"/> for subtitle tracks and restores the current selection/overrides.</summary>
    public void Load(string mediaPath, IReadOnlyList<CueSubtitleSelection> current)
    {
        var selected = current.Where(s => s.IsEmbedded).Select(s => s.StreamIndex!.Value).ToHashSet();

        Tracks.Clear();
        foreach (var t in SubtitleTrackProbe.List(mediaPath))
            Tracks.Add(new CueSubtitleTrackChoice(t.StreamIndex, t.DisplayLabel, selected.Contains(t.StreamIndex)));

        var styled = current.FirstOrDefault(s => s.FontFamily is not null || s.FontScale is not null || s.Alignment is not null);
        FontFamily = styled?.FontFamily;
        FontScale = styled?.FontScale;
        SelectedAlignment = AlignmentChoices.FirstOrDefault(a => a.Value == styled?.Alignment) ?? AlignmentChoices[0];
        StatusMessage = Tracks.Count == 0 ? "No embedded subtitle tracks found in this file." : null;
        OnPropertyChanged(nameof(HasTracks));
    }

    /// <summary>The selection list to store on the item (empty = no subtitles).</summary>
    public IReadOnlyList<CueSubtitleSelection> BuildSelections()
    {
        var family = string.IsNullOrWhiteSpace(FontFamily) ? null : FontFamily.Trim();
        return Tracks
            .Where(t => t.IsSelected)
            .Select(t => new CueSubtitleSelection
            {
                StreamIndex = t.StreamIndex,
                Label = t.Label,
                FontFamily = family,
                FontScale = FontScale,
                Alignment = SelectedAlignment?.Value,
            })
            .ToList();
    }
}
