using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using HaPlay.Models;
using HaPlay.ViewModels;
using HaPlay.Views;
using S.Media.Core.Video;
using S.Media.SDL3;

namespace HaPlay.OutputPreview;

internal interface ILocalVideoPreviewRuntime : IDisposable
{
    /// <summary>Current definition. May be swapped by <see cref="ReconfigureAsync"/>.</summary>
    LocalVideoOutputDefinition Definition { get; }

    /// <summary>
    /// Raised after <see cref="ReconfigureAsync"/> applies new placement / sizing to the window. Active
    /// playback sessions don't need to re-acquire (the underlying sink reference stays valid — only its
    /// window framing changes), but Phase B may use this hook to refresh UI bindings.
    /// </summary>
    event EventHandler? Reconfigured;

    Task StartAsync(CancellationToken cancellationToken = default);

    void SetFullscreen(bool fullscreen);

    /// <summary>
    /// Hands the underlying <see cref="IVideoSink"/> to a playback session so it can route decoded frames
    /// to the existing window (no new window). The session will <see cref="IVideoSink.Configure"/> the sink
    /// for the media's negotiated format. Returns <c>null</c> when the preview isn't ready or another
    /// acquirer holds it. Pair every successful acquire with <see cref="ReleaseFromPlayback"/>.
    /// </summary>
    IVideoSink? AcquireForPlayback();

    /// <summary>
    /// Returns the sink to "idle preview" mode after a playback session ends — reconfigures it to a
    /// small black frame so the window keeps showing something even with no media loaded.
    /// </summary>
    void ReleaseFromPlayback();

    /// <summary>
    /// Phase 3 — resize the preview window to the supplied pixel dimensions while a hold image is
    /// engaged so the output matches the image's native size. <c>null</c> reverts to the user's
    /// previously chosen size.
    /// </summary>
    void ApplyHoldImageWindowSize(int? width, int? height);

    /// <summary>
    /// Phase A (§9.6) — applies a new <see cref="LocalVideoOutputDefinition"/> in place. Window size,
    /// screen index, and surface mode are honoured live. <see cref="LocalVideoOutputDefinition.Engine"/>
    /// must not change (engine switches go through Remove + Add at the management layer).
    /// </summary>
    Task ReconfigureAsync(LocalVideoOutputDefinition newDefinition, CancellationToken cancellationToken = default);
}

internal static class PreviewVideoFrames
{
    public static VideoFrame CreateBlackBgra(VideoFormat format, TimeSpan presentationTime = default)
    {
        var stride = format.Width * 4;
        var bytes = new byte[stride * format.Height];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = 0;
            bytes[i + 1] = 0;
            bytes[i + 2] = 0;
            bytes[i + 3] = 255;
        }

        return new VideoFrame(presentationTime, format, bytes, stride);
    }

    public static VideoFormat PreviewFormat(int width, int height) =>
        new(width, height, PixelFormat.Bgra32, new Rational(60, 1));
}

internal static class LocalVideoWindowPlacement
{
    public static (int Width, int Height) InitialWindowPixelSize(LocalVideoOutputDefinition d)
    {
        if (d.SurfaceMode == VideoSurfaceMode.Windowed && d.WindowWidth is { } ww && d.WindowHeight is { } wh)
            return (ww, wh);
        return (1280, 720);
    }

    public static void Apply(Window win, LocalVideoOutputDefinition d, Window? screenReference, bool? fullscreenOverride)
    {
        var fullscreen = fullscreenOverride ?? (d.SurfaceMode == VideoSurfaceMode.FullScreen);
        var screens = (screenReference ?? win).Screens?.All;
        if (screens is null || screens.Count == 0)
            return;

        var idx = Math.Clamp(d.ScreenIndex, 0, screens.Count - 1);
        var scr = screens[idx];
        var b = scr.WorkingArea;

        if (fullscreen)
        {
            win.WindowState = WindowState.Normal;
            win.Position = new PixelPoint(b.X, b.Y);
            win.WindowState = WindowState.FullScreen;
        }
        else
        {
            win.WindowState = WindowState.Normal;
            var ww = d.WindowWidth ?? (int)Math.Clamp(win.Width, 320, 4096);
            var wh = d.WindowHeight ?? (int)Math.Clamp(win.Height, 240, 4096);
            win.Width = ww;
            win.Height = wh;
            var x = b.X + Math.Max(0, (b.Width - ww) / 2);
            var y = b.Y + Math.Max(0, (b.Height - wh) / 2);
            win.Position = new PixelPoint(x, y);
        }
    }
}

