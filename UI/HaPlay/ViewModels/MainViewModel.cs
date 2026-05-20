using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Models;

namespace HaPlay.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private int _nextPlayerNumber = 1;

    public MainViewModel()
    {
        OutputManagement = new OutputManagementViewModel();
        Players = new ObservableCollection<MediaPlayerViewModel>();
        // First player can't be removed — there's always at least one in the UI.
        Players.Add(CreatePlayer(removable: false));
        SelectedPlayer = Players[0];

        // Phase B (§3.6) — give the Edit dialog a way to ask "is any player playing through this line?".
        // Iterating the Players collection on each probe is fine: outputs are edited rarely, never
        // during a hot loop, and this is the single source of truth that doesn't require a new event.
        OutputManagement.PlaybackUsageProbe =
            line => Players.Any(p => p.IsActivelyPlayingThroughLine(line));

        LoadRecentProjects();
        _appSettings = AppSettings.Load();
        _sidebarCollapsed = _appSettings.SidebarCollapsed;
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Id == _appSettings.LastSelectedWorkspace)
                            ?? WorkspaceItem.Players;
    }

    // ----- Phase B (§12.1): App-shell sidebar -------------------------------------------------

    private readonly AppSettings _appSettings;

    public IReadOnlyList<WorkspaceItem> Workspaces { get; } =
        [WorkspaceItem.Players, WorkspaceItem.Cues, WorkspaceItem.Outputs, WorkspaceItem.Project];

    /// <summary>True when the sidebar is in icon-only mode (~48 px). Toggled by the hamburger or Ctrl+B.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarWidth))]
    private bool _sidebarCollapsed;

    /// <summary>Width binding for the sidebar column. 48 px collapsed, 180 px expanded (§12.1).</summary>
    public double SidebarWidth => SidebarCollapsed ? 48 : 180;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayersWorkspaceSelected))]
    [NotifyPropertyChangedFor(nameof(IsCuesWorkspaceSelected))]
    [NotifyPropertyChangedFor(nameof(IsOutputsWorkspaceSelected))]
    [NotifyPropertyChangedFor(nameof(IsProjectWorkspaceSelected))]
    private WorkspaceItem _selectedWorkspace = WorkspaceItem.Players;

    public bool IsPlayersWorkspaceSelected => SelectedWorkspace == WorkspaceItem.Players;
    public bool IsCuesWorkspaceSelected => SelectedWorkspace == WorkspaceItem.Cues;
    public bool IsOutputsWorkspaceSelected => SelectedWorkspace == WorkspaceItem.Outputs;
    public bool IsProjectWorkspaceSelected => SelectedWorkspace == WorkspaceItem.Project;

    partial void OnSidebarCollapsedChanged(bool value)
    {
        _appSettings.SidebarCollapsed = value;
        _appSettings.Save();
    }

    partial void OnSelectedWorkspaceChanged(WorkspaceItem value)
    {
        _appSettings.LastSelectedWorkspace = value.Id;
        _appSettings.Save();
    }

    [RelayCommand]
    private void ToggleSidebar() => SidebarCollapsed = !SidebarCollapsed;

    [RelayCommand]
    private void SelectWorkspace(WorkspaceItem workspace) => SelectedWorkspace = workspace;

    /// <summary>Ctrl+1..N keyboard handler. Index is 1-based to match the modifier key. (§12.5)</summary>
    public void SelectWorkspaceByIndex(int oneBasedIndex)
    {
        var idx = oneBasedIndex - 1;
        if (idx >= 0 && idx < Workspaces.Count)
            SelectedWorkspace = Workspaces[idx];
    }

    public OutputManagementViewModel OutputManagement { get; }
    public ObservableCollection<MediaPlayerViewModel> Players { get; }

    [ObservableProperty]
    private MediaPlayerViewModel? _selectedPlayer;

    [RelayCommand]
    private void AddPlayer()
    {
        var p = CreatePlayer(removable: true);
        Players.Add(p);
        SelectedPlayer = p;
    }

    private MediaPlayerViewModel CreatePlayer(bool removable)
    {
        var name = $"Player {_nextPlayerNumber++}";
        return new MediaPlayerViewModel(OutputManagement, name, removable ? RemovePlayer : null);
    }

    private void RemovePlayer(MediaPlayerViewModel player)
    {
        var idx = Players.IndexOf(player);
        if (idx < 0) return;
        Players.RemoveAt(idx);
        if (SelectedPlayer == player)
            SelectedPlayer = Players.Count > 0 ? Players[Math.Min(idx, Players.Count - 1)] : null;
    }

    /// <summary>
    /// Phase A — build a <see cref="HaPlayProject"/> snapshot from the current VM state. Pure projection,
    /// no I/O. Phase B will wire this through a File → Save menu; for now tests and programmatic callers
    /// can round-trip via <see cref="ProjectIO"/>.
    /// </summary>
    public HaPlayProject BuildProjectSnapshot() => new()
    {
        SchemaVersion = HaPlayProject.CurrentSchemaVersion,
        HaPlayVersion = typeof(MainViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(MainViewModel).Assembly.GetName().Version?.ToString(),
        Outputs = OutputManagement.Outputs.Select(o => o.Definition).ToList(),
        Players = Players.Select(p => p.BuildPlayerConfigSnapshot()).ToList(),
    };

    /// <summary>
    /// Applies a previously-saved <see cref="HaPlayProject"/> to this VM. Player count is matched (extra
    /// players added, surplus removed); outputs replace the existing list. Routing references inside
    /// player configs are matched by <see cref="OutputDefinition.DisplayName"/> per the existing
    /// <see cref="MediaPlayerConfig.SelectedOutputDisplayNames"/> contract.
    /// </summary>
    /// <remarks>
    /// Phase A intentionally does NOT spin up the underlying runtimes (PortAudio open / NDI start /
    /// preview windows) — that's a Phase B concern. Tests use this to verify round-trip projection
    /// without touching real devices.
    /// </remarks>
    public void ApplyProjectSnapshot(HaPlayProject project)
    {
        // Reconcile outputs: rebuild the list from the project definitions. Phase B will need a richer
        // "rebind missing devices" flow (§7.3, §7.4); for now we just project the definitions.
        OutputManagement.ReplaceDefinitionsForLoad(project.Outputs);

        // Reconcile players: extend or shrink to match the project's player count, then apply each one.
        while (Players.Count < project.Players.Count)
            Players.Add(new MediaPlayerViewModel(OutputManagement, $"Player {_nextPlayerNumber++}", RemovePlayer));
        while (Players.Count > project.Players.Count && Players.Count > 1)
            Players.RemoveAt(Players.Count - 1);

        for (var i = 0; i < project.Players.Count && i < Players.Count; i++)
            Players[i].ApplyPlayerConfigSnapshot(project.Players[i]);
    }

    // ----- Phase B (§7): Project save / load command plumbing --------------------------------

    /// <summary>Path of the project file last saved or opened in this session — drives "Save" vs "Save As".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectTitle))]
    [NotifyPropertyChangedFor(nameof(HasOpenProject))]
    private string? _currentProjectPath;

    /// <summary>Status banner text for the title bar (e.g. "Loaded. Missing outputs: …").</summary>
    [ObservableProperty]
    private string? _projectStatus;

    public ObservableCollection<string> RecentProjects { get; } = new();

    public bool HasOpenProject => !string.IsNullOrEmpty(CurrentProjectPath);

    public string ProjectTitle =>
        string.IsNullOrEmpty(CurrentProjectPath)
            ? "HaPlay — Untitled"
            : $"HaPlay — {Path.GetFileNameWithoutExtension(CurrentProjectPath)}";

    /// <summary>Default location for project files (§7.3 — ~/Documents/HaPlay Projects/).</summary>
    public static string DefaultProjectsFolder
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs))
                docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(docs, "HaPlay Projects");
        }
    }

    [RelayCommand]
    private void NewProject()
    {
        // Reset to a single empty player + no outputs. Don't prompt for unsaved changes yet — Phase B
        // ships the basic flow; "are you sure?" can land in B.5 with the dialog convention pass.
        ApplyProjectSnapshot(new HaPlayProject());
        CurrentProjectPath = null;
        ProjectStatus = null;
    }

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null) return;

        var startFolder = await TryGetStartLocationAsync(owner);
        var opts = new FilePickerOpenOptions
        {
            Title = "Open HaPlay project",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter =
            [
                new FilePickerFileType("HaPlay project") { Patterns = ["*." + ProjectIO.FileExtension] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        };
        var picks = await owner.StorageProvider.OpenFilePickerAsync(opts);
        var picked = picks.FirstOrDefault();
        if (picked is null) return;
        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        await OpenProjectFromPathAsync(path);
    }

    public async Task OpenProjectFromPathAsync(string path)
    {
        HaPlayProject project;
        try
        {
            project = await ProjectIO.LoadAsync(path);
        }
        catch (UnsupportedSchemaVersionException ex)
        {
            ProjectStatus = $"Open failed: {ex.Message}";
            return;
        }
        catch (Exception ex)
        {
            ProjectStatus = $"Open failed: {ex.Message}";
            return;
        }

        // Capture the existing project's output display names BEFORE we replace them, so we can detect
        // routes that reference outputs the new project doesn't have.
        var requestedRoutes = project.Players
            .SelectMany(p => p.SelectedOutputDisplayNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableNames = project.Outputs
            .Select(o => o.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = requestedRoutes.Where(r => !availableNames.Contains(r)).ToList();

        ApplyProjectSnapshot(project);
        var outputStartErrors = await OutputManagement.StartRuntimesForLoadedDefinitionsAsync();
        CurrentProjectPath = path;
        PushRecentProject(path);

        var statusParts = new List<string> { $"Loaded '{Path.GetFileName(path)}'." };
        if (missing.Count > 0)
            statusParts.Add($"{missing.Count} player route(s) reference missing outputs: {string.Join(", ", missing)}.");
        if (outputStartErrors.Count > 0)
            statusParts.Add($"{outputStartErrors.Count} output runtime(s) could not start: {string.Join("; ", outputStartErrors)}.");
        ProjectStatus = string.Join(" ", statusParts);
    }

    [RelayCommand]
    private Task SaveProjectAsync() =>
        string.IsNullOrEmpty(CurrentProjectPath) ? SaveProjectAsAsync() : SaveProjectToPathAsync(CurrentProjectPath!);

    [RelayCommand]
    private async Task SaveProjectAsAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null) return;

        var startFolder = await TryGetStartLocationAsync(owner);
        var opts = new FilePickerSaveOptions
        {
            Title = "Save HaPlay project",
            DefaultExtension = ProjectIO.FileExtension,
            SuggestedFileName = string.IsNullOrEmpty(CurrentProjectPath)
                ? "project." + ProjectIO.FileExtension
                : Path.GetFileName(CurrentProjectPath),
            SuggestedStartLocation = startFolder,
            FileTypeChoices =
            [
                new FilePickerFileType("HaPlay project") { Patterns = ["*." + ProjectIO.FileExtension] },
            ],
        };
        var picked = await owner.StorageProvider.SaveFilePickerAsync(opts);
        if (picked is null) return;
        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        await SaveProjectToPathAsync(path);
    }

    private async Task SaveProjectToPathAsync(string path)
    {
        try
        {
            var snapshot = BuildProjectSnapshot();
            await ProjectIO.SaveAsync(snapshot, path);
            CurrentProjectPath = path;
            PushRecentProject(path);
            ProjectStatus = $"Saved '{Path.GetFileName(path)}'.";
        }
        catch (Exception ex)
        {
            ProjectStatus = $"Save failed: {ex.Message}";
        }
    }

    private async Task<IStorageFolder?> TryGetStartLocationAsync(Window owner)
    {
        try
        {
            var folder = DefaultProjectsFolder;
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            return await owner.StorageProvider.TryGetFolderFromPathAsync(folder);
        }
        catch
        {
            return null;
        }
    }

    private static Window? TryGetOwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow;
        return null;
    }

    private const int RecentProjectsCap = 8;

    private void PushRecentProject(string path)
    {
        // Move-to-front: if it's already in the list, lift it; otherwise prepend. Cap depth so a
        // long-running operator with 200 shows doesn't get an unmanageable menu.
        for (var i = RecentProjects.Count - 1; i >= 0; i--)
        {
            if (string.Equals(RecentProjects[i], path, StringComparison.OrdinalIgnoreCase))
                RecentProjects.RemoveAt(i);
        }

        RecentProjects.Insert(0, path);
        while (RecentProjects.Count > RecentProjectsCap)
            RecentProjects.RemoveAt(RecentProjects.Count - 1);

        try { SaveRecentProjects(); } catch { /* best effort */ }
    }

    /// <summary>Stored alongside the user's profile so it survives reinstalls of the app.</summary>
    private static string RecentProjectsFilePath
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "HaPlay", "recent-projects.json");
        }
    }

    public void LoadRecentProjects()
    {
        try
        {
            var path = RecentProjectsFilePath;
            if (!File.Exists(path))
                return;
            using var stream = File.OpenRead(path);
            var list = JsonSerializer.Deserialize<List<string>>(stream);
            if (list is null) return;
            RecentProjects.Clear();
            foreach (var p in list.Take(RecentProjectsCap))
                RecentProjects.Add(p);
        }
        catch
        {
            /* corrupted recent-projects file: ignore, will be overwritten on next save */
        }
    }

    private void SaveRecentProjects()
    {
        var path = RecentProjectsFilePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, RecentProjects.ToList());
    }

    /// <summary>Bound from the recent-projects menu items.</summary>
    [RelayCommand]
    private Task OpenRecentAsync(string path) => OpenProjectFromPathAsync(path);
}
