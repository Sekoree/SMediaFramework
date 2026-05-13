using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;
using S.Media.Core.Video;
using S.Media.OpenGL;
using System.Threading;
using SilkGL = Silk.NET.OpenGL.GL;

namespace S.Media.SDL3;

/// <summary>
/// <see cref="IVideoSink"/> backed by an SDL3 window + an OpenGL 3.3 Core
/// context. Same dispatch model as <see cref="SDL3VideoSink"/>
/// (auto-thread or manual <see cref="Pump"/>) but rendering goes through
/// <see cref="YuvVideoRenderer"/>, so the same shader pipeline can be
/// shared with an Avalonia OpenGL surface later on.
/// </summary>
/// <remarks>
/// <para>
/// On Windows, when the negotiated format is <see cref="PixelFormat.Nv12"/>, a default
/// hardware <c>ID3D11Device</c> is created via <see cref="D3D11GlInteropDeviceHost"/> so <see cref="VideoFrame.Win32Nv12"/> frames
/// (D3D11 shared-handle decode path) can upload through <see cref="Nv12Win32SharedHandleGpuUploader"/>,
/// unless <paramref name="borrowD3D11DeviceComPtrForNv12Gl"/> supplies libav's device (same adapter as decoded textures),
/// or <see cref="VideoFormatNegotiator.Connect"/> wires an <see cref="IHardwareD3D11GlInteropSource"/> via <see cref="IVideoSinkD3D11GlBorrowSetup"/>.
/// When <paramref name="createFallbackD3D11InteropDeviceForWin32Nv12"/> is <see langword="false"/>, SDL does not create that helper device;
/// <see cref="YuvVideoRenderer"/> then binds from <see cref="VideoWin32Nv12Backing.LibavD3D11DeviceComPtr"/> on the first frame (true zero-host; requires D3D11VA COM pointers on the backing or a negotiated borrow).
/// </para>
/// <para>
/// Pixel-format support matches <see cref="YuvVideoRenderer.SupportedPixelFormats"/>
/// (BGRA/RGBA/RGB24, planar and semi-planar YUV, 10-bit 422, packed 422, P010/P016, …).
/// </para>
/// <para>
/// VSYNC: enabled by default via <c>SDL_GL_SetSwapInterval(1)</c>; pass
/// <c>vsync:false</c> to free-run. The render thread (or the
/// <see cref="Pump"/> caller in manual mode) blocks on
/// <c>SDL_GL_SwapWindow</c> when VSYNC is on. During <see cref="Dispose"/>, if teardown races a pending present,
/// the sink forces <c>SDL_GL_SetSwapInterval(0)</c> before the next swap so shutdown is less likely to block on vsync.
/// </para>
/// <para>
/// <b>Texture mirrors (same pixels, extra windows)</b>: call <see cref="CreateTextureMirror"/>, configure it with the same
/// <see cref="VideoFormat"/> as the anchor, then <see cref="RegisterTextureMirror"/>. The anchor performs a single
/// <see cref="YuvVideoRenderer.Upload"/> per frame; mirrors only draw shared GL textures (via <see cref="YuvVideoRenderer.CreateSharedTextureDrawView"/>).
/// The anchor must use <c>ownsThread:false</c> so mirror initialization can safely call <c>SDL_GL_MakeCurrent</c> on the anchor context.
/// NV12 dmabuf / Win32 D3D11 paths cannot be mirrored yet (CPU-plane and other non-interop formats can).
/// </para>
/// </remarks>
public sealed unsafe class SDL3GLVideoSink : IVideoSink, IVideoSinkD3D11GlBorrowSetup, IDisposable
{
    private static readonly PixelFormat[] AcceptedFormats = YuvVideoRenderer.SupportedPixelFormats.ToArray();

    private readonly string _title;
    private readonly int _initialWindowWidth;
    private readonly int _initialWindowHeight;
    private readonly bool _vsync;
    private readonly bool _ownsThread;

    private GlVideoSinkHdrPreference _hdrPreference = GlVideoSinkHdrPreference.FollowFrameHints;

    private VideoFormat _format;
    private bool _configured;
    private volatile bool _disposed;

    private Thread? _renderThread;
    private CancellationTokenSource? _cts;
    private readonly AutoResetEvent _wakeup = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _renderError;

    private VideoFrame? _pendingFrame;
    private long _displayed;
    private long _droppedNew;

