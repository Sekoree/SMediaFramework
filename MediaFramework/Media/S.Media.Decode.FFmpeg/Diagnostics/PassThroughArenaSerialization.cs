using System.Threading;

namespace S.Media.Decode.FFmpeg.Diagnostics;

/// <summary>
/// Opt-in global mutex for <c>S.Media.FFmpeg.Video.PassThroughDescriptorArena</c> rent/return when Treiber
/// contention shows up under <c>MF_MEDIA_PROFILE_PASS_THROUGH_ARENA=1</c> (<see cref="PassThroughArenaProfiling.TreiberCasRetries"/>).
/// Set <c>MF_MEDIA_PASS_THROUGH_ARENA_SERIALIZE=1</c> (or <c>true</c>) to serialize all arena operations on each arena instance
/// (decode thread vs frame release thread still interleave, but never two rent/return calls on the same arena concurrently).
/// </summary>
public static class PassThroughArenaSerialization
{
    private static readonly bool EnvEnabled = ReadEnvFlag("MF_MEDIA_PASS_THROUGH_ARENA_SERIALIZE");
    private static int _overrideState;

    public static bool IsEnabled => Volatile.Read(ref _overrideState) switch
    {
        1 => true,
        2 => false,
        _ => EnvEnabled
    };

    public static void SetTestOverride(bool? enabled) =>
        Volatile.Write(ref _overrideState, enabled is null ? 0 : (enabled.Value ? 1 : 2));

    private static bool ReadEnvFlag(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(v)) return false;
        return v.Equals("1", StringComparison.OrdinalIgnoreCase)
               || v.Equals("true", StringComparison.OrdinalIgnoreCase)
               || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
