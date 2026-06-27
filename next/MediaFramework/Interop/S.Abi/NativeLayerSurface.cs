using System.Runtime.InteropServices;
using System.Text;
using S.Media.Compositor;
using S.Media.Core.Video;
using Silk.NET.OpenGL;

namespace S.Abi;

/// <summary>
/// Adapts a native plugin's <c>MfpLayerSurfaceFactoryVTable</c> — creates a configured surface instance from the
/// opaque <c>config_json</c> blob and wraps it as a managed <see cref="IVideoCompositorLayerSurface"/> via
/// <see cref="NativeLayerSurface"/>. Registered into an <see cref="ICompositorRegistryBuilder"/> by kind.
/// </summary>
public sealed unsafe class NativeLayerSurfaceFactory
{
    private readonly MfpLayerSurfaceFactoryVTable* _vt;
    private readonly void* _self;

    internal NativeLayerSurfaceFactory(nint vtable, nint self)
    {
        _vt = (MfpLayerSurfaceFactoryVTable*)vtable;
        _self = (void*)self;
    }

    public IVideoCompositorLayerSurface? Create(string? configJson)
    {
        if (_vt->Create == null || _vt->SurfaceVTable == null)
            return null;

        var bytes = configJson is null ? null : Utf8(configJson);
        void* instance;
        fixed (byte* p = bytes)
            instance = _vt->Create(_self, p);

        return instance == null ? null : new NativeLayerSurface(_vt->SurfaceVTable, instance);
    }

    private static byte[] Utf8(string s)
    {
        var n = Encoding.UTF8.GetByteCount(s);
        var b = new byte[n + 1];
        Encoding.UTF8.GetBytes(s, b);
        return b;
    }
}

/// <summary>
/// A native plugin GL layer-surface instance: forwards <see cref="ConfigureGl"/>/<see cref="Render"/> to the
/// plugin's <c>MfpLayerSurfaceVTable</c>, which renders directly into the compositor's canvas FBO in the
/// compositor's GL context. The plugin loads GL entry points through the ABI's <c>MfpGlContext.get_proc_address</c>,
/// bridged here to Silk.NET's <c>GL.Context.GetProcAddress</c> via a thread-static GL set around each call (the
/// plugin only resolves procs synchronously inside configure_gl / render, on the compositor thread).
/// </summary>
internal sealed unsafe class NativeLayerSurface : IVideoCompositorLayerSurface
{
    [ThreadStatic] private static GL? s_currentGl;

    private readonly MfpLayerSurfaceVTable* _vt;
    private readonly void* _surface;
    private bool _disposed;

    internal NativeLayerSurface(MfpLayerSurfaceVTable* vt, void* surface)
    {
        _vt = vt;
        _surface = surface;
    }

    public void ConfigureGl(GL gl, VideoFormat canvas)
    {
        if (_disposed || _vt->ConfigureGl == null)
            return;

        var prev = s_currentGl;
        s_currentGl = gl;
        try
        {
            var ctx = MakeContext();
            _vt->ConfigureGl(_surface, &ctx, (uint)canvas.Width, (uint)canvas.Height);
        }
        finally
        {
            s_currentGl = prev;
        }
    }

    public void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity)
    {
        if (_disposed || _vt->Render == null)
            return;

        var prev = s_currentGl;
        s_currentGl = gl;
        try
        {
            var ctx = MakeContext();
            var t = new MfpTransform2D
            {
                A = transform.M11, B = transform.M12,
                C = transform.M21, D = transform.M22,
                Tx = transform.Tx, Ty = transform.Ty,
            };
            _vt->Render(_surface, &ctx, targetFbo, masterTime.Ticks, &t, opacity);
        }
        finally
        {
            s_currentGl = prev;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_vt->Destroy != null)
            _vt->Destroy(_surface);
    }

    private static MfpGlContext MakeContext() => new() { ContextId = 1, GetProcAddress = &GlGetProcAddress };

    [UnmanagedCallersOnly]
    private static void* GlGetProcAddress(byte* name)
    {
        var gl = s_currentGl;
        if (gl is null)
            return null;
        var n = Marshal.PtrToStringUTF8((nint)name);
        return string.IsNullOrEmpty(n) ? null : (void*)gl.Context.GetProcAddress(n);
    }
}
