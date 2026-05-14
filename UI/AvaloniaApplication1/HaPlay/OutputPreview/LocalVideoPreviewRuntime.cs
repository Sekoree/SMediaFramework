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
    Task StartAsync(CancellationToken cancellationToken = default);

    void SetFullscreen(bool fullscreen);
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

internal sealed class SdlLocalVideoPreviewRuntime : ILocalVideoPreviewRuntime
{
    private readonly LocalVideoOutputDefinition _definition;
    private readonly OutputLineViewModel _line;
    private readonly OutputManagementViewModel _owner;
    private SDL3GLVideoSink? _sink;
    private int _closeHandlerPosted;

    public SdlLocalVideoPreviewRuntime(
        LocalVideoOutputDefinition definition,
        OutputLineViewModel line,
        OutputManagementViewModel owner)
    {
        _definition = definition;
        _line = line;
        _owner = owner;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (iw, ih) = InitialWindowPixelSize(_definition);
            var sink = new SDL3GLVideoSink(_definition.DisplayName, iw, ih);
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

    public void Dispose()
    {
        Interlocked.Exchange(ref _sink, null)?.Dispose();
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

    private static (int Width, int Height) InitialWindowPixelSize(LocalVideoOutputDefinition d)
    {
        if (d.SurfaceMode == VideoSurfaceMode.Windowed && d.WindowWidth is { } ww && d.WindowHeight is { } wh)
            return (ww, wh);
        return (1280, 720);
    }
}

internal sealed class AvaloniaLocalVideoPreviewRuntime : ILocalVideoPreviewRuntime
{
    private readonly LocalVideoOutputDefinition _definition;
    private readonly OutputLineViewModel _line;
    private readonly OutputManagementViewModel _owner;
    private readonly Window? _screenReference;
    private LocalVideoPreviewWindow? _window;
    private int _ended;

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

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var win = new LocalVideoPreviewWindow { Title = _definition.DisplayName };
            var (w, h) = InitialWindowPixelSize(_definition);
            win.Width = w;
            win.Height = h;
            ApplyWindowPlacement(win, _definition, null);
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
            ApplyWindowPlacement(_window, _definition, fullscreen);
        }, DispatcherPriority.Normal);

    public void Dispose()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _window?.Close();
        }, DispatcherPriority.Normal);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
            return;
        _window = null;
        _owner.NotifyLocalPreviewEnded(_line);
    }

    private void ApplyWindowPlacement(Window win, LocalVideoOutputDefinition d, bool? fullscreenOverride)
    {
        var fullscreen = fullscreenOverride ?? (d.SurfaceMode == VideoSurfaceMode.FullScreen);
        var screens = (_screenReference ?? win).Screens?.All;
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

    private static (int Width, int Height) InitialWindowPixelSize(LocalVideoOutputDefinition d)
    {
        if (d.SurfaceMode == VideoSurfaceMode.Windowed && d.WindowWidth is { } ww && d.WindowHeight is { } wh)
            return (ww, wh);
        return (1280, 720);
    }
}