    private nint _window;
    private nint _glContext;
    private SilkGL? _gl;
    private YuvVideoRenderer? _renderer;
    /// <summary>Hardware D3D11 device host for Win32 NV12 → GL when no borrowed pointer is supplied.</summary>
    private D3D11GlInteropDeviceHost? _nv12D3d11Host;
    /// <summary>Non-zero: use this libav-owned <c>ID3D11Device</c> COM pointer (do not dispose).</summary>
    private readonly nint _borrowD3D11DeviceComPtrForNv12Gl;
    /// <summary>When false, Win32 NV12 defers <see cref="D3D11GlInteropDeviceHost"/> creation; <see cref="YuvVideoRenderer"/> binds from libav on first frame (true zero-host).</summary>
    private readonly bool _createFallbackD3D11InteropDeviceForWin32Nv12;
    /// <summary>Set by <see cref="IVideoSinkD3D11GlBorrowSetup.SetBorrowVideoSourceForWin32Nv12Gl"/> when ctor borrow is zero.</summary>
    private IVideoSource? _borrowVideoSourceForWin32Nv12Gl;
    private int _viewportWidth;
    private int _viewportHeight;

    /// <summary>Set after <see cref="InitGraphics"/> completes; controls whether <see cref="ForceDisposeMirrorFromAnchor"/> must release the SDL runtime.</summary>
    private bool _graphicsInitialized;

    /// <summary>When non-null, this sink is a texture mirror: no <see cref="Submit"/>; GL shares plane textures with the anchor.</summary>
    private readonly SDL3GLVideoSink? _textureMirrorAnchor;

    private readonly List<SDL3GLVideoSink> _registeredTextureMirrors = new();
    private readonly object _textureMirrorLock = new();

    private static int _nv12BorrowAdapterDiagLogged;
    private static int _nv12OwnedInteropDiagLogged;
    private static int _nv12ZeroHostModeLogged;

    public VideoFormat Format
    {
        get
        {
            if (!_configured)
                throw new InvalidOperationException("SDL3GLVideoSink.Configure has not been called yet");
            return _format;
        }
    }

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => AcceptedFormats;
    public bool OwnsThread => _ownsThread;
    public long DisplayedCount => Volatile.Read(ref _displayed);
    public long DroppedNewer => Volatile.Read(ref _droppedNew);

    /// <summary>True when this sink was created with <see cref="CreateTextureMirror"/> (frames go to the anchor only).</summary>
    public bool IsTextureMirror => _textureMirrorAnchor is not null;

    public event EventHandler? CloseRequested;
    public event EventHandler<(int Width, int Height)>? Resized;

    public SDL3GLVideoSink(string title = "SDL3 GL Video", int initialWidth = 1280, int initialHeight = 720,
                          bool vsync = true, bool ownsThread = true,
                          GlVideoSinkHdrPreference hdrPreference = GlVideoSinkHdrPreference.FollowFrameHints,
                          nint borrowD3D11DeviceComPtrForNv12Gl = 0,
                          bool createFallbackD3D11InteropDeviceForWin32Nv12 = true,
                          SDL3GLVideoSink? textureMirrorAnchor = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        if (initialWidth <= 0) throw new ArgumentOutOfRangeException(nameof(initialWidth));
        if (initialHeight <= 0) throw new ArgumentOutOfRangeException(nameof(initialHeight));
        if (textureMirrorAnchor is not null && ownsThread)
        {
            throw new ArgumentException(
                "Texture mirror sinks cannot use ownsThread:true; the anchor drives rendering. Use ownsThread:false or CreateTextureMirror.",
                nameof(ownsThread));
        }

        _title = title;
        _initialWindowWidth = initialWidth;
        _initialWindowHeight = initialHeight;
        _vsync = vsync;
        _ownsThread = ownsThread;
        _hdrPreference = hdrPreference;
        _viewportWidth = initialWidth;
        _viewportHeight = initialHeight;
        _borrowD3D11DeviceComPtrForNv12Gl = borrowD3D11DeviceComPtrForNv12Gl;
        _createFallbackD3D11InteropDeviceForWin32Nv12 = createFallbackD3D11InteropDeviceForWin32Nv12;
        _textureMirrorAnchor = textureMirrorAnchor;
    }

