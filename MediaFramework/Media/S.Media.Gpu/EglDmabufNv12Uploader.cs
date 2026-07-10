using System.Runtime.InteropServices;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using Silk.NET.OpenGL;

namespace S.Media.Gpu;

public sealed class YuvDmabufEglInterop
{
    public YuvDmabufEglInterop(nint eglDisplay, Func<string, nint> resolveProcedureAddress)
    {
        EglDisplay = eglDisplay;
        ResolveProcedureAddress = resolveProcedureAddress
                                  ?? throw new ArgumentNullException(nameof(resolveProcedureAddress));
    }

    public nint EglDisplay { get; }
    public Func<string, nint> ResolveProcedureAddress { get; }
}

/// <summary>
/// Linux EGL/GL upload for NV12, P010, and P016 DRM PRIME dma-bufs (split-plane EGL import). Other decoded layouts are not imported here - see
/// <see cref="LinuxDmabufGlHardwareFormats.IsSupportedForPrimeGlImport"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Dispose"/> only marks the instance disposed: EGLImages are created and destroyed inside each
/// <c>TryUpload*</c> path (no long-lived KHR image or texture owned at the field level). There is therefore no separate native teardown step here beyond the contract gate.
/// </para>
/// </remarks>
public sealed unsafe class Nv12DmabufGpuUploader : IDisposable
{
    private const int EGL_NONE = 0x3038;
    private const uint EGL_EXTENSIONS = 0x3055;
    private const uint EGL_LINUX_DMA_BUF_EXT = 0x3270;
    private const int EGL_WIDTH = 0x3057;
    private const int EGL_HEIGHT = 0x3058;
    private const int EGL_LINUX_DRM_FOURCC_EXT = 0x3271;
    private const int EGL_DMA_BUF_PLANE0_FD_EXT = 0x3272;
    private const int EGL_DMA_BUF_PLANE0_OFFSET_EXT = 0x3273;
    private const int EGL_DMA_BUF_PLANE0_PITCH_EXT = 0x3274;
    private const int EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT = 0x3443;
    private const int EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT = 0x3444;
    private const int EGL_SUCCESS_CONST = 0x3000;

    private delegate IntPtr eglQueryString_d(nint dpy, uint name);
    private delegate IntPtr eglCreateImageKHR_d(nint dpy, nint ctx, uint target, nint buf, int* attribs);
    private delegate void eglDestroyImageKHR_d(nint dpy, IntPtr image);
    private delegate int eglGetError_d();

    private delegate void glEGLImageTargetTexStorageEXT_d(uint target, void* image, int* attribs);

    private readonly GL _gl;
    private readonly nint _dpy;

    private readonly eglCreateImageKHR_d _eglCreateImage;
    private readonly eglDestroyImageKHR_d _eglDestroyImage;
    private readonly eglGetError_d _eglGetError;
    private readonly glEGLImageTargetTexStorageEXT_d _glEGLStorage;
    private readonly bool _dmaBufImportModifiersExt;

    private readonly int[] _attribsScratch = new int[32];

    private bool _disposed;

    private Nv12DmabufGpuUploader(GL gl, nint display, eglCreateImageKHR_d eglCreate,
        eglDestroyImageKHR_d eglDestroy, eglGetError_d eglErr, glEGLImageTargetTexStorageEXT_d glEGL,
        bool dmaBufImportModifiersExt)
    {
        _gl = gl;
        _dpy = display;
        _eglCreateImage = eglCreate;
        _eglDestroyImage = eglDestroy;
        _eglGetError = eglErr;
        _glEGLStorage = glEGL;
        _dmaBufImportModifiersExt = dmaBufImportModifiersExt;
    }

