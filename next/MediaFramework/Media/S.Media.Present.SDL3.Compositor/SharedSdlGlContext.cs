using S.Media.Core.Diagnostics;
using SilkGL = Silk.NET.OpenGL.GL;

namespace S.Media.Present.SDL3;

/// <summary>
/// One hidden-window OpenGL context shared by every <see cref="SDL3GLVideoCompositor"/> that runs on
/// the same thread. GL contexts are thread-affine and only one can be current per thread, so several
/// compositors sharing a pump thread — a canvas mixer plus its composition-FX and per-output mapping
/// stages — must share a single context. Giving each its own private context made them overwrite one
/// another's "current" binding, so a compositor would render and read back through the wrong context
/// (the "red / flipped / flickering" corruption). One shared context removes that class of bug and the
/// per-stage window/context churn.
/// </summary>
/// <remarks>
/// <para>
/// Reference-counted and created on the first compositor, torn down with the last. The per-thread
/// instance lives in a <c>[ThreadStatic]</c> slot, so compositors created lazily on the same thread
/// find and share it with no plumbing through the compositor factory.
/// </para>
/// <para>
/// <strong>Threading:</strong> <see cref="Acquire"/>, <see cref="Release"/>, and <see cref="MakeCurrent"/>
/// run on the owner thread only (the thread that first acquired it). All state is therefore single-thread
/// and needs no locking. <see cref="SDL3GLVideoCompositor"/> already pins its GL work to the thread that
/// initialized it, so this holds by construction.
/// </para>
/// </remarks>
internal sealed class SharedSdlGlContext
{
    [ThreadStatic] private static SharedSdlGlContext? _threadCurrent;

    private nint _window;
    private nint _glContext;
    private SilkGL? _gl;
    private int _refCount;

    private SharedSdlGlContext()
    {
    }

    /// <summary>The shared GL API bound to this context (valid until the last holder releases).</summary>
    public SilkGL Gl => _gl ?? throw new InvalidOperationException("SharedSdlGlContext used before initialization.");

    /// <summary>
    /// Acquires the calling thread's shared context, creating it on first use. Balance with exactly one
    /// <see cref="Release"/> on the same thread.
    /// </summary>
    public static SharedSdlGlContext Acquire()
    {
        if (_threadCurrent is { } existing)
        {
            existing._refCount++;
            return existing;
        }

        var ctx = new SharedSdlGlContext();
        ctx.Initialize();
        ctx._refCount = 1;
        _threadCurrent = ctx;
        return ctx;
    }

    /// <summary>Makes this context current on the calling (owner) thread. Cheap and idempotent — used
    /// as defence-in-depth in case an unrelated GL user (e.g. a probe) displaced the binding.</summary>
    public void MakeCurrent()
    {
        if (_window == nint.Zero || _glContext == nint.Zero)
            return;
        if (!SDL.GLMakeCurrent(_window, _glContext))
            throw new InvalidOperationException(
                $"SDL_GL_MakeCurrent failed for shared compositor context: {SDL.GetError()}");
    }

    /// <summary>Releases one reference; destroys the context (and its SDL video ref) when the last holder
    /// lets go. Must run on the owner thread with the caller's GL objects already deleted.</summary>
    public void Release()
    {
        if (_refCount == 0)
            return;
        if (--_refCount > 0)
            return;

        if (_threadCurrent == this)
            _threadCurrent = null;

        // Teardown runs against this context — make it current in case a probe displaced the binding.
        if (_window != nint.Zero && _glContext != nint.Zero)
        {
            try { SDL.GLMakeCurrent(_window, _glContext); }
            catch (Exception ex) { Trace(ex, "make current before teardown"); }
        }

        try { _gl?.Dispose(); }
        catch (Exception ex) { Trace(ex, "GL api dispose"); }
        _gl = null;

        if (_glContext != nint.Zero)
        {
            try { SDL.GLDestroyContext(_glContext); }
            catch (Exception ex) { Trace(ex, "GL context destroy"); }
            _glContext = nint.Zero;
        }

        if (_window != nint.Zero)
        {
            try { SDL.DestroyWindow(_window); }
            catch (Exception ex) { Trace(ex, "window destroy"); }
            _window = nint.Zero;
        }

        try { SDL3Runtime.Release(); }
        catch (Exception ex) { Trace(ex, "SDL release"); }
    }

    private void Initialize()
    {
        SDL3Runtime.Acquire();
        try
        {
            SDL3GLVideoCompositor.ApplyGlAttributes();
            _window = SDL.CreateWindow(
                "S.Media SDL3 GL Compositor",
                16,
                16,
                SDL.WindowFlags.OpenGL | SDL.WindowFlags.Hidden);
            if (_window == nint.Zero)
                throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL.GetError()}");

            _glContext = SDL.GLCreateContext(_window);
            if (_glContext == nint.Zero)
                throw new InvalidOperationException($"SDL_GL_CreateContext failed: {SDL.GetError()}");
            if (!SDL.GLMakeCurrent(_window, _glContext))
                throw new InvalidOperationException($"SDL_GL_MakeCurrent failed: {SDL.GetError()}");

            _gl = SilkGL.GetApi(SDL.GLGetProcAddress);
        }
        catch
        {
            if (_glContext != nint.Zero)
            {
                try { SDL.GLDestroyContext(_glContext); } catch { /* best effort */ }
                _glContext = nint.Zero;
            }
            if (_window != nint.Zero)
            {
                try { SDL.DestroyWindow(_window); } catch { /* best effort */ }
                _window = nint.Zero;
            }
            SDL3Runtime.Release();
            throw;
        }
    }

    private static void Trace(Exception ex, string what) =>
        MediaDiagnostics.LogWarning("SharedSdlGlContext: {0} failed: {1}", what, ex.Message);
}
