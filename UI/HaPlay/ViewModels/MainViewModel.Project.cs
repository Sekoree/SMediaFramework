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
using HaPlay.Services;
using S.Control;
using OSCLib;
using PMLib;
using PMLib.Devices;
using PMLib.MessageTypes;
using PMLib.Types;

namespace HaPlay.ViewModels;

public partial class MainViewModel
{
    internal Func<Task<Views.Dialogs.UnsavedChangesChoice?>>? UnsavedChangesPromptOverride { get; set; }

    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (!await ConfirmCanReplaceProjectAsync().ConfigureAwait(true))
            return;
        NewProjectCore();
    }

    private void NewProjectCore()
    {
        ApplyProjectSnapshot(new HaPlayProject());
        CurrentProjectPath = null;
        ProjectStatus = null;
        AutoSaveEnabled = false;
        _autoSaveSuspendedForRecovery = false;
        MarkProjectClean();
    }

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null) return;

        var startFolder = await TryGetStartLocationAsync(owner);
        var opts = new FilePickerOpenOptions
        {
            Title = Strings.OpenProjectDialogTitle,
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.HaPlayProjectFileTypeLabel) { Patterns = ["*." + ProjectIO.FileExtension] },
                new FilePickerFileType(Strings.AllFilesFileTypeLabel) { Patterns = ["*"] },
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
            ProjectStatus = Strings.Format(nameof(Strings.ProjectOpenFailedFormat), ex.Message);
            return;
        }
        catch (Exception ex)
        {
            ProjectStatus = Strings.Format(nameof(Strings.ProjectOpenFailedFormat), ex.Message);
            return;
        }

        if (project.SavedSections is null && !await ConfirmCanReplaceProjectAsync().ConfigureAwait(true))
            return;

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
        _ = RefreshCuePreRollAsync();
        var outputStartErrors = await OutputManagement.StartRuntimesForLoadedDefinitionsAsync();
        if (project.SavedSections is { Count: > 0 } imported)
        {
            // Partial file = a section import into the current show, not a project switch: keep the
            // current project path so a later Save can't overwrite the full project with a fragment.
            ToastCenter.Info(Strings.Format(nameof(Strings.SectionsImportedToastFormat), string.Join(", ", imported)));
        }
        else
        {
            _autoSaveSuspendedForRecovery = false;
            CurrentProjectPath = path;
            PushRecentProject(path);
            // As-loaded from file is the clean baseline; any edit after this (including a rebind below) makes the
            // project dirty and will prompt to save on close.
            MarkProjectClean();
        }

        if (missing.Count > 0)
        {
            var replacementMap = await PromptRebindMissingOutputsAsync(missing);
            if (replacementMap.Count > 0)
            {
                foreach (var player in Players)
                    player.RemapSelectedOutputs(replacementMap);
                missing = missing.Where(m => !replacementMap.ContainsKey(m)).ToList();
            }
        }

        CuePlayer.RefreshBrokenEndpointFlags();
        await PromptRebindMissingActionEndpointsAsync();
        _ = RefreshAllEndpointHealthAsync();

        var statusParts = new List<string> { Strings.Format(nameof(Strings.ProjectLoadedStatusFormat), Path.GetFileName(path)) };
        if (missing.Count > 0)
            statusParts.Add(Strings.Format(nameof(Strings.ProjectMissingRoutesStatusFormat), missing.Count, string.Join(", ", missing)));
        if (outputStartErrors.Count > 0)
            statusParts.Add(Strings.Format(nameof(Strings.ProjectOutputRuntimesStartFailedFormat), outputStartErrors.Count, string.Join("; ", outputStartErrors)));
        ProjectStatus = string.Join(" ", statusParts);
    }

    /// <summary>One Save / Discard / Cancel gate for Close, New, Open, Open Recent, and recovery replacement.</summary>
    public async Task<bool> ConfirmCanReplaceProjectAsync(bool closing = false)
    {
        NotifyDirtyStateChanged();
        if (!HasUnsavedChanges)
            return true;

        // Auto-save is trusted only after this exact content hash has been verified on disk. Dirty editor text
        // deliberately bypasses auto-save because it requires an explicit script-file decision.
        if (AutoSaveEnabled && HasOpenProject && !_autoSaveSuspendedForRecovery && !Control.IsSelectedScriptDirty)
        {
            await _recovery.FlushAutoSaveAsync().ConfigureAwait(true);
            NotifyDirtyStateChanged();
            if (!HasUnsavedChanges)
                return true;
        }

        Views.Dialogs.UnsavedChangesChoice? choice;
        if (UnsavedChangesPromptOverride is not null)
        {
            choice = await UnsavedChangesPromptOverride().ConfigureAwait(true);
        }
        else
        {
            var owner = TryGetOwnerWindow();
            if (owner is null)
                return false;
            choice = await new Views.Dialogs.UnsavedChangesDialog()
                .ShowDialog<Views.Dialogs.UnsavedChangesChoice?>(owner);
        }
        switch (choice)
        {
            case Views.Dialogs.UnsavedChangesChoice.Save:
                await SaveProjectCommand.ExecuteAsync(null);
                NotifyDirtyStateChanged();
                return !HasUnsavedChanges;
            case Views.Dialogs.UnsavedChangesChoice.Discard:
                if (closing)
                    _discardUnsavedOnShutdown = true;
                return true;
            default:
                return false;
        }
    }

    [RelayCommand]
    private Task SaveProjectAsync() =>
        string.IsNullOrEmpty(CurrentProjectPath) ? SaveProjectAsAsync() : SaveProjectToPathAsync(CurrentProjectPath!);

    /// <summary>Save/load rework: export a partial project file carrying only the checked sections
    /// (see <see cref="ProjectSections"/>). Opening such a file later imports just those sections.</summary>
    [RelayCommand]
    private async Task ExportProjectSectionsAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null) return;

        var dialog = new Views.Dialogs.ProjectExportDialog();
        await dialog.ShowDialog(owner);
        if (dialog.Result is not { Count: > 0 } sections)
            return;

        var startFolder = await TryGetStartLocationAsync(owner);
        var opts = new FilePickerSaveOptions
        {
            Title = Strings.ExportSectionsDialogTitle,
            DefaultExtension = ProjectIO.FileExtension,
            SuggestedFileName = Strings.Format(nameof(Strings.SectionExportDefaultFileNameFormat), ProjectIO.FileExtension),
            SuggestedStartLocation = startFolder,
            FileTypeChoices =
            [
                new FilePickerFileType(Strings.HaPlayProjectFileTypeLabel) { Patterns = ["*." + ProjectIO.FileExtension] },
            ],
        };
        var picked = await owner.StorageProvider.SaveFilePickerAsync(opts);
        var path = picked?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            await ProjectIO.SaveAsync(BuildProjectSnapshot(sections), path);
            ProjectStatus = Strings.Format(nameof(Strings.SectionsExportedStatusFormat),
                Path.GetFileName(path), string.Join(", ", ProjectSections.Normalize(sections)));
        }
        catch (Exception ex)
        {
            ProjectStatus = Strings.Format(nameof(Strings.ProjectSaveFailedFormat), ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveProjectAsAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null) return;

        var startFolder = await TryGetStartLocationAsync(owner);
        var opts = new FilePickerSaveOptions
        {
            Title = Strings.SaveProjectDialogTitle,
            DefaultExtension = ProjectIO.FileExtension,
            SuggestedFileName = string.IsNullOrEmpty(CurrentProjectPath)
                ? Strings.Format(nameof(Strings.ProjectDefaultFileNameFormat), ProjectIO.FileExtension)
                : Path.GetFileName(CurrentProjectPath),
            SuggestedStartLocation = startFolder,
            FileTypeChoices =
            [
                new FilePickerFileType(Strings.HaPlayProjectFileTypeLabel) { Patterns = ["*." + ProjectIO.FileExtension] },
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
            if (!Control.TrySaveDirtyScriptBuffer())
            {
                ProjectStatus = Strings.Format(nameof(Strings.ProjectSaveFailedFormat), "the script editor buffer could not be saved");
                return;
            }

            var snapshot = BuildProjectSnapshot();
            var persisted = await PersistProjectForRecoveryAsync(snapshot, path, CancellationToken.None);
            if (!persisted.Succeeded)
            {
                if (persisted.WasSuperseded)
                    ProjectStatus = Strings.Format(nameof(Strings.ProjectSaveFailedFormat), "a newer save replaced this request");
                else
                    ProjectStatus = Strings.Format(nameof(Strings.ProjectSaveFailedFormat),
                        persisted.Error?.Message ?? "unknown persistence error");
                return;
            }
            CurrentProjectPath = path;
            PushRecentProject(path);
            _autoSaveSuspendedForRecovery = false;
            AutoSaveStatusIsError = persisted.Published.Errors.Count > 0;
            AutoSaveStatusText = persisted.Published.Errors.Count > 0
                ? Strings.Format(nameof(Strings.AutoSaveStatusFailedFormat), string.Join("; ", persisted.Published.Errors))
                : AutoSaveEnabled
                    ? Strings.Format(nameof(Strings.AutoSaveStatusSavedFormat), DateTimeOffset.Now.ToString("t"))
                    : Strings.AutoSaveStatusRecoveryOnly;
            MarkProjectClean();

            var statusParts = new List<string>
            {
                Strings.Format(nameof(Strings.ProjectSavedStatusFormat), Path.GetFileName(path)),
            };
            if (persisted.Published.SidecarPaths.Count > 0)
                statusParts.Add(Strings.Format(nameof(Strings.ProjectShowSidecarsSavedFormat), persisted.Published.SidecarPaths.Count));
            if (persisted.Published.Errors.Count > 0)
                statusParts.Add(Strings.Format(nameof(Strings.ProjectShowSidecarFailedFormat),
                    persisted.Published.Errors.Count, string.Join("; ", persisted.Published.Errors)));
            ProjectStatus = string.Join(" ", statusParts);
        }
        catch (Exception ex)
        {
            ProjectStatus = Strings.Format(nameof(Strings.ProjectSaveFailedFormat), ex.Message);
        }
    }

    /// <summary>The single persistence pipeline used by explicit Save and recovery auto-save. Project JSON and
    /// framework sidecars are ordered together by <see cref="ProjectPersistenceCoordinator"/>.</summary>
    private Task<ProjectPersistenceResult> PersistProjectForRecoveryAsync(
        HaPlayProject snapshot,
        string path,
        CancellationToken cancellationToken) =>
        _projectPersistence.PersistAsync(snapshot, path, PublishShowSidecarsAsync, cancellationToken);

    private static async Task<ProjectPublishResult> PublishShowSidecarsAsync(
        HaPlayProject snapshot,
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = new List<string>();
        var sidecars = await ShowDocumentSidecar.WriteAllAsync(snapshot, path, errors).ConfigureAwait(false);
        return new ProjectPublishResult(sidecars, errors);
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

    private async Task PromptRebindMissingActionEndpointsAsync()
    {
        var groups = CuePlayer.GetBrokenEndpointGroups();
        if (groups.Count == 0)
            return;

        var owner = TryGetOwnerWindow();
        if (owner is null)
            return;

        var vm = new Dialogs.RebindMissingActionEndpointsDialogViewModel(groups, ActionEndpoints.ToList());
        if (vm.Rows.Count == 0)
            return;

        var dialog = new Views.Dialogs.RebindMissingActionEndpointsDialog { DataContext = vm };
        var result = await dialog.ShowDialog<IReadOnlyDictionary<Guid, Guid>?>(owner);
        if (result is { Count: > 0 })
            CuePlayer.RemapActionEndpoints(result);
    }

    private async Task<IReadOnlyDictionary<string, string>> PromptRebindMissingOutputsAsync(
        IReadOnlyList<string> missingDisplayNames)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null || missingDisplayNames.Count == 0)
            return new Dictionary<string, string>();

        var available = OutputManagement.Outputs
            .Select(o => o.Definition.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (available.Count == 0)
            return new Dictionary<string, string>();

        var vm = new Dialogs.RebindMissingOutputsDialogViewModel(missingDisplayNames, available);
        var dialog = new Views.Dialogs.RebindMissingOutputsDialog { DataContext = vm };
        var result = await dialog.ShowDialog<IReadOnlyDictionary<string, string>?>(owner);
        return result ?? new Dictionary<string, string>();
    }
}
