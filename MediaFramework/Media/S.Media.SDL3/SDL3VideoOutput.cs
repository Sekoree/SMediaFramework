using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;
using S.Media.Core.Video;
using CoreVideo = S.Media.Core.Video;

namespace S.Media.SDL3;

/// <summary>
/// <see cref="IVideoOutput"/> backed by an SDL3 window + renderer. Two
/// dispatch modes:
/// <list type="bullet">
///   <item><b>Auto-thread</b> (default): the output owns its render thread.
///   <see cref="Submit"/> is wait-free (latest-wins frame slot) and safe to
///   call from any thread (including <see cref="Core.Clock.MediaClock.VideoTick"/>).</item>
///   <item><b>Manual</b>: no internal thread. The host calls
///   <see cref="Configure"/>, <see cref="Submit"/>, <see cref="Pump"/>, and
///   <see cref="Dispose"/> all from one thread of its choosing — required
///   on macOS (SDL pins window/event handling to the main thread) and
///   useful when sharing a thread with other host work.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Pixel formats: declares native SDL upload paths for BGRA32, I420, NV12,
/// UYVY, and YUY2 — picking any of those means the output uploads the source's
/// planes directly (no CPU conversion).
/// </para>
/// <para>
/// Window lifecycle: the output polls SDL events each render cycle (or each
/// <see cref="Pump"/> call in manual mode) so the window stays responsive.
/// Closing the window does not stop the output; <see cref="CloseRequested"/>
/// fires and the host decides when to <see cref="Dispose"/>.
/// </para>
/// </remarks>
public sealed unsafe class SDL3VideoOutput : IVideoOutput, IDisposable
{
    private static readonly PixelFormat[] AcceptedFormats =
    [
        PixelFormat.Bgra32,
        PixelFormat.I420,
        PixelFormat.Nv12,
        PixelFormat.Uyvy,
        PixelFormat.Yuyv,
    ];

    private readonly string _title;
    private readonly int _initialWindowWidth;
    private readonly int _initialWindowHeight;
    private readonly bool _vsync;
    private readonly bool _ownsThread;

    private VideoFormat _format;
    private bool _configured;
    private volatile bool _disposed;

    private Thread? _renderThread;
    private CancellationTokenSource? _cts;
    private readonly AutoResetEvent _wakeup = new(false);
    private readonly ManualResetEventSlim _ready = new(false);
    private Exception? _renderError;

    // Latest-wins frame slot. Producer Submit swaps; render thread / Pump takes.
    private VideoFrame? _pendingFrame;

    private long _displayed;
    private long _droppedNew;

    public VideoFormat Format
    {
        get
        {
            if (!_configured)
                throw new InvalidOperationException("SDL3VideoOutput.Configure has not been called yet");
            return _format;
        }
    }

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => AcceptedFormats;

    /// <summary>True when the output owns its render thread (auto mode).</summary>
    public bool OwnsThread => _ownsThread;

    /// <summary>Total frames the render thread / Pump has uploaded + presented.</summary>
    public long DisplayedCount => Volatile.Read(ref _displayed);
    /// <summary>Frames dropped because a newer one arrived before render consumed the previous.</summary>
    public long DroppedNewer => Volatile.Read(ref _droppedNew);

    /// <summary>
    /// Raised when the user clicks the window's close button (or the OS asks
    /// the window to close). The output does NOT auto-dispose — host code
    /// decides whether to tear down. In auto mode fires on the render
    /// thread; in manual mode fires on the <see cref="Pump"/> caller.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Raised when the OS reports the window was resized by the user.
    /// Payload is the new client-area size in pixels.
    /// </summary>
    public event EventHandler<(int Width, int Height)>? Resized;

