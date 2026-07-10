using Avalonia.Threading;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using HaPlay.Models;
using HaPlay.Resources;
using HaPlay.Services;
using HaPlay.Views.Dialogs;

namespace HaPlay.ViewModels;

public partial class MainViewModel
{
    private static readonly StringComparison RecoveryPathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    /// <summary>
    /// Startup crash-recovery check (called once the main window is up, from <c>MainWindow.OnOpened</c>). Offers
    /// the most recent crashed session for restore, then ages out stale recovery folders. Best-effort - a
    /// recovery hiccup must never block the app from opening.
    /// </summary>
    public async Task CheckForRecoverableSessionAsync()
    {
        try
        {
            SessionRecoveryService.CleanupExpired(SessionRecoveryService.DefaultRetention, _recovery.SessionId);
            var orphans = SessionRecoveryService.DiscoverOrphans(_recovery.SessionId);
            if (orphans.Count == 0)
                return;

            // Never pop a modal during the CI smoke self-exit - it would block the "first frame → shutdown" gate.
            if (Environment.GetEnvironmentVariable("HAPLAY_SMOKE") is "1" or "true")
                return;

            var owner = TryGetOwnerWindow();
            if (owner is null)
                return;

            var dialog = new RecoverSessionDialog(orphans);
            await dialog.ShowDialog(owner);
            var session = dialog.SelectedSession;
            if (session is null)
                return;

            switch (dialog.Result)
            {
                case RecoverSessionChoice.RestoreIntoOriginal:
                    if (await ConfirmCanReplaceProjectAsync().ConfigureAwait(true))
                        await RestoreRecoverySessionAndDeleteAsync(session, intoOriginal: true).ConfigureAwait(true);
                    break;
                case RecoverSessionChoice.RestoreAsCopy:
                    if (await ConfirmCanReplaceProjectAsync().ConfigureAwait(true))
                        await RestoreRecoverySessionAndDeleteAsync(session, intoOriginal: false).ConfigureAwait(true);
                    break;
                case RecoverSessionChoice.Discard:
                    SessionRecoveryService.Delete(session);
                    break;
                case RecoverSessionChoice.Ignore:
                default:
                    break; // leave it in place; it'll be offered again next launch (subject to retention)
            }
        }
        catch (Exception ex)
        {
            Trace.LogError(ex, "Session recovery: startup check failed");
        }
    }

    internal async Task<bool> RestoreRecoverySessionAndDeleteAsync(RecoverableSession session, bool intoOriginal)
    {
        if (!await RestoreFromRecoveryAsync(session, intoOriginal).ConfigureAwait(true))
            return false;
        SessionRecoveryService.Delete(session);
        return true;
    }

