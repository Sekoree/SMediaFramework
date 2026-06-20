using System.Diagnostics;
using System.Threading;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.OpenGL.Diagnostics;
using S.Media.OpenGL.Internal;
using Silk.NET.OpenGL;
using Vortice.Direct3D11;
using DxgiFormat = Vortice.DXGI.Format;
using DxgiSampleDescription = Vortice.DXGI.SampleDescription;
using GlPixelFormat = Silk.NET.OpenGL.PixelFormat;
using GlPixelType = Silk.NET.OpenGL.PixelType;
using GlTextureTarget = Silk.NET.OpenGL.TextureTarget;
using GlTextureUnit = Silk.NET.OpenGL.TextureUnit;

namespace S.Media.OpenGL;

/// <summary>
/// Windows-only: imports <see cref="Win32SharedNv12Backing"/> via DXGI NT shared <c>OpenSharedResource</c>
/// (using this instance’s consumer <see cref="ID3D11Device"/> from <see cref="TryCreate"/>) when only NT handles
/// are populated on the backing, or, when <see cref="Win32SharedNv12Backing.LibavD3D11Texture2DComPtr"/> matches the uploader device,
/// uses the libav-held <c>ID3D11Texture2D</c> COM pointer directly (no duplicate open).
/// <see href="https://registry.khronos.org/OpenGL/extensions/NV/WGL_NV_DX_interop.txt">WGL_NV_DX_interop</see>
/// (GPU path) and falls back to a D3D11 staging <c>Map</c> + <c>glTexSubImage2D</c> upload if interop is unavailable
/// or registration fails. When the D3D11 texture exposes <see cref="Vortice.DXGI.IDXGIKeyedMutex"/>, uploads call
/// <c>D3d11TextureKeyedMutexScope.TryAcquireForGpuRead</c> (key <c>0</c>, FFmpeg <c>d3d11va</c> convention); if acquire fails,
/// both interop and staging paths abort for that frame. Optional: <c>WIN32_NV12_D3D11_KEYED_MUTEX_TIMEOUT_MS</c> (1–60000 ms).
/// </summary>
/// <remarks>
/// Pass a borrowed <see cref="ID3D11Device"/> (COM pointer) created on the same adapter as the OpenGL context.
/// <see cref="Dispose"/> releases the COM wrapper reference acquired in <see cref="TryCreate"/> (it does not tear down libav’s device while other references exist).
/// Decode frames may omit libav device/texture COM on the backing (DXGI shared-handle export); this uploader’s device
/// is still required for <c>OpenSharedResource</c> on those handles. Product backlog **PO-01** tracks removing that
/// consumer-device requirement from the end-to-end “zero COM on <see cref="S.Media.Core.Video.HardwareVideoSurfaceDescriptor"/>” story (<c>Doc/Todo.md</c>).
/// </remarks>
public sealed unsafe class Nv12Win32SharedHandleGpuUploader : IDisposable
{
    private const string VertFullscreen = """
#version 330 core
void main()
{
    const vec2 p[3] = vec2[](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
    gl_Position = vec4(p[gl_VertexID], 0.0, 1.0);
}
""";

    private const string FragY = """
#version 330 core
uniform sampler2D uNV12;
uniform int uLumaW;
uniform int uLumaH;
layout(location = 0) out float oY;
void main()
{
    ivec2 p = ivec2(gl_FragCoord.xy);
    if (p.x >= uLumaW || p.y >= uLumaH)
        discard;
    oY = texelFetch(uNV12, p, 0).r;
}
""";

    private const string FragUv = """
#version 330 core
uniform sampler2D uNV12;
uniform int uLumaH;
uniform int uChromaW;
uniform int uChromaH;
layout(location = 0) out vec2 oUV;
void main()
{
    ivec2 p = ivec2(gl_FragCoord.xy);
    if (p.x >= uChromaW || p.y >= uChromaH)
        discard;
    int bx = int(p.x) * 2;
    int sy = uLumaH + int(p.y);
    float u = texelFetch(uNV12, ivec2(bx, sy), 0).r;
    float v = texelFetch(uNV12, ivec2(bx + 1, sy), 0).r;
    oUV = vec2(u, v);
}
""";

