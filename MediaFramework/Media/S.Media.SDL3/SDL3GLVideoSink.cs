using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;
using S.Media.Core.Video;
using S.Media.OpenGL;
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
/// Pixel-format support matches <see cref="YuvVideoRenderer.SupportedPixelFormats"/>
/// (BGRA/RGBA/RGB24, planar and semi-planar YUV, 10-bit 422, packed 422, P010/P016, …).
/// </para>
/// <para>
/// VSYNC: enabled by default via <c>SDL_GL_SetSwapInterval(1)</c>; pass
/// <c>vsync:false</c> to free-run. The render thread (or the
/// <see cref="Pump"/> caller in manual mode) blocks on
/// <c>SDL_GL_SwapWindow</c> when VSYNC is on.
/// </para>
/// </remarks>
public sealed unsafe class SDL3GLVideoSink : IVideoSink, IDisposable
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
    private int _viewportWidth;
    private int _viewportHeight;

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

    public event EventHandler? CloseRequested;
    public event EventHandler<(int Width, int Height)>? Resized;

    public SDL3GLVideoSink(string title = "SDL3 GL Video", int initialWidth = 1280, int initialHeight = 720,
                          bool vsync = true, bool ownsThread = true,
                          GlVideoSinkHdrPreference hdrPreference = GlVideoSinkHdrPreference.FollowFrameHints)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        if (initialWidth <= 0) throw new ArgumentOutOfRangeException(nameof(initialWidth));
        if (initialHeight <= 0) throw new ArgumentOutOfRangeException(nameof(initialHeight));

        _title = title;
        _initialWindowWidth = initialWidth;
        _initialWindowHeight = initialHeight;
        _vsync = vsync;
        _ownsThread = ownsThread;
        _hdrPreference = hdrPreference;
        _viewportWidth = initialWidth;
        _viewportHeight = initialHeight;
    }

    /// <summary>Allows changing HDR handling after construction (effective on the next rendered frame).</summary>
    public GlVideoSinkHdrPreference HdrPreference
    {
        get => _hdrPreference;
        set => _hdrPreference = value;
    }

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
        _configured = true;

        if (_ownsThread)
        {
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
            try { InitGraphics(); }
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
        _disposed = true;

        if (_ownsThread)
        {
            _cts?.Cancel();
            _wakeup.Set();
            CooperativePlaybackJoin.JoinThread(_renderThread, TimeSpan.FromSeconds(2));
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

    private void InitGraphics()
    {
        // Request OpenGL 3.3 Core — matches the shader #version directives.
        SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
        SDL.GLSetAttribute(SDL.GLAttr.DoubleBuffer, 1);
        SDL.GLSetAttribute(SDL.GLAttr.RedSize, 8);
        SDL.GLSetAttribute(SDL.GLAttr.GreenSize, 8);
        SDL.GLSetAttribute(SDL.GLAttr.BlueSize, 8);
        SDL.GLSetAttribute(SDL.GLAttr.AlphaSize, 0);
        SDL.GLSetAttribute(SDL.GLAttr.DepthSize, 0);

        _window = SDL.CreateWindow(_title, _initialWindowWidth, _initialWindowHeight,
            SDL.WindowFlags.OpenGL | SDL.WindowFlags.Resizable);
        if (_window == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL.GetError()}");

        _glContext = SDL.GLCreateContext(_window);
        if (_glContext == nint.Zero)
            throw new InvalidOperationException($"SDL_GL_CreateContext failed: {SDL.GetError()}");

        if (!SDL.GLMakeCurrent(_window, _glContext))
            throw new InvalidOperationException($"SDL_GL_MakeCurrent failed: {SDL.GetError()}");

        SDL.GLSetSwapInterval(_vsync ? 1 : 0);

        _gl = SilkGL.GetApi(name => SDL.GLGetProcAddress(name));
        _renderer = new YuvVideoRenderer(_gl, _format, eglDmabufInterop: OperatingSystem.IsLinux()
            ? new YuvDmabufEglInterop(SDL.EGLGetCurrentDisplay(), name => SDL.EGLGetProcAddress(name))
            : null);
    }

    private void TeardownGraphics()
    {
        try { _renderer?.Dispose(); } catch { /* best effort */ } _renderer = null;
        try { _gl?.Dispose(); } catch { /* best effort */ } _gl = null;
        if (_glContext != nint.Zero) { SDL.GLDestroyContext(_glContext); _glContext = nint.Zero; }
        if (_window != nint.Zero)    { SDL.DestroyWindow(_window);       _window = nint.Zero; }
    }

    private void PresentFrame(VideoFrame frame)
    {
        if (_renderer is null || _gl is null) return;
        ApplyTransferHintToRenderer(frame);
        _renderer.Upload(frame);
        _renderer.Render(_viewportWidth, _viewportHeight);
        SDL.GLSwapWindow(_window);
        Interlocked.Increment(ref _displayed);
    }

    private void ApplyTransferHintToRenderer(VideoFrame frame)
    {
        var r = _renderer;
        if (r == null)
            return;

        switch (_hdrPreference)
        {
            case GlVideoSinkHdrPreference.IgnoreFrameHints:
                return;
            case GlVideoSinkHdrPreference.ForceSdrDisplay:
                r.HdrTransfer = VideoHdrTransfer.None;
                return;
            case GlVideoSinkHdrPreference.ForceSrgbPreview:
                r.HdrTransfer = VideoHdrTransfer.Srgb;
                return;
            case GlVideoSinkHdrPreference.ForcePqPreview:
                r.HdrTransfer = VideoHdrTransfer.Pq;
                return;
            case GlVideoSinkHdrPreference.ForceHlgPreview:
                r.HdrTransfer = VideoHdrTransfer.Hlg;
                return;
            case GlVideoSinkHdrPreference.FollowFrameHints:
                break;
            default:
                r.HdrTransfer = VideoHdrTransfer.None;
                return;
        }

        switch (frame.ColorTransferHint)
        {
            case VideoTransferHint.Unspecified:
                return;
            case VideoTransferHint.Sdr:
                r.HdrTransfer = VideoHdrTransfer.None;
                return;
            case VideoTransferHint.FromSrgb:
                r.HdrTransfer = VideoHdrTransfer.Srgb;
                return;
            case VideoTransferHint.FromPq:
                r.HdrTransfer = VideoHdrTransfer.Pq;
                return;
            case VideoTransferHint.FromHlg:
                r.HdrTransfer = VideoHdrTransfer.Hlg;
                return;
            default:
                r.HdrTransfer = VideoHdrTransfer.None;
                return;
        }
    }

    private void DrainEvents()
    {
        SDL.PumpEvents();
        while (SDL.PollEvent(out var ev))
        {
            switch ((SDL.EventType)ev.Type)
            {
                case SDL.EventType.WindowCloseRequested:
                    if (ev.Window.WindowID == GetWindowId())
                        SafeRaise(CloseRequested);
                    break;
                case SDL.EventType.WindowResized:
                case SDL.EventType.WindowPixelSizeChanged:
                    if (ev.Window.WindowID == GetWindowId())
                    {
                        _viewportWidth = ev.Window.Data1;
                        _viewportHeight = ev.Window.Data2;
                        SafeRaiseResized(ev.Window.Data1, ev.Window.Data2);
                    }
                    break;
                case SDL.EventType.Quit:
                    SafeRaise(CloseRequested);
                    break;
            }
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