    /// <summary>
    /// Loads a recovered snapshot into the live shell. <paramref name="intoOriginal"/> re-points it at the
    /// crashed session's file (as unsaved changes - the operator then chooses Save to overwrite, or Save As);
    /// otherwise it lands as an untitled copy that leaves the original file untouched. For an untitled recovery
    /// the mirrored scratch scripts are re-materialized first so the control config's script references resolve.
    /// </summary>
    private async Task<bool> RestoreFromRecoveryAsync(RecoverableSession session, bool intoOriginal)
    {
        HaPlayProject project;
        try
        {
            project = await ProjectIO.LoadAsync(session.ProjectFilePath);
        }
        catch (Exception ex)
        {
            ProjectStatus = Strings.Format(nameof(Strings.RecoveryFailedFormat), ex.Message);
            return false;
        }

        var targetPath = intoOriginal ? session.Info.OriginalProjectPath : null;

        if (intoOriginal && session.Info.DirtyScriptPaths.Count > 0
            && !ValidateRecoveredDirtyScripts(project, session))
        {
            ProjectStatus = Strings.Format(nameof(Strings.RecoveryFailedFormat),
                "the recovered script editor buffer is incomplete");
            return false;
        }

        // Untitled/copy restore: seed the scratch script root before applying the config (a saved project's
        // scripts live on disk beside the original, so none were mirrored and none need seeding).
        if (!intoOriginal && project.ControlSystem.Scripts.Count > 0)
        {
            if (session.ScriptsDir is null || !Control.RestoreScratchScriptsFrom(session.ScriptsDir))
            {
                ProjectStatus = Strings.Format(nameof(Strings.RecoveryFailedFormat),
                    "the recovered script files could not be materialized");
                return false;
            }
        }

        ApplyProjectSnapshot(project);
        CurrentProjectPath = targetPath;

        if (intoOriginal)
        {
            // Match the dialog promise: recovered content is unsaved until the operator explicitly saves it.
            _autoSaveSuspendedForRecovery = true;
            AutoSaveStatusIsError = false;
            AutoSaveStatusText = Strings.AutoSaveStatusSuspended;
            if (session.Info.DirtyScriptPaths.Count > 0
                && (session.ScriptsDir is null
                    || !Control.RestoreDirtyScriptBufferFrom(session.ScriptsDir, session.Info.DirtyScriptPaths)))
            {
                ProjectStatus = Strings.Format(nameof(Strings.RecoveryFailedFormat),
                    "the recovered script editor buffer could not be restored");
                return false;
            }
        }
        else
        {
            _autoSaveSuspendedForRecovery = false;
        }
        MarkProjectDirty();

        _ = RefreshCuePreRollAsync();
        var outputStartErrors = await OutputManagement.StartRuntimesForLoadedDefinitionsAsync();
        CuePlayer.RefreshBrokenEndpointFlags();
        await PromptRebindMissingActionEndpointsAsync();
        _ = RefreshAllEndpointHealthAsync();

        if (intoOriginal && !string.IsNullOrEmpty(targetPath))
            PushRecentProject(targetPath!);

        var status = intoOriginal && !string.IsNullOrEmpty(targetPath)
            ? Strings.Format(nameof(Strings.RecoveredIntoOriginalStatusFormat), Path.GetFileName(targetPath!))
            : Strings.RecoveredAsCopyStatus;
        if (outputStartErrors.Count > 0)
            status += " " + Strings.Format(nameof(Strings.ProjectOutputRuntimesStartFailedFormat),
                outputStartErrors.Count, string.Join("; ", outputStartErrors));
        ProjectStatus = status;
        return true;
    }

    private static bool ValidateRecoveredDirtyScripts(HaPlayProject project, RecoverableSession session)
    {
        if (session.ScriptsDir is null)
            return false;
        var root = Path.GetFullPath(session.ScriptsDir) + Path.DirectorySeparatorChar;
        foreach (var dirtyPath in session.Info.DirtyScriptPaths)
        {
            var configured = project.ControlSystem.Scripts.Any(script =>
                string.Equals(script.ScriptPath, dirtyPath, StringComparison.OrdinalIgnoreCase));
            var recovered = Path.GetFullPath(Path.Combine(session.ScriptsDir, dirtyPath));
            if (!configured || !recovered.StartsWith(root, RecoveryPathComparison) || !File.Exists(recovered))
                return false;
        }
        return true;
    }

    private void OnRecoveryStatusChanged(SessionRecoveryStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AutoSaveStatusIsError = status.State == SessionRecoveryState.Failed;
            AutoSaveStatusText = status.State switch
            {
                SessionRecoveryState.Capturing => Strings.AutoSaveStatusSaving,
                SessionRecoveryState.Saved => Strings.Format(nameof(Strings.AutoSaveStatusSavedFormat),
                    (status.Timestamp ?? DateTimeOffset.Now).ToLocalTime().ToString("t")),
                SessionRecoveryState.Failed => Strings.Format(nameof(Strings.AutoSaveStatusFailedFormat),
                    status.Error ?? Strings.UnknownError),
                _ when _autoSaveSuspendedForRecovery => Strings.AutoSaveStatusSuspended,
                _ => Strings.AutoSaveStatusRecoveryOnly,
            };
            NotifyDirtyStateChanged();
        }, DispatcherPriority.Background);
    }

    [RelayCommand]
    private async Task RetryAutoSaveAsync()
    {
        await _recovery.RetryNowAsync().ConfigureAwait(true);
        NotifyDirtyStateChanged();
    }

    [RelayCommand]
    private async Task OpenRecoveryFolderAsync()
    {
        try
        {
            Directory.CreateDirectory(HaPlayStoragePaths.RecoveryRoot);
            var owner = TryGetOwnerWindow();
            if (owner is not null)
                await owner.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(HaPlayStoragePaths.RecoveryRoot));
        }
        catch (Exception ex)
        {
            AutoSaveStatusIsError = true;
            AutoSaveStatusText = Strings.Format(nameof(Strings.AutoSaveStatusFailedFormat), ex.Message);
        }
    }
}
