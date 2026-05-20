using System.Collections.ObjectModel;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;

namespace HaPlay.ViewModels.Dialogs;

public sealed class ScreenListItem
{
    public required int Index { get; init; }
    public required string Label { get; init; }
}

public sealed class CloneParentChoice
{
    /// <summary>The "None — not a clone" sentinel. Its <see cref="Definition"/> is null.</summary>
    public static readonly CloneParentChoice None = new(null, "None — independent output");

    public CloneParentChoice(LocalVideoOutputDefinition? definition, string label)
    {
        Definition = definition;
        Label = label;
    }

    public LocalVideoOutputDefinition? Definition { get; }
    public string Label { get; }
}

public sealed record VideoEngineChoice(VideoOutputEngine Value, string Label, string Subtitle);

public partial class AddLocalVideoOutputDialogViewModel : ViewModelBase
{
    private Guid? _existingId;

    /// <summary>User-visible engine choices (§12.3). Subtitle exposes the technical name for power users.</summary>
    public VideoEngineChoice[] Engines { get; } =
    [
        new(VideoOutputEngine.AvaloniaOpenGl, "In-app preview", "Avalonia — paints on the UI thread, lives inside the app shell"),
        new(VideoOutputEngine.SdlOpenGl, "Standalone window", "SDL3 — own thread, dedicated fullscreen-capable window"),
    ];

    public VideoSurfaceMode[] SurfaceModes { get; } = Enum.GetValues<VideoSurfaceMode>();

    [ObservableProperty] private string _displayName = "Program output";
    [ObservableProperty] private string? _validationMessage;

    public ObservableCollection<ScreenListItem> Screens { get; } = new();
    public ObservableCollection<CloneParentChoice> CloneParentChoices { get; } = new();

    [ObservableProperty] private ScreenListItem? _selectedScreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Engine))]
    private VideoEngineChoice _selectedEngine = new(
        VideoOutputEngine.SdlOpenGl, "Standalone window",
        "SDL3 — own thread, dedicated fullscreen-capable window");

    /// <summary>Convenience accessor for the legacy enum field; bound from <see cref="SelectedEngine"/>.</summary>
    public VideoOutputEngine Engine => SelectedEngine.Value;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowMetricsEnabled))]
    private VideoSurfaceMode _surfaceMode = VideoSurfaceMode.Windowed;

    public bool WindowMetricsEnabled => SurfaceMode == VideoSurfaceMode.Windowed;

    [ObservableProperty] private int _windowWidth = 1280;
    [ObservableProperty] private int _windowHeight = 720;

    /// <summary>§3.4 clone-of selection. <c>None</c> means this output is independent (not a clone).</summary>
    [ObservableProperty] private CloneParentChoice _selectedCloneParent = CloneParentChoice.None;

    /// <summary>True when editing — the Engine combobox is disabled because switching engines in place
    /// is rejected by <see cref="OutputManagementViewModel.ReconfigureLineAsync"/> (remove + re-add only).</summary>
    public bool IsEditing => _existingId is not null;
    public bool EngineLockedByEdit => IsEditing;

    public string DialogTitle => IsEditing ? "Edit local video output" : "Add local video output";

    public string PrimaryButtonLabel => IsEditing ? "Save" : "Add";

    public void InitializeScreens(IReadOnlyList<Screen> screens)
    {
        Screens.Clear();
        for (var i = 0; i < screens.Count; i++)
        {
            var b = screens[i].Bounds;
            Screens.Add(new ScreenListItem
            {
                Index = i,
                Label = $"#{i} — {b.Width:0}×{b.Height:0} @ ({b.X:0}, {b.Y:0})",
            });
        }

        SelectedScreen = Screens.FirstOrDefault();
    }

    /// <summary>Populate the clone-of dropdown from the management VM's eligible parents (§3.4).</summary>
    public void InitializeCloneParents(IEnumerable<LocalVideoOutputDefinition> potentialParents)
    {
        CloneParentChoices.Clear();
        CloneParentChoices.Add(CloneParentChoice.None);
        foreach (var lv in potentialParents)
            CloneParentChoices.Add(new CloneParentChoice(lv, lv.DisplayName));
        SelectedCloneParent = CloneParentChoice.None;
    }

    /// <summary>Pre-populate the dialog from <paramref name="existing"/>.</summary>
    public void LoadFromExisting(LocalVideoOutputDefinition existing)
    {
        _existingId = existing.Id;
        DisplayName = existing.DisplayName;
        SelectedEngine = Engines.First(e => e.Value == existing.Engine);
        SurfaceMode = existing.SurfaceMode;
        SelectedScreen = Screens.FirstOrDefault(s => s.Index == existing.ScreenIndex)
                         ?? Screens.FirstOrDefault();
        WindowWidth = existing.WindowWidth ?? 1280;
        WindowHeight = existing.WindowHeight ?? 720;

        // Map CloneOfId onto the dropdown choice. Falls back to None when the parent is no longer in
        // the dropdown (e.g. it was removed since the project was saved).
        if (existing.CloneOfId is { } cloneOf)
        {
            var match = CloneParentChoices.FirstOrDefault(c => c.Definition?.Id == cloneOf);
            SelectedCloneParent = match ?? CloneParentChoice.None;
        }
        else
        {
            SelectedCloneParent = CloneParentChoice.None;
        }

        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(EngineLockedByEdit));
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(PrimaryButtonLabel));
    }

    public LocalVideoOutputDefinition? TryCommit()
    {
        ValidationMessage = null;
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ValidationMessage = "Display name is required.";
            return null;
        }

        if (SelectedScreen is null)
        {
            ValidationMessage = "Select a display.";
            return null;
        }

        if (SurfaceMode == VideoSurfaceMode.Windowed)
        {
            if (WindowWidth < 320 || WindowHeight < 240)
            {
                ValidationMessage = "Windowed mode needs a reasonable width and height (at least about 320×240).";
                return null;
            }
        }

        return new LocalVideoOutputDefinition(
            _existingId ?? Guid.NewGuid(),
            DisplayName.Trim(),
            Engine,
            SurfaceMode,
            SelectedScreen.Index,
            SurfaceMode == VideoSurfaceMode.Windowed ? WindowWidth : null,
            SurfaceMode == VideoSurfaceMode.Windowed ? WindowHeight : null,
            CloneOfId: SelectedCloneParent.Definition?.Id);
    }
}
