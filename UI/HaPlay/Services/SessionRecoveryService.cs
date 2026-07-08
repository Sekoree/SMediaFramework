using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using HaPlay.Models;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace HaPlay.Services;

/// <summary>
/// Crash-recovery autosave for the HaPlay shell. On a cadence it captures the full project snapshot to a
/// per-launch folder under <c>…/HaPlay/recovery/{sessionId}</c> (writing only when the serialized project
/// actually changed), so an unclean exit leaves a recoverable copy behind. A clean shutdown deletes the
/// folder, so anything left over on the next launch is offered for restore.
/// </summary>
/// <remarks>
/// Design notes:
/// <list type="bullet">
/// <item>There is no central "dirty" flag in the shell, so change detection is a content hash of the
/// serialized snapshot rather than per-view-model instrumentation — a redundant tick writes nothing.</item>
/// <item>The snapshot must be <em>built</em> on the UI thread (it reads observable collections); the returned
/// <see cref="HaPlayProject"/> is a detached record whose serialize + disk write are offloaded off the UI
/// thread by the timer tick.</item>
/// <item>Recovery must never take down the app: every filesystem step is best-effort and logged, and a
/// startup failure disables the service rather than throwing.</item>
/// </list>
/// </remarks>
public sealed class SessionRecoveryService : IDisposable
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal const string ProjectFileName = "project.haplayproj";
    internal const string SessionFileName = "session.json";
    internal const string ScriptsDirName = "scripts";

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.SessionRecovery");
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    /// <summary>Orphan folders older than this (by last capture) are aged out on startup even if never
    /// restored, so an operator who keeps dismissing the prompt doesn't accumulate stale shows forever.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    private readonly TimeSpan _interval;
    private readonly Func<HaPlayProject> _buildSnapshot;
    private readonly Func<string?> _currentProjectPath;
    private readonly Func<bool> _autoSaveEnabled;
    private readonly Func<IReadOnlyList<RecoveryScriptFile>> _recoveryScripts;
    private readonly Func<HaPlayProject, string, CancellationToken, Task<ProjectPersistenceResult>> _persistProject;
    private readonly Func<string?, string, bool> _isProjectPersisted;
    private readonly string _untitledTitle;

    private readonly string _sessionFolder;
    private readonly bool _disabled;
    private RecoverySessionInfo _info;

    private DispatcherTimer? _timer;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private int _captureInFlight;
    private Task<RecoveryCaptureResult>? _activeCapture;
    private string? _lastRecoveryHash;
    private int _finalized;

    public event Action<SessionRecoveryStatus>? StatusChanged;

    /// <param name="buildSnapshot">Projects current shell state to a fresh <see cref="HaPlayProject"/>. Invoked
    /// on the UI thread by the timer.</param>
    /// <param name="currentProjectPath">The open project's file path, or <see langword="null"/> when untitled.</param>
    /// <param name="autoSaveEnabled">The open project's per-project auto-save flag (write-through to its file).</param>
    /// <param name="recoveryScripts">Every configured script plus any dirty editor overlay.</param>
    /// <param name="haPlayVersion">Best-effort version stamp recorded in the session metadata.</param>
    /// <param name="untitledTitle">Localized "Untitled" label used when the project has no file.</param>
    /// <param name="interval">Capture cadence; defaults to 2 s.</param>
    /// <param name="recoveryRoot">Root the session folder lives under; defaults to
    /// <see cref="HaPlayStoragePaths.RecoveryRoot"/>. Tests pass a temp dir for hermetic isolation.</param>
    public SessionRecoveryService(
        Func<HaPlayProject> buildSnapshot,
        Func<string?> currentProjectPath,
        Func<bool> autoSaveEnabled,
        Func<IReadOnlyList<RecoveryScriptFile>> recoveryScripts,
        string? haPlayVersion,
        string untitledTitle,
        TimeSpan? interval = null,
        string? recoveryRoot = null,
        Func<HaPlayProject, string, CancellationToken, Task<ProjectPersistenceResult>>? persistProject = null,
        Func<string?, string, bool>? isProjectPersisted = null)
    {
        _buildSnapshot = buildSnapshot;
        _currentProjectPath = currentProjectPath;
        _autoSaveEnabled = autoSaveEnabled;
        _recoveryScripts = recoveryScripts;
        _untitledTitle = untitledTitle;
        _interval = interval ?? DefaultInterval;

        if (persistProject is null || isProjectPersisted is null)
        {
            var fallbackPersistence = new ProjectPersistenceCoordinator();
            _persistProject = (project, path, token) => fallbackPersistence.PersistAsync(project, path, cancellationToken: token);
            _isProjectPersisted = fallbackPersistence.IsPersisted;
        }
        else
        {
            _persistProject = persistProject;
            _isProjectPersisted = isProjectPersisted;
        }

        var id = Guid.NewGuid().ToString("N");
        _sessionFolder = Path.Combine(recoveryRoot ?? HaPlayStoragePaths.RecoveryRoot, id);
        _info = new RecoverySessionInfo
        {
            SessionId = id,
            Pid = Environment.ProcessId,
            HaPlayVersion = haPlayVersion,
            StartedUtc = DateTimeOffset.UtcNow,
            LastSavedUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            Directory.CreateDirectory(_sessionFolder);
            WriteSessionInfo();
        }
        catch (Exception ex)
        {
            _disabled = true;
            Trace.LogWarning(ex, "Session recovery disabled: could not create session folder {Folder}", _sessionFolder);
        }
    }

    /// <summary>This launch's session id (also the recovery folder name). Discovery excludes it so the live
    /// session is never offered to itself.</summary>
    public string SessionId => _info.SessionId;

    /// <summary>Start the capture timer (UI thread). No-op if construction disabled the service.</summary>
    public void Start()
    {
        if (_disabled || _timer is not null)
            return;

        _timer = new DispatcherTimer { Interval = _interval };
        _timer.Tick += OnTick;
        _timer.Start();
        // Establish a recovery copy immediately. Later edits are picked up by the short cadence, while a launch
        // that crashes before the first timer tick still has a structurally valid session snapshot.
        OnTick(null, EventArgs.Empty);
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        // Single-flight: a slow disk must not let ticks pile up and race on the session files.
        if (Volatile.Read(ref _finalized) != 0 || Interlocked.Exchange(ref _captureInFlight, 1) != 0)
            return;
        try
        {
            StatusChanged?.Invoke(new SessionRecoveryStatus(SessionRecoveryState.Capturing));
            // Build on the UI thread (reads observable collections); serialize + write off it.
            var snapshot = _buildSnapshot();
            var path = _currentProjectPath();
            var autoSave = _autoSaveEnabled();
            var scripts = _recoveryScripts();
            _activeCapture = Task.Run(() => CaptureCoreAsync(snapshot, path, autoSave, scripts));
            var result = await _activeCapture.ConfigureAwait(false);
            PublishStatus(result, autoSave && !string.IsNullOrWhiteSpace(path));
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "Session recovery tick failed");
            StatusChanged?.Invoke(new SessionRecoveryStatus(SessionRecoveryState.Failed, Error: ex.Message));
        }
        finally
        {
            Volatile.Write(ref _captureInFlight, 0);
        }
    }

    /// <summary>
    /// Core capture: serialize the snapshot, and when it differs from the last write, refresh the recovery
    /// copy (+ session metadata + mirrored scratch scripts) and, when the project has auto-save on, write the
    /// change through to its own file. Returns whether the recovery copy was (re)written. Thread-agnostic and
    /// directly unit-testable.
    /// </summary>
    public async Task<bool> CaptureAsync(
        HaPlayProject snapshot,
        string? projectPath,
        bool autoSaveEnabled,
        IReadOnlyList<RecoveryScriptFile>? scripts = null)
        => (await CaptureCoreAsync(snapshot, projectPath, autoSaveEnabled, scripts ?? []).ConfigureAwait(false)).WroteRecovery;

    private async Task<RecoveryCaptureResult> CaptureCoreAsync(
        HaPlayProject snapshot,
        string? projectPath,
        bool autoSaveEnabled,
        IReadOnlyList<RecoveryScriptFile> scripts)
    {
        await _captureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await CaptureCoreUnderGateAsync(snapshot, projectPath, autoSaveEnabled, scripts).ConfigureAwait(false);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private async Task<RecoveryCaptureResult> CaptureCoreUnderGateAsync(
        HaPlayProject snapshot,
        string? projectPath,
        bool autoSaveEnabled,
        IReadOnlyList<RecoveryScriptFile> scripts)
    {
        if (_disabled)
            return new RecoveryCaptureResult(false, false, new InvalidOperationException("Recovery is disabled."), string.Empty);

        string json;
        try
        {
            json = ProjectIO.Serialize(snapshot);
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "Session recovery: snapshot serialization failed");
            return new RecoveryCaptureResult(false, false, ex, string.Empty);
        }

        // Script paths, contents, and dirty-buffer flags are part of the recovery generation. A script-only
        // edit therefore refreshes the mirror even when project JSON is unchanged.
        var recoveryHash = ComputeRecoveryHash(json, projectPath, scripts);
        var recoveryChanged = !string.Equals(recoveryHash, _lastRecoveryHash, StringComparison.Ordinal)
                              || !RecoveryGenerationExists(scripts.Count > 0);
        var wroteRecovery = !recoveryChanged;
        if (recoveryChanged)
        {
            wroteRecovery = await TryWriteRecoveryCopyAsync(json, projectPath, scripts).ConfigureAwait(false);
            if (wroteRecovery)
                _lastRecoveryHash = recoveryHash;
        }

        ProjectPersistenceResult? persistence = null;
        var projectHash = ProjectHash.Of(snapshot);
        if (autoSaveEnabled && !string.IsNullOrEmpty(projectPath))
        {
            if (!_isProjectPersisted(projectPath, projectHash))
                persistence = await _persistProject(snapshot, projectPath!, CancellationToken.None).ConfigureAwait(false);
        }

        var projectPersisted = !autoSaveEnabled || string.IsNullOrEmpty(projectPath)
            || _isProjectPersisted(projectPath, projectHash);
        var error = !wroteRecovery
            ? new IOException("The recovery copy could not be written.")
            : persistence is { Succeeded: false, WasSuperseded: false }
                ? persistence.Error ?? new IOException("The project auto-save could not be written.")
                : persistence is { Published.Errors.Count: > 0 }
                    ? new IOException("Project saved, but one or more show sidecars failed: "
                                      + string.Join("; ", persistence.Published.Errors))
                : null;
        return new RecoveryCaptureResult(recoveryChanged && wroteRecovery, projectPersisted, error, projectHash);
    }

    private bool RecoveryGenerationExists(bool expectsScripts) =>
        File.Exists(Path.Combine(_sessionFolder, ProjectFileName))
        && File.Exists(Path.Combine(_sessionFolder, SessionFileName))
        && (!expectsScripts || Directory.Exists(Path.Combine(_sessionFolder, ScriptsDirName)));

    private async Task<bool> TryWriteRecoveryCopyAsync(
        string json,
        string? projectPath,
        IReadOnlyList<RecoveryScriptFile> scripts)
    {
        try
        {
            Directory.CreateDirectory(_sessionFolder);
            await AtomicWriteTextAsync(Path.Combine(_sessionFolder, ProjectFileName), json).ConfigureAwait(false);

            MirrorScripts(scripts);

            _info = _info with
            {
                OriginalProjectPath = projectPath,
                ProjectTitle = string.IsNullOrEmpty(projectPath)
                    ? _untitledTitle
                    : Path.GetFileNameWithoutExtension(projectPath),
                HasUnsavedScripts = scripts.Count > 0,
                DirtyScriptPaths = scripts.Where(file => file.IsDirtyBuffer)
                    .Select(file => file.RelativePath)
                    .ToList(),
                LastSavedUtc = DateTimeOffset.UtcNow,
            };
            WriteSessionInfo();
            return true;
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "Session recovery: capture to {Folder} failed", _sessionFolder);
            return false;
        }
    }

    /// <summary>Mirrors the untitled project's scratch scripts into <c>{session}/scripts</c> so an unsaved show
    /// restores faithfully. When the project has a file (scripts live on disk beside it) the mirror is removed.</summary>
    private void MirrorScripts(IReadOnlyList<RecoveryScriptFile> scripts)
    {
        var dest = Path.Combine(_sessionFolder, ScriptsDirName);
        if (scripts.Count == 0)
        {
            if (Directory.Exists(dest))
                Directory.Delete(dest, recursive: true);
            return;
        }

        var pending = dest + ".pending-" + Guid.NewGuid().ToString("N");
        var backup = dest + ".backup-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(pending);
            var pendingRoot = Path.GetFullPath(pending) + Path.DirectorySeparatorChar;
            foreach (var script in scripts)
            {
                var target = Path.GetFullPath(Path.Combine(pending, script.RelativePath));
                if (!target.StartsWith(pendingRoot, PathComparison))
                    throw new InvalidDataException($"Recovery script path escapes its root: {script.RelativePath}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, script.Contents, new UTF8Encoding(false));
            }

            // Keep the old complete mirror until the replacement has been fully materialized. If the swap fails,
            // restore it so a transient I/O error never degrades the last known-good recovery generation.
            if (Directory.Exists(dest))
                Directory.Move(dest, backup);
            try
            {
                Directory.Move(pending, dest);
            }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(dest))
                    Directory.Move(backup, dest);
                throw;
            }

            if (Directory.Exists(backup))
                Directory.Delete(backup, recursive: true);
        }
        finally
        {
            try { if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true); } catch { }
            try { if (Directory.Exists(backup) && Directory.Exists(dest)) Directory.Delete(backup, recursive: true); } catch { }
        }
    }

    /// <summary>Immediately captures and, when enabled, persists the current UI snapshot. Called before a close
    /// or project replacement so the decision is based on a verified write rather than the toggle state.</summary>
    public async Task<bool> FlushAutoSaveAsync()
    {
        if (_disabled || !_autoSaveEnabled() || string.IsNullOrWhiteSpace(_currentProjectPath()))
            return false;

        var result = await CaptureNowAsync().ConfigureAwait(true);
        return result.ProjectPersisted;
    }

    public async Task<RecoveryRetryResult> RetryNowAsync()
    {
        var result = await CaptureNowAsync().ConfigureAwait(true);
        return new RecoveryRetryResult(result.Error is null, result.ProjectPersisted, result.Error?.Message);
    }

    private async Task<RecoveryCaptureResult> CaptureNowAsync()
    {
        StatusChanged?.Invoke(new SessionRecoveryStatus(SessionRecoveryState.Capturing));
        var snapshot = _buildSnapshot();
        var path = _currentProjectPath();
        var scripts = _recoveryScripts();
        var autoSave = _autoSaveEnabled() && !string.IsNullOrWhiteSpace(path);
        var result = await CaptureCoreAsync(snapshot, path, autoSave, scripts).ConfigureAwait(true);
        PublishStatus(result, projectWriteExpected: autoSave);
        return result;
    }

    private void PublishStatus(RecoveryCaptureResult result, bool projectWriteExpected)
    {
        var timestamp = DateTimeOffset.UtcNow;
        if (result.Error is not null)
        {
            StatusChanged?.Invoke(new SessionRecoveryStatus(
                SessionRecoveryState.Failed, timestamp, result.Error.Message,
                result.ProjectPersisted ? result.ProjectHash : null));
            return;
        }

        StatusChanged?.Invoke(projectWriteExpected
            ? new SessionRecoveryStatus(SessionRecoveryState.Saved, timestamp, PersistedHash: result.ProjectHash)
            : new SessionRecoveryStatus(SessionRecoveryState.RecoveryOnly, timestamp));
    }

    /// <summary>
    /// Clean-shutdown finalize (UI thread, called from the app teardown). Flushes a final write-through when
    /// auto-save is on so the last sub-cadence edits reach the file, then removes this session's recovery
    /// folder so it is not offered for restore next launch. Idempotent and best-effort.
    /// </summary>
    public bool FinalizeCleanShutdown(bool discardChanges = false, bool retainRecovery = false)
    {
        if (_disabled || Interlocked.Exchange(ref _finalized, 1) != 0)
            return _disabled;

        _timer?.Stop();

        var mayDeleteRecovery = discardChanges;
        try
        {
            // Stop only prevents new ticks. Drain the already-started capture before taking the final snapshot,
            // otherwise its older write can land after this flush or recreate the deleted session directory.
            _activeCapture?.GetAwaiter().GetResult();
            var path = _currentProjectPath();
            if (!discardChanges && _autoSaveEnabled() && !string.IsNullOrEmpty(path))
            {
                var snapshot = _buildSnapshot();
                var scripts = _recoveryScripts();
                var result = CaptureCoreAsync(snapshot, path, true, scripts).GetAwaiter().GetResult();
                mayDeleteRecovery = result.ProjectPersisted;
                PublishStatus(result, projectWriteExpected: true);
            }
            else if (!discardChanges)
                mayDeleteRecovery = !retainRecovery;
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "Session recovery: final auto-save flush failed");
            StatusChanged?.Invoke(new SessionRecoveryStatus(SessionRecoveryState.Failed, Error: ex.Message));
        }

        if (mayDeleteRecovery)
        {
            try
            {
                if (Directory.Exists(_sessionFolder))
                    Directory.Delete(_sessionFolder, recursive: true);
            }
            catch (Exception ex)
            {
                mayDeleteRecovery = false;
                Trace.LogWarning(ex, "Session recovery: removing session folder {Folder} on clean shutdown failed", _sessionFolder);
            }
        }
        return mayDeleteRecovery;
    }

    private void WriteSessionInfo()
    {
        var json = JsonSerializer.Serialize(_info, RecoveryJsonContext.Default.RecoverySessionInfo);
        AtomicWriteTextAsync(Path.Combine(_sessionFolder, SessionFileName), json).GetAwaiter().GetResult();
    }

    public void Dispose() => _timer?.Stop();

    // ----- Discovery / cleanup (static; run at startup) -----------------------------------------

    /// <summary>Finds crashed sessions from previous launches: recovery folders (other than
    /// <paramref name="excludeSessionId"/>) that hold a captured project and whose owning process is no longer
    /// alive. Ordered most-recent capture first. Never throws.</summary>
    public static IReadOnlyList<RecoverableSession> DiscoverOrphans(string? excludeSessionId, string? recoveryRoot = null)
    {
        var root = recoveryRoot ?? HaPlayStoragePaths.RecoveryRoot;
        var result = new List<RecoverableSession>();
        if (!Directory.Exists(root))
            return result;

        foreach (var dir in EnumerateDirectoriesSafe(root))
        {
            var id = Path.GetFileName(dir);
            if (excludeSessionId is not null && string.Equals(id, excludeSessionId, StringComparison.OrdinalIgnoreCase))
                continue;

            var projectFile = Path.Combine(dir, ProjectFileName);
            if (!File.Exists(projectFile))
                continue; // folder created but nothing captured yet — nothing to recover

            var info = TryReadInfo(Path.Combine(dir, SessionFileName));
            if (info is null)
                continue;

            if (IsProcessAlive(info.Pid))
                continue; // a concurrent live instance, not a crash

            var scriptsDir = Path.Combine(dir, ScriptsDirName);
            if (!Directory.Exists(scriptsDir))
            {
                // A process can die in the tiny directory-swap window after the previous complete mirror was
                // renamed to a backup. Surface that backup rather than losing otherwise recoverable scripts.
                scriptsDir = EnumerateDirectoriesSafe(dir)
                    .Where(candidate => Path.GetFileName(candidate).StartsWith(ScriptsDirName + ".backup-", StringComparison.Ordinal))
                    .FirstOrDefault() ?? scriptsDir;
            }
            result.Add(new RecoverableSession
            {
                FolderPath = dir,
                Info = info,
                ProjectFilePath = projectFile,
                ScriptsDir = Directory.Exists(scriptsDir) ? scriptsDir : null,
            });
        }

        result.Sort((a, b) => b.Info.LastSavedUtc.CompareTo(a.Info.LastSavedUtc));
        return result;
    }

    /// <summary>Deletes a recovery folder (after the operator restores or discards it). Best-effort.</summary>
    public static void Delete(RecoverableSession session)
    {
        try
        {
            if (Directory.Exists(session.FolderPath))
                Directory.Delete(session.FolderPath, recursive: true);
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "Session recovery: deleting {Folder} failed", session.FolderPath);
        }
    }

    /// <summary>Ages out stale/dead recovery folders (older than <paramref name="maxAge"/> by last capture, or
    /// malformed) so dismissed crashes don't accumulate. Skips the live session and any still-alive process.</summary>
    public static void CleanupExpired(TimeSpan maxAge, string? excludeSessionId, string? recoveryRoot = null)
    {
        var root = recoveryRoot ?? HaPlayStoragePaths.RecoveryRoot;
        if (!Directory.Exists(root))
            return;

        var cutoff = DateTimeOffset.UtcNow - maxAge;
        foreach (var dir in EnumerateDirectoriesSafe(root))
        {
            var id = Path.GetFileName(dir);
            if (excludeSessionId is not null && string.Equals(id, excludeSessionId, StringComparison.OrdinalIgnoreCase))
                continue;

            var info = TryReadInfo(Path.Combine(dir, SessionFileName));
            if (info is not null && IsProcessAlive(info.Pid))
                continue;

            DateTimeOffset stamp;
            try
            {
                stamp = info?.LastSavedUtc ?? new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir), TimeSpan.Zero);
            }
            catch
            {
                stamp = DateTimeOffset.MinValue; // unreadable → treat as expired
            }

            if (stamp > cutoff)
                continue;

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "Session recovery: pruning stale folder {Folder} failed", dir);
            }
        }
    }

    private static RecoverySessionInfo? TryReadInfo(string sessionFile)
    {
        try
        {
            if (!File.Exists(sessionFile))
                return null;
            using var stream = File.OpenRead(sessionFile);
            return JsonSerializer.Deserialize(stream, RecoveryJsonContext.Default.RecoverySessionInfo);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Best-effort: a session's process counts as alive only when a process with its id exists AND
    /// shares this process's name — so a rebooted machine that recycled the pid onto some unrelated process
    /// still surfaces the crash for recovery.</summary>
    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
            return false;
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            using var other = System.Diagnostics.Process.GetProcessById(pid);
            return string.Equals(other.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // no process with that id
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static string ComputeRecoveryHash(
        string json,
        string? projectPath,
        IReadOnlyList<RecoveryScriptFile> scripts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }

        Add(json);
        Add(projectPath ?? string.Empty);
        foreach (var script in scripts.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            Add(script.RelativePath);
            Add(script.IsDirtyBuffer ? "dirty" : "saved");
            Add(script.Contents);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Writes text atomically (temp in the same directory → flush → move-replace), matching the
    /// durability the rest of the app's saves use so a mid-write crash can't corrupt the target.</summary>
    private static async Task AtomicWriteTextAsync(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        else
            directory = Directory.GetCurrentDirectory();

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(contents).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    private sealed record RecoveryCaptureResult(
        bool WroteRecovery,
        bool ProjectPersisted,
        Exception? Error,
        string ProjectHash);
}

public sealed record RecoveryRetryResult(bool Succeeded, bool ProjectPersisted, string? Error);
