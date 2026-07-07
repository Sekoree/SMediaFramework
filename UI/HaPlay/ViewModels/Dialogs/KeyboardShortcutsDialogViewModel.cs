using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Resources;

namespace HaPlay.ViewModels.Dialogs;

/// <summary>One keyboard shortcut row in the help overlay. <paramref name="Gesture"/> is a key name and is
/// intentionally not localized; <paramref name="Description"/> is localized.</summary>
public sealed record ShortcutEntry(string Gesture, string Description);

/// <summary>A titled group of shortcuts (Project / Workspaces / Playback).</summary>
public sealed record ShortcutGroup(string Title, IReadOnlyList<ShortcutEntry> Entries);

/// <summary>
/// UX-03: the searchable keyboard-shortcut help overlay. This VM is the single documented source of truth
/// for the app's shortcuts — the same gestures wired in <c>MainView</c>'s KeyBindings and the cue/player
/// key handlers. Filtering matches gesture or description, case-insensitively.
/// </summary>
public sealed partial class KeyboardShortcutsDialogViewModel : ObservableObject
{
    private static readonly IReadOnlyList<ShortcutGroup> AllGroups =
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
            new("Space", Strings.ShortcutGo),
            new("Esc", Strings.ShortcutPanic),
        ]),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredGroups))]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    private string _searchText = string.Empty;

    public IReadOnlyList<ShortcutGroup> FilteredGroups => Filter(SearchText);

    public bool HasResults => FilteredGroups.Count > 0;

    private static IReadOnlyList<ShortcutGroup> Filter(string query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q))
            return AllGroups;

        var result = new List<ShortcutGroup>(AllGroups.Count);
        foreach (var group in AllGroups)
        {
            var matches = group.Entries
                .Where(e => e.Gesture.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || e.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 0)
                result.Add(new ShortcutGroup(group.Title, matches));
        }

        return result;
    }
}
