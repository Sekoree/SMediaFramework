using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using HaPlay.Resources;

namespace HaPlay.Views.Dialogs;

/// <summary>One checkbox row in <see cref="ProjectExportDialog"/>.</summary>
public sealed partial class SectionChoice(string id, string label, bool isChecked) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isChecked = isChecked;
}

/// <summary>
/// Save/load rework (2026-06-10): pick which <see cref="ProjectSections"/> a partial project file
/// carries. One checkbox list instead of a menu entry per section combination; the same dialog
/// serves "outputs only", "cue lists only", "audio + MIDI", etc.
/// </summary>
public partial class ProjectExportDialog : Window
{
    public ProjectExportDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
        Choices =
        [
            new SectionChoice(ProjectSections.OutputsAudio, Strings.SectionAudioOutputsLabel, true),
            new SectionChoice(ProjectSections.OutputsVideo, Strings.SectionVideoOutputsLabel, true),
            new SectionChoice(ProjectSections.TargetsMIDI, Strings.SectionMIDITargetsLabel, false),
            new SectionChoice(ProjectSections.TargetsOSC, Strings.SectionOSCTargetsLabel, false),
            new SectionChoice(ProjectSections.Players, Strings.SectionPlayersLabel, false),
            new SectionChoice(ProjectSections.CueLists, Strings.SectionCueListsLabel, false),
            new SectionChoice(ProjectSections.Soundboards, Strings.SectionSoundboardsLabel, false),
            new SectionChoice(ProjectSections.Control, Strings.SectionControlLabel, false),
        ];
        DataContext = this;
    }

    public IReadOnlyList<SectionChoice> Choices { get; }

    /// <summary>Checked leaf ids, or null when the dialog was cancelled / nothing picked.</summary>
    public List<string>? Result { get; private set; }

    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        var picked = Choices.Where(c => c.IsChecked).Select(c => c.Id).ToList();
        Result = picked.Count == 0 ? null : picked;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
