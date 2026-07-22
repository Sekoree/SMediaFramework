using System.Diagnostics;
using ProjectMLib;
using S.Media.Audio.PortAudio;
using S.Media.Compositor;
using S.Media.Compositor.OpenGL;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.NDI;
using S.Media.Visualizer.ProjectM;
using SDL3;
using SilkGL = Silk.NET.OpenGL;

namespace S.Media.Tools.NDIVisualizer;

/// <summary>
/// The live path: PortAudio capture (opened at the device's max channel count) → pick the selected channels
/// and mix to stereo with gain → projectM (GL layer surface) → <see cref="GlVideoCompositor"/> CPU readback →
/// <see cref="NDIOutput"/> video. Runs a single-threaded, stopwatch-paced render loop that also drives the
/// live HUD and keyboard controls. Everything touching GL stays on this one thread.
/// </summary>
internal sealed class VizToNDIPipeline
{
    /// <summary>Resolved, index-level parameters for one run (produced by the wizard from a <see cref="VizConfig"/>).</summary>
    public sealed record Parameters(
        int DeviceIndex,
        string DeviceName,
        int MaxChannels,
        int[] SelectedChannels0, // 0-based indices into the interleaved capture frame
        int SampleRate,
        int Width,
        int Height,
        int Fps,
        string NDIName,
        string? PresetDirectory,
        double PresetDurationSeconds,
        bool Shuffle,
        double InitialGainDb);

    private readonly Parameters _p;
    private readonly Action<double>? _onSaveRequested;

    private double _gainDb;
    private double _meterDb = -100;
    private double _rmsDb = -100;
    private long _clipHoldUntilFrame = -1;

    public VizToNDIPipeline(Parameters p, Action<double>? onSaveRequested = null)
    {
        _p = p;
        _gainDb = p.InitialGainDb;
        _onSaveRequested = onSaveRequested;
    }

