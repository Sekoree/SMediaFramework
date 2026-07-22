using System.Text.Json;
using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.Services;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Covers <see cref="SessionRecoveryService"/>'s crash-recovery contract: change-gated capture, per-project
/// write-through, unsaved-script mirroring, orphan discovery (with the process-liveness guard), retention
/// cleanup, and clean-shutdown deletion. Each test injects its own throwaway recovery root, so the shared
/// process state is never touched and tests can't interfere with one another.
/// </summary>
public sealed class SessionRecoveryTests : IDisposable
{
    private readonly string _root;

    public SessionRecoveryTests() => _root = Directory.CreateTempSubdirectory("haplay-recovery-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private SessionRecoveryService NewService() => new(
        buildSnapshot: () => new HaPlayProject(),
        currentProjectPath: () => null,
        autoSaveEnabled: () => false,
        recoveryScripts: () => [],
        haPlayVersion: "test",
        untitledTitle: "Untitled",
        recoveryRoot: _root);

    private string SessionFolder(SessionRecoveryService svc) => Path.Combine(_root, svc.SessionId);

    [Fact]
    public async Task CaptureAsync_WritesRecoveryCopy_ThenSkipsWhenUnchanged()
    {
        var svc = NewService();
        var project = new HaPlayProject { HaPlayVersion = "1.0" };

        Assert.True(await svc.CaptureAsync(project, projectPath: null, autoSaveEnabled: false, scripts: null));

        var copy = Path.Combine(SessionFolder(svc), SessionRecoveryService.ProjectFileName);
        Assert.True(File.Exists(copy));
        Assert.True(File.Exists(Path.Combine(SessionFolder(svc), SessionRecoveryService.SessionFileName)));

        // Identical content ⇒ no rewrite (change detection is a content hash, not a timer).
        Assert.False(await svc.CaptureAsync(project, null, false, null));

        // A real change ⇒ writes again.
        Assert.True(await svc.CaptureAsync(project with { HaPlayVersion = "1.1" }, null, false, null));
    }

    [Fact]
    public async Task CaptureAsync_WritesThrough_OnlyWhenAutoSaveEnabled()
    {
        var svc = NewService();
        var projectPath = Path.Combine(_root, "show.haplayproj");

        // Auto-save off: the original file is never written, but the recovery copy still is.
        Assert.True(await svc.CaptureAsync(new HaPlayProject(), projectPath, autoSaveEnabled: false, scripts: null));
        Assert.False(File.Exists(projectPath));

        // Auto-save on: the change is written through to the project's own file.
        Assert.True(await svc.CaptureAsync(new HaPlayProject { HaPlayVersion = "x" }, projectPath, autoSaveEnabled: true, scripts: null));
        Assert.True(File.Exists(projectPath));
        var written = ProjectIO.Deserialize(await File.ReadAllTextAsync(projectPath));
        Assert.Equal("x", written.HaPlayVersion);
    }