internal sealed class SdlLocalVideoPreviewRuntime : ILocalVideoPreviewRuntime
{
    private LocalVideoOutputDefinition _definition;
    private readonly OutputLineViewModel _line;
    private readonly OutputManagementViewModel _owner;
    private SDL3GLVideoSink? _sink;
    private int _closeHandlerPosted;
    private int _playbackHolders;

    public SdlLocalVideoPreviewRuntime(
        LocalVideoOutputDefinition definition,
        OutputLineViewModel line,
        OutputManagementViewModel owner)
    {
        _definition = definition;
        _line = line;
        _owner = owner;
    }

    public LocalVideoOutputDefinition Definition => _definition;

    public event EventHandler? Reconfigured;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (iw, ih) = LocalVideoWindowPlacement.InitialWindowPixelSize(_definition);
            var sink = new SDL3GLVideoSink(_definition.DisplayName, iw, ih)
            {
                // Local previews should preserve the source's aspect ratio (letterbox / pillarbox)
                // instead of stretching to fill the window.
                ViewportFit = S.Media.OpenGL.VideoViewportFit.Contain,
            };
            sink.CloseRequested += OnSdlCloseRequested;
            var format = PreviewVideoFrames.PreviewFormat(iw, ih);
            sink.Configure(format);
            sink.ApplyWindowPlacement(
                _definition.ScreenIndex,
                _definition.SurfaceMode == VideoSurfaceMode.FullScreen,
                _definition.WindowWidth,
                _definition.WindowHeight);
            sink.Submit(PreviewVideoFrames.CreateBlackBgra(format));
            Interlocked.Exchange(ref _sink, sink);
        }, cancellationToken).ConfigureAwait(false);
    }

    public void SetFullscreen(bool fullscreen)
    {
        _sink?.ApplyWindowPlacement(
            _definition.ScreenIndex,
            fullscreen,
            fullscreen ? null : _definition.WindowWidth ?? 1280,
            fullscreen ? null : _definition.WindowHeight ?? 720);
    }

    public void ApplyHoldImageWindowSize(int? width, int? height)
    {
        var sink = _sink;
        if (sink is null) return;
        if (_definition.SurfaceMode == VideoSurfaceMode.FullScreen)
            return; // honour fullscreen — image is letterboxed via ViewportFit
        var w = width ?? _definition.WindowWidth ?? 1280;
        var h = height ?? _definition.WindowHeight ?? 720;
        sink.ApplyWindowPlacement(_definition.ScreenIndex, false, w, h);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _sink, null)?.Dispose();
    }

    public Task ReconfigureAsync(LocalVideoOutputDefinition newDefinition, CancellationToken cancellationToken = default)
    {
        if (newDefinition.Id != _definition.Id)
            throw new ArgumentException(
                $"ReconfigureAsync requires the same line Id ({_definition.Id}); got {newDefinition.Id}.",
                nameof(newDefinition));
        if (newDefinition.Engine != _definition.Engine)
            throw new ArgumentException(
                "Cannot switch VideoOutputEngine in-place — remove and re-add the output.",
                nameof(newDefinition));

        _definition = newDefinition;

        // Window placement runs on whichever thread SDL prefers — Task.Run avoids blocking the caller
        // (typically the UI thread invoking Apply Edit from a dialog).
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sink = _sink;
            if (sink is null)
                return;
            sink.ApplyWindowPlacement(
                newDefinition.ScreenIndex,
                newDefinition.SurfaceMode == VideoSurfaceMode.FullScreen,
                newDefinition.WindowWidth,
                newDefinition.WindowHeight);
            Reconfigured?.Invoke(this, EventArgs.Empty);
        }, cancellationToken);
    }

    public IVideoSink? AcquireForPlayback()
    {
        var sink = _sink;
        if (sink is null)
            return null;
        if (Interlocked.CompareExchange(ref _playbackHolders, 1, 0) != 0)
            return null;
        return sink;
    }

    public void ReleaseFromPlayback()
    {
        // Reset to the idle preview frame so the window keeps showing something between sessions —
        // the SDL3GLVideoSink retains its existing window/GL context across the reconfigure.
        var sink = _sink;
        if (sink is not null)
        {
            try
            {
                var (iw, ih) = LocalVideoWindowPlacement.InitialWindowPixelSize(_definition);
                var fmt = PreviewVideoFrames.PreviewFormat(iw, ih);
                sink.Configure(fmt);
                sink.Submit(PreviewVideoFrames.CreateBlackBgra(fmt));
            }
            catch
            {
                /* best effort — the sink may have been closed by the user */
            }
        }

        Interlocked.Exchange(ref _playbackHolders, 0);
    }

    private void OnSdlCloseRequested(object? sender, EventArgs e)
    {
        if (Interlocked.CompareExchange(ref _closeHandlerPosted, 1, 0) != 0)
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Interlocked.Exchange(ref _sink, null)?.Dispose();
            }
            finally
            {
                Dispatcher.UIThread.Post(() => _owner.NotifyLocalPreviewEnded(_line));
            }
        });
    }

}