    public void Run(CancellationToken cancel)
    {
        if (!ProjectMRuntime.IsAvailable)
            throw new InvalidOperationException($"projectM is not available: {ProjectMRuntime.UnavailableReason}");

        // --- GL context (hidden SDL window; we render to the compositor's own FBO, so window size is moot). ---
        if (!SDL.Init(SDL.InitFlags.Video))
            throw new InvalidOperationException("SDL_Init(Video) failed: " + SDL.GetError());

        nint win = 0, glCtx = 0;
        IVideoCompositorLayerSurface? surface = null;
        ProjectMVisualSource? source = null;
        GlVideoCompositor? compositor = null;
        NDIOutput? ndiOut = null;
        IAudioSource? input = null;
        try
        {
            SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
            SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
            SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
            win = SDL.CreateWindow("ndi-visualizer", 64, 64, SDL.WindowFlags.OpenGL | SDL.WindowFlags.Hidden);
            if (win == 0)
                throw new InvalidOperationException("SDL_CreateWindow failed: " + SDL.GetError());
            glCtx = SDL.GLCreateContext(win);
            if (glCtx == 0)
                throw new InvalidOperationException("SDL_GL_CreateContext failed: " + SDL.GetError());
            SDL.GLMakeCurrent(win, glCtx);
            var gl = SilkGL.GL.GetApi(SDL.GLGetProcAddress);

            var frameRate = new Rational(_p.Fps, 1);
            var canvas = new VideoFormat(_p.Width, _p.Height, PixelFormat.Bgra32, frameRate);
            compositor = new GlVideoCompositor(gl, canvas);
            compositor.Configure(canvas);

            source = new ProjectMVisualSource(_p.Width, _p.Height, frameRate, new ProjectMOptions
            {
                PresetDirectory = _p.PresetDirectory,
                PresetDurationSeconds = _p.PresetDurationSeconds,
                Shuffle = _p.Shuffle,
                AudioSampleRate = _p.SampleRate,
                RenderWidth = _p.Width,
                RenderHeight = _p.Height,
                Fps = _p.Fps,
            });
            surface = source.CreateLayerSurface();
            var surfaceLayers = new[] { new CompositorSurfaceLayer(surface, LayerTransform2D.Identity, 1f) };
            var noFrameLayers = Array.Empty<CompositorLayer>();

            ndiOut = new NDIOutput(_p.NDIName, clockVideo: false);
            ndiOut.Video.Configure(canvas);

            // Open capture at the device's full channel count so any channel can be picked.
            var backend = new PortAudioBackend();
            input = backend.CreateInput(
                _p.DeviceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new AudioFormat(_p.SampleRate, _p.MaxChannels));

            PrintBanner();

            // --- Scratch buffers: up to 4 frames of capture per tick allows a slow tick to catch up. ---
            var framesPerTick = Math.Max(1, _p.SampleRate / _p.Fps);
            var capBuf = new float[framesPerTick * 4 * _p.MaxChannels];
            var stereoBuf = new float[framesPerTick * 4 * 2];
            var sel = _p.SelectedChannels0;

            var keysUsable = TryProbeConsoleKeys();
            var framePeriod = TimeSpan.FromSeconds(1.0 / _p.Fps);
            var loopClock = Stopwatch.StartNew();
            var nextFrame = loopClock.Elapsed;
            long frameIndex = 0;
            var quit = false;

            // Actual-fps measurement window.
            var fpsWindow = Stopwatch.StartNew();
            long fpsFrames = 0;
            var actualFps = (double)_p.Fps;

            var hudClock = Stopwatch.StartNew();

            while (!quit && !cancel.IsCancellationRequested)
            {
                // 1) Pull captured audio, select channels, mix to stereo with gain, feed projectM.
                var gotFloats = input.ReadInto(capBuf);
                var frames = _p.MaxChannels > 0 ? gotFloats / _p.MaxChannels : 0;
                if (frames > 0)
                {
                    var gain = (float)Math.Pow(10.0, _gainDb / 20.0);
                    double sumSq = 0;
                    var peak = 0f;
                    for (var f = 0; f < frames; f++)
                    {
                        var baseIdx = f * _p.MaxChannels;
                        float l, r;
                        if (sel.Length == 2)
                        {
                            l = capBuf[baseIdx + sel[0]] * gain;
                            r = capBuf[baseIdx + sel[1]] * gain;
                        }
                        else
                        {
                            float acc = 0;
                            for (var s = 0; s < sel.Length; s++)
                                acc += capBuf[baseIdx + sel[s]];
                            l = r = acc / sel.Length * gain;
                        }

                        stereoBuf[f * 2] = l;
                        stereoBuf[f * 2 + 1] = r;
                        var al = MathF.Abs(l);
                        var ar = MathF.Abs(r);
                        if (al > peak) peak = al;
                        if (ar > peak) peak = ar;
                        sumSq += (double)l * l + (double)r * r;
                    }

                    source.Submit(stereoBuf.AsSpan(0, frames * 2));
                    UpdateMeter(peak, Math.Sqrt(sumSq / (frames * 2)), framePeriod.TotalSeconds, frameIndex);
                }
                else
                {
                    // No fresh audio this tick: let the meter fall back toward silence.
                    UpdateMeter(0f, 0, framePeriod.TotalSeconds, frameIndex);
                }

                // 2) Composite projectM's surface to a CPU frame and send it out over NDI.
                var pts = TimeSpan.FromTicks(TimeSpan.TicksPerSecond * frameIndex / _p.Fps);
                var frame = compositor.CompositeWithSurfaces(noFrameLayers, surfaceLayers, pts);
                ndiOut.Video.Submit(frame);
                frame.Dispose();
                frameIndex++;
                fpsFrames++;

                // 3) Keyboard controls.
                if (keysUsable)
                    quit = HandleKeys(source);

                // 4) HUD (throttled).
                if (fpsWindow.Elapsed.TotalSeconds >= 0.5)
                {
                    actualFps = fpsFrames / fpsWindow.Elapsed.TotalSeconds;
                    fpsFrames = 0;
                    fpsWindow.Restart();
                }

                if (hudClock.Elapsed.TotalMilliseconds >= 100)
                {
                    DrawHud(ndiOut, source, actualFps, frameIndex);
                    hudClock.Restart();
                }

                // 5) Pace to the target frame rate (drift-corrected; resync if we fall far behind).
                nextFrame += framePeriod;
                var remaining = nextFrame - loopClock.Elapsed;
                if (remaining > TimeSpan.Zero)
                    Thread.Sleep(remaining);
                else if (remaining < TimeSpan.FromMilliseconds(-250))
                    nextFrame = loopClock.Elapsed;
            }

            Console.WriteLine();
            Console.WriteLine($"Stopped after {frameIndex} frames.");
        }
        finally
        {
            (input as IDisposable)?.Dispose();
            surface?.Dispose();
            source?.Dispose();
            compositor?.Dispose();
            ndiOut?.Dispose();
            if (glCtx != 0) SDL.GLDestroyContext(glCtx);
            if (win != 0) SDL.DestroyWindow(win);
            SDL.Quit();
        }
    }