    private readonly GL _gl;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    // ID3D11Multithread on the immediate context (when the device exposes it — libav's d3d11va device does and
    // runs with SetMultithreadProtected(TRUE)). Used to serialize our staging CopySubresourceRegion/Map/Unmap
    // against the decode thread that shares this immediate context. Null when the device has no such interface
    // (then there is no concurrent decoder on it either, e.g. an output-owned interop device).
    private readonly ID3D11Multithread? _multithread;
    private ID3D11Texture2D? _staging;
    private int _stagingCapW;
    private int _stagingCapH;

    private WglNvDxInterop _wgl = new();
    private bool _wglLoadAttempted;
    private bool _wglInteropDisabled;
    private nint _wglDeviceHandle;
    private nint _wglRegisteredObject;
    private uint _interopGlTex;
    private uint _interopFbo;
    private uint _interopProgY;
    private uint _interopProgUv;
    private uint _interopVao;
    private int _interopAllocW;
    private int _interopAllocH;
    // Owned single-slice NV12 texture: the decoder hands us a slice of a Texture2D *array*, which
    // wglDXRegisterObjectNV cannot register; we CopySubresourceRegion (GPU->GPU) the slice into this single
    // texture and register THAT once with WGL_NV_DX_interop. Persistent across frames (re-created only on resize).
    private ID3D11Texture2D? _interopCopyTex;
    private int _interopCopyCapW;
    private int _interopCopyCapH;
    private bool _uploadMechanismLogged;
    private bool _disposed;

    private static int _keyedMutexAcquireFailLogged;