    public static Nv12DmabufGpuUploader? TryCreate(GL gl, YuvDmabufEglInterop? egl)
    {
        if (egl == null || egl.EglDisplay == nint.Zero || egl.ResolveProcedureAddress is null)
            return null;
        try
        {
            if (!gl.IsExtensionPresent("GL_EXT_EGL_image_storage"))
                return null;

            var query = GetDelegateOrNull<eglQueryString_d>(egl, "eglQueryString");
            if (query is null)
                return null;

            IntPtr extensionsPtr = query(egl.EglDisplay, EGL_EXTENSIONS);
            string extStr = Marshal.PtrToStringAnsi(extensionsPtr) ?? "";
            if (!extStr.Contains("EGL_EXT_image_dma_buf_import", StringComparison.Ordinal))
                return null;

            var modifiersExt =
                extStr.Contains("EGL_EXT_image_dma_buf_import_modifiers", StringComparison.Ordinal);

            IntPtr eglCreateProc = egl.ResolveProcedureAddress("eglCreateImageKHR");
            IntPtr eglDestroyProc = egl.ResolveProcedureAddress("eglDestroyImageKHR");
            IntPtr eglErrProc = egl.ResolveProcedureAddress("eglGetError");
            if (eglCreateProc == nint.Zero || eglDestroyProc == nint.Zero || eglErrProc == nint.Zero)
                return null;

            var eglCreate = Marshal.GetDelegateForFunctionPointer<eglCreateImageKHR_d>(eglCreateProc);
            var eglDestroy = Marshal.GetDelegateForFunctionPointer<eglDestroyImageKHR_d>(eglDestroyProc);
            var eglErrLocal = Marshal.GetDelegateForFunctionPointer<eglGetError_d>(eglErrProc);

            IntPtr glEGLAddr = gl.Context.GetProcAddress("glEGLImageTargetTexStorageEXT");
            if (glEGLAddr == nint.Zero)
                return null;

            var glEGL = Marshal.GetDelegateForFunctionPointer<glEGLImageTargetTexStorageEXT_d>(glEGLAddr);
            return new Nv12DmabufGpuUploader(gl, egl.EglDisplay, eglCreate, eglDestroy, eglErrLocal, glEGL,
                modifiersExt);
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "Nv12DmabufGpuUploader.TryCreate");
            return null;
        }
    }

    private static T? GetDelegateOrNull<T>(YuvDmabufEglInterop egl, string proc) where T : Delegate
    {
        nint pfn = egl.ResolveProcedureAddress(proc);
        if (pfn == nint.Zero)
            return null;
        return Marshal.GetDelegateForFunctionPointer<T>(pfn);
    }

    /// <inheritdoc />
    public bool TryUpload(uint texYId, uint texUvId, in VideoFormat format, DmabufNv12Backing dma)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (format.PixelFormat != global::S.Media.Core.Video.PixelFormat.Nv12)
        {
            var blocker = LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker(format.PixelFormat)!;
            throw new ArgumentException($"expected NV12 renderer format ({format.PixelFormat}): {blocker}", nameof(format));
        }
        if ((dma.YPlaneDrmFormatModifier != 0 || dma.UvPlaneDrmFormatModifier != 0) && !_dmaBufImportModifiersExt)
            return false;

        int cw = PixelFormatInfo.ChromaWidth420(format.Width);
        int ch = PixelFormatInfo.ChromaHeight420(format.Height);
        if (!AppendPlaneAttribs(dma.YPlaneFd, dma.YPlaneOffsetBytes, dma.YPlanePitchBytes, format.Width,
                format.Height,
                DrmPixelFormats.R8,
                dma.YPlaneDrmFormatModifier))
            return false;

        IntPtr eglY;
        fixed (int* attrib = _attribsScratch)
        {
            eglY = _eglCreateImage(_dpy, IntPtr.Zero, EGL_LINUX_DMA_BUF_EXT, IntPtr.Zero, attrib);
        }

        if (eglY == IntPtr.Zero || _eglGetError() != EGL_SUCCESS_CONST)
            return false;

        if (!AppendPlaneAttribs(dma.UvPlaneFd, dma.UvPlaneOffsetBytes, dma.UvPlanePitchBytes, cw, ch,
                DrmPixelFormats.Gr88,
                dma.UvPlaneDrmFormatModifier))
        {
            _eglDestroyImage(_dpy, eglY);
            return false;
        }

        IntPtr eglUv;
        fixed (int* attribUv = _attribsScratch)
        {
            eglUv = _eglCreateImage(_dpy, IntPtr.Zero, EGL_LINUX_DMA_BUF_EXT, IntPtr.Zero, attribUv);
        }

        if (eglUv == IntPtr.Zero || _eglGetError() != EGL_SUCCESS_CONST)
        {
            _eglDestroyImage(_dpy, eglY);
            return false;
        }

        uint glTarget = (uint)TextureTarget.Texture2D;
        bool glOk;

        _gl.BindTexture(TextureTarget.Texture2D, texYId);
        _glEGLStorage(glTarget, (void*)(nint)eglY, null);
        glOk = _gl.GetError() == GLEnum.NoError;

        _gl.BindTexture(TextureTarget.Texture2D, texUvId);
        if (glOk)
        {
            _glEGLStorage(glTarget, (void*)(nint)eglUv, null);
            glOk = _gl.GetError() == GLEnum.NoError;
        }

        _eglDestroyImage(_dpy, eglY);
        _eglDestroyImage(_dpy, eglUv);

        return glOk;
    }

    /// <summary>P010 semi-planar: Y as <see cref="DrmPixelFormats.R16"/>, UV as <see cref="DrmPixelFormats.Gr1616"/>.</summary>
    public bool TryUploadP010(uint texYId, uint texUvId, in VideoFormat format, DmabufP010Backing dma)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (format.PixelFormat != global::S.Media.Core.Video.PixelFormat.P010)
        {
            var blocker = LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker(format.PixelFormat)!;
            throw new ArgumentException($"expected P010 renderer format ({format.PixelFormat}): {blocker}", nameof(format));
        }
        if ((dma.YPlaneDrmFormatModifier != 0 || dma.UvPlaneDrmFormatModifier != 0) && !_dmaBufImportModifiersExt)
            return false;

        int cw = PixelFormatInfo.ChromaWidth420(format.Width);
        int ch = PixelFormatInfo.ChromaHeight420(format.Height);
        if (!AppendPlaneAttribs(dma.YPlaneFd, dma.YPlaneOffsetBytes, dma.YPlanePitchBytes, format.Width,
                format.Height,
                DrmPixelFormats.R16,
                dma.YPlaneDrmFormatModifier))
            return false;

        IntPtr eglY;
        fixed (int* attrib = _attribsScratch)
        {
            eglY = _eglCreateImage(_dpy, IntPtr.Zero, EGL_LINUX_DMA_BUF_EXT, IntPtr.Zero, attrib);
        }

        if (eglY == IntPtr.Zero || _eglGetError() != EGL_SUCCESS_CONST)
            return false;

        if (!AppendPlaneAttribs(dma.UvPlaneFd, dma.UvPlaneOffsetBytes, dma.UvPlanePitchBytes, cw, ch,
                DrmPixelFormats.Gr1616,
                dma.UvPlaneDrmFormatModifier))
        {
            _eglDestroyImage(_dpy, eglY);
            return false;
        }

        IntPtr eglUv;
        fixed (int* attribUv = _attribsScratch)
        {
            eglUv = _eglCreateImage(_dpy, IntPtr.Zero, EGL_LINUX_DMA_BUF_EXT, IntPtr.Zero, attribUv);
        }

        if (eglUv == IntPtr.Zero || _eglGetError() != EGL_SUCCESS_CONST)
        {
            _eglDestroyImage(_dpy, eglY);
            return false;
        }

        uint glTarget = (uint)TextureTarget.Texture2D;
        bool glOk;

        _gl.BindTexture(TextureTarget.Texture2D, texYId);
        _glEGLStorage(glTarget, (void*)(nint)eglY, null);
        glOk = _gl.GetError() == GLEnum.NoError;

        _gl.BindTexture(TextureTarget.Texture2D, texUvId);
        if (glOk)
        {
            _glEGLStorage(glTarget, (void*)(nint)eglUv, null);
            glOk = _gl.GetError() == GLEnum.NoError;
        }

        _eglDestroyImage(_dpy, eglY);
        _eglDestroyImage(_dpy, eglUv);

        return glOk;
    }

    /// <summary>P016 semi-planar: same EGL plane FOURCCs as <see cref="TryUploadP010"/> (16-bit words in memory).</summary>
    public bool TryUploadP016(uint texYId, uint texUvId, in VideoFormat format, DmabufP016Backing dma)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (format.PixelFormat != global::S.Media.Core.Video.PixelFormat.P016)
        {
            var blocker = LinuxDmabufGlHardwareFormats.GetPrimeGlImportBlocker(format.PixelFormat)!;
            throw new ArgumentException($"expected P016 renderer format ({format.PixelFormat}): {blocker}", nameof(format));
        }
        if ((dma.YPlaneDrmFormatModifier != 0 || dma.UvPlaneDrmFormatModifier != 0) && !_dmaBufImportModifiersExt)
            return false;

        int cw = PixelFormatInfo.ChromaWidth420(format.Width);
        int ch = PixelFormatInfo.ChromaHeight420(format.Height);
        if (!AppendPlaneAttribs(dma.YPlaneFd, dma.YPlaneOffsetBytes, dma.YPlanePitchBytes, format.Width,
                format.Height,
                DrmPixelFormats.R16,
                dma.YPlaneDrmFormatModifier))
            return false;

        IntPtr eglY;
        fixed (int* attrib = _attribsScratch)
        {
            eglY = _eglCreateImage(_dpy, IntPtr.Zero, EGL_LINUX_DMA_BUF_EXT, IntPtr.Zero, attrib);
        }

        if (eglY == IntPtr.Zero || _eglGetError() != EGL_SUCCESS_CONST)
            return false;

        if (!AppendPlaneAttribs(dma.UvPlaneFd, dma.UvPlaneOffsetBytes, dma.UvPlanePitchBytes, cw, ch,
                DrmPixelFormats.Gr1616,
                dma.UvPlaneDrmFormatModifier))
        {
            _eglDestroyImage(_dpy, eglY);
            return false;
        }

        IntPtr eglUv;
        fixed (int* attribUv = _attribsScratch)
        {
            eglUv = _eglCreateImage(_dpy, IntPtr.Zero, EGL_LINUX_DMA_BUF_EXT, IntPtr.Zero, attribUv);
        }

        if (eglUv == IntPtr.Zero || _eglGetError() != EGL_SUCCESS_CONST)
        {
            _eglDestroyImage(_dpy, eglY);
            return false;
        }

        uint glTarget = (uint)TextureTarget.Texture2D;
        bool glOk;

        _gl.BindTexture(TextureTarget.Texture2D, texYId);
        _glEGLStorage(glTarget, (void*)(nint)eglY, null);
        glOk = _gl.GetError() == GLEnum.NoError;

        _gl.BindTexture(TextureTarget.Texture2D, texUvId);
        if (glOk)
        {
            _glEGLStorage(glTarget, (void*)(nint)eglUv, null);
            glOk = _gl.GetError() == GLEnum.NoError;
        }

        _eglDestroyImage(_dpy, eglY);
        _eglDestroyImage(_dpy, eglUv);

        return glOk;
    }

    private bool AppendPlaneAttribs(int dupFd, nint planeOffsetBytes, int pitchBytes, int w, int h, uint drmFourCc,
        ulong drmFormatModifier)
    {
        long ofs = unchecked((long)planeOffsetBytes);
        if ((ulong)ofs > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(planeOffsetBytes));

        unchecked
        {
            _attribsScratch[0] = EGL_WIDTH;
            _attribsScratch[1] = w;
            _attribsScratch[2] = EGL_HEIGHT;
            _attribsScratch[3] = h;
            _attribsScratch[4] = EGL_LINUX_DRM_FOURCC_EXT;
            _attribsScratch[5] = (int)drmFourCc;
            _attribsScratch[6] = EGL_DMA_BUF_PLANE0_FD_EXT;
            _attribsScratch[7] = dupFd;
            _attribsScratch[8] = EGL_DMA_BUF_PLANE0_OFFSET_EXT;
            _attribsScratch[9] = (int)ofs;
            _attribsScratch[10] = EGL_DMA_BUF_PLANE0_PITCH_EXT;
            _attribsScratch[11] = pitchBytes;
            if (drmFormatModifier != 0)
            {
                _attribsScratch[12] = EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT;
                _attribsScratch[13] = unchecked((int)drmFormatModifier);
                _attribsScratch[14] = EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT;
                _attribsScratch[15] = unchecked((int)(drmFormatModifier >> 32));
                _attribsScratch[16] = EGL_NONE;
            }
            else
                _attribsScratch[12] = EGL_NONE;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Per-upload paths call eglDestroyImageKHR; no persistent EGLImages between calls.
        _disposed = true;
    }
}
