using System.Text.Json.Serialization;
using HaPlay.Resources;

namespace HaPlay.Services;

/// <summary>
/// Metadata written as <c>session.json</c> inside each recovery folder (<c>…/HaPlay/recovery/{sessionId}</c>).
/// The folder's mere existence is the crash marker - created when the app starts and deleted on a clean
/// shutdown - so any folder still present on the next launch is an unclean exit. This record lets the restore
/// prompt describe what it found (which project, how recent) and lets discovery skip a folder whose process is
/// still alive (a concurrent second instance, not a crash).
/// </summary>
public sealed record RecoverySessionInfo
{
    /// <summary>Opaque per-launch id; also the recovery folder name.</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>OS process id that owns this session - used as a best-effort liveness check so a still-running
    /// instance is not mistaken for a crashed one.</summary>
    public int Pid { get; init; }

    /// <summary>The <c>.haplayproj</c> this session maps to, or <see langword="null"/> for a never-saved
    /// (untitled) show. Drives whether Restore can offer "into the original file".</summary>
    public string? OriginalProjectPath { get; init; }

    /// <summary>Display title for the restore prompt (file name without extension, or an "Untitled" fallback
    /// supplied by the caller).</summary>
    public string? ProjectTitle { get; init; }

    /// <summary>True when the captured project is untitled and had control scripts living only in the scratch
    /// cache - in that case the recovery folder also carries a <c>scripts/</c> copy so restore is faithful.</summary>
    public bool HasUnsavedScripts { get; init; }

    /// <summary>Project-relative script paths whose editor buffers had not reached disk when captured.</summary>
    public List<string> DirtyScriptPaths { get; init; } = [];

    /// <summary>Best-effort app-version stamp of the process that produced the capture.</summary>
    public string? HaPlayVersion { get; init; }

    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>When the last snapshot was captured - shown in the prompt and used to order multiple orphans
    /// (most recent first) and to age out stale folders.</summary>
    public DateTimeOffset LastSavedUtc { get; init; }
}

/// <summary>A crashed session discovered on the current launch: the folder, its metadata, and the concrete
/// paths of the captured project and (optional) mirrored scratch scripts.</summary>
public sealed record RecoverableSession
{
    public required string FolderPath { get; init; }
    public required RecoverySessionInfo Info { get; init; }

    /// <summary>Absolute path of the captured <c>project.haplayproj</c> inside <see cref="FolderPath"/>.</summary>
    public required string ProjectFilePath { get; init; }

    /// <summary>Absolute path of the mirrored scratch-scripts directory, or <see langword="null"/> when the
    /// captured project was already saved (its scripts live next to the project file on disk).</summary>
    public string? ScriptsDir { get; init; }

    /// <summary>Whether the crashed session had a real project file (⇒ Restore can target the original).</summary>
    public bool HadSavedProject => !string.IsNullOrEmpty(Info.OriginalProjectPath);

    public string DisplayTitle => string.IsNullOrWhiteSpace(Info.ProjectTitle)
        ? Strings.RecoverSessionUntitledLabel
        : Info.ProjectTitle!;

    public string DisplayDetails =>
        $"{Info.LastSavedUtc.ToLocalTime():g} · {Info.HaPlayVersion ?? Strings.RecoveryUnknownVersion}" +
        (HadSavedProject && !File.Exists(Info.OriginalProjectPath)
            ? " · " + Strings.RecoveryOriginalMissing
            : string.Empty);
}

/// <summary>A script file captured alongside a recovery project. <see cref="IsDirtyBuffer"/> distinguishes an
/// editor overlay from a file that was already on disk.</summary>
public sealed record RecoveryScriptFile(string RelativePath, string Contents, bool IsDirtyBuffer = false);

public enum SessionRecoveryState
{
    Idle,
    Capturing,
    Saved,
    RecoveryOnly,
    Failed,
}

public sealed record SessionRecoveryStatus(
    SessionRecoveryState State,
    DateTimeOffset? Timestamp = null,
    string? Error = null,
    string? PersistedHash = null);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RecoverySessionInfo))]
internal partial class RecoveryJsonContext : JsonSerializerContext;
