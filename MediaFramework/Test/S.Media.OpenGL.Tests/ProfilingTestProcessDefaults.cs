using System.Runtime.CompilerServices;
using S.Media.OpenGL.Diagnostics;

namespace S.Media.OpenGL.Tests;

/// <summary>
/// Same rationale as FFmpeg tests: <c>MF_MEDIA_PROFILE_WIN32_NV12_UPLOAD=1</c> must not pollute parallel xUnit workers.
/// </summary>
internal static class ProfilingTestProcessDefaults
{
    [ModuleInitializer]
    internal static void DisableNv12UploadProfilingUnlessTestOptsIn()
    {
        Nv12Win32SharedHandleGpuUploadProfiling.SetTestOverride(false);
    }
}
