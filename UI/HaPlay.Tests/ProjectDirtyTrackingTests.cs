using Avalonia.Headless;
using HaPlay.ViewModels;
using S.Control;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Unsaved-changes tracking that drives the close prompt: a fresh project is clean, an edit makes it
/// dirty, New resets the baseline, and per-project auto-save becomes clean only after the exact content hash is
/// verified on disk.</summary>
public sealed class ProjectDirtyTrackingTests
{
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(ProjectDirtyTrackingTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    // These tests write a project via the atomic save (temp → flush → File.Move) and then delete the temp
    // root. On Windows a just-written/renamed file can be held for a few ms by a filter driver (Defender
    // real-time scan, Search indexer, lazy close), so a recursive delete intermittently throws
    // "the process cannot access the file '.show.haplayproj.<guid>.tmp' because it is being used by another
    // process" even though the save itself completed - the 2026-07-09 win-x64 flake. The save is correct; the
    // teardown just has to tolerate the transient handle. Retry briefly (the scan clears in well under a
    // second), then give up - a leftover dir under %TEMP% is harmless and the OS reaps it. The rest of the
    // suite already swallows this with a bare try/catch; this variant also actually deletes once it can.
    private static void DeleteTempDirectoryBestEffort(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50); // let the transient Windows handle clear, then retry
            }
        }
    }

    [Fact]
    public void FreshProject_IsClean()
    {
        DispatchUi(static () =>
        {
            var vm = new MainViewModel();
            Assert.False(vm.IsProjectDirty);
            Assert.False(vm.HasUnsavedChanges);
        });
    }

    [Fact]
    public async Task EditingProject_MakesItDirty_AndNewResetsBaseline()
    {
        await HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(ProjectDirtyTrackingTests).Assembly)
            .DispatchAsync(async () =>
        {
            var vm = new MainViewModel();

            // AutoSaveEnabled is a persisted project field, so toggling it is a genuine unsaved change.
            vm.AutoSaveEnabled = true;
            Assert.True(vm.IsProjectDirty);
            Assert.True(vm.HasUnsavedChanges); // untitled ⇒ auto-save can't write through ⇒ still prompts

            vm.UnsavedChangesPromptOverride = () =>
                Task.FromResult<HaPlay.Views.Dialogs.UnsavedChangesChoice?>(HaPlay.Views.Dialogs.UnsavedChangesChoice.Discard);
            await vm.NewProjectCommand.ExecuteAsync(null);
            Assert.False(vm.IsProjectDirty);
            Assert.False(vm.HasUnsavedChanges);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AutoSave_WithProjectFile_IsCleanOnlyAfterVerifiedFlush()
    {
        await HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(ProjectDirtyTrackingTests).Assembly)
            .DispatchAsync(async () =>
        {
            var vm = new MainViewModel();
            // Simulate an open, saved project by assigning a path (not part of the document hash).
            vm.CurrentProjectPath = Path.Combine(Path.GetTempPath(), "haplay-dirty-" + Guid.NewGuid().ToString("N") + ".haplayproj");
            vm.AutoSaveEnabled = true;

            Assert.True(vm.IsProjectDirty);
            Assert.True(vm.HasUnsavedChanges); // toggle state alone is not proof that a write succeeded

            Assert.True(await vm.ConfirmCanReplaceProjectAsync()); // verifies the exact hash on disk
            Assert.False(vm.IsProjectDirty);
            Assert.False(vm.HasUnsavedChanges);

            // Drop the file association: auto-save can no longer write through, so it must prompt again.
            vm.CurrentProjectPath = null;
            Assert.True(vm.HasUnsavedChanges);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DirtyScriptEditorBuffer_IsUnsavedEvenWhenProjectJsonIsClean()
    {
        var root = Directory.CreateTempSubdirectory("haplay-dirty-script-").FullName;
        var script = Path.Combine(root, "control.mnd");
        await File.WriteAllTextAsync(script, "return 1");
        try
        {
            await HeadlessUnitTestSession
                .GetOrStartForAssembly(typeof(ProjectDirtyTrackingTests).Assembly)
                .Dispatch(() =>
                {
                    var vm = new MainViewModel();
                    vm.CurrentProjectPath = Path.Combine(root, "show.haplayproj");
                    vm.Control.LoadConfig(new ControlSystemConfig
                    {
                        Scripts = [new ControlScriptConfig { Name = "Control", ScriptPath = "control.mnd" }],
                    });
                    vm.Control.SelectedScriptText = "return 2";

                    Assert.True(vm.Control.IsSelectedScriptDirty);
                    Assert.True(vm.HasUnsavedChanges);
                    Assert.EndsWith(" *", vm.ProjectTitle, StringComparison.Ordinal);
                }, CancellationToken.None);
        }
        finally
        {
            DeleteTempDirectoryBestEffort(root);
        }
    }

    [Fact]
    public async Task OpenProject_WhenDiscardPromptIsCancelled_PreservesCurrentProject()
    {
        var target = Path.Combine(Path.GetTempPath(), "haplay-open-" + Guid.NewGuid().ToString("N") + ".haplayproj");
        await ProjectIO.SaveAsync(new HaPlayProject { HaPlayVersion = "target" }, target);
        try
        {
            await HeadlessUnitTestSession
                .GetOrStartForAssembly(typeof(ProjectDirtyTrackingTests).Assembly)
                .DispatchAsync(async () =>
                {
                    var vm = new MainViewModel { AutoSaveEnabled = true }; // dirty untitled project
                    vm.UnsavedChangesPromptOverride = () =>
                        Task.FromResult<HaPlay.Views.Dialogs.UnsavedChangesChoice?>(HaPlay.Views.Dialogs.UnsavedChangesChoice.Cancel);

                    await vm.OpenProjectFromPathAsync(target);

                    Assert.Null(vm.CurrentProjectPath);
                    Assert.True(vm.AutoSaveEnabled);
                    Assert.True(vm.HasUnsavedChanges);
                }, CancellationToken.None);
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public async Task SaveProjectCommand_SavesDirtyScriptBufferBeforeProject()
    {
        var root = Directory.CreateTempSubdirectory("haplay-save-all-").FullName;
        var projectPath = Path.Combine(root, "show.haplayproj");
        var scriptPath = Path.Combine(root, "control.mnd");
        await File.WriteAllTextAsync(scriptPath, "return 1");
        try
        {
            await HeadlessUnitTestSession
                .GetOrStartForAssembly(typeof(ProjectDirtyTrackingTests).Assembly)
                .DispatchAsync(async () =>
                {
                    var vm = new MainViewModel { CurrentProjectPath = projectPath };
                    vm.Control.LoadConfig(new ControlSystemConfig
                    {
                        Scripts = [new ControlScriptConfig { Name = "Control", ScriptPath = "control.mnd" }],
                    });
                    vm.Control.SelectedScriptText = "return 2";

                    await vm.SaveProjectCommand.ExecuteAsync(null);

                    Assert.Equal("return 2", await File.ReadAllTextAsync(scriptPath));
                    Assert.False(vm.Control.IsSelectedScriptDirty);
                    Assert.False(vm.HasUnsavedChanges);
                }, CancellationToken.None);
        }
        finally
        {
            DeleteTempDirectoryBestEffort(root);
        }
    }
}
