using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;
using HaPlay.Resources;

namespace HaPlay.ViewModels.Dialogs;

public sealed record ShortcutEntry(string Gesture, string Description);

public sealed record ShortcutGroup(string Title, IReadOnlyList<ShortcutEntry> Entries);

public sealed partial class CueHotkeyEditorRow(
    string id,
    string description,
    string gesture) : ObservableObject
{
    public string Id { get; } = id;
    public string Description { get; } = description;

    [ObservableProperty]
    private string _gesture = gesture;
}

/// <summary>Searchable shortcut reference plus the per-machine cue-player hotkey editor.</summary>
public sealed partial class KeyboardShortcutsDialogViewModel : ObservableObject
{
    public KeyboardShortcutsDialogViewModel(CueHotkeyProfile? hotkeys = null)
    {
        LoadRows(hotkeys ?? new CueHotkeyProfile());
        foreach (var row in CueHotkeyRows)
            row.PropertyChanged += (_, _) =>
            {
                ValidationMessage = null;
                OnPropertyChanged(nameof(FilteredGroups));
                OnPropertyChanged(nameof(HasResults));
            };
    }

    public List<CueHotkeyEditorRow> CueHotkeyRows { get; } = [];

    [ObservableProperty]
    private string? _validationMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredGroups))]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    private string _searchText = string.Empty;

    public IReadOnlyList<ShortcutGroup> FilteredGroups => Filter(SearchText);

    public bool HasResults => FilteredGroups.Count > 0;

    public void ResetCueHotkeys()
    {
        var defaults = new CueHotkeyProfile();
        foreach (var row in CueHotkeyRows)
            row.Gesture = GestureFor(defaults, row.Id);
        ValidationMessage = null;
    }

    public bool TryBuildCueHotkeys(out CueHotkeyProfile profile)
    {
        profile = new CueHotkeyProfile();
        var used = new Dictionary<(Key Key, KeyModifiers Modifiers), string>();
        foreach (var row in CueHotkeyRows)
        {
            var value = row.Gesture?.Trim() ?? string.Empty;
            if (!CueHotkeyGesture.IsValid(value))
            {
                ValidationMessage = $"'{value}' is not a valid hotkey for {row.Description}. Use forms such as Space, N or Ctrl+Escape.";
                return false;
            }

            if (CueHotkeyGesture.TryParse(value, out var key, out var modifiers))
            {
                var identity = (key, modifiers);
                if (used.TryGetValue(identity, out var other))
                {
                    ValidationMessage = $"{row.Description} conflicts with {other} ({value}).";
                    return false;
                }
                used[identity] = row.Description;
            }

            SetGesture(profile, row.Id, value);
        }

        ValidationMessage = null;
        return true;
    }

    private void LoadRows(CueHotkeyProfile profile)
    {
        CueHotkeyRows.Clear();
        CueHotkeyRows.AddRange(
        [
            new("go", "GO / fire selected or standby cue", profile.Go),
            new("stopPanic", "Stop; press twice quickly to panic", profile.StopThenPanic),
            new("panicNow", "Panic immediately", profile.PanicNow),
            new("standby", "Standby selected cue", profile.StandbySelected),
            new("back", "Move standby back", profile.Back),
            new("pause", "Pause / resume running cues", profile.Pause),
            new("nextPreset", "Next preset for selected Visualizer cue", profile.NextVisualizerPreset),
        ]);
    }

    private IReadOnlyList<ShortcutGroup> AllGroups()
    {
        var current = TryBuildCueHotkeys(out var profile) ? profile : new CueHotkeyProfile();
        // Filtering the reference should not show validation while the operator is halfway through typing.
        ValidationMessage = null;
        return
        [
            new(Strings.ShortcutGroupProject,
            [
                new("Ctrl+N", Strings.MenuNewProjectHeader),
                new("Ctrl+O", Strings.MenuOpenProjectHeader),
                new("Ctrl+S", Strings.MenuSaveProjectHeader),
                new("Ctrl+Shift+S", Strings.MenuSaveProjectAsHeader),
            ]),
            new(Strings.ShortcutGroupWorkspaces,
            [
                new("Ctrl+1 … Ctrl+6", Strings.ShortcutSwitchWorkspace),
                new("Ctrl+B", Strings.MenuToggleSidebarHeader),
            ]),
            new(Strings.ShortcutGroupPlayback,
            [
                new("Space", Strings.ShortcutPlayPause),
                new(profile.Go, Strings.ShortcutGo),
                new($"{profile.StopThenPanic} / {profile.PanicNow}", Strings.ShortcutPanic),
                new(profile.NextVisualizerPreset, "Next preset for the selected Visualizer cue"),
            ]),
        ];
    }

    private IReadOnlyList<ShortcutGroup> Filter(string query)
    {
        var groups = AllGroups();
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q))
            return groups;

        var result = new List<ShortcutGroup>(groups.Count);
        foreach (var group in groups)
        {
            var matches = group.Entries
                .Where(entry => entry.Gesture.Contains(q, StringComparison.OrdinalIgnoreCase)
                                || entry.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 0)
                result.Add(new ShortcutGroup(group.Title, matches));
        }
        return result;
    }

    private static string GestureFor(CueHotkeyProfile profile, string id) => id switch
    {
        "go" => profile.Go,
        "stopPanic" => profile.StopThenPanic,
        "panicNow" => profile.PanicNow,
        "standby" => profile.StandbySelected,
        "back" => profile.Back,
        "pause" => profile.Pause,
        "nextPreset" => profile.NextVisualizerPreset,
        _ => string.Empty,
    };

    private static void SetGesture(CueHotkeyProfile profile, string id, string value)
    {
        switch (id)
        {
            case "go": profile.Go = value; break;
            case "stopPanic": profile.StopThenPanic = value; break;
            case "panicNow": profile.PanicNow = value; break;
            case "standby": profile.StandbySelected = value; break;
            case "back": profile.Back = value; break;
            case "pause": profile.Pause = value; break;
            case "nextPreset": profile.NextVisualizerPreset = value; break;
        }
    }
}
