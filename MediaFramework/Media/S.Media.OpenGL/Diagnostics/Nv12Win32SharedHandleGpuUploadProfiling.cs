using System.Threading;

namespace S.Media.OpenGL.Diagnostics;

/// <summary>
/// Opt-in counters for <see cref="Nv12Win32SharedHandleGpuUploader.TryUpload"/> on Windows.
/// Set <c>MF_MEDIA_PROFILE_WIN32_NV12_UPLOAD=1</c> (or <c>true</c>) to enable in apps.
/// Tests call <see cref="SetTestOverride"/> so parallel xUnit workers do not inherit stray env flags.
/// </summary>
public static class Nv12Win32SharedHandleGpuUploadProfiling
{
    private static readonly bool EnvEnabled = ReadEnvFlag("MF_MEDIA_PROFILE_WIN32_NV12_UPLOAD");
    private static int _overrideState;

    public static bool IsEnabled => Volatile.Read(ref _overrideState) switch
    {
        1 => true,
        2 => false,
        _ => EnvEnabled
    };

    public static long UploadAttempts => Volatile.Read(ref _uploadAttempts);
    public static long UploadInteropSuccess => Volatile.Read(ref _interopOk);
    public static long UploadStagingSuccess => Volatile.Read(ref _stagingOk);
    public static long UploadInteropFailedBeforeStaging => Volatile.Read(ref _interopFailedBeforeStaging);
    public static long UploadBothPathsFailed => Volatile.Read(ref _bothFailed);

    private static long _uploadAttempts;
    private static long _interopOk;
    private static long _stagingOk;
    private static long _interopFailedBeforeStaging;
    private static long _bothFailed;

    private static bool ReadEnvFlag(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(v)) return false;
        return v.Equals("1", StringComparison.OrdinalIgnoreCase)
               || v.Equals("true", StringComparison.OrdinalIgnoreCase)
               || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public static void ResetCounters()
    {
        Interlocked.Exchange(ref _uploadAttempts, 0);
        Interlocked.Exchange(ref _interopOk, 0);
        Interlocked.Exchange(ref _stagingOk, 0);
        Interlocked.Exchange(ref _interopFailedBeforeStaging, 0);
        Interlocked.Exchange(ref _bothFailed, 0);
    }

    /// <summary>Force enable (<c>true</c>), disable (<c>false</c>), or follow env (<c>null</c>).</summary>
    public static void SetTestOverride(bool? enabled) =>
        Volatile.Write(ref _overrideState, enabled is null ? 0 : (enabled.Value ? 1 : 2));

    internal static void RecordUploadAttempt()
    {
        if (!IsEnabled) return;
        Interlocked.Increment(ref _uploadAttempts);
    }

    internal static void RecordInteropSuccess()
    {
        if (!IsEnabled) return;
        Interlocked.Increment(ref _interopOk);
    }

    internal static void RecordInteropMissBeforeStaging()
    {
        if (!IsEnabled) return;
        Interlocked.Increment(ref _interopFailedBeforeStaging);
    }

    internal static void RecordStagingSuccess()
    {
        if (!IsEnabled) return;
        Interlocked.Increment(ref _stagingOk);
    }

    internal static void RecordBothPathsFailed()
    {
        if (!IsEnabled) return;
        Interlocked.Increment(ref _bothFailed);
    }
}
