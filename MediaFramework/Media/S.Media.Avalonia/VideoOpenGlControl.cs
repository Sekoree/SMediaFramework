using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.OpenGL;
using Silk.NET.OpenGL;
using SilkGl = Silk.NET.OpenGL.GL;
using VideoPixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Avalonia;

/// <summary>
/// Avalonia <see cref="OpenGlControlBase"/> that implements <see cref="IVideoSink"/> using the same
/// <see cref="YuvVideoRenderer"/> / shader pipeline as <c>SDL3GLVideoSink</c> (<see cref="S.Media.OpenGL"/>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Submit"/> may be called from decoder threads: frames are swapped atomically and
/// <see cref="OpenGlControlBase.RequestNextFrameRendering"/> is posted to the UI dispatcher.
/// All GL calls run only inside Avalonia's OpenGL callbacks.
/// </para>
/// <para>
/// On Linux with an EGL current display, dma-buf NV12 upload uses <see cref="YuvDmabufEglInterop"/> with
/// <c>eglGetProcAddress</c> from libEGL (same pattern as SDL's EGL resolve). GLX-only contexts leave dma-buf import disabled.
/// </para>
/// </remarks>
public sealed class VideoOpenGlControl : OpenGlControlBase, IVideoSink, IVideoSinkD3D11GlBorrowSetup
{
    private static readonly VideoPixelFormat[] AcceptedFormats = YuvVideoRenderer.SupportedPixelFormats.ToArray();

    private readonly Win32Nv12GlUploadDeviceResolver _win32Nv12Device;
    private GlVideoSinkHdrPreference _hdrPreference;

    private readonly Lock _configureLock = new();
    private VideoFormat _format;
    private bool _configured;
    private volatile bool _sinkDisposed;

    private SilkGl? _gl;
    private YuvVideoRenderer? _renderer;
    private bool _hasUploadedOnce;

    private VideoFrame? _pendingFrame;
    private readonly object _frameLock = new();

    public VideoOpenGlControl(
        GlVideoSinkHdrPreference hdrPreference = GlVideoSinkHdrPreference.FollowFrameHints,
        nint borrowD3D11DeviceComPtrForNv12Gl = 0,
        bool createFallbackD3D11InteropDeviceForWin32Nv12 = true)
    {
        _hdrPreference = hdrPreference;
        _win32Nv12Device = new Win32Nv12GlUploadDeviceResolver(borrowD3D11DeviceComPtrForNv12Gl,
            createFallbackD3D11InteropDeviceForWin32Nv12,
            nameof(VideoOpenGlControl));
    }

    /// <summary>Letterboxing / stretch for <see cref="YuvVideoRenderer.Render(int,int,VideoViewportFit)"/>.</summary>
    public VideoViewportFit ViewportFit { get; set; } = VideoViewportFit.Stretch;

    public GlVideoSinkHdrPreference HdrPreference
    {
        get => _hdrPreference;
        set => _hdrPreference = value;
    }

    public IReadOnlyList<VideoPixelFormat> AcceptedPixelFormats => AcceptedFormats;

    public VideoFormat Format
    {
        get
        {
            if (!_configured)
                throw new InvalidOperationException($"{nameof(VideoOpenGlControl)}.{nameof(Configure)} has not been called yet");
            return _format;
        }
    }

    public void SetBorrowVideoSourceForWin32Nv12Gl(IVideoSource? videoSource) =>
        _win32Nv12Device.SetBorrowVideoSourceForWin32Nv12Gl(videoSource);

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_sinkDisposed, this);
        lock (_configureLock)
        {
            if (_configured)
                throw new InvalidOperationException($"{nameof(VideoOpenGlControl)} is already configured");
            if (Array.IndexOf(AcceptedFormats, format.PixelFormat) < 0)
                throw new NotSupportedException(
                    $"{nameof(VideoOpenGlControl)} does not accept pixel format {format.PixelFormat}; supported: {string.Join(", ", AcceptedFormats)}");
            if (format.Width <= 0 || format.Height <= 0)
                throw new ArgumentException("video format must have positive dimensions", nameof(format));

            _format = format;
            _configured = true;
        }

        PostRequestNextFrame();
    }

    public void Submit(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_sinkDisposed, this);
        if (!_configured)
        {
            frame.Dispose();
            throw new InvalidOperationException($"{nameof(Submit)} called before {nameof(Configure)}");
        }

        if (frame.Format.Width != _format.Width || frame.Format.Height != _format.Height
            || frame.Format.PixelFormat != _format.PixelFormat)
        {
            frame.Dispose();
            throw new ArgumentException($"frame format {frame.Format} does not match sink format {_format}", nameof(frame));
        }

        VideoFrame? prev;
        lock (_frameLock)
        {
            prev = _pendingFrame;
            _pendingFrame = frame;
        }

        prev?.Dispose();
        PostRequestNextFrame();
    }

    private void PostRequestNextFrame()
    {
        if (_sinkDisposed)
            return;
        var d = Dispatcher;
        if (d.CheckAccess())
            RequestNextFrameRendering();
        else
            d.Post(() => RequestNextFrameRendering(), DispatcherPriority.Render);
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _gl = SilkGl.GetApi(name => gl.GetProcAddress(name));
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, $"{nameof(VideoOpenGlControl)}.{nameof(OnOpenGlInit)}: GL.GetApi");
            return;
        }

        TryCreateRenderer(gl);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        try { _renderer?.Dispose(); }
