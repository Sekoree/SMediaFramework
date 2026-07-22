using S.Media.Compositor;
using S.Media.Core.Diagnostics;
using SilkGL = Silk.NET.OpenGL.GL;

namespace S.Media.Present.SDL3;

/// <summary>
/// The SDL3-backed <see cref="IOffscreenGlContext"/>: a hidden-window GL context on the calling
/// thread, refcounted through <see cref="SharedSDLGlContext"/> (a renderer thread that acquires one
/// gets its own per-thread context; a compositor later created on the same thread would share it).
/// Created via <see cref="TryCreate"/> so callers degrade gracefully when GL is unavailable.
/// </summary>
public sealed class SDL3OffscreenGlContext : IOffscreenGlContext
{
    private SharedSDLGlContext? _context;

    private SDL3OffscreenGlContext(SharedSDLGlContext context) => _context = context;

    /// <summary>Creates a context current on the CALLING thread, or null when GL init fails (headless
    /// host, no driver). Call from the render thread that will own it.</summary>
    public static IOffscreenGlContext? TryCreate()
    {
        try
        {
            return new SDL3OffscreenGlContext(SharedSDLGlContext.Acquire());
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogWarning("SDL3OffscreenGlContext: GL context unavailable: {0}", ex.Message);
            return null;
        }
    }

    public SilkGL Gl => _context?.Gl ?? throw new ObjectDisposedException(nameof(SDL3OffscreenGlContext));

    public void MakeCurrent() => _context?.MakeCurrent();

    public void Dispose()
    {
        var ctx = _context;
        _context = null;
        ctx?.Release();
    }
}