    /// <summary>
    /// Creates a secondary window that draws the same GL plane textures as <paramref name="anchor"/> (one upload on the anchor).
    /// Configure with the same <see cref="VideoFormat"/> as the anchor, then call <see cref="RegisterTextureMirror"/> on the anchor.
    /// </summary>
    public static SDL3GLVideoSink CreateTextureMirror(SDL3GLVideoSink anchor, string title = "SDL3 GL (mirror)",
        int initialWidth = 1280, int initialHeight = 720, bool vsync = true,
        GlVideoSinkHdrPreference hdrPreference = GlVideoSinkHdrPreference.FollowFrameHints)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return new SDL3GLVideoSink(title, initialWidth, initialHeight, vsync, ownsThread: false, hdrPreference,
            borrowD3D11DeviceComPtrForNv12Gl: 0, createFallbackD3D11InteropDeviceForWin32Nv12: true, textureMirrorAnchor: anchor);
    }

    /// <summary>
    /// After both sinks are <see cref="Configure"/>d with the same format, register this mirror so the anchor presents it each frame.
    /// </summary>
    /// <exception cref="ArgumentException">When <paramref name="mirror"/> was not created for this anchor.</exception>
    /// <exception cref="InvalidOperationException">When formats differ or sinks are not configured.</exception>
    public void RegisterTextureMirror(SDL3GLVideoSink mirror)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(mirror);
        if (mirror._textureMirrorAnchor != this)
            throw new ArgumentException("Mirror must be created with CreateTextureMirror(this, ...).", nameof(mirror));
        if (mirror == this)
            throw new ArgumentException("Cannot register a sink as its own mirror.", nameof(mirror));
        if (!_configured || !mirror._configured)
            throw new InvalidOperationException("Configure both the anchor and the mirror before RegisterTextureMirror.");
        if (_ownsThread)
            throw new InvalidOperationException(
                "RegisterTextureMirror requires the anchor to use ownsThread:false (see CreateTextureMirror remarks).");
        if (mirror._format != _format)
            throw new InvalidOperationException(
                $"Mirror format {mirror._format} must match anchor format {_format}.");

        lock (_textureMirrorLock)
        {
            if (_registeredTextureMirrors.Contains(mirror))
                return;
            _registeredTextureMirrors.Add(mirror);
        }
    }

    public void UnregisterTextureMirror(SDL3GLVideoSink mirror)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        lock (_textureMirrorLock)
            _registeredTextureMirrors.Remove(mirror);
    }

    private SDL3GLVideoSink[] SnapshotRegisteredMirrors()
    {
        lock (_textureMirrorLock)
            return _registeredTextureMirrors.Count == 0
                ? []
                : _registeredTextureMirrors.ToArray();
    }

    /// <summary>Allows changing HDR handling after construction (effective on the next rendered frame).</summary>
    public GlVideoSinkHdrPreference HdrPreference
    {
        get => _hdrPreference;
        set => _hdrPreference = value;
    }

    public void SetBorrowVideoSourceForWin32Nv12Gl(IVideoSource? videoSource) =>
        _borrowVideoSourceForWin32Nv12Gl = videoSource;

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_configured)
            throw new InvalidOperationException("SDL3GLVideoSink already configured; create a new sink to switch format");
        if (Array.IndexOf(AcceptedFormats, format.PixelFormat) < 0)
            throw new NotSupportedException(
                $"SDL3GLVideoSink does not accept pixel format {format.PixelFormat}; supported: {string.Join(", ", AcceptedFormats)}");
        if (format.Width <= 0 || format.Height <= 0)
            throw new ArgumentException("video format must have positive dimensions", nameof(format));

        _format = format;

        if (_textureMirrorAnchor is { } anchor && !anchor._configured)
            throw new InvalidOperationException("Configure the anchor SDL3GLVideoSink before configuring a texture mirror.");

        if (_ownsThread)
        {
            _configured = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _renderThread = new Thread(() => RenderLoop(token))
            {
                IsBackground = true,
                Name = $"SDL3GLVideoSink:{_title}",
            };
            _renderThread.Start();

            _ready.Wait(token);
            if (_renderError is not null)
            {
                var err = _renderError;
                _renderError = null;
                throw err;
            }
        }
        else
        {
            SDL3Runtime.Acquire();
            try
            {
                InitGraphics();
                _configured = true;
            }
            catch
            {
                SDL3Runtime.Release();
                throw;
            }
        }
    }

    public void Submit(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_configured)
        {
            frame.Dispose();
            throw new InvalidOperationException("SDL3GLVideoSink.Submit called before Configure");
        }
        if (_textureMirrorAnchor is not null)
        {
            frame.Dispose();
            throw new NotSupportedException(
                "Texture mirror sinks do not accept frames; submit to the anchor sink that this mirror was created from.");
        }
        if (frame.Format.Width != _format.Width || frame.Format.Height != _format.Height
            || frame.Format.PixelFormat != _format.PixelFormat)
        {
            frame.Dispose();
            throw new ArgumentException(
                $"frame format {frame.Format} does not match sink format {_format}", nameof(frame));
        }

        var prev = Interlocked.Exchange(ref _pendingFrame, frame);
        if (prev is not null)
        {
            prev.Dispose();
            Interlocked.Increment(ref _droppedNew);
        }
        if (_ownsThread) _wakeup.Set();
    }

    /// <summary>Manual mode driver (see <see cref="SDL3VideoSink.Pump"/>).</summary>
    public void Pump()
    {
        if (_ownsThread)
            throw new InvalidOperationException("SDL3GLVideoSink.Pump called on an auto-thread sink — use ownsThread:false");
        if (_disposed) return;
        if (!_configured) return;

        DrainEvents();

        var frame = Interlocked.Exchange(ref _pendingFrame, null);
        if (frame is null) return;

        try { PresentFrame(frame); }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "SDL3GLVideoSink.Pump PresentFrame");
        }
        finally { frame.Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _textureMirrorAnchor?.UnregisterTextureMirror(this);
        _disposed = true;
        _borrowVideoSourceForWin32Nv12Gl = null;

        if (_ownsThread)
        {
            _cts?.Cancel();
            _wakeup.Set();
            CooperativePlaybackJoin.JoinThread(_renderThread, TimeSpan.FromSeconds(45));
            _cts?.Dispose();
        }
        else
        {
            try { TeardownGraphics(); }
            finally { SDL3Runtime.Release(); }
        }

        var leftover = Interlocked.Exchange(ref _pendingFrame, null);
        leftover?.Dispose();

        _wakeup.Dispose();
        _ready.Dispose();
    }

    internal bool TryGetGlStateForMirror(out nint window, out nint glContext, out YuvVideoRenderer? anchorRenderer)
    {
        window = _window;
        glContext = _glContext;
        anchorRenderer = _renderer;
        return _gl != null && _window != nint.Zero && _glContext != nint.Zero && _renderer != null;
    }

    /// <summary>Used when the anchor tears down: mirrors must release GL before the anchor destroys shared textures.</summary>
    internal void ForceDisposeMirrorFromAnchor()
    {
        if (_textureMirrorAnchor is null)
            return;
        if (_disposed)
            return;
        _disposed = true;
        var releaseSdl = _graphicsInitialized;
        TeardownGraphicsCore();
        if (releaseSdl)
            SDL3Runtime.Release();
    }

    // ---------- render thread (auto mode) ---------------------------------

    private void RenderLoop(CancellationToken token)
    {
        try
        {
            SDL3Runtime.Acquire();
            try
            {
                InitGraphics();
                _ready.Set();

                var handles = new WaitHandle[] { _wakeup, token.WaitHandle };
                while (!token.IsCancellationRequested)
                {
                    var idx = WaitHandle.WaitAny(handles, 50);
                    if (idx == 1) break;
                    DrainEvents();

                    var frame = Interlocked.Exchange(ref _pendingFrame, null);
                    if (frame is null) continue;

                    try { PresentFrame(frame); }
                    catch (Exception ex)
                    {
                        MediaDiagnostics.LogError(ex, "SDL3GLVideoSink.RenderLoop PresentFrame");
                    }
                    finally { frame.Dispose(); }
                }
            }
            finally
            {
                TeardownGraphics();
                SDL3Runtime.Release();
            }
        }
        catch (Exception ex)
        {
            _renderError = ex;
            _ready.Set();
        }
    }

    private void TryResolveWin32Nv12D3d11DeviceComPtr(out nint win32D3d11DevicePtr)
    {
        win32D3d11DevicePtr = 0;
        if (_borrowD3D11DeviceComPtrForNv12Gl != 0)
        {
            if (D3D11InteropUtility.TryValidateDeviceComPointer(_borrowD3D11DeviceComPtrForNv12Gl, out var ctorErr))
            {
                win32D3d11DevicePtr = _borrowD3D11DeviceComPtrForNv12Gl;
                return;
            }

            MediaDiagnostics.LogWarning(
                "SDL3GLVideoSink: ctor borrowD3D11DeviceComPtrForNv12Gl is not a valid ID3D11Device ({0}) — falling back to libav or owned device.",
                ctorErr);
        }

        if (_borrowVideoSourceForWin32Nv12Gl is IHardwareD3D11GlInteropSource hw
            && hw.TryGetHardwareD3D11DeviceForWin32Gl(out var libavPtr)
            && libavPtr != 0)
        {
            if (D3D11InteropUtility.TryValidateDeviceComPointer(libavPtr, out var libavErr))
            {
                win32D3d11DevicePtr = libavPtr;
                TryLogNv12D3d11BorrowAdapterOnce(hw, libavPtr);
                return;
            }

            MediaDiagnostics.LogWarning(
                "SDL3GLVideoSink: libav D3D11 device pointer is invalid ({0}){1}",
                libavErr,
                _createFallbackD3D11InteropDeviceForWin32Nv12
                    ? " — trying owned interop device instead."
                    : " — true zero-host mode will not create an SDL-owned D3D11 device.");
        }

        if (_createFallbackD3D11InteropDeviceForWin32Nv12)
        {
            _nv12D3d11Host = D3D11GlInteropDeviceHost.TryCreateOwned(out var d3dErr);
            if (_nv12D3d11Host != null)
            {
                win32D3d11DevicePtr = _nv12D3d11Host.NativeComPointer;
                TryLogNv12OwnedInteropAdapterOnce(win32D3d11DevicePtr);
                return;
            }

            if (d3dErr is not null)
            {
                MediaDiagnostics.LogWarning(
                    "SDL3GLVideoSink: could not create D3D11 device for Win32 NV12 GL upload — Win32Nv12 frames will fail until a device is supplied: {0}",
                    d3dErr);
            }

            return;
        }

        if (win32D3d11DevicePtr == 0 && Interlocked.Exchange(ref _nv12ZeroHostModeLogged, 1) == 0)
        {
            MediaDiagnostics.LogInformation(
                "SDL3GLVideoSink: Win32 NV12 true zero-host — skipping SDL-owned D3D11GlInteropDeviceHost; YuvVideoRenderer will use libav ID3D11Device from the first Win32 NV12 frame (requires LibavD3D11DeviceComPtr on backing or negotiator-borrowed device).");
        }
    }

    private static void TryLogNv12D3d11BorrowAdapterOnce(IHardwareD3D11GlInteropSource hw, nint devicePtr)
    {
        if (Interlocked.Exchange(ref _nv12BorrowAdapterDiagLogged, 1) != 0)
            return;

        if (hw.TryGetHardwareD3D11AdapterLuid(out var decodeLuid) && decodeLuid != 0
            && D3D11InteropUtility.TryGetAdapterLuid(devicePtr, out var deviceLuid))
        {
            if (decodeLuid != deviceLuid)
            {
                MediaDiagnostics.LogWarning(
                    "SDL3GLVideoSink: DXGI adapter LUID from libav decode (packed={0}) differs from LUID derived from the D3D11 device used for GL (packed={1}) — OpenSharedResource / WGL_NV_DX_interop may fail on multi-GPU systems.",
                    decodeLuid,
                    deviceLuid);
                return;
            }

            MediaDiagnostics.LogInformation(
                "SDL3GLVideoSink: Win32 NV12 GL using libav D3D11 device (DXGI adapter LUID packed={0}).",
                decodeLuid);
            return;
        }

        MediaDiagnostics.LogInformation(
            "SDL3GLVideoSink: Win32 NV12 GL using libav D3D11 device (adapter LUID unavailable for diagnostics — decode path may still be valid).");
    }

    private static void TryLogNv12OwnedInteropAdapterOnce(nint devicePtr)
    {
        if (Interlocked.Exchange(ref _nv12OwnedInteropDiagLogged, 1) != 0)
            return;

        if (D3D11InteropUtility.TryGetAdapterLuid(devicePtr, out var luid))
        {
            MediaDiagnostics.LogInformation(
                "SDL3GLVideoSink: Win32 NV12 GL using owned D3D11 interop helper (DXGI adapter LUID packed={0}).",
                luid);
            return;
        }

        MediaDiagnostics.LogInformation(
            "SDL3GLVideoSink: Win32 NV12 GL using owned D3D11 interop helper (adapter LUID unavailable for diagnostics).");
    }

    private void InitGraphics()
    {
        ApplyStandardGlContextAttributes();

        if (_textureMirrorAnchor is { } shareFrom)
        {
            if (shareFrom._ownsThread)
            {
                throw new InvalidOperationException(
                    "Texture mirror initialization requires the anchor sink to use ownsThread:false so the anchor GL context can be made current on this thread. Initializing a mirror while the anchor runs OpenGL on a background thread is not supported (unsafe SDL_GL_MakeCurrent across threads).");
            }

            if (!shareFrom.TryGetGlStateForMirror(out var anchorWindow, out var anchorGlContext, out var anchorRenderer)
                || anchorRenderer is null)
            {
                throw new InvalidOperationException(
                    "Texture mirror init requires the anchor sink to finish OpenGL initialization first (configure the anchor before the mirror).");
            }

            if (!SDL.GLMakeCurrent(anchorWindow, anchorGlContext))
                throw new InvalidOperationException($"SDL_GL_MakeCurrent (anchor) failed: {SDL.GetError()}");

            try
            {
                if (!SDL.GLSetAttribute(SDL.GLAttr.ShareWithCurrentContext, 1))
                    MediaDiagnostics.LogWarning("SDL3GLVideoSink: SDL_GL_SetAttribute(ShareWithCurrentContext) failed: {0}", SDL.GetError());

                _window = SDL.CreateWindow(_title, _initialWindowWidth, _initialWindowHeight,
                    SDL.WindowFlags.OpenGL | SDL.WindowFlags.Resizable);
                if (_window == nint.Zero)
                    throw new InvalidOperationException($"SDL_CreateWindow (mirror) failed: {SDL.GetError()}");

                if (!SDL.ShowWindow(_window))
                    MediaDiagnostics.LogWarning("SDL3GLVideoSink: SDL_ShowWindow failed: {0}", SDL.GetError());
                if (!SDL.RaiseWindow(_window))
                    MediaDiagnostics.LogWarning("SDL3GLVideoSink: SDL_RaiseWindow failed: {0}", SDL.GetError());

                _glContext = SDL.GLCreateContext(_window);
                if (_glContext == nint.Zero)
                    throw new InvalidOperationException($"SDL_GL_CreateContext (mirror) failed: {SDL.GetError()}");

                if (!SDL.GLSetAttribute(SDL.GLAttr.ShareWithCurrentContext, 0))
                    MediaDiagnostics.LogWarning("SDL3GLVideoSink: clearing ShareWithCurrentContext failed: {0}", SDL.GetError());

                if (!SDL.GLMakeCurrent(_window, _glContext))
                    throw new InvalidOperationException($"SDL_GL_MakeCurrent (mirror) failed: {SDL.GetError()}");

                SDL.GLSetSwapInterval(_vsync ? 1 : 0);

                _gl = SilkGL.GetApi(name => SDL.GLGetProcAddress(name));

                try
                {
                    _renderer = YuvVideoRenderer.CreateSharedTextureDrawView(_gl, anchorRenderer, sharedShaderPrograms: true,
                        yPlaneMipmaps: false);
                }
                catch (NotSupportedException ex)
                {
                    throw new InvalidOperationException(
                        "Texture mirror is not supported for this pixel format or upload path (NV12 interop and similar require CPU-plane decoding or future shared-interop work).",
                        ex);
                }

                _graphicsInitialized = true;
                return;
            }
            catch
            {
                TeardownGraphicsCore();
                if (!SDL.GLMakeCurrent(anchorWindow, anchorGlContext))
                {
                    MediaDiagnostics.LogWarning("SDL3GLVideoSink: SDL_GL_MakeCurrent (restore anchor after mirror init failure) failed: {0}",
                        SDL.GetError());
                }

                throw;
            }
        }

        _window = SDL.CreateWindow(_title, _initialWindowWidth, _initialWindowHeight,
            SDL.WindowFlags.OpenGL | SDL.WindowFlags.Resizable);
        if (_window == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL.GetError()}");

        if (!SDL.ShowWindow(_window))
            MediaDiagnostics.LogWarning("SDL3GLVideoSink: SDL_ShowWindow failed: {0}", SDL.GetError());
        if (!SDL.RaiseWindow(_window))
            MediaDiagnostics.LogWarning("SDL3GLVideoSink: SDL_RaiseWindow failed: {0}", SDL.GetError());

        _glContext = SDL.GLCreateContext(_window);
        if (_glContext == nint.Zero)
            throw new InvalidOperationException($"SDL_GL_CreateContext failed: {SDL.GetError()}");

        if (!SDL.GLMakeCurrent(_window, _glContext))
            throw new InvalidOperationException($"SDL_GL_MakeCurrent failed: {SDL.GetError()}");

        SDL.GLSetSwapInterval(_vsync ? 1 : 0);

        _gl = SilkGL.GetApi(name => SDL.GLGetProcAddress(name));

        nint win32D3d11DevicePtr = 0;
        if (OperatingSystem.IsWindows() && _format.PixelFormat == PixelFormat.Nv12)
            TryResolveWin32Nv12D3d11DeviceComPtr(out win32D3d11DevicePtr);

        var allowLazyNv12 = OperatingSystem.IsWindows()
            && _format.PixelFormat == PixelFormat.Nv12
            && win32D3d11DevicePtr == 0
            && !_createFallbackD3D11InteropDeviceForWin32Nv12;

        _renderer = new YuvVideoRenderer(_gl, _format, eglDmabufInterop: OperatingSystem.IsLinux()
                ? new YuvDmabufEglInterop(SDL.EGLGetCurrentDisplay(), name => SDL.EGLGetProcAddress(name))
                : null,
            win32D3D11DeviceComPtrForNv12: win32D3d11DevicePtr,
            allowLazyWin32Nv12UploaderFromDecodedFrame: allowLazyNv12);

        _graphicsInitialized = true;
    }

    private static void ApplyStandardGlContextAttributes()
    {
        SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
        SDL.GLSetAttribute(SDL.GLAttr.DoubleBuffer, 1);
        SDL.GLSetAttribute(SDL.GLAttr.RedSize, 8);
        SDL.GLSetAttribute(SDL.GLAttr.GreenSize, 8);
        SDL.GLSetAttribute(SDL.GLAttr.BlueSize, 8);
        SDL.GLSetAttribute(SDL.GLAttr.AlphaSize, 0);
        SDL.GLSetAttribute(SDL.GLAttr.DepthSize, 0);
    }

    private void TeardownGraphics()
    {
        if (_textureMirrorAnchor is null)
        {
            SDL3GLVideoSink[] copy;
            lock (_textureMirrorLock)
            {
                copy = _registeredTextureMirrors.ToArray();
                _registeredTextureMirrors.Clear();
            }

            foreach (var m in copy)
            {
                try { m.ForceDisposeMirrorFromAnchor(); }
#if DEBUG
                catch (Exception ex) { MediaDiagnostics.LogError(ex, "SDL3GLVideoSink.TeardownGraphics: mirror"); }
#else
                catch { /* best effort */ }
#endif
            }
        }

        TeardownGraphicsCore();
    }

    private void TeardownGraphicsCore()
    {
        try { _renderer?.Dispose(); }
#if DEBUG
        catch (Exception ex) { MediaDiagnostics.LogError(ex, "SDL3GLVideoSink.TeardownGraphicsCore: renderer"); }
#else
        catch { /* best effort */ }
#endif
        _renderer = null;
        try { _nv12D3d11Host?.Dispose(); }
#if DEBUG
        catch (Exception ex) { MediaDiagnostics.LogError(ex, "SDL3GLVideoSink.TeardownGraphicsCore: D3D11 host"); }
#else
        catch { /* best effort */ }
#endif
        _nv12D3d11Host = null;
        try { _gl?.Dispose(); }
#if DEBUG
        catch (Exception ex) { MediaDiagnostics.LogError(ex, "SDL3GLVideoSink.TeardownGraphicsCore: GL"); }
#else
        catch { /* best effort */ }
#endif
        _gl = null;
        if (_glContext != nint.Zero)
        {
            SDL.GLDestroyContext(_glContext);
            _glContext = nint.Zero;
        }

        if (_window != nint.Zero)
        {
            SDL.DestroyWindow(_window);
            _window = nint.Zero;
        }

        _graphicsInitialized = false;
    }

    private void PresentFrame(VideoFrame frame)
    {
        if (_renderer is null || _gl is null) return;
        if (_disposed)
            return;

        ApplyTransferHintToRenderer(_renderer, frame);
        _renderer.Upload(frame);
        _gl.Flush();
        PresentTextureMirrors(frame);
        if (_window != nint.Zero && _glContext != nint.Zero
            && !SDL.GLMakeCurrent(_window, _glContext))
        {
            MediaDiagnostics.LogWarning("SDL3GLVideoSink: SDL_GL_MakeCurrent (anchor) before render failed: {0}", SDL.GetError());
            return;
        }

        _renderer.Render(_viewportWidth, _viewportHeight);
        if (_disposed)
            SDL.GLSetSwapInterval(0);

        SDL.GLSwapWindow(_window);
        Interlocked.Increment(ref _displayed);
    }

    private void PresentTextureMirrors(VideoFrame frame)
    {
        if (_textureMirrorAnchor is not null)
            return;

        foreach (var mirror in SnapshotRegisteredMirrors())
        {
            if (mirror._disposed)
                continue;
            try { mirror.PresentMirroredFrame(frame); }
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, "SDL3GLVideoSink mirror present");
            }
        }
    }

    internal void PresentMirroredFrame(VideoFrame frame)
    {
        if (_textureMirrorAnchor is null)
            return;
        if (_textureMirrorAnchor._disposed || _disposed)
            return;
        if (_renderer is null || _gl is null)
            return;
        var anchor = _textureMirrorAnchor;
        if (anchor._window == nint.Zero || anchor._glContext == nint.Zero)
            return;

        ApplyTransferHintToRenderer(_renderer, frame);
        if (!SDL.GLMakeCurrent(_window, _glContext))
        {
            MediaDiagnostics.LogWarning("SDL3GLVideoSink: SDL_GL_MakeCurrent (mirror) failed: {0}", SDL.GetError());
            return;
        }

        try
        {
            _renderer.Render(_viewportWidth, _viewportHeight);
            if (_disposed || anchor._disposed)
                SDL.GLSetSwapInterval(0);

            SDL.GLSwapWindow(_window);
            Interlocked.Increment(ref _displayed);
        }
        finally
        {
            if (!SDL.GLMakeCurrent(anchor._window, anchor._glContext))
            {
                MediaDiagnostics.LogWarning("SDL3GLVideoSink: SDL_GL_MakeCurrent (restore anchor) failed: {0}",
                    SDL.GetError());
            }
        }
    }

    private void ApplyTransferHintToRenderer(YuvVideoRenderer renderer, VideoFrame frame)
    {
        switch (_hdrPreference)
        {
            case GlVideoSinkHdrPreference.IgnoreFrameHints:
                return;
            case GlVideoSinkHdrPreference.ForceSdrDisplay:
                renderer.HdrTransfer = VideoHdrTransfer.None;
                return;
            case GlVideoSinkHdrPreference.ForceSrgbPreview:
                renderer.HdrTransfer = VideoHdrTransfer.Srgb;
                return;
            case GlVideoSinkHdrPreference.ForcePqPreview:
                renderer.HdrTransfer = VideoHdrTransfer.Pq;
                return;
            case GlVideoSinkHdrPreference.ForceHlgPreview:
                renderer.HdrTransfer = VideoHdrTransfer.Hlg;
                return;
            case GlVideoSinkHdrPreference.FollowFrameHints:
                break;
            default:
                renderer.HdrTransfer = VideoHdrTransfer.None;
                return;
        }

        switch (frame.ColorTransferHint)
        {
            case VideoTransferHint.Unspecified:
                return;
            case VideoTransferHint.Sdr:
                renderer.HdrTransfer = VideoHdrTransfer.None;
                return;
            case VideoTransferHint.FromSrgb:
                renderer.HdrTransfer = VideoHdrTransfer.Srgb;
                return;
            case VideoTransferHint.FromPq:
                renderer.HdrTransfer = VideoHdrTransfer.Pq;
                return;
            case VideoTransferHint.FromHlg:
                renderer.HdrTransfer = VideoHdrTransfer.Hlg;
                return;
            default:
                renderer.HdrTransfer = VideoHdrTransfer.None;
                return;
        }
    }

    private void DrainEvents()
    {
        SDL.PumpEvents();
        var mirrors = SnapshotRegisteredMirrors();
        while (SDL.PollEvent(out var ev))
        {
            if (TryConsumeWindowEvent(ev))
                continue;

            var consumed = false;
            foreach (var m in mirrors)
            {
                if (m.TryConsumeWindowEvent(ev))
                {
                    consumed = true;
                    break;
                }
            }

            if (consumed)
                continue;

            if ((SDL.EventType)ev.Type == SDL.EventType.Quit)
                SafeRaise(CloseRequested);
        }
    }

    private bool TryConsumeWindowEvent(SDL.Event ev)
    {
        switch ((SDL.EventType)ev.Type)
        {
            case SDL.EventType.WindowCloseRequested:
                if (ev.Window.WindowID == GetWindowId())
                {
                    SafeRaise(CloseRequested);
                    return true;
                }

                return false;
            case SDL.EventType.WindowResized:
            case SDL.EventType.WindowPixelSizeChanged:
                if (ev.Window.WindowID == GetWindowId())
                {
                    _viewportWidth = ev.Window.Data1;
                    _viewportHeight = ev.Window.Data2;
                    SafeRaiseResized(ev.Window.Data1, ev.Window.Data2);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private uint GetWindowId() => _window == nint.Zero ? 0u : SDL.GetWindowID(_window);

    private void SafeRaise(EventHandler? handler)
    {
        if (handler is null) return;
        try { handler.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "SDL3GLVideoSink event subscriber");
        }
    }

    private void SafeRaiseResized(int width, int height)
    {
        var handler = Resized;
        if (handler is null) return;
        try { handler.Invoke(this, (width, height)); }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "SDL3GLVideoSink Resized subscriber");
        }
    }
}