    [Fact]
    public async Task CaptureAsync_RetriesUnchangedProjectAfterWriteThroughFailure()
    {
        var attempts = 0;
        var persistence = new ProjectPersistenceCoordinator(async (project, path, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new IOException("temporary failure");
            await ProjectIO.SaveAsync(project, path, token);
        });
        var svc = new SessionRecoveryService(
            () => new HaPlayProject(), () => null, () => false, () => [], "test", "Untitled",
            recoveryRoot: _root,
            persistProject: (project, path, token) => persistence.PersistAsync(project, path, cancellationToken: token),
            isProjectPersisted: persistence.IsPersisted);
        var path = Path.Combine(_root, "retry.haplayproj");
        var project = new HaPlayProject { HaPlayVersion = "retry" };

        Assert.True(await svc.CaptureAsync(project, path, true)); // recovery copy changed; project write failed
        Assert.False(await svc.CaptureAsync(project, path, true)); // recovery unchanged; project write retried

        Assert.Equal(2, attempts);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CaptureAsync_MirrorsUnsavedScripts()
    {
        var svc = NewService();
        var scriptsRoot = Directory.CreateTempSubdirectory("haplay-scripts-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(scriptsRoot, "main.mond"), "return 1");
            Directory.CreateDirectory(Path.Combine(scriptsRoot, "lib"));
            await File.WriteAllTextAsync(Path.Combine(scriptsRoot, "lib", "helper.mond"), "return 2");

            var scripts = new[]
            {
                new RecoveryScriptFile("main.mond", "return 1", IsDirtyBuffer: true),
                new RecoveryScriptFile(Path.Combine("lib", "helper.mond"), "return 2"),
            };
            Assert.True(await svc.CaptureAsync(new HaPlayProject(), null, false, scripts));

            var mirrored = Path.Combine(SessionFolder(svc), SessionRecoveryService.ScriptsDirName);
            Assert.True(File.Exists(Path.Combine(mirrored, "main.mond")));
            Assert.True(File.Exists(Path.Combine(mirrored, "lib", "helper.mond")));
        }
        finally
        {
            try { Directory.Delete(scriptsRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task CaptureAsync_ScriptOnlyEdit_RefreshesRecoveryMirror()
    {
        var svc = NewService();
        var first = new[] { new RecoveryScriptFile("main.mond", "return 1") };
        var second = new[] { new RecoveryScriptFile("main.mond", "return 2") };

        Assert.True(await svc.CaptureAsync(new HaPlayProject(), null, false, first));
        Assert.True(await svc.CaptureAsync(new HaPlayProject(), null, false, second));

        var mirrored = Path.Combine(SessionFolder(svc), SessionRecoveryService.ScriptsDirName, "main.mond");
        Assert.Equal("return 2", await File.ReadAllTextAsync(mirrored));
    }

    [Fact]
    public async Task DiscoverOrphans_FindsDeadSession_ExcludesLiveAndSelf()
    {
        var svc = NewService();
        await svc.CaptureAsync(new HaPlayProject(), null, false, null);

        // The capture stamped the session with THIS (live) process id, so discovery treats it as a concurrent
        // instance and skips it - that guard is what stops a second running copy being offered as a "crash".
        Assert.DoesNotContain(
            SessionRecoveryService.DiscoverOrphans(excludeSessionId: Guid.NewGuid().ToString("N"), recoveryRoot: _root),
            o => o.Info.SessionId == svc.SessionId);

        // Simulate the owning process having died, then it must surface as recoverable.
        PatchPid(SessionFolder(svc), pid: int.MaxValue);
        var orphans = SessionRecoveryService.DiscoverOrphans(excludeSessionId: Guid.NewGuid().ToString("N"), recoveryRoot: _root);
        Assert.Contains(orphans, o => o.Info.SessionId == svc.SessionId);

        // ...unless it's the current session id (never offer a session to itself).
        Assert.DoesNotContain(
            SessionRecoveryService.DiscoverOrphans(excludeSessionId: svc.SessionId, recoveryRoot: _root),
            o => o.Info.SessionId == svc.SessionId);
    }

    [Fact]
    public void DiscoverOrphans_SkipsFolderWithoutCapturedProject()
    {
        var svc = NewService();
        // Constructed a session folder + session.json but never captured a project.
        PatchPid(SessionFolder(svc), pid: int.MaxValue);
        Assert.DoesNotContain(
            SessionRecoveryService.DiscoverOrphans(excludeSessionId: Guid.NewGuid().ToString("N"), recoveryRoot: _root),
            o => o.Info.SessionId == svc.SessionId);
    }

    [Fact]
    public async Task DiscoverOrphans_UsesScriptBackupLeftByInterruptedSwap()
    {
        var svc = NewService();
        await svc.CaptureAsync(new HaPlayProject(), null, false,
            [new RecoveryScriptFile("main.mond", "return 1")]);
        var folder = SessionFolder(svc);
        var scripts = Path.Combine(folder, SessionRecoveryService.ScriptsDirName);
        var backup = scripts + ".backup-test";
        Directory.Move(scripts, backup);
        PatchPid(folder, int.MaxValue);

        var orphan = Assert.Single(SessionRecoveryService.DiscoverOrphans(Guid.NewGuid().ToString("N"), _root));
        Assert.Equal(backup, orphan.ScriptsDir);
    }

    [Fact]
    public async Task FinalizeCleanShutdown_RemovesSessionFolder()
    {
        var svc = NewService();
        await svc.CaptureAsync(new HaPlayProject(), null, false, null);
        Assert.True(Directory.Exists(SessionFolder(svc)));

        svc.FinalizeCleanShutdown();
        Assert.False(Directory.Exists(SessionFolder(svc)));
    }

    [Fact]
    public async Task FinalizeCleanShutdown_KeepsRecoveryFolderWhenFinalAutoSaveFails()
    {
        var projectPath = Path.Combine(_root, "readonly.haplayproj");
        var persistence = new ProjectPersistenceCoordinator((_, _, _) =>
            Task.FromException(new IOException("disk unavailable")));
        var svc = new SessionRecoveryService(
            () => new HaPlayProject { HaPlayVersion = "dirty" },
            () => projectPath,
            () => true,
            () => [],
            "test",
            "Untitled",
            recoveryRoot: _root,
            persistProject: (project, path, token) => persistence.PersistAsync(project, path, cancellationToken: token),
            isProjectPersisted: persistence.IsPersisted);
        await svc.CaptureAsync(new HaPlayProject { HaPlayVersion = "dirty" }, projectPath, false);

        Assert.False(svc.FinalizeCleanShutdown());
        Assert.True(Directory.Exists(SessionFolder(svc)));
    }

    [Fact]
    public async Task FinalizeCleanShutdown_KeepsRecoveryForForcedExitWithUnsavedWork()
    {
        var svc = NewService();
        await svc.CaptureAsync(new HaPlayProject { HaPlayVersion = "unsaved" }, null, false);

        Assert.False(svc.FinalizeCleanShutdown(retainRecovery: true));
        Assert.True(Directory.Exists(SessionFolder(svc)));
    }

    [Fact]
    public async Task CleanupExpired_PrunesStaleDeadFolders()
    {
        var svc = NewService();
        await svc.CaptureAsync(new HaPlayProject(), null, false, null);
        var folder = SessionFolder(svc);
        // Dead process + an old capture timestamp ⇒ eligible for pruning.
        PatchInfo(folder, i => i with { Pid = int.MaxValue, LastSavedUtc = DateTimeOffset.UtcNow - TimeSpan.FromDays(30) });

        SessionRecoveryService.CleanupExpired(TimeSpan.FromDays(7), excludeSessionId: Guid.NewGuid().ToString("N"), recoveryRoot: _root);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task CleanupExpired_KeepsRecentFolders()
    {
        var svc = NewService();
        await svc.CaptureAsync(new HaPlayProject(), null, false, null);
        var folder = SessionFolder(svc);
        PatchInfo(folder, i => i with { Pid = int.MaxValue, LastSavedUtc = DateTimeOffset.UtcNow });

        SessionRecoveryService.CleanupExpired(TimeSpan.FromDays(7), excludeSessionId: Guid.NewGuid().ToString("N"), recoveryRoot: _root);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task FailedRestore_DoesNotDeleteRecoveryFolder()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "broken-session")).FullName;
        var projectFile = Path.Combine(folder, SessionRecoveryService.ProjectFileName);
        await File.WriteAllTextAsync(projectFile, "not json");
        var recoverable = new RecoverableSession
        {
            FolderPath = folder,
            ProjectFilePath = projectFile,
            Info = new RecoverySessionInfo { SessionId = "broken-session", LastSavedUtc = DateTimeOffset.UtcNow },
        };

        await HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(SessionRecoveryTests).Assembly)
            .DispatchAsync(async () =>
            {
                var vm = new MainViewModel();
                Assert.False(await vm.RestoreRecoverySessionAndDeleteAsync(recoverable, intoOriginal: false));
            }, CancellationToken.None);

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public async Task RestoreIntoOriginal_SuspendsAutoSaveUntilExplicitSave()
    {
        var original = Path.Combine(_root, "original.haplayproj");
        await ProjectIO.SaveAsync(new HaPlayProject { HaPlayVersion = "old" }, original);
        var folder = Directory.CreateDirectory(Path.Combine(_root, "recover-auto")).FullName;
        var projectFile = Path.Combine(folder, SessionRecoveryService.ProjectFileName);
        await ProjectIO.SaveAsync(new HaPlayProject { HaPlayVersion = "recovered", AutoSaveEnabled = true }, projectFile);
        var recoverable = new RecoverableSession
        {
            FolderPath = folder,
            ProjectFilePath = projectFile,
            Info = new RecoverySessionInfo
            {
                SessionId = "recover-auto",
                OriginalProjectPath = original,
                ProjectTitle = "original",
                LastSavedUtc = DateTimeOffset.UtcNow,
            },
        };

        await HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(SessionRecoveryTests).Assembly)
            .DispatchAsync(async () =>
            {
                var vm = new MainViewModel();
                Assert.True(await vm.RestoreRecoverySessionAndDeleteAsync(recoverable, intoOriginal: true));
                Assert.True(vm.AutoSaveEnabled);
                Assert.True(vm.HasUnsavedChanges);

                vm.ShutdownCleanup();
            }, CancellationToken.None);

        Assert.Equal("old", (await ProjectIO.LoadAsync(original)).HaPlayVersion);
    }

    private static void PatchPid(string folder, int pid) => PatchInfo(folder, i => i with { Pid = pid });

    private static void PatchInfo(string folder, Func<RecoverySessionInfo, RecoverySessionInfo> mutate)
    {
        var file = Path.Combine(folder, SessionRecoveryService.SessionFileName);
        var info = JsonSerializer.Deserialize(File.ReadAllText(file), RecoveryJsonContext.Default.RecoverySessionInfo)!;
        File.WriteAllText(file, JsonSerializer.Serialize(mutate(info), RecoveryJsonContext.Default.RecoverySessionInfo));
    }
}