#if DEBUG
        catch (Exception ex) { MediaDiagnostics.LogError(ex, $"{nameof(VideoOpenGlControl)}: renderer dispose"); }
#else
        catch { /* best effort */ }
#endif
        _renderer = null;

        try { _win32Nv12Device.DisposeOwnedInteropHost(); }
#if DEBUG
        catch (Exception ex) { MediaDiagnostics.LogError(ex, $"{nameof(VideoOpenGlControl)}: Win32 NV12 resolver"); }
#else
        catch { /* best effort */ }
#endif

        try { _gl?.Dispose(); }
#if DEBUG
        catch (Exception ex) { MediaDiagnostics.LogError(ex, $"{nameof(VideoOpenGlControl)}: GL dispose"); }
#else
        catch { /* best effort */ }
#endif
        _gl = null;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_gl is null)
            return;

        TryCreateRenderer(gl);

        var renderer = _renderer;
        if (renderer is null)
            return;

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var w = (int)Math.Max(1, Math.Ceiling(Bounds.Width * scaling));
        var h = (int)Math.Max(1, Math.Ceiling(Bounds.Height * scaling));

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        _gl.Viewport(0, 0, (uint)w, (uint)h);
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        VideoFrame? frame;
        lock (_frameLock)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
        }

        if (frame is not null)
        {
            try
            {
                GlVideoSinkHdr.ApplyTransferHint(renderer, frame, _hdrPreference);
                renderer.Upload(frame);
                renderer.Render(w, h, ViewportFit);
                _gl.Flush();
                _hasUploadedOnce = true;
            }
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, $"{nameof(VideoOpenGlControl)}.{nameof(OnOpenGlRender)}");
            }
            finally
            {
                frame.Dispose();
            }
        }
        else if (_hasUploadedOnce)
        {
            try
            {
                renderer.Render(w, h, ViewportFit);
                _gl.Flush();
            }
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, $"{nameof(VideoOpenGlControl)}.{nameof(OnOpenGlRender)} idle redraw");
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _sinkDisposed = true;
        VideoFrame? leftover;
        lock (_frameLock)
        {
            leftover = _pendingFrame;
            _pendingFrame = null;
        }

        leftover?.Dispose();
        _win32Nv12Device.SetBorrowVideoSourceForWin32Nv12Gl(null);
        _win32Nv12Device.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    private void TryCreateRenderer(GlInterface gl)
    {
        if (_gl is null)
            return;

        lock (_configureLock)
        {
            if (!_configured || _renderer is not null)
                return;

            nint win32D3d11DevicePtr = 0;
            if (OperatingSystem.IsWindows() && _format.PixelFormat == VideoPixelFormat.Nv12)
                win32D3d11DevicePtr = _win32Nv12Device.ResolveDevicePointerForNv12RendererConstruction();

            var allowLazyNv12 = OperatingSystem.IsWindows()
                && _format.PixelFormat == VideoPixelFormat.Nv12
                && win32D3d11DevicePtr == 0
                && !_win32Nv12Device.CreatesFallbackOwnedInteropDevice;

            YuvDmabufEglInterop? eglDmabuf = null;
            if (OperatingSystem.IsLinux())
            {
                nint dpy = LinuxEglNative.TryGetCurrentDisplay();
                if (dpy != 0)
                    eglDmabuf = new YuvDmabufEglInterop(dpy, name => LinuxEglNative.ResolveEglOrGlProc(name, gl));
            }

            try
            {
                _renderer = new YuvVideoRenderer(_gl, _format, eglDmabufInterop: eglDmabuf,
                    win32D3D11DeviceComPtrForNv12: win32D3d11DevicePtr,
                    allowLazyWin32Nv12UploaderFromDecodedFrame: allowLazyNv12);
            }
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, $"{nameof(VideoOpenGlControl)}: YuvVideoRenderer construction");
            }
        }
    }

    private static class LinuxEglNative
    {
        private const string EglLib = "libEGL.so.1";

        [DllImport(EglLib, EntryPoint = "eglGetCurrentDisplay", ExactSpelling = true)]
        private static extern nint eglGetCurrentDisplay();

        [DllImport(EglLib, EntryPoint = "eglGetProcAddress", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern nint eglGetProcAddress(string proc);

        public static nint TryGetCurrentDisplay()
        {
            try { return eglGetCurrentDisplay(); }
            catch (DllNotFoundException) { return 0; }
            catch (EntryPointNotFoundException) { return 0; }
        }

        public static nint ResolveEglOrGlProc(string name, GlInterface gl)
        {
            try
            {
                nint egl = eglGetProcAddress(name);
                if (egl != 0)
                    return egl;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            return gl.GetProcAddress(name);
        }
    }
}
