using Silk.NET.OpenGL;

namespace S.Media.Compositor;

/// <summary>
/// A dedicated offscreen GL context for a renderer that lives OUTSIDE any composition - e.g. a
/// visualizer that must keep rendering while compositions are torn down and rebuilt around it.
///
/// <para><strong>Contract:</strong> the factory that produces one is called ON the renderer's own
/// thread; the context is created current on that thread and stays owned by it (GL contexts are
/// thread-affine). All GL work and <see cref="Dispose"/> must run on that same thread.
/// <see cref="MakeCurrent"/> re-asserts the binding (cheap, idempotent) in case another GL user on
/// the process displaced it.</para>
///
/// <para>Lives in the compositor layer so GL-consuming modules (visualizers) can accept a factory
/// without referencing a concrete windowing backend; the SDL3 compositor host provides the standard
/// implementation and the app wires the factory at startup (same graceful-degradation pattern as
/// module availability probes - a null factory or a factory returning null just disables the path).</para>
/// </summary>
public interface IOffscreenGlContext : IDisposable
{
    /// <summary>The GL API bound to this context (valid until <see cref="Dispose"/>).</summary>
    GL Gl { get; }

    /// <summary>Re-asserts this context as current on the owner thread.</summary>
    void MakeCurrent();
}