    private static int KeyedMutexTimeoutMilliseconds()
    {
        var raw = Environment.GetEnvironmentVariable("WIN32_NV12_D3D11_KEYED_MUTEX_TIMEOUT_MS");
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var v))
            return 2000;
        return Math.Clamp(v, 1, 60_000);
    }

    private static void LogKeyedMutexAcquireFailedOnce()
    {
        if (Interlocked.Exchange(ref _keyedMutexAcquireFailLogged, 1) != 0)
            return;
        MediaDiagnostics.LogWarning(
            "{0}: IDXGIKeyedMutex.AcquireSync(0) failed or timed out; NV12 upload aborted. " +
            "Ensure the producer released keyed mutex 0 before handing the frame to GL (libav d3d11va does in normal operation). " +
            "Optional: set WIN32_NV12_D3D11_KEYED_MUTEX_TIMEOUT_MS (1–60000, default 2000).",
            nameof(Nv12Win32SharedHandleGpuUploader));
    }

    private Nv12Win32SharedHandleGpuUploader(GL gl, ID3D11Device device, ID3D11DeviceContext context,
        ID3D11Multithread? multithread)
    {
        _gl = gl;
        _device = device;
        _context = context;
        _multithread = multithread;
    }

    /// <summary>Creates an uploader when <paramref name="d3d11DeviceComPtr"/> is a non-null <c>ID3D11Device</c> pointer.</summary>
    public static Nv12Win32SharedHandleGpuUploader? TryCreate(GL gl, nint d3d11DeviceComPtr)
    {
        if (!OperatingSystem.IsWindows() || d3d11DeviceComPtr == 0)
            return null;
        if (!D3D11InteropUtility.TryValidateDeviceComPointer(d3d11DeviceComPtr, out var validateErr))
        {
            MediaDiagnostics.LogWarning(
                "{0}.{1}: invalid D3D11 device pointer — {2}",
                nameof(Nv12Win32SharedHandleGpuUploader),
                nameof(TryCreate),
                validateErr);
            return null;
        }

        try
        {
            var dev = new ID3D11Device(d3d11DeviceComPtr);
            var ctx = dev.ImmediateContext;
            // The decode thread and this uploader share one immediate context. Acquire ID3D11Multithread and make
            // sure protection is on so Enter()/Leave() around our staging copy actually serializes against the
            // decoder's context calls (libav already enables it; harmless to re-assert for an owned device).
            var mt = ctx.QueryInterfaceOrNull<ID3D11Multithread>();
            mt?.SetMultithreadProtected(true);
            return new Nv12Win32SharedHandleGpuUploader(gl, dev, ctx, mt);
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, nameof(Nv12Win32SharedHandleGpuUploader) + "." + nameof(TryCreate));
            return null;
        }
    }

    /// <summary>Uploads NV12 from shared D3D11 memory into existing GL texture objects (texture unit 0 = Y, 1 = UV).</summary>
    public bool TryUpload(uint texYId, uint texUvId, in VideoFormat format, Win32SharedNv12Backing backing)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (format.PixelFormat != global::S.Media.Core.Video.PixelFormat.Nv12)
            return false;

        Nv12Win32SharedHandleGpuUploadProfiling.RecordUploadAttempt();

        if (TryUploadInterop(texYId, texUvId, in format, backing))
        {
            Nv12Win32SharedHandleGpuUploadProfiling.RecordInteropSuccess();
            LogUploadMechanismOnce(interop: true);
            return true;
        }

        Nv12Win32SharedHandleGpuUploadProfiling.RecordInteropMissBeforeStaging();

        if (TryUploadStaging(texYId, texUvId, in format, backing))
        {
            Nv12Win32SharedHandleGpuUploadProfiling.RecordStagingSuccess();
            LogUploadMechanismOnce(interop: false);
            return true;
        }

        Nv12Win32SharedHandleGpuUploadProfiling.RecordBothPathsFailed();
        return false;
    }

    private void LogUploadMechanismOnce(bool interop)
    {
        if (_uploadMechanismLogged)
            return;
        _uploadMechanismLogged = true;
        if (interop)
        {
            MediaDiagnostics.LogInformation(
                "Nv12Win32SharedHandleGpuUploader: using WGL_NV_DX_interop (GPU path) for NV12 shared-handle uploads.");
        }
        else
        {
            MediaDiagnostics.LogInformation(
                "Nv12Win32SharedHandleGpuUploader: using D3D11 staging Map + glTexSubImage2D (CPU path) for NV12 shared-handle uploads " +
                "(WGL_NV_DX_interop unavailable, failed to load, or registration failed).");
        }
    }

    private static int _strictTextureAdapterMismatchLogged;

    private static bool StrictTextureAdapterLuidEnabled() =>
        OperatingSystem.IsWindows()
        && string.Equals(Environment.GetEnvironmentVariable("WIN32_NV12_D3D11_STRICT_TEXTURE_ADAPTER_LUID"), "1",
            StringComparison.Ordinal);

    /// <summary>
    /// When <c>WIN32_NV12_D3D11_STRICT_TEXTURE_ADAPTER_LUID=1</c>, rejects textures whose DXGI adapter LUID differs from the uploader device (multi-GPU guard).
    /// </summary>
    private bool AdapterLuidAllowsTextureOrDispose(ref ID3D11Texture2D? tex)
    {
        if (tex is null)
            return false;
        if (!StrictTextureAdapterLuidEnabled())
            return true;
        if (!D3D11InteropUtility.TryGetAdapterLuid(_device.NativePointer, out var uploadLuid)
            || !D3D11InteropUtility.TryGetAdapterLuidFromTexture(tex.NativePointer, out var texLuid))
            return true;
        if (uploadLuid == texLuid)
            return true;
        if (Interlocked.Exchange(ref _strictTextureAdapterMismatchLogged, 1) == 0)
        {
            MediaDiagnostics.LogWarning(
                "{0}: DXGI adapter LUID of ID3D11Texture2D (packed={1}) != uploader ID3D11Device LUID (packed={2}) — set WIN32_NV12_D3D11_STRICT_TEXTURE_ADAPTER_LUID=0 to allow (not recommended on multi-GPU).",
                nameof(Nv12Win32SharedHandleGpuUploader),
                texLuid,
                uploadLuid);
        }

        tex.Dispose();
        tex = null;
        return false;
    }

    private bool TryOpenD3D11GpuTexture(Win32SharedNv12Backing backing, out ID3D11Texture2D? gpuTex, out uint subresourceIndex)
    {
        gpuTex = null;
        subresourceIndex = 0;

        if (backing.LibavD3D11Texture2DComPtr != 0
            && backing.LibavD3D11DeviceComPtr != 0
            && backing.LibavD3D11DeviceComPtr == _device.NativePointer
            && D3D11InteropUtility.TryValidateTexture2DComPointer(backing.LibavD3D11Texture2DComPtr, out _))
        {
            try
            {
                var t = new ID3D11Texture2D(backing.LibavD3D11Texture2DComPtr);
                if (t.Device?.NativePointer != _device.NativePointer)
                {
                    t.Dispose();
                    return false;
                }

                var desc = t.Description;
                subresourceIndex = (uint)(backing.D3D11TextureArraySliceIndex * (int)desc.MipLevels);
                gpuTex = t;
                if (!AdapterLuidAllowsTextureOrDispose(ref gpuTex))
                    return false;
                return true;
            }
            catch
            {
                gpuTex?.Dispose();
                gpuTex = null;
                return false;
            }
        }

        if (backing.LumaSharedNtHandle == 0)
            return false;

        try
        {
            gpuTex = _device.OpenSharedResource<ID3D11Texture2D>(backing.LumaSharedNtHandle);
            var desc = gpuTex.Description;
            subresourceIndex = (uint)(backing.D3D11TextureArraySliceIndex * (int)desc.MipLevels);
            if (!AdapterLuidAllowsTextureOrDispose(ref gpuTex))
                return false;
            return true;
        }
        catch
        {
            gpuTex?.Dispose();
            gpuTex = null;
            return false;
        }
    }

    private bool EnsureWglProcs()
    {
        if (_wglLoadAttempted)
            return !_wglInteropDisabled && _wgl.IsComplete;
        _wglLoadAttempted = true;
        if (!WglNvDxInterop.TryLoad(_gl, out _wgl) || !_wgl.IsComplete)
        {
            _wglInteropDisabled = true;
            LogInteropUnavailableOnce(
                "WGL_NV_DX_interop entry points did not resolve on this GL context (driver does not export the extension). " +
                _wgl.DescribeResolvedProcs());
            return false;
        }

        return true;
    }

    private bool EnsureWglDevice()
    {
        if (_wglDeviceHandle != 0)
            return true;
        if (!EnsureWglProcs())
            return false;
        _wglDeviceHandle = _wgl.OpenDevice!.Invoke(_device.NativePointer);
        if (_wglDeviceHandle == 0)
        {
            _wglInteropDisabled = true;
            LogInteropUnavailableOnce(
                "wglDXOpenDeviceNV returned NULL for the upload ID3D11Device (entry points resolved but the driver " +
                "rejected the device — e.g. it is on a different adapter than the GL context, or a software/WARP device).");
            return false;
        }

        return true;
    }

    private int _interopUnavailableLogged;

    /// <summary>
    /// Logs once, with the GL vendor/renderer/version and the supplied reason, why the zero-copy WGL_NV_DX_interop
    /// path is unavailable and uploads fall back to the slower D3D11 staging + glTexSubImage2D copy. Lets a black/slow
    /// Windows playback report be told apart: "driver lacks the extension" vs "extension present but device rejected".
    /// </summary>
    private void LogInteropUnavailableOnce(string reason)
    {
        if (Interlocked.Exchange(ref _interopUnavailableLogged, 1) != 0)
            return;

        string vendor = "?", renderer = "?", version = "?";
        try
        {
            vendor = _gl.GetStringS(StringName.Vendor) ?? "?";
            renderer = _gl.GetStringS(StringName.Renderer) ?? "?";
            version = _gl.GetStringS(StringName.Version) ?? "?";
        }
        catch
        {
            /* GL string query best-effort */
        }

        MediaDiagnostics.LogWarning(
            "{0}: zero-copy WGL_NV_DX_interop unavailable — using the slower D3D11 staging CPU upload path. " +
            "Reason: {1} | GL_VENDOR='{2}' GL_RENDERER='{3}' GL_VERSION='{4}'.",
            nameof(Nv12Win32SharedHandleGpuUploader),
            reason,
            vendor,
            renderer,
            version);
    }

    private bool EnsureInteropPrograms()
    {
        if (_interopProgY != 0 && _interopProgUv != 0)
            return true;
        try
        {
            _interopProgY = LinkSimpleProgram(VertFullscreen, FragY, "nv12-dxinterop-y");
            _interopProgUv = LinkSimpleProgram(VertFullscreen, FragUv, "nv12-dxinterop-uv");
            return true;
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogWarning("Nv12Win32SharedHandleGpuUploader: interop shader compile failed: {0}", ex.Message);
            _wglInteropDisabled = true;
            return false;
        }
    }

    private bool EnsureInteropFboAndTexture()
    {
        if (_interopFbo == 0)
            _interopFbo = _gl.GenFramebuffer();
        if (_interopGlTex == 0)
            _interopGlTex = _gl.GenTexture();
        if (_interopVao == 0)
            _interopVao = _gl.GenVertexArray();
        return true;
    }

    private void ResizeInteropTexture(int tw, int th)
    {
        if (tw == _interopAllocW && th == _interopAllocH && _interopGlTex != 0)
            return;

        _interopAllocW = tw;
        _interopAllocH = th;

        _gl.BindTexture(GlTextureTarget.Texture2D, _interopGlTex);
        _gl.TexParameter(GlTextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(GlTextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.TexParameter(GlTextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(GlTextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.TexImage2D(GlTextureTarget.Texture2D, 0, InternalFormat.R8, (uint)tw, (uint)th, 0, GlPixelFormat.Red, GlPixelType.UnsignedByte, null);
        _gl.BindTexture(GlTextureTarget.Texture2D, 0);
    }

    private bool TryUploadInterop(uint texYId, uint texUvId, in VideoFormat format, Win32SharedNv12Backing backing)
    {
        if (_wglInteropDisabled || !EnsureWglDevice() || !EnsureInteropPrograms() || !EnsureInteropFboAndTexture())
            return false;

        var lumaW = format.Width;
        var lumaH = format.Height;
        var chromaW = PixelFormatInfo.ChromaWidth420(lumaW);
        var chromaH = PixelFormatInfo.ChromaHeight420(lumaH);

        try
        {
            if (!TryOpenD3D11GpuTexture(backing, out var gpuTex, out var srcSub))
                return false;

            using (gpuTex)
            {
                var desc = gpuTex!.Description;
                if (desc.Width == 0 || desc.Height == 0)
                    return false;

                // libav decodes into a Texture2D ARRAY (this frame = array slice `srcSub`). wglDXRegisterObjectNV
                // cannot register an array texture or select a slice, so register a persistent owned SINGLE NV12
                // texture and copy the slice into it on the GPU each frame. Sets _interopCopyTex + _wglRegisteredObject.
                if (!EnsureInteropCopyTextureAndRegistration(desc.Width, desc.Height))
                    return false;

                if (!D3d11TextureKeyedMutexScope.TryAcquireForGpuRead(gpuTex!, out var keyedScope, KeyedMutexTimeoutMilliseconds()))
                {
                    LogKeyedMutexAcquireFailedOnce();
                    return false;
                }

                // Copy the decoded slice into the owned interop texture (must be D3D-owned == not GL-locked here).
                // Hold the D3D11 multithread lock so the decode thread sharing this immediate context can't interleave.
                try
                {
                    _multithread?.Enter();
                    try
                    {
                        _context.CopySubresourceRegion(_interopCopyTex!, 0, 0, 0, 0, gpuTex, srcSub, null);
                    }
                    finally
                    {
                        _multithread?.Leave();
                    }
                }
                finally
                {
                    keyedScope?.Dispose();
                }

                // Hand the interop texture to GL for the duration of the sample, then return it to D3D.
                var hObj = _wglRegisteredObject;
                if (_wgl.LockObjects!.Invoke(_wglDeviceHandle, 1, &hObj) == 0)
                    return false;

                try
                {
                    SaveGlState(out var st);
                    try
                    {
                        _gl.ActiveTexture(GlTextureUnit.Texture0 + 2);
                        _gl.BindTexture(GlTextureTarget.Texture2D, _interopGlTex);
                        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _interopFbo);
                        _gl.BindVertexArray(_interopVao);

                        DrawYPlane(texYId, lumaW, lumaH);
                        DrawUvPlane(texUvId, lumaH, chromaW, chromaH);

                        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)st.DrawFramebufferBinding);
                    }
                    finally
                    {
                        RestoreGlState(in st);
                    }

                    return true;
                }
                finally
                {
                    var h = _wglRegisteredObject;
                    _ = _wgl.UnlockObjects!.Invoke(_wglDeviceHandle, 1, &h);
                }
            }
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogWarning("Nv12Win32SharedHandleGpuUploader: WGL interop upload failed ({0}); falling back to staging.", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Ensures the owned single-slice NV12 copy texture exists at <paramref name="gpuW"/>×<paramref name="gpuH"/> and
    /// is registered with WGL_NV_DX_interop against <see cref="_interopGlTex"/>. Re-creates on size change. Returns
    /// <see langword="false"/> (and permanently disables the interop path) if the driver refuses to register an NV12
    /// single texture — planar interop is not universally supported; the BGRA convert-then-register path is the
    /// alternative. The registered object stays D3D-owned (unlocked) between frames so the per-frame copy is valid.
    /// </summary>
    private bool EnsureInteropCopyTextureAndRegistration(uint gpuW, uint gpuH)
    {
        var wi = (int)gpuW;
        var hi = (int)gpuH;
        if (_interopCopyTex is not null && _wglRegisteredObject != 0 && _interopCopyCapW == wi && _interopCopyCapH == hi)
            return true;

        // Rebuild: unregister the stale object, then recreate the D3D copy texture and re-allocate the GL texture
        // storage (the GL texture must NOT be registered while ResizeInteropTexture runs glTexImage2D on it).
        if (_wglRegisteredObject != 0)
        {
            var h = _wglRegisteredObject;
            _ = _wgl.UnregisterObject!.Invoke(_wglDeviceHandle, h);
            _wglRegisteredObject = 0;
        }

        _interopCopyTex?.Dispose();
        _interopCopyTex = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = gpuW,
            Height = gpuH,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.NV12,
            SampleDescription = new DxgiSampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

        ResizeInteropTexture(wi, hi);

        var reg = _wgl.RegisterObject!.Invoke(_wglDeviceHandle, _interopCopyTex.NativePointer, _interopGlTex,
            WglNvDxInterop.Texture2DArb, WglNvDxInterop.AccessReadOnlyNv);
        if (reg == 0)
        {
            _interopCopyTex.Dispose();
            _interopCopyTex = null;
            _interopCopyCapW = 0;
            _interopCopyCapH = 0;
            _wglInteropDisabled = true;
            LogInteropUnavailableOnce(
                "wglDXRegisterObjectNV refused an NV12 single texture (planar WGL_NV_DX_interop unsupported on this " +
                "driver). Zero-copy disabled; using CPU staging upload. A GPU NV12->BGRA convert-then-register path " +
                "would be the alternative.");
            return false;
        }

        _wglRegisteredObject = reg;
        _interopCopyCapW = wi;
        _interopCopyCapH = hi;
        return true;
    }

    private void DrawYPlane(uint texYId, int lumaW, int lumaH)
    {
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            GlTextureTarget.Texture2D, texYId, 0);
        DebugAssertFramebufferComplete("Y plane attach");
        _gl.Viewport(0, 0, (uint)lumaW, (uint)lumaH);
        _gl.UseProgram(_interopProgY);
        var loc = _gl.GetUniformLocation(_interopProgY, "uNV12");
        if (loc >= 0)
            _gl.Uniform1(loc, 2);
        _gl.Uniform1(_gl.GetUniformLocation(_interopProgY, "uLumaW"), lumaW);
        _gl.Uniform1(_gl.GetUniformLocation(_interopProgY, "uLumaH"), lumaH);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    private void DrawUvPlane(uint texUvId, int lumaH, int chromaW, int chromaH)
    {
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            GlTextureTarget.Texture2D, texUvId, 0);
        DebugAssertFramebufferComplete("UV plane attach");
        _gl.Viewport(0, 0, (uint)chromaW, (uint)chromaH);
        _gl.UseProgram(_interopProgUv);
        var loc = _gl.GetUniformLocation(_interopProgUv, "uNV12");
        if (loc >= 0)
            _gl.Uniform1(loc, 2);
        _gl.Uniform1(_gl.GetUniformLocation(_interopProgUv, "uLumaH"), lumaH);
        _gl.Uniform1(_gl.GetUniformLocation(_interopProgUv, "uChromaW"), chromaW);
        _gl.Uniform1(_gl.GetUniformLocation(_interopProgUv, "uChromaH"), chromaH);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    private readonly record struct SavedGlState(
        int ViewportX,
        int ViewportY,
        int ViewportW,
        int ViewportH,
        int DrawFramebufferBinding,
        int VertexArrayBinding,
        int CurrentProgram,
        int ActiveTexture,
        int TexBinding0,
        int TexBinding1,
        int TexBinding2);

    [Conditional("DEBUG")]
    private void DebugAssertFramebufferComplete(string context)
    {
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            MediaDiagnostics.LogWarning("Nv12Win32SharedHandleGpuUploader: framebuffer incomplete ({0}) after {1}.", status, context);
    }

    private void SaveGlState(out SavedGlState st)
    {
        Span<int> vp = stackalloc int[4];
        _gl.GetInteger(GetPName.Viewport, vp);
        _gl.GetInteger(GetPName.DrawFramebufferBinding, out var fbo);
        _gl.GetInteger(GetPName.VertexArrayBinding, out var vao);
        _gl.GetInteger(GetPName.CurrentProgram, out var prog);
        _gl.GetInteger(GetPName.ActiveTexture, out var activeTex);

        _gl.ActiveTexture(GlTextureUnit.Texture0);
        _gl.GetInteger(GetPName.TextureBinding2D, out var t0);
        _gl.ActiveTexture(GlTextureUnit.Texture1);
        _gl.GetInteger(GetPName.TextureBinding2D, out var t1);
        _gl.ActiveTexture(GlTextureUnit.Texture0 + 2);
        _gl.GetInteger(GetPName.TextureBinding2D, out var t2);

        st = new SavedGlState(vp[0], vp[1], vp[2], vp[3], fbo, vao, prog, activeTex, t0, t1, t2);
    }

    private void RestoreGlState(in SavedGlState st)
    {
        _gl.UseProgram((uint)st.CurrentProgram);
        _gl.BindVertexArray((uint)st.VertexArrayBinding);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)st.DrawFramebufferBinding);
        _gl.Viewport(st.ViewportX, st.ViewportY, (uint)st.ViewportW, (uint)st.ViewportH);

        _gl.ActiveTexture(GlTextureUnit.Texture0);
        _gl.BindTexture(GlTextureTarget.Texture2D, (uint)st.TexBinding0);
        _gl.ActiveTexture(GlTextureUnit.Texture1);
        _gl.BindTexture(GlTextureTarget.Texture2D, (uint)st.TexBinding1);
        _gl.ActiveTexture(GlTextureUnit.Texture0 + 2);
        _gl.BindTexture(GlTextureTarget.Texture2D, (uint)st.TexBinding2);
        _gl.ActiveTexture((TextureUnit)st.ActiveTexture);
    }

    private uint LinkSimpleProgram(string vert, string frag, string labelForErrors)
    {
        var vs = CompileShader(ShaderType.VertexShader, vert);
        var fs = CompileShader(ShaderType.FragmentShader, frag);
        try
        {
            var program = _gl.CreateProgram();
            _gl.AttachShader(program, vs);
            _gl.AttachShader(program, fs);
            _gl.LinkProgram(program);
            _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var ok);
            if (ok == 0)
            {
                var log = _gl.GetProgramInfoLog(program);
                _gl.DeleteProgram(program);
                throw new InvalidOperationException($"{labelForErrors}: {log}");
            }

            _gl.DetachShader(program, vs);
            _gl.DetachShader(program, fs);
            return program;
        }
        finally
        {
            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);
        }
    }

    private uint CompileShader(ShaderType type, string source)
    {
        var s = _gl.CreateShader(type);
        _gl.ShaderSource(s, source);
        _gl.CompileShader(s);
        _gl.GetShader(s, ShaderParameterName.CompileStatus, out var ok);
        if (ok == 0)
        {
            var log = _gl.GetShaderInfoLog(s);
            _gl.DeleteShader(s);
            throw new InvalidOperationException($"shader compile failed: {log}");
        }

        return s;
    }

    private bool TryUploadStaging(uint texYId, uint texUvId, in VideoFormat format, Win32SharedNv12Backing backing)
    {
        var w = format.Width;
        var h = format.Height;
        var cw = PixelFormatInfo.ChromaWidth420(w);
        var ch = PixelFormatInfo.ChromaHeight420(h);

        try
        {
            if (!TryOpenD3D11GpuTexture(backing, out var gpuTex, out var srcSub))
                return false;

            using (gpuTex)
            {
                if (!D3d11TextureKeyedMutexScope.TryAcquireForGpuRead(gpuTex!, out var keyedScope, KeyedMutexTimeoutMilliseconds()))
                {
                    LogKeyedMutexAcquireFailedOnce();
                    return false;
                }

                try
                {
                    var gpuDesc = gpuTex!.Description;

                    EnsureStaging(gpuDesc.Width, gpuDesc.Height);
                    if (_staging == null)
                        return false;

                    // Hold the D3D11 multithread lock across the whole copy-out so no decode-thread context call
                    // interleaves between CopySubresourceRegion, Map and Unmap on the shared immediate context.
                    // The GL upload in between only reads the mapped CPU pointer (no _context calls), so keeping it
                    // inside the lock is safe and just briefly defers the decoder.
                    _multithread?.Enter();
                    try
                    {
                        _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, gpuTex, srcSub, null);

                        var mapped = _context.Map(_staging, 0, MapMode.Read, MapFlags.None);
                        try
                        {
                            var yPitch = (int)mapped.RowPitch;
                            if (yPitch <= 0)
                                return false;

                            var yPtr = (byte*)mapped.DataPointer;
                            if (yPtr == null)
                                return false;

                            var uvPtr = yPtr + yPitch * (nint)h;
                            UploadR8Plane(texYId, yPtr, yPitch, w, h);
                            UploadRgPlane(texUvId, uvPtr, yPitch, cw, ch);
                            return true;
                        }
                        finally
                        {
                            _context.Unmap(_staging, 0);
                        }
                    }
                    finally
                    {
                        _multithread?.Leave();
                    }
                }
                finally
                {
                    keyedScope?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, nameof(Nv12Win32SharedHandleGpuUploader) + "." + nameof(TryUploadStaging));
            return false;
        }
    }

    private void EnsureStaging(uint gpuW, uint gpuH)
    {
        var wi = (int)gpuW;
        var hi = (int)gpuH;
        if (_staging is not null && _stagingCapW >= wi && _stagingCapH >= hi)
            return;

        _staging?.Dispose();
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = gpuW,
            Height = gpuH,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.NV12,
            SampleDescription = new DxgiSampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
        _stagingCapW = wi;
        _stagingCapH = hi;
    }

    private void UploadR8Plane(uint texId, byte* src, int srcRowPitch, int planeW, int planeH)
    {
        _gl.ActiveTexture(GlTextureUnit.Texture0);
        _gl.BindTexture(GlTextureTarget.Texture2D, texId);
        SetUnpackRowLength(srcRowPitch, planeW, bytesPerPixel: 1);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        _gl.TexSubImage2D(GlTextureTarget.Texture2D, 0, 0, 0, (uint)planeW, (uint)planeH, GlPixelFormat.Red, GlPixelType.UnsignedByte, src);
        ClearUnpackState();
    }

    private void UploadRgPlane(uint texId, byte* src, int srcRowPitch, int planeW, int planeH)
    {
        _gl.ActiveTexture(GlTextureUnit.Texture1);
        _gl.BindTexture(GlTextureTarget.Texture2D, texId);
        SetUnpackRowLength(srcRowPitch, planeW, bytesPerPixel: 2);
        var align = srcRowPitch % 4 == 0 ? 4 : 1;
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, align);
        _gl.TexSubImage2D(GlTextureTarget.Texture2D, 0, 0, 0, (uint)planeW, (uint)planeH, GlPixelFormat.RG, GlPixelType.UnsignedByte, src);
        ClearUnpackState();
    }

    private void SetUnpackRowLength(int rowPitchBytes, int visiblePixelsPerRow, int bytesPerPixel)
    {
        var value = OpenGlUnpackRowLength.Compute(rowPitchBytes, visiblePixelsPerRow, bytesPerPixel);
        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, value);
    }

    private void ClearUnpackState()
    {
        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_wglRegisteredObject != 0 && _wgl.UnregisterObject != null && _wglDeviceHandle != 0)
        {
            var h = _wglRegisteredObject;
            _ = _wgl.UnregisterObject(_wglDeviceHandle, h);
            _wglRegisteredObject = 0;
        }

        if (_wglDeviceHandle != 0 && _wgl.CloseDevice != null)
        {
            _ = _wgl.CloseDevice(_wglDeviceHandle);
            _wglDeviceHandle = 0;
        }

        if (_interopProgY != 0)
        {
            _gl.DeleteProgram(_interopProgY);
            _interopProgY = 0;
        }

        if (_interopProgUv != 0)
        {
            _gl.DeleteProgram(_interopProgUv);
            _interopProgUv = 0;
        }

        if (_interopFbo != 0)
        {
            _gl.DeleteFramebuffer(_interopFbo);
            _interopFbo = 0;
        }

        if (_interopVao != 0)
        {
            _gl.DeleteVertexArray(_interopVao);
            _interopVao = 0;
        }

        if (_interopGlTex != 0)
        {
            _gl.DeleteTexture(_interopGlTex);
            _interopGlTex = 0;
        }

        _interopAllocW = 0;
        _interopAllocH = 0;

        // Unregistered above (the WGL object is registered against this texture); now release the D3D resource.
        MediaDiagnostics.SwallowDisposeErrors(() => _interopCopyTex?.Dispose(), "Nv12Win32SharedHandleGpuUploader.Dispose: interop copy texture");
        _interopCopyTex = null;
        _interopCopyCapW = 0;
        _interopCopyCapH = 0;

        _staging?.Dispose();
        _staging = null;

        MediaDiagnostics.SwallowDisposeErrors(() => _multithread?.Dispose(), "Nv12Win32SharedHandleGpuUploader.Dispose: multithread");

        // Balance the COM reference taken by `new ID3D11Device(comPtr)` in TryCreate (borrowed or shared host pointer).
        MediaDiagnostics.SwallowDisposeErrors(_device.Dispose, "Nv12Win32SharedHandleGpuUploader.Dispose: device");
    }
}
