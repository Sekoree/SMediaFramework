using System.Runtime.InteropServices;

namespace NDILib;

/// <summary>
/// Manages the NDI runtime lifetime. Must be created before any other NDI object and disposed last.
/// </summary>
/// <remarks>
/// <para>
/// Static query methods (<see cref="Version"/>, <see cref="IsSupportedCpu"/>) are safe to call
/// without creating an instance.
/// </para>
/// <para>
/// <b>Reference counting (P2.12):</b> Multiple <see cref="Create"/> calls are safe —
/// only the first calls <c>NDIlib_initialize</c>, and only the last <see cref="Dispose"/>
/// calls <c>NDIlib_destroy</c>.
/// </para>
/// </remarks>
public sealed class NDIRuntime : IDisposable
{
    private static readonly Lock RefLock = new();
    private static int _refCount;
    private bool _disposed;

    private NDIRuntime() { }

    // ------------------------------------------------------------------
    // Static queries — safe without an active instance
    // ------------------------------------------------------------------

    /// <summary>The NDI SDK version string (e.g. "6.x.x.xxxxx").</summary>
    public static string Version
        => Marshal.PtrToStringUTF8(Native.NDIlib_version()) ?? string.Empty;

    /// <summary>Returns <see langword="true"/> if the current CPU supports NDI (requires SSE4.2).</summary>
    public static bool IsSupportedCpu()
        => Native.NDIlib_is_supported_CPU();

    // ------------------------------------------------------------------
    // Factory
    // ------------------------------------------------------------------

    /// <summary>
    /// Initialises the NDI runtime and returns a lifetime scope.
    /// Dispose the returned instance to shut the runtime down.
    /// Multiple calls are reference-counted — only the first actually initialises.
    /// </summary>
    /// <param name="runtime">
    /// On success, the initialised runtime scope. <see langword="null"/> on failure.
    /// </param>
    /// <returns>
    /// <c>0</c> on success; <c>(int)<see cref="NDIErrorCode.NDIRuntimeInitFailed"/></c>
    /// if the NDI runtime is not installed or the CPU does not meet requirements.
    /// </returns>
    public static int Create(out NDIRuntime? runtime)
    {
        runtime = null;
        lock (RefLock)
        {
            if (_refCount == 0)
            {
                if (!Native.NDIlib_initialize())
                    return (int)NDIErrorCode.NDIRuntimeInitFailed;
            }
            _refCount++;
        }

        runtime = new NDIRuntime();
        return 0;
    }

    // ------------------------------------------------------------------
    // Lifetime
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (RefLock)
        {
            _refCount--;
            if (_refCount <= 0)
            {
                Native.NDIlib_destroy();
                _refCount = 0;
            }
        }
    }
}
