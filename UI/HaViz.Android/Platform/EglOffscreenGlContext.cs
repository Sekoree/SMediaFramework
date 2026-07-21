using System.Runtime.InteropServices;
using S.Media.Compositor;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenGL;

namespace HaViz.Android.Platform;

/// <summary>
/// Android implementation of <see cref="IOffscreenGlContext"/>: an EGL GLES3 context on a tiny
/// pbuffer, created (and therefore owned, per the interface contract) on the calling thread - the
/// projectM renderer thread. All real rendering targets the renderer's own FBO, so the pbuffer is
/// never drawn to. The <see cref="GL"/> binding resolves entry points through
/// <c>eglGetProcAddress</c> (EGL 1.5 on Android resolves core functions too) with a
/// <c>libGLESv3.so</c> dlsym fallback; the renderer only touches the GL/GLES-common subset and
/// probes BGRA-readback support itself.
/// </summary>
internal sealed class EglOffscreenGlContext : IOffscreenGlContext
{
    private readonly nint _display;
    private readonly nint _surface;
    private readonly nint _context;
    private nint _glesLib;

    public GL Gl { get; }

    private EglOffscreenGlContext(nint display, nint surface, nint context)
    {
        _display = display;
        _surface = surface;
        _context = context;
        Gl = GL.GetApi(new EglNativeContext(this));
    }

    /// <summary>Factory for <c>ProjectMVisualSource.OffscreenGlContextFactory</c> - called on the
    /// renderer's thread. Null (visualizer disabled, app keeps running) when EGL/GLES3 is missing.</summary>
    public static EglOffscreenGlContext? TryCreate()
    {
        var display = Egl.eglGetDisplay(Egl.EGL_DEFAULT_DISPLAY);
        if (display == 0)
            return null;
        if (Egl.eglInitialize(display, out _, out _) == 0)
            return null;

        int[] configAttribs =
        [
            Egl.EGL_RENDERABLE_TYPE, Egl.EGL_OPENGL_ES3_BIT,
            Egl.EGL_SURFACE_TYPE, Egl.EGL_PBUFFER_BIT,
            Egl.EGL_RED_SIZE, 8,
            Egl.EGL_GREEN_SIZE, 8,
            Egl.EGL_BLUE_SIZE, 8,
            Egl.EGL_ALPHA_SIZE, 8,
            Egl.EGL_NONE,
        ];
        if (Egl.eglChooseConfig(display, configAttribs, out var config, 1, out var configCount) == 0
            || configCount < 1)
        {
            return null;
        }

        // Never rendered to (all draws go to the renderer's FBO); minimal but nonzero for drivers
        // that dislike 0-sized surfaces.
        int[] surfaceAttribs = [Egl.EGL_WIDTH, 16, Egl.EGL_HEIGHT, 16, Egl.EGL_NONE];
        var surface = Egl.eglCreatePbufferSurface(display, config, surfaceAttribs);
        if (surface == 0)
            return null;

        _ = Egl.eglBindAPI(Egl.EGL_OPENGL_ES_API);
        int[] contextAttribs = [Egl.EGL_CONTEXT_CLIENT_VERSION, 3, Egl.EGL_NONE];
        var context = Egl.eglCreateContext(display, config, 0, contextAttribs);
        if (context == 0)
        {
            _ = Egl.eglDestroySurface(display, surface);
            return null;
        }

        if (Egl.eglMakeCurrent(display, surface, surface, context) == 0)
        {
            _ = Egl.eglDestroyContext(display, context);
            _ = Egl.eglDestroySurface(display, surface);
            return null;
        }

        return new EglOffscreenGlContext(display, surface, context);
    }

    public void MakeCurrent() => _ = Egl.eglMakeCurrent(_display, _surface, _surface, _context);

    public void Dispose()
    {
        _ = Egl.eglMakeCurrent(_display, 0, 0, 0);
        _ = Egl.eglDestroyContext(_display, _context);
        _ = Egl.eglDestroySurface(_display, _surface);
        if (_glesLib != 0)
        {
            NativeLibrary.Free(_glesLib);
            _glesLib = 0;
        }
    }

    private nint ResolveGlFunction(string name)
    {
        var addr = Egl.eglGetProcAddress(name);
        if (addr != 0)
            return addr;

        if (_glesLib == 0 && !NativeLibrary.TryLoad("libGLESv3.so", out _glesLib))
            return 0;
        return NativeLibrary.TryGetExport(_glesLib, name, out var export) ? export : 0;
    }

    private sealed class EglNativeContext(EglOffscreenGlContext owner) : INativeContext
    {
        public nint GetProcAddress(string procName, int? slot = null) => owner.ResolveGlFunction(procName);

        public bool TryGetProcAddress(string procName, out nint addr, int? slot = null)
        {
            addr = owner.ResolveGlFunction(procName);
            return addr != 0;
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>Minimal EGL 1.4 P/Invoke surface (libEGL.so is a public NDK library).</summary>
internal static partial class Egl
{
    private const string Lib = "libEGL.so";

    internal const nint EGL_DEFAULT_DISPLAY = 0;
    internal const int EGL_NONE = 0x3038;
    internal const int EGL_RED_SIZE = 0x3024;
    internal const int EGL_GREEN_SIZE = 0x3023;
    internal const int EGL_BLUE_SIZE = 0x3022;
    internal const int EGL_ALPHA_SIZE = 0x3021;
    internal const int EGL_SURFACE_TYPE = 0x3033;
    internal const int EGL_PBUFFER_BIT = 0x0001;
    internal const int EGL_RENDERABLE_TYPE = 0x3040;
    internal const int EGL_OPENGL_ES3_BIT = 0x0040;
    internal const int EGL_WIDTH = 0x3057;
    internal const int EGL_HEIGHT = 0x3056;
    internal const int EGL_CONTEXT_CLIENT_VERSION = 0x3098;
    internal const uint EGL_OPENGL_ES_API = 0x30A0;

    [LibraryImport(Lib)]
    internal static partial nint eglGetDisplay(nint displayId);

    [LibraryImport(Lib)]
    internal static partial uint eglInitialize(nint display, out int major, out int minor);

    [LibraryImport(Lib)]
    internal static partial uint eglChooseConfig(
        nint display, int[] attribs, out nint config, int configSize, out int numConfig);

    [LibraryImport(Lib)]
    internal static partial nint eglCreatePbufferSurface(nint display, nint config, int[] attribs);

    [LibraryImport(Lib)]
    internal static partial uint eglBindAPI(uint api);

    [LibraryImport(Lib)]
    internal static partial nint eglCreateContext(nint display, nint config, nint shareContext, int[] attribs);

    [LibraryImport(Lib)]
    internal static partial uint eglMakeCurrent(nint display, nint draw, nint read, nint context);

    [LibraryImport(Lib)]
    internal static partial uint eglDestroySurface(nint display, nint surface);

    [LibraryImport(Lib)]
    internal static partial uint eglDestroyContext(nint display, nint context);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint eglGetProcAddress(string name);
}
