// Phase 5 Avalonia present-path runtime smoke: host the real VideoOpenGlControl in an Avalonia window, play a
// video into it through MediaPlayer, and confirm it presents on screen — exercising the shared YuvVideoRenderer
// and (with HW decode) the dma-buf EGL zero-copy import, the same path SDL3 uses. Closes the runtime-verification
// gap the checklist flagged ("Present.Avalonia … runtime needs a display"). Needs a GL-capable display + libav.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using S.Media.Core.Registry;
using S.Media.Decode.FFmpeg;
using S.Media.Players;
using S.Media.Present.Avalonia;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: AvaloniaVideoSmoke <video-file-or-uri> [seconds=5]");
    return 2;
}

Smoke.Uri = args[0];
Smoke.Seconds = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 5.0;

try
{
    // Empty args to the lifetime — our media path lives in Smoke.Uri, not Avalonia's CLI parsing. Platform-detect
    // picks GLX here; the control's dma-buf zero-copy import engages only on an EGL context (the shared
    // YuvDmabufEglInterop, verified on the SDL3 EGL path). Forcing X11 EGL segfaults on this radeonsi/Mesa setup —
    // the same EGL/dma-buf limitation noted for the compositor export — so we verify presentation on GLX here.
    AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime([]);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: Avalonia host crashed: {ex}");
    return 1;
}

Console.WriteLine(Smoke.Report);
return Smoke.Result;

internal static class Smoke
{
    public static string Uri = "";
    public static double Seconds = 5;
    public static int Result = 1;
    public static string Report = "AvaloniaVideoSmoke produced no result (the window never reported back).";
}

internal sealed class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class MainWindow : Window
{
    private readonly VideoOpenGlControl _control = new();
    private MediaPlayer? _player;
    private bool _softwareFallback;

    public MainWindow()
    {
        Title = "AvaloniaVideoSmoke";
        Width = 1280;
        Height = 720;
        Content = _control;
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        var registry = MediaRegistry.Build(b => b.Use(new FFmpegModule()));
        try
        {
            // Prefer hardware decode + dma-buf retention so the control's zero-copy import path is exercised;
            // fall back to software (CPU upload) if HW negotiation isn't available, so the present path is still
            // verified. Video-only (no audio router) keeps the smoke focused on presentation.
            try
            {
                _player = MediaPlayer.Open(registry, Smoke.Uri,
                    new MediaPlayerOpenOptions { TryHardwareAcceleration = true, RetainDmabufForGl = true, IncludeAudioRouter = false });
            }
            catch
            {
                _softwareFallback = true;
                _player = MediaPlayer.Open(registry, Smoke.Uri,
                    new MediaPlayerOpenOptions { TryHardwareAcceleration = false, IncludeAudioRouter = false });
            }

            _player.AttachVideoOutput(_control, "ava");
            _player.Play();
        }
        catch (Exception ex)
        {
            Smoke.Report = $"FAIL: open/play threw: {ex}";
            Smoke.Result = 1;
            Close();
            return;
        }

        DispatcherTimer.RunOnce(Finish, TimeSpan.FromSeconds(Smoke.Seconds));
    }

    private void Finish()
    {
        var rendered = _control.RenderedFrameCount;
        var hw = _control.HardwareFrameCount;
        var dmabuf = _control.DmabufImportAvailable;
        var fault = _player?.Video.Fault;
        var fmt = _player?.Video.Format;

        if (fault is not null)
        {
            Smoke.Report = $"FAIL: player faulted: {fault.Message}";
            Smoke.Result = 1;
        }
        else if (rendered <= 0)
        {
            Smoke.Report = "FAIL: VideoOpenGlControl rendered 0 frames (no on-screen presentation).";
            Smoke.Result = 1;
        }
        else
        {
            var zeroCopy = dmabuf && hw > 0;
            Smoke.Report =
                $"AvaloniaVideoSmoke OK — VideoOpenGlControl presented {rendered} frames " +
                $"(src {fmt?.Width}x{fmt?.Height} {fmt?.PixelFormat}, decode={(_softwareFallback ? "software" : "hardware")}). " +
                $"hardware-backed frames={hw}, dma-buf import available={dmabuf} → " +
                $"zero-copy path {(zeroCopy ? "EXERCISED (dma-buf frames imported)" : "not exercised (CPU upload — SW frames or no EGL dma-buf)")}.";
            Smoke.Result = 0;
        }

        _player?.Dispose();
        Close();
    }
}