    /// <param name="ownsThread">
    /// When <c>true</c> (default), the output starts a dedicated render thread
    /// in <see cref="Configure"/>. When <c>false</c>, the host owns the
    /// thread — call <see cref="Pump"/> periodically (typically from a UI
    /// loop tick) to drain events and present pending frames.
    /// </param>
    public SDL3VideoOutput(string title = "SDL3 Video", int initialWidth = 1280, int initialHeight = 720,
                        bool vsync = true, bool ownsThread = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        if (initialWidth <= 0) throw new ArgumentOutOfRangeException(nameof(initialWidth));
        if (initialHeight <= 0) throw new ArgumentOutOfRangeException(nameof(initialHeight));

        _title = title;
        _initialWindowWidth = initialWidth;
        _initialWindowHeight = initialHeight;
        _vsync = vsync;
        _ownsThread = ownsThread;
    }

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_configured)
        {
            // Same-format re-Configure is a no-op — matches SDL3GLVideoOutput and the VideoRouter
            // primary re-Configure on branch-route changes (which would otherwise throw here but not
            // for the GL output). A real format change still requires a new output (single-format
            // lifetime for the non-GL SDL path).
            if (_format == format)
                return;
            throw new InvalidOperationException("SDL3VideoOutput already configured; create a new output to switch format");
        }
        if (Array.IndexOf(AcceptedFormats, format.PixelFormat) < 0)
            throw new NotSupportedException(
                $"SDL3VideoOutput does not accept pixel format {format.PixelFormat}; supported: {string.Join(", ", AcceptedFormats)}");
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
                Name = $"SDL3VideoOutput:{_title}",
            };
            _renderThread.Start();

            // Wait for the render thread to either succeed or fail in init,
            // so callers see the failure on Configure rather than later.
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
            // Manual mode: caller IS the render thread. Init synchronously
            // here so any failure throws on Configure as in auto mode.
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
            throw new InvalidOperationException("SDL3VideoOutput.Submit called before Configure");
        }
        if (frame.Format.Width != _format.Width || frame.Format.Height != _format.Height
            || frame.Format.PixelFormat != _format.PixelFormat)
        {
            frame.Dispose();
            throw new ArgumentException(
                $"frame format {frame.Format} does not match output format {_format}", nameof(frame));
        }

        var prev = Interlocked.Exchange(ref _pendingFrame, frame);
        if (prev is not null)
        {
            prev.Dispose();
            Interlocked.Increment(ref _droppedNew);
        }
        if (_ownsThread) _wakeup.Set();
        // Manual mode: no signal — the host pumps when ready.
    }

    /// <summary>
    /// Manual-mode driver: drain SDL window events and present any pending
    /// frame. Must be called from the same thread that called
    /// <see cref="Configure"/>. No-op (and throws) in auto-thread mode.
    /// </summary>
    public void Pump()
    {
        if (_ownsThread)
            throw new InvalidOperationException("SDL3VideoOutput.Pump called on an auto-thread output — use ownsThread:false");
        if (_disposed) return;
        if (!_configured) return;

        DrainEvents();

        var frame = Interlocked.Exchange(ref _pendingFrame, null);
        if (frame is null) return;

        try { PresentFrame(frame); }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "SDL3VideoOutput.Pump PresentFrame");
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
            _wakeup.Set(); // poke the render thread out of WaitOne
            CooperativePlaybackJoin.JoinThread(_renderThread, TimeSpan.FromSeconds(2));
            _cts?.Dispose();
        }
        else
        {
            // Manual mode: the host is the render thread. Tear down here
            // — caller must invoke Dispose on the same thread that called
            // Configure (SDL's single-thread requirement).
            try { TeardownGraphics(); }
            finally { SDL3Runtime.Release(); }
        }

        // Anything still in the slot at teardown is the caller's loss.
        var leftover = Interlocked.Exchange(ref _pendingFrame, null);
        leftover?.Dispose();

        _wakeup.Dispose();
        _ready.Dispose();
    }

    // ----- render thread (auto mode) --------------------------------------

    private nint _window;
    private nint _renderer;
    private nint _texture;

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
                    // Pump events on every wakeup (and at least every 50 ms)
                    // so the OS keeps the window responsive even when the
                    // producer goes idle.
                    var idx = WaitHandle.WaitAny(handles, 50);
                    if (idx == 1) break;
                    DrainEvents();

                    var frame = Interlocked.Exchange(ref _pendingFrame, null);
                    if (frame is null) continue;

                    try { PresentFrame(frame); }
                    catch (Exception ex)
                    {
                        MediaDiagnostics.LogError(ex, "SDL3VideoOutput.RenderLoop PresentFrame");
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
        if (!SDL.CreateWindowAndRenderer(_title, _initialWindowWidth, _initialWindowHeight,
                SDL.WindowFlags.Resizable, out _window, out _renderer))
            throw new InvalidOperationException($"SDL_CreateWindowAndRenderer failed: {SDL.GetError()}");

        if (!SDL.ShowWindow(_window))
            MediaDiagnostics.LogWarning("SDL3VideoOutput: SDL_ShowWindow failed: {0}", SDL.GetError());
        if (!SDL.RaiseWindow(_window))
            MediaDiagnostics.LogWarning("SDL3VideoOutput: SDL_RaiseWindow failed: {0}", SDL.GetError());

        SDL.SetRenderVSync(_renderer, _vsync ? 1 : 0);

        var sdlFormat = ToSdlPixelFormat(_format.PixelFormat);
        _texture = SDL.CreateTexture(_renderer, sdlFormat, SDL.TextureAccess.Streaming, _format.Width, _format.Height);
        if (_texture == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateTexture failed: {SDL.GetError()}");
    }

    private void DrainEvents()
    {
        // Pump first so the queue has fresh events; then poll-and-dispatch
        // until empty. PollEvent removes events from the queue.
        SDL.PumpEvents();
        while (SDL.PollEvent(out var ev))
        {
            // Filter to events that target our window — multi-window apps
            // shouldn't get cross-fired because of us.
            switch ((SDL.EventType)ev.Type)
            {
                case SDL.EventType.WindowCloseRequested:
                    if (ev.Window.WindowID == GetWindowId())
                        SafeRaise(CloseRequested);
                    break;
                case SDL.EventType.WindowResized:
                case SDL.EventType.WindowPixelSizeChanged:
                    if (ev.Window.WindowID == GetWindowId())
                        SafeRaiseResized(ev.Window.Data1, ev.Window.Data2);
                    break;
                case SDL.EventType.Quit:
                    // App-level quit (e.g. last window closed) — surface as
                    // close request so the host can react uniformly.
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
            MediaDiagnostics.LogError(ex, "SDL3VideoOutput event subscriber");
        }
    }

    private void SafeRaiseResized(int width, int height)
    {
        var handler = Resized;
        if (handler is null) return;
        try { handler.Invoke(this, (width, height)); }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "SDL3VideoOutput Resized subscriber");
        }
    }

    private void TeardownGraphics()
    {
        if (_texture != nint.Zero)  { SDL.DestroyTexture(_texture);  _texture = nint.Zero; }
        if (_renderer != nint.Zero) { SDL.DestroyRenderer(_renderer); _renderer = nint.Zero; }
        if (_window != nint.Zero)   { SDL.DestroyWindow(_window);     _window = nint.Zero; }
    }

    private void PresentFrame(VideoFrame frame)
    {
        switch (_format.PixelFormat)
        {
            case PixelFormat.Bgra32:
            case PixelFormat.Uyvy:
            case PixelFormat.Yuyv:
                UploadPacked(frame);
                break;
            case PixelFormat.I420:
                UploadYuv(frame);
                break;
            case PixelFormat.Nv12:
                UploadNv(frame);
                break;
            default:
                throw new NotSupportedException($"PresentFrame: {_format.PixelFormat}");
        }

        SDL.RenderClear(_renderer);
        SDL.RenderTexture(_renderer, _texture, nint.Zero, nint.Zero);
        SDL.RenderPresent(_renderer); // VSYNC blocks here when enabled
        Interlocked.Increment(ref _displayed);
    }

    private void UploadPacked(VideoFrame frame)
    {
        using var pin = frame.Planes[0].Pin();
        SDL.UpdateTexture(_texture, nint.Zero, (nint)pin.Pointer, frame.Strides[0]);
    }

    private void UploadYuv(VideoFrame frame)
    {
        using var pinY = frame.Planes[0].Pin();
        using var pinU = frame.Planes[1].Pin();
        using var pinV = frame.Planes[2].Pin();
        SDL.UpdateYUVTexture(_texture, nint.Zero,
            (nint)pinY.Pointer, frame.Strides[0],
            (nint)pinU.Pointer, frame.Strides[1],
            (nint)pinV.Pointer, frame.Strides[2]);
    }

    private void UploadNv(VideoFrame frame)
    {
        using var pinY  = frame.Planes[0].Pin();
        using var pinUV = frame.Planes[1].Pin();
        SDL.UpdateNVTexture(_texture, nint.Zero,
            (nint)pinY.Pointer, frame.Strides[0],
            (nint)pinUV.Pointer, frame.Strides[1]);
    }

    private static SDL.PixelFormat ToSdlPixelFormat(CoreVideo.PixelFormat fmt) => fmt switch
    {
        // Memory layout B,G,R,A → SDL's native-endian-aware ARGB8888
        // (which on little-endian targets places A in the high byte).
        CoreVideo.PixelFormat.Bgra32 => SDL.PixelFormat.ARGB8888,
        CoreVideo.PixelFormat.I420   => SDL.PixelFormat.IYUV,
        CoreVideo.PixelFormat.Nv12   => SDL.PixelFormat.NV12,
        CoreVideo.PixelFormat.Uyvy   => SDL.PixelFormat.UYVY,
        CoreVideo.PixelFormat.Yuyv   => SDL.PixelFormat.YUY2,
        _ => throw new NotSupportedException($"ToSdlPixelFormat: {fmt}"),
    };
}
