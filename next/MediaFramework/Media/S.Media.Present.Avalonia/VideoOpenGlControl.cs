using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Gpu;
using Silk.NET.OpenGL;
using SilkGl = Silk.NET.OpenGL.GL;
using VideoPixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Present.Avalonia;

/// <summary>
/// Avalonia <see cref="OpenGlControlBase"/> that implements <see cref="IVideoOutput"/> using the same
/// <see cref="YuvVideoRenderer"/> / shader pipeline as <c>SDL3GLVideoOutput</c> (<see cref="S.Media.OpenGL"/>).
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
public sealed class VideoOpenGlControl : OpenGlControlBase, IVideoOutput, IVideoOutputD3D11GlBorrowSetup
{
    private static readonly VideoPixelFormat[] AcceptedFormats = YuvVideoRenderer.SupportedPixelFormats.ToArray();

    private readonly Win32Nv12GlUploadDeviceResolver _win32Nv12Device;
    private GlVideoOutputHdrPreference _hdrPreference;
    private GlOutputBitDepth _swapchainBitDepth = GlOutputBitDepth.Eight;

    private readonly Lock _configureLock = new();
    private VideoFormat _format;
    private bool _configured;
    private volatile bool _sinkDisposed;

    private SilkGl? _gl;
    private YuvVideoRenderer? _renderer;
    private bool _hasUploadedOnce;
    /// <summary>
    /// Flipped to true by <see cref="Configure"/> when called with a format different from the current one.
    /// The next <see cref="OnOpenGlRender"/> tick tears down the renderer (GL-thread only) and rebuilds it
    /// for the new format. Lets a single control host successive playback sessions with different formats.
    /// </summary>
    private volatile bool _rendererNeedsRebuild;

    private VideoFrame? _pendingFrame;
    private readonly object _frameLock = new();

    public VideoOpenGlControl(
        GlVideoOutputHdrPreference hdrPreference = GlVideoOutputHdrPreference.FollowFrameHints,
        GlOutputBitDepth swapchainBitDepth = GlOutputBitDepth.Eight,
        nint borrowD3D11DeviceComPtrForNv12Gl = 0,
        bool createFallbackD3D11InteropDeviceForWin32Nv12 = true)
    {
        _hdrPreference = hdrPreference;
        _swapchainBitDepth = swapchainBitDepth;
        _win32Nv12Device = new Win32Nv12GlUploadDeviceResolver(borrowD3D11DeviceComPtrForNv12Gl,
            createFallbackD3D11InteropDeviceForWin32Nv12,
            nameof(VideoOpenGlControl));
    }

    /// <summary>Letterboxing / stretch for <see cref="YuvVideoRenderer.Render(int,int,VideoViewportFit)"/>.
    /// Defaults to aspect-preserving <see cref="VideoViewportFit.Contain"/> (matches the SDL3 presenter; equal to
    /// <see cref="VideoViewportFit.Stretch"/> when the control matches the frame aspect).</summary>
    public VideoViewportFit ViewportFit { get; set; } = VideoViewportFit.Contain;

    public GlVideoOutputHdrPreference HdrPreference
    {
        get => _hdrPreference;
        set => _hdrPreference = value;
    }

