using System.Runtime.CompilerServices;
using S.Media.FFmpeg.Diagnostics;

namespace S.Media.FFmpeg.Tests;

/// <summary>
/// Same rationale as Core tests: <c>MF_MEDIA_PROFILE_PASS_THROUGH_ARENA=1</c> must not pollute parallel workers.
/// </summary>
internal static class ProfilingTestProcessDefaults
{
    [ModuleInitializer]
    internal static void DisablePassThroughArenaProfilingUnlessTestOptsIn()
    {
        PassThroughArenaProfiling.SetTestOverride(false);
        PassThroughArenaSerialization.SetTestOverride(false);
    }
}
