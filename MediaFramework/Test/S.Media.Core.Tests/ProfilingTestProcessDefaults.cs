using System.Runtime.CompilerServices;
using S.Media.Core.Diagnostics;

namespace S.Media.Core.Tests;

/// <summary>
/// With <c>MF_MEDIA_PROFILE_CHANNEL_MAP=1</c>, parallel xUnit workers would otherwise share static profiling counters.
/// Force profiling off for the process unless a test opts in via <see cref="ChannelRouteMixProfiling.EnterTestRecordingScope"/> + <see cref="ChannelRouteMixProfiling.SetTestOverride"/>.
/// </summary>
internal static class ProfilingTestProcessDefaults
{
    [ModuleInitializer]
    internal static void DisableChannelRouteProfilingUnlessTestOptsIn()
    {
        ChannelRouteMixProfiling.SetTestOverride(false);
    }
}