    /// <summary>
    /// Requested drawable bit depth. Avalonia's OpenGL surface may ignore 10-bit attributes on some backends;
    /// use <see cref="S.Media.SDL3.SDL3GLVideoOutput"/> when a 10-bit swapchain is required.
    /// </summary>
    public GlOutputBitDepth SwapchainBitDepth
    {
        get => _swapchainBitDepth;
        set => _swapchainBitDepth = value;
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

    private long _renderedFrames;
    private long _hardwareFrames;
    private volatile bool _dmabufImportAvailable;

    /// <summary>Diagnostic: frames uploaded + rendered on the GL thread — i.e. actually presented on screen.</summary>
    public long RenderedFrameCount => Volatile.Read(ref _renderedFrames);

    /// <summary>Diagnostic: of <see cref="RenderedFrameCount"/>, how many carried a hardware (dma-buf / Win32
    /// D3D11) backing — uploaded zero-copy through the interop rather than CPU-reuploaded.</summary>
    public long HardwareFrameCount => Volatile.Read(ref _hardwareFrames);

    /// <summary>Diagnostic: true once the Linux dma-buf EGL import is wired (EGL display present); dma-buf-backed
    /// frames are then imported zero-copy. (Windows uses the separate D3D11 shared-handle path.)</summary>
    public bool DmabufImportAvailable => _dmabufImportAvailable;

    public void SetBorrowVideoSourceForWin32Nv12Gl(IVideoSource? videoSource) =>
        _win32Nv12Device.SetBorrowVideoSourceForWin32Nv12Gl(videoSource);

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_sinkDisposed, this);
        VideoFrame? droppedPending = null;
        lock (_configureLock)
        {
            if (Array.IndexOf(AcceptedFormats, format.PixelFormat) < 0)
                throw new NotSupportedException(
                    $"{nameof(VideoOpenGlControl)} does not accept pixel format {format.PixelFormat}; supported: {string.Join(", ", AcceptedFormats)}");
            if (format.Width <= 0 || format.Height <= 0)
                throw new ArgumentException("video format must have positive dimensions", nameof(format));

            // Idempotent for the same format so back-to-back negotiations on a persistent control are cheap.
            if (_configured && _format == format)
                return;

            if (_configured)
            {
                // Reconfigure: the next GL render will dispose the old YuvVideoRenderer (GL resources must
                // be released on the GL thread) and rebuild for the new format. Any frame queued under the
                // old format is dropped here to avoid the Submit format-mismatch guard rejecting it.
                _rendererNeedsRebuild = true;
                _hasUploadedOnce = false;
                lock (_frameLock)
                {
                    droppedPending = _pendingFrame;
                    _pendingFrame = null;
                }
            }

            _format = format;
            _configured = true;
        }

        droppedPending?.Dispose();
        if (_rendererNeedsRebuild)
            PostRequestNextFrame();

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
            throw new ArgumentException($"frame format {frame.Format} does not match output format {_format}", nameof(frame));
        }

        VideoFrame? prev;
        lock (_frameLock)
        {
            // Re-check inside the lock: OnDetachedFromVisualTree sets _sinkDisposed
            // and then drains _pendingFrame. Without this re-check a Submit that passed
            // the line-above guard can race detach and end up parking the new frame in
            // _pendingFrame with no future OnOpenGlRender callback to drain it (leak).
            if (_sinkDisposed)
            {
                frame.Dispose();
                throw new ObjectDisposedException(nameof(VideoOpenGlControl));
            }
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
        MediaDiagnostics.SwallowDisposeErrors(() => _renderer?.Dispose(), $"{nameof(VideoOpenGlControl)}: renderer dispose");
        _renderer = null;

        MediaDiagnostics.SwallowDisposeErrors(_win32Nv12Device.DisposeOwnedInteropHost, $"{nameof(VideoOpenGlControl)}: Win32 NV12 resolver");

        MediaDiagnostics.SwallowDisposeErrors(() => _gl?.Dispose(), $"{nameof(VideoOpenGlControl)}: GL dispose");
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
                GlVideoOutputHdr.ApplyTransferHint(renderer, frame, _hdrPreference);
                renderer.Upload(frame);
                renderer.Render(w, h, ViewportFit);
                _gl.Flush();
                _hasUploadedOnce = true;
                Interlocked.Increment(ref _renderedFrames);
                if (frame.HardwareBacking is not null)
                    Interlocked.Increment(ref _hardwareFrames);
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
            if (!_configured)
                return;

            if (_rendererNeedsRebuild && _renderer is not null)
            {
                MediaDiagnostics.SwallowDisposeErrors(_renderer.Dispose, $"{nameof(VideoOpenGlControl)}: renderer dispose during reconfigure");
                _renderer = null;
                _rendererNeedsRebuild = false;
            }

            if (_renderer is not null)
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
                _dmabufImportAvailable = eglDmabuf is not null;
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