internal sealed class AvaloniaLocalVideoPreviewRuntime : ILocalVideoPreviewRuntime
{
    private LocalVideoOutputDefinition _definition;
    private readonly OutputLineViewModel _line;
    private readonly OutputManagementViewModel _owner;
    private readonly Window? _screenReference;
    private LocalVideoPreviewWindow? _window;
    private int _ended;
    private int _playbackHolders;

    public AvaloniaLocalVideoPreviewRuntime(
        LocalVideoOutputDefinition definition,
        OutputLineViewModel line,
        OutputManagementViewModel owner,
        Window? screenReference)
    {
        _definition = definition;
        _line = line;
        _owner = owner;
        _screenReference = screenReference;
    }

    public LocalVideoOutputDefinition Definition => _definition;

    public event EventHandler? Reconfigured;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var win = new LocalVideoPreviewWindow { Title = _definition.DisplayName };
            var (w, h) = LocalVideoWindowPlacement.InitialWindowPixelSize(_definition);
            win.Width = w;
            win.Height = h;
            // Preserve aspect ratio for the embedded Avalonia GL control too.
            win.Video.ViewportFit = S.Media.OpenGL.VideoViewportFit.Contain;
            LocalVideoWindowPlacement.Apply(win, _definition, _screenReference, null);
            var format = PreviewVideoFrames.PreviewFormat(w, h);
            win.Video.Configure(format);
            win.Video.Submit(PreviewVideoFrames.CreateBlackBgra(format));
            win.Closed += OnWindowClosed;
            _window = win;
            win.Show();
        }, DispatcherPriority.Normal);
    }

    public void SetFullscreen(bool fullscreen) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_window is null)
                return;
            LocalVideoWindowPlacement.Apply(_window, _definition, _screenReference, fullscreen);
        }, DispatcherPriority.Normal);

    public void ApplyHoldImageWindowSize(int? width, int? height) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_window is null) return;
            if (_definition.SurfaceMode == VideoSurfaceMode.FullScreen)
                return;
            var w = width ?? _definition.WindowWidth ?? 1280;
            var h = height ?? _definition.WindowHeight ?? 720;
            _window.Width = w;
            _window.Height = h;
        }, DispatcherPriority.Normal);

    public void Dispose()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _window?.Close();
        }, DispatcherPriority.Normal);
    }

    public async Task ReconfigureAsync(LocalVideoOutputDefinition newDefinition, CancellationToken cancellationToken = default)
    {
        if (newDefinition.Id != _definition.Id)
            throw new ArgumentException(
                $"ReconfigureAsync requires the same line Id ({_definition.Id}); got {newDefinition.Id}.",
                nameof(newDefinition));
        if (newDefinition.Engine != _definition.Engine)
            throw new ArgumentException(
                "Cannot switch VideoOutputEngine in-place — remove and re-add the output.",
                nameof(newDefinition));

        _definition = newDefinition;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_window is null)
                return;
            LocalVideoWindowPlacement.Apply(_window, newDefinition, _screenReference, null);
        }, DispatcherPriority.Normal);

        Reconfigured?.Invoke(this, EventArgs.Empty);
    }

    public IVideoSink? AcquireForPlayback()
    {
        var win = _window;
        if (win is null)
            return null;
        if (Interlocked.CompareExchange(ref _playbackHolders, 1, 0) != 0)
            return null;
        return win.Video;
    }

    public void ReleaseFromPlayback()
    {
        var win = _window;
        if (win is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var (w, h) = LocalVideoWindowPlacement.InitialWindowPixelSize(_definition);
                    var fmt = PreviewVideoFrames.PreviewFormat(w, h);
                    win.Video.Configure(fmt);
                    win.Video.Submit(PreviewVideoFrames.CreateBlackBgra(fmt));
                }
                catch
                {
                    /* best effort — window may have been closed externally */
                }
            }, DispatcherPriority.Normal);
        }

        Interlocked.Exchange(ref _playbackHolders, 0);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
            return;
        _window = null;
        _owner.NotifyLocalPreviewEnded(_line);
    }
}