    private void UpdateMeter(float tickPeak, double tickRms, double dtSeconds, long frameIndex)
    {
        var peakDb = tickPeak > 1e-6f ? 20.0 * Math.Log10(tickPeak) : -100.0;
        // Instant attack, ~30 dB/s release so the bar is readable rather than jittery.
        _meterDb = peakDb >= _meterDb ? peakDb : Math.Max(peakDb, _meterDb - 30.0 * dtSeconds);
        var rmsDb = tickRms > 1e-6 ? 20.0 * Math.Log10(tickRms) : -100.0;
        _rmsDb = rmsDb >= _rmsDb ? rmsDb : Math.Max(rmsDb, _rmsDb - 30.0 * dtSeconds);
        if (tickPeak >= 0.999f)
            _clipHoldUntilFrame = frameIndex + _p.Fps; // latch CLIP for ~1s
    }

    private bool HandleKeys(ProjectMVisualSource source)
    {
        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                case ConsoleKey.Add:
                    AdjustGain(+1);
                    break;
                case ConsoleKey.DownArrow:
                case ConsoleKey.Subtract:
                    AdjustGain(-1);
                    break;
                case ConsoleKey.N:
                    source.RequestNextPreset();
                    Notice("→ next preset");
                    break;
                case ConsoleKey.S:
                    _onSaveRequested?.Invoke(_gainDb);
                    break;
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    return true;
                default:
                    // '+' / '-' arrive as OemPlus/OemMinus (and with the char) on most layouts.
                    if (key.KeyChar is '+' or '=')
                        AdjustGain(+1);
                    else if (key.KeyChar is '-' or '_')
                        AdjustGain(-1);
                    break;
            }
        }

        return false;
    }

    private void AdjustGain(double deltaDb)
    {
        _gainDb = Math.Clamp(_gainDb + deltaDb, -60, 40);
        Notice($"gain {_gainDb:+0;-0;0} dB");
    }

    private void PrintBanner()
    {
        var chans = string.Join(", ", _p.SelectedChannels0.Select(c => c + 1));
        Console.WriteLine();
        Console.WriteLine("=== NDI Visualizer running ===");
        Console.WriteLine($"  Device   : {_p.DeviceName} (opened at {_p.MaxChannels} ch, {_p.SampleRate} Hz)");
        Console.WriteLine($"  Channels : {chans}");
        Console.WriteLine($"  Output   : NDI '{_p.NDIName}'  {_p.Width}x{_p.Height} @ {_p.Fps} fps");
        Console.WriteLine($"  Presets  : {(_p.PresetDirectory is { Length: > 0 } d ? d : "(idle preset only)")}");
        Console.WriteLine();
        Console.WriteLine("  Controls : [+/-] or [Up/Down] gain   [n] next preset   [s] save config   [q] quit");
        Console.WriteLine();
    }

    private static bool TryProbeConsoleKeys()
    {
        try
        {
            _ = Console.KeyAvailable;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Console.WriteLine("(stdin is not an interactive console - live key controls disabled; Ctrl+C to stop)");
            return false;
        }
    }

    // --- HUD rendering (single carriage-return-updated line) --------------------------------------------

    private static void Notice(string message)
    {
        // End the live status line, print the notice, then let the next DrawHud start a fresh status line.
        Console.WriteLine();
        Console.WriteLine("  " + message);
    }

    private void DrawHud(NDIOutput ndiOut, ProjectMVisualSource source, double actualFps, long frameIndex)
    {
        var conns = ndiOut.ConnectionCount;
        var clip = frameIndex < _clipHoldUntilFrame;
        var preset = source.CurrentPresetName is { Length: > 0 } n ? Truncate(n, 22) : "(idle)";

        var line =
            $"\rNDI:{conns}  in {_meterDb,6:0.0}dB {Meter(_meterDb)} {(clip ? "CLIP" : "    ")}  " +
            $"rms {_rmsDb,6:0.0}dB  gain {_gainDb:+0;-0;0}dB  {actualFps,4:0.0}fps  preset:{preset}";

        var width = SafeWindowWidth();
        if (line.Length > width)
            line = line[..width];
        else
            line = line.PadRight(width);
        Console.Write(line);
    }

    // A 12-cell bar spanning -60..0 dBFS.
    private static string Meter(double db)
    {
        const int cells = 12;
        var norm = Math.Clamp((db + 60.0) / 60.0, 0, 1);
        var filled = (int)Math.Round(norm * cells);
        return "[" + new string('#', filled) + new string('-', cells - filled) + "]";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static int SafeWindowWidth()
    {
        try
        {
            var w = Console.WindowWidth;
            return w > 20 ? w - 1 : 100;
        }
        catch (IOException)
        {
            return 100;
        }
    }
}
