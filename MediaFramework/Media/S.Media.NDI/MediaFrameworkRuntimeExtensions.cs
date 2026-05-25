using NDILib;
using S.Media.Core.Diagnostics;

namespace S.Media.NDI;

/// <summary>NDI module hook for <see cref="MediaFrameworkRuntime"/>.</summary>
public static class MediaFrameworkRuntimeNdiExtensions
{
    /// <summary>Acquires one NDI runtime scope (released on <see cref="MediaFrameworkRuntime.Shutdown"/>).</summary>
    public static MediaFrameworkRuntimeBuilder UseNDI(this MediaFrameworkRuntimeBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var err = NDIRuntime.Create(out var runtime);
        if (err != 0 || runtime is null)
            throw new InvalidOperationException($"NDI runtime init failed (error {err}).");

        MediaFrameworkRuntime.RegisterShutdown(runtime.Dispose);
        return builder;
    }
}
