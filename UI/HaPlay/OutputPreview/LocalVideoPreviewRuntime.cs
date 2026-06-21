using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using HaPlay.Playback;
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
    /// playback sessions don't need to re-acquire (the underlying output reference stays valid — only its
    /// window framing changes), but Phase B may use this hook to refresh UI bindings.
    /// </summary>
    event EventHandler? Reconfigured;

    Task StartAsync(CancellationToken cancellationToken = default);

    void SetFullscreen(bool fullscreen);

    /// <summary>
    /// Hands the underlying <see cref="IVideoOutput"/> to a playback session so it can route decoded frames
    /// to the existing window (no new window). The session will <see cref="IVideoOutput.Configure"/> the output
    /// for the media's negotiated format. Returns <c>null</c> when the preview isn't ready or another
    /// acquirer holds it. Pair every successful acquire with <see cref="ReleaseFromPlayback"/>.
    /// </summary>
    IVideoOutput? AcquireForPlayback();

    /// <summary>
    /// Returns the output to "idle preview" mode after a playback session ends — reconfigures it to a
    /// small black frame so the window keeps showing something even with no media loaded.
    /// </summary>
    void ReleaseFromPlayback();

    /// <summary>
    /// Optional preview-window size override while a hold image is engaged.
    /// Current policy keeps windowed preview dimensions stable, so runtimes may ignore this call.
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

    /// <summary>Idle frame for a local output: the configured background image (letterboxed into
    /// <paramref name="format"/>) when one is set and loadable, otherwise opaque black.</summary>
    public static VideoFrame CreateIdleFrame(VideoFormat format, string? backgroundImagePath)
    {
        if (!string.IsNullOrWhiteSpace(backgroundImagePath) && File.Exists(backgroundImagePath))
        {
            var frame = FallbackImageLoader.TryBuildHoldCpuFrame(format, backgroundImagePath);
            if (frame is not null)
                return frame;
        }

        return CreateBlackBgra(format);
    }
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
    private readonly Window? _screenReference;
    private SDL3GLVideoOutput? _sink;
    private int _closeHandlerPosted;
    private int _playbackHolders;

    public SdlLocalVideoPreviewRuntime(
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
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (iw, ih) = LocalVideoWindowPlacement.InitialWindowPixelSize(_definition);
            var output = new SDL3GLVideoOutput(_definition.DisplayName, iw, ih)
            {
                // Local previews should preserve the source's aspect ratio (letterbox / pillarbox)
                // instead of stretching to fill the window.
                ViewportFit = S.Media.OpenGL.VideoViewportFit.Contain,
            };
            output.CloseRequested += OnSdlCloseRequested;
            output.Resized += OnSdlResized;
            var format = PreviewVideoFrames.PreviewFormat(iw, ih);
            output.Configure(format);
            ApplySdlWindowPlacement(output, _definition, _definition.SurfaceMode == VideoSurfaceMode.FullScreen,
                _definition.WindowWidth, _definition.WindowHeight);
            output.Submit(PreviewVideoFrames.CreateIdleFrame(format, _definition.BackgroundImagePath));
            Interlocked.Exchange(ref _sink, output);
        }, cancellationToken).ConfigureAwait(false);
    }

    public void SetFullscreen(bool fullscreen)
    {
        if (_sink is { } sink)
            ApplySdlWindowPlacement(
                sink,
                _definition,
                fullscreen,
                fullscreen ? null : _definition.WindowWidth ?? 1280,
                fullscreen ? null : _definition.WindowHeight ?? 720);
    }

    public void ApplyHoldImageWindowSize(int? width, int? height)
    {
        // Keep windowed local outputs stable while source/hold image sizes change.
        _ = width;
        _ = height;
    }

    public void Dispose()
    {
        var sink = Interlocked.Exchange(ref _sink, null);
        if (sink is not null)
        {
            sink.CloseRequested -= OnSdlCloseRequested;
            sink.Resized -= OnSdlResized;
            sink.Dispose();
        }
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
            var output = _sink;
            if (output is null)
                return;
            ApplySdlWindowPlacement(
                output,
                newDefinition,
                newDefinition.SurfaceMode == VideoSurfaceMode.FullScreen,
                newDefinition.WindowWidth,
                newDefinition.WindowHeight);
            // Reflect a background-image change immediately while idle; during playback the session owns
            // the frame stream, so leave it alone.
            if (Volatile.Read(ref _playbackHolders) == 0)
            {
                var (iw, ih) = LocalVideoWindowPlacement.InitialWindowPixelSize(newDefinition);
                var fmt = PreviewVideoFrames.PreviewFormat(iw, ih);
                output.Configure(fmt);
                output.Submit(PreviewVideoFrames.CreateIdleFrame(fmt, newDefinition.BackgroundImagePath));
            }
            Reconfigured?.Invoke(this, EventArgs.Empty);
        }, cancellationToken);
    }

    private void ApplySdlWindowPlacement(
        SDL3GLVideoOutput output,
        LocalVideoOutputDefinition definition,
        bool fullscreen,
        int? windowWidth,
        int? windowHeight)
    {
        var bounds = ResolveScreenWorkingArea(definition.ScreenIndex);
        if (bounds is { } b)
        {
            output.ApplyWindowPlacementToBounds(b.X, b.Y, b.Width, b.Height, fullscreen, windowWidth, windowHeight);
            return;
        }

        output.ApplyWindowPlacement(definition.ScreenIndex, fullscreen, windowWidth, windowHeight);
    }

    private PixelRect? ResolveScreenWorkingArea(int screenIndex)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return ResolveScreenWorkingAreaCore(screenIndex);

        return Dispatcher.UIThread.InvokeAsync(() => ResolveScreenWorkingAreaCore(screenIndex))
            .GetAwaiter()
            .GetResult();
    }

    private PixelRect? ResolveScreenWorkingAreaCore(int screenIndex)
    {
        var screens = _screenReference?.Screens?.All;
        if (screens is null || screens.Count == 0)
            return null;

        var idx = Math.Clamp(screenIndex, 0, screens.Count - 1);
        return screens[idx].WorkingArea;
    }

    public IVideoOutput? AcquireForPlayback()
    {
        var output = _sink;
        if (output is null)
            return null;
        if (Interlocked.CompareExchange(ref _playbackHolders, 1, 0) != 0)
            return null;
        return output;
    }

    public void ReleaseFromPlayback()
    {
        // Reset to the idle preview frame so the window keeps showing something between sessions —
        // the SDL3GLVideoOutput retains its existing window/GL context across the reconfigure.
        var output = _sink;
        if (output is not null)
        {
            try
            {
                var (iw, ih) = LocalVideoWindowPlacement.InitialWindowPixelSize(_definition);
                var fmt = PreviewVideoFrames.PreviewFormat(iw, ih);
                output.Configure(fmt);
                output.Submit(PreviewVideoFrames.CreateIdleFrame(fmt, _definition.BackgroundImagePath));
            }
            catch
            {
                /* best effort — the output may have been closed by the user */
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
                var sink = Interlocked.Exchange(ref _sink, null);
                if (sink is not null)
                {
                    sink.CloseRequested -= OnSdlCloseRequested;
                    sink.Resized -= OnSdlResized;
                    sink.Dispose();
                }
            }
            finally
            {
                // Reached only via SDL's WindowCloseRequested/Quit — our own Dispose() unsubscribes this
                // handler before tearing the sink down, so this is always an operator-initiated close.
                Dispatcher.UIThread.Post(() => _owner.NotifyLocalPreviewEnded(_line, userInitiated: true));
            }
        });
    }

    private void OnSdlResized(object? sender, (int Width, int Height) size)
    {
        if (!TryRecordWindowedResize(size.Width, size.Height))
            return;
        Dispatcher.UIThread.Post(() => _owner.NotifyLocalPreviewResized(_line, size.Width, size.Height),
            DispatcherPriority.Background);
    }

    private bool TryRecordWindowedResize(int width, int height)
    {
        if (_definition.SurfaceMode != VideoSurfaceMode.Windowed || width < 320 || height < 240)
            return false;
        if (_definition.WindowWidth == width && _definition.WindowHeight == height)
            return false;

        _definition = _definition with { WindowWidth = width, WindowHeight = height };
        return true;
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
    private int _disposing;
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
            var win = new LocalVideoPreviewWindow
            {
                Title = _definition.DisplayName,
                Topmost = _definition.AlwaysOnTop,
            };
            var (w, h) = LocalVideoWindowPlacement.InitialWindowPixelSize(_definition);
            win.Width = w;
            win.Height = h;
            // Preserve aspect ratio for the embedded Avalonia GL control too.
            win.Video.ViewportFit = S.Media.OpenGL.VideoViewportFit.Contain;
            LocalVideoWindowPlacement.Apply(win, _definition, _screenReference, null);
            var format = PreviewVideoFrames.PreviewFormat(w, h);
            win.Video.Configure(format);
            win.Video.Submit(PreviewVideoFrames.CreateIdleFrame(format, _definition.BackgroundImagePath));
            win.Closed += OnWindowClosed;
            win.SizeChanged += OnWindowSizeChanged;
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
            // Keep windowed local outputs stable while source/hold image sizes change.
            _ = width;
            _ = height;
        }, DispatcherPriority.Normal);

    public void Dispose()
    {
        // Mark this as a programmatic teardown so the resulting Closed event isn't mistaken for the
        // operator closing the window (which would remove the whole output line from the I/O page).
        Interlocked.Exchange(ref _disposing, 1);
        Dispatcher.UIThread.Post(() =>
        {
            if (_window is not null)
                _window.SizeChanged -= OnWindowSizeChanged;
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
            _window.Topmost = newDefinition.AlwaysOnTop;
            LocalVideoWindowPlacement.Apply(_window, newDefinition, _screenReference, null);
            // Reflect a background-image change immediately while idle; during playback the session owns
            // the frame stream, so leave it alone.
            if (Volatile.Read(ref _playbackHolders) == 0)
            {
                var (w, h) = LocalVideoWindowPlacement.InitialWindowPixelSize(newDefinition);
                var fmt = PreviewVideoFrames.PreviewFormat(w, h);
                _window.Video.Configure(fmt);
                _window.Video.Submit(PreviewVideoFrames.CreateIdleFrame(fmt, newDefinition.BackgroundImagePath));
            }
        }, DispatcherPriority.Normal);

        Reconfigured?.Invoke(this, EventArgs.Empty);
    }

    public IVideoOutput? AcquireForPlayback()
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
                    win.Video.Submit(PreviewVideoFrames.CreateIdleFrame(fmt, _definition.BackgroundImagePath));
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
        if (sender is LocalVideoPreviewWindow win)
            win.SizeChanged -= OnWindowSizeChanged;
        _window = null;
        // _disposing is set only by our own Dispose(); a window the operator closes leaves it clear.
        var userInitiated = Volatile.Read(ref _disposing) == 0;
        _owner.NotifyLocalPreviewEnded(_line, userInitiated);
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var width = (int)Math.Round(e.NewSize.Width);
        var height = (int)Math.Round(e.NewSize.Height);
        if (!TryRecordWindowedResize(width, height))
            return;
        _owner.NotifyLocalPreviewResized(_line, width, height);
    }

    private bool TryRecordWindowedResize(int width, int height)
    {
        if (_definition.SurfaceMode != VideoSurfaceMode.Windowed || width < 320 || height < 240)
            return false;
        if (_definition.WindowWidth == width && _definition.WindowHeight == height)
            return false;

        _definition = _definition with { WindowWidth = width, WindowHeight = height };
        return true;
    }
}
