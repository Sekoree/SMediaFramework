namespace HaPlay.Models;

/// <summary>
/// Central resolver for HaPlay's per-machine cache/config root (<c>…/LocalApplicationData/HaPlay</c>).
/// The empty-special-folder fallback (NativeAOT can return an empty <see cref="Environment.SpecialFolder.LocalApplicationData"/>
/// in minimal/service environments) was previously copy-pasted across <see cref="AppSettings"/>, the recent-projects
/// store and the Control workspace's script scratch cache; new code (session recovery) shares this one copy.
/// </summary>
public static class HaPlayStoragePaths
{
    /// <summary>The <c>HaPlay</c> folder under the user's local application data (created on demand by callers).
    /// Honors the <c>HAPLAY_CACHE_ROOT</c> environment variable (used to sandbox the whole cache under a temp
    /// dir in tests, mirroring <see cref="AppSettings"/>'s <c>HAPLAY_SETTINGS_PATH</c>), and otherwise falls
    /// back to a user-scoped path when the special folder resolves empty so we never write into the process
    /// working directory.</summary>
    public static string LocalAppRoot =>
        Environment.GetEnvironmentVariable("HAPLAY_CACHE_ROOT") is { Length: > 0 } sandbox
            ? sandbox
            : Path.Combine(ResolveLocalBase(), "HaPlay");

    /// <summary>Root under which crashed-session recovery folders live
    /// (<c>…/HaPlay/recovery/{sessionId}</c>).</summary>
    public static string RecoveryRoot => Path.Combine(LocalAppRoot, "recovery");

    private static string ResolveLocalBase()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
            return local;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Path.GetTempPath(), "HaPlay-user")
            : Path.Combine(home, ".local", "share");
    }
}
