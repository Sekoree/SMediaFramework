using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace S.Media.Compositor.OpenGL;

/// <summary>
/// Exports a composited GL framebuffer as a cross-boundary external image (Doc 04 §4 / D7 / OQ2). On
/// Linux/Mesa it uses <c>EGL_MESA_image_dma_buf_export</c> to hand the consumer dmabuf fd(s); the Windows
/// D3D11 DXGI-NT-handle path is the counterpart (structured on <see cref="ExternalImageHandle"/>, wired in
/// the Windows build). Lives in the compositor (not <c>S.Media.Gpu</c>) because it produces the
/// compositor-owned <see cref="ExternalImageHandle"/> currency.
/// </summary>
internal static unsafe partial class GlExternalImageexport_Egl
{
    private const int EGL_NONE = 0x3038;
    private const int EGL_SUCCESS = 0x3000;
    private const uint EGL_GL_TEXTURE_2D = 0x30B1;

    [LibraryImport("libEGL.so.1")] private static partial nint eglGetCurrentDisplay();
    [LibraryImport("libEGL.so.1")] private static partial nint eglGetCurrentContext();
    [LibraryImport("libEGL.so.1")] private static partial int eglGetError();
    [LibraryImport("libEGL.so.1", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint eglGetProcAddress(string name);

    private delegate nint EglCreateImageKHR(nint dpy, nint ctx, uint target, nint buffer, int* attribs);
    private delegate uint EglDestroyImageKHR(nint dpy, nint image);
    private delegate uint EglExportDmabufImageQueryMesa(nint dpy, nint image, int* fourcc, int* numPlanes, ulong* modifiers);
    private delegate uint EglExportDmabufImageMesa(nint dpy, nint image, int* fds, int* strides, int* offsets);

    private static bool _resolved;
    private static EglCreateImageKHR? _createImage;
    private static EglDestroyImageKHR? _destroyImage;
    private static EglExportDmabufImageQueryMesa? _exportQuery;
    private static EglExportDmabufImageMesa? _exportImage;

    private static T? Resolve<T>(string proc) where T : Delegate
    {
        var pfn = eglGetProcAddress(proc);
        return pfn == nint.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(pfn);
    }

    private static bool EnsureResolved()
    {
        if (_resolved)
            return _createImage is not null && _exportQuery is not null && _exportImage is not null;
        _resolved = true;
        _createImage = Resolve<EglCreateImageKHR>("eglCreateImageKHR");
        _destroyImage = Resolve<EglDestroyImageKHR>("eglDestroyImageKHR");
        _exportQuery = Resolve<EglExportDmabufImageQueryMesa>("eglExportDMABUFImageQueryMESA");
        _exportImage = Resolve<EglExportDmabufImageMesa>("eglExportDMABUFImageMESA");
        return _createImage is not null && _destroyImage is not null
            && _exportQuery is not null && _exportImage is not null;
    }

    public static bool TryExport(
        GL gl,
        uint srcFbo,
        VideoFormat format,
        Action<Action> releaseOnOwnerThread,
        out ExternalImageHandle handle)
    {
        handle = null!;
        ArgumentNullException.ThrowIfNull(releaseOnOwnerThread);
        if (!OperatingSystem.IsLinux())
            return false;

        nint display;
        nint context;
        try
        {
            display = eglGetCurrentDisplay();
            context = eglGetCurrentContext();
        }
        catch (DllNotFoundException)
        {
            return false; // no libEGL on this platform/build
        }

        if (display == nint.Zero || context == nint.Zero || !EnsureResolved())
            return false;

        // Copy the composited result into a dedicated, owned RGBA8 texture (the warp/canvas textures are
        // recycled next frame; the consumer may still be reading). GPU→GPU blit — no CPU readback.
        var exportTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, exportTex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
            (uint)format.Width, (uint)format.Height, 0, Silk.NET.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        var exportFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, exportFbo);
        gl.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, exportTex, 0);
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, srcFbo);
        gl.BlitFramebuffer(0, 0, format.Width, format.Height, 0, 0, format.Width, format.Height,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        // Sync (OQ2): the simplest negotiated primitive — the producer fully completes before handing over,
        // so the consumer needs no fence (SyncKind.None). A sync-fd fence is the zero-stall upgrade later.
        gl.Finish();

        Span<int> noAttribs = [EGL_NONE];
        nint image;
        fixed (int* a = noAttribs)
            image = _createImage!(display, context, EGL_GL_TEXTURE_2D, (nint)exportTex, a);
        if (image == nint.Zero || eglGetError() != EGL_SUCCESS)
        {
            DeleteGl(gl, exportFbo, exportTex);
            return false;
        }

        int fourcc = 0, numPlanes = 0;
        ulong modifier = 0;
        if (_exportQuery!(display, image, &fourcc, &numPlanes, &modifier) == 0 || numPlanes != 1)
        {
            _destroyImage!(display, image);
            DeleteGl(gl, exportFbo, exportTex);
            return false;
        }

        int fd = -1, stride = 0, offset = 0;
        if (_exportImage!(display, image, &fd, &stride, &offset) == 0 || fd < 0)
        {
            _destroyImage!(display, image);
            DeleteGl(gl, exportFbo, exportTex);
            return false;
        }

        // Copy into closure-captured locals (the export call took &fd, which can't also be captured).
        var dpy = display;
        var img = image;
        var fboLocal = exportFbo;
        var texLocal = exportTex;
        var fdClose = fd;
        var released = 0;
        handle = new ExternalImageHandle
        {
            HandleType = "dmabuf",
            Width = format.Width,
            Height = format.Height,
            DrmFourcc = (uint)fourcc,
            DrmModifier = modifier,
            DmabufFds = [fdClose],
            Offsets = [offset],
            Strides = [stride],
            SyncKind = ExternalImageSyncKind.None,
            Release = () =>
            {
                if (Interlocked.Exchange(ref released, 1) != 0) return;
                if (fdClose >= 0) CloseFd(fdClose);
                releaseOnOwnerThread(() =>
                {
                    _destroyImage!(dpy, img);
                    DeleteGl(gl, fboLocal, texLocal);
                });
            },
        };
        return true;
    }

    private static void DeleteGl(GL gl, uint fbo, uint tex)
    {
        gl.DeleteFramebuffer(fbo);
        gl.DeleteTexture(tex);
    }

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int CloseFd(int fd);
}

internal static class GlExternalImageExport
{
    /// <summary>
    /// Attempts to export <paramref name="srcFbo"/> as a dmabuf external image when "dmabuf" is among
    /// <paramref name="acceptedHandleTypes"/> and the platform/context supports it. Returns <c>false</c>
    /// so the caller can fall back to a CPU target otherwise.
    /// </summary>
    public static bool TryExportDmabuf(
        GL gl,
        uint srcFbo,
        VideoFormat format,
        IReadOnlyList<string> acceptedHandleTypes,
        Action<Action> releaseOnOwnerThread,
        out ExternalImageHandle handle)
    {
        handle = null!;
        ArgumentNullException.ThrowIfNull(releaseOnOwnerThread);
        var wantsDmabuf = false;
        for (var i = 0; i < acceptedHandleTypes.Count; i++)
            if (string.Equals(acceptedHandleTypes[i], "dmabuf", StringComparison.OrdinalIgnoreCase))
                wantsDmabuf = true;
        if (!wantsDmabuf)
            return false;

        return GlExternalImageexport_Egl.TryExport(gl, srcFbo, format, releaseOnOwnerThread, out handle);
    }
}
