using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ProjectMLib;
using S.Media.Compositor;
using Silk.NET.OpenGL;

namespace S.Media.Visualizer.ProjectM;

/// <summary>
/// The CONTINUOUS projectM renderer: owns a dedicated thread with its own offscreen GL context (via the
/// injected <see cref="IOffscreenGlContext"/> factory) and renders projectM there for the source's whole
/// lifetime - completely decoupled from any composition. Compositions come and go per track (the deck's
/// stable rebuild-per-track flow); their surfaces just blit <see cref="TryCopyLatestFrame"/>'s output, so
/// the visualizer never restarts on a track change and a stalling preset load can only ever freeze THIS
/// thread's frames (the transport/dispatcher stays untouched - the failure mode that previously wedged
/// the session when projectM rendered on the composition pump).
///
/// <para>Audio: drains the source's SPSC PCM ring on this thread. Frames: rendered into a private FBO at
/// the configured resolution, read back as BGRA, published under a lock (renderer-paced, ~one buffer copy
/// per frame; consumers copy out under the same lock). Row order matches the GL FBO layout, so the blit
/// shader's existing V-flip renders it upright - identical orientation to the in-composition path.</para>
/// </summary>
internal sealed class ProjectMOffscreenRenderer : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Visualizer.ProjectM.Renderer");

    private readonly ProjectMVisualSource _source;
    private readonly ProjectMOptions _options;
    private readonly Func<IOffscreenGlContext?> _contextFactory;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly float[] _pcmScratch = new float[4096];
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    private readonly object _frameGate = new();
    private readonly byte[] _latestFrame;
    private long _frameVersion; // under _frameGate; 0 = nothing rendered yet

    private string[] _presets = [];
    private int _presetIndex = -1;
    private readonly Random _random = new();
    private volatile int _presetCount = -1; // -1 = not enumerated yet
    private volatile string? _currentPresetName;
    private string? _lastSlowPresetLogged; // render thread only
    private volatile bool _failed;

    internal ProjectMOffscreenRenderer(
        ProjectMVisualSource source, ProjectMOptions options,
        int width, int height, int fps,
        Func<IOffscreenGlContext?> contextFactory)
    {
        _source = source;
        _options = options;
        _contextFactory = contextFactory;
        _width = Math.Max(16, width) & ~1;
        _height = Math.Max(16, height) & ~1;
        _fps = Math.Max(1, fps);
        _latestFrame = new byte[_width * _height * 4];
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "ProjectMRenderer" };
        _thread.Start();
    }

    public int Width => _width;

    public int Height => _height;

    /// <summary>Presets found in the configured directory (-1 until enumeration ran on the render thread).</summary>
    public int PresetCount => _presetCount;

    public string? CurrentPresetName => _currentPresetName;

    /// <summary>True when GL/projectM never came up - the blit surface then renders nothing (logged).</summary>
    public bool Failed => _failed;

    /// <summary>Copies the newest frame into <paramref name="destination"/> when it changed since
    /// <paramref name="lastSeenVersion"/> (updated on copy). Any thread.</summary>
    public bool TryCopyLatestFrame(byte[] destination, ref long lastSeenVersion)
    {
        lock (_frameGate)
        {
            if (_frameVersion == 0 || _frameVersion == lastSeenVersion)
                return false;
            System.Buffer.BlockCopy(_latestFrame, 0, destination, 0, Math.Min(destination.Length, _latestFrame.Length));
            lastSeenVersion = _frameVersion;
            return true;
        }
    }

    private unsafe void RenderLoop()
    {
        IOffscreenGlContext? context = null;
        nint projectM = 0;
        uint fbo = 0, colorTex = 0, depthRbo = 0;
        GL? gl = null;
        try
        {
            context = _contextFactory();
            if (context is null)
            {
                _failed = true;
                Trace.LogWarning("offscreen GL context unavailable - continuous visualizer disabled");
                return;
            }

            gl = context.Gl;

            // projectM defaults expect clean pixel-store state (see the surface's SOIL segfault notes).
            gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);

            projectM = Native.projectm_create();
            if (projectM == 0)
            {
                _failed = true;
                Trace.LogError("projectm_create returned NULL - continuous visualizer disabled (GL 3.3+ required)");
                return;
            }

            Native.projectm_set_window_size(projectM, (nuint)_width, (nuint)_height);
            Native.projectm_set_fps(projectM, _fps);
            Native.projectm_set_preset_duration(projectM, double.MaxValue); // rotation is ours
            Native.projectm_set_soft_cut_duration(projectM, Math.Max(0, _options.TransitionSeconds));
            Native.projectm_set_beat_sensitivity(projectM, (float)Math.Clamp(_options.BeatSensitivity, 0.0, 5.0));
            Native.projectm_set_aspect_correction(projectM, true);
            Native.projectm_set_preset_locked(projectM, true);

            _presets = ProjectMGlLayerSurface.EnumeratePresets(_options.PresetDirectory);
            _presetCount = _presets.Length;
            if (_presets.Length > 0)
                LoadNextPreset(projectM, smooth: false);
            Trace.LogInformation(
                "continuous projectM {Version} renderer running ({Presets} presets, {W}x{H}@{Fps})",
                ProjectMRuntime.Version, _presets.Length, _width, _height, _fps);

            // Private FBO (color + depth - some presets enable depth ops).
            fbo = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            colorTex = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, colorTex);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)_width, (uint)_height, 0, GLEnum.Rgba, GLEnum.UnsignedByte, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, colorTex, 0);
            depthRbo = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthRbo);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
                (uint)_width, (uint)_height);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, depthRbo);
            if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            {
                _failed = true;
                Trace.LogError("continuous renderer FBO incomplete - visualizer disabled");
                return;
            }

            var readback = new byte[_width * _height * 4];
            var frameInterval = TimeSpan.FromSeconds(1.0 / _fps);
            var started = Stopwatch.GetTimestamp();
            long frame = 0;
            var lastPresetSwitch = Stopwatch.GetTimestamp();
            var token = _cts.Token;

            while (!token.IsCancellationRequested)
            {
                // Feed the tapped PCM (single consumer of the source's SPSC ring - this thread).
                while (true)
                {
                    var read = _source.DrainPcm(_pcmScratch);
                    if (read <= 0)
                        break;
                    fixed (float* pcm = _pcmScratch)
                        Native.projectm_pcm_add_float(projectM, pcm, (uint)(read / 2), ProjectMChannels.Stereo);
                    if (read < _pcmScratch.Length)
                        break;
                }

                // Manual rotation + operator "next preset" requests. A stalling load only stalls THIS
                // thread - frames freeze briefly, the transport never blocks on it.
                var operatorSkip = _source.ConsumeNextPresetRequest();
                if (_presets.Length > 0
                    && (operatorSkip
                        || (_presets.Length > 1
                            && Stopwatch.GetElapsedTime(lastPresetSwitch).TotalSeconds
                               >= Math.Max(5, _options.PresetDurationSeconds))))
                {
                    LoadNextPreset(projectM, smooth: !operatorSkip);
                    lastPresetSwitch = Stopwatch.GetTimestamp();
                }

                context.MakeCurrent(); // defensive: nothing else should displace it on this thread
                gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
                gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
                gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                gl.Viewport(0, 0, (uint)_width, (uint)_height);
                gl.Disable(EnableCap.ScissorTest);
                gl.ClearColor(0f, 0f, 0f, 1f);
                gl.Clear((uint)ClearBufferMask.ColorBufferBit);
                var renderStarted = Stopwatch.GetTimestamp();
                Native.projectm_opengl_render_frame(projectM);
                var renderElapsed = Stopwatch.GetElapsedTime(renderStarted);
                if (renderElapsed.TotalMilliseconds > 500 && _currentPresetName != _lastSlowPresetLogged)
                {
                    // Once per offending preset: a frame this slow means the preset (or its lazy texture/
                    // shader work) is hammering the GPU - name it so the operator can prune it.
                    _lastSlowPresetLogged = _currentPresetName;
                    Trace.LogWarning(
                        "SLOW preset render: {Ms:0}ms/frame on '{Preset}' - GPU-heavy preset; consider removing it",
                        renderElapsed.TotalMilliseconds, _currentPresetName ?? "(idle)");
                }

                // Read the frame back (BGRA, FBO row order - the blit shader's V-flip shows it upright).
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
                gl.PixelStore(PixelStoreParameter.PackRowLength, 0);
                fixed (byte* dst = readback)
                    gl.ReadPixels(0, 0, (uint)_width, (uint)_height, GLEnum.Bgra, GLEnum.UnsignedByte, dst);

                lock (_frameGate)
                {
                    System.Buffer.BlockCopy(readback, 0, _latestFrame, 0, _latestFrame.Length);
                    _frameVersion++;
                }

                frame++;
                var sleepTicks = frame * frameInterval.Ticks - Stopwatch.GetElapsedTime(started).Ticks;
                if (sleepTicks > 0 && token.WaitHandle.WaitOne(TimeSpan.FromTicks(sleepTicks)))
                    break;
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            Trace.LogError(ex, "continuous projectM renderer faulted - visualizer disabled");
        }
        finally
        {
            // Owner-thread teardown: GL objects, then projectM, then the context itself.
            try
            {
                if (gl is not null)
                {
                    context?.MakeCurrent();
                    if (fbo != 0) gl.DeleteFramebuffer(fbo);
                    if (colorTex != 0) gl.DeleteTexture(colorTex);
                    if (depthRbo != 0) gl.DeleteRenderbuffer(depthRbo);
                }
            }
            catch (Exception ex) { Trace.LogWarning(ex, "renderer GL teardown"); }
            try
            {
                if (projectM != 0)
                    Native.projectm_destroy(projectM);
            }
            catch (Exception ex) { Trace.LogWarning(ex, "projectm_destroy"); }
            try { context?.Dispose(); }
            catch (Exception ex) { Trace.LogWarning(ex, "offscreen context dispose"); }
        }
    }

    private void LoadNextPreset(nint projectM, bool smooth)
    {
        if (_presets.Length == 0)
            return;
        _presetIndex = _options.Shuffle && _presets.Length > 1
            ? NextRandomIndex()
            : (_presetIndex + 1) % _presets.Length;
        var preset = _presets[_presetIndex];
        try
        {
            // Timed: a pathological preset's shader compile can take SECONDS and stalls the whole GPU
            // driver process-wide (Mesa serializes compiles) - confirmed by a captured hang dump where a
            // compile jammed Avalonia's render thread and, through a tooltip-close sync-wait, the UI
            // thread. Loading here (our own thread) keeps the transport safe; the log names the offender
            // so the operator can prune it from the pack.
            var started = Stopwatch.GetTimestamp();
            Native.projectm_load_preset_file(projectM, preset, smooth);
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed.TotalMilliseconds > 250)
            {
                Trace.LogWarning(
                    "SLOW preset load: {Ms:0}ms for '{Preset}' - heavy shader compile; consider removing it from the pack",
                    elapsed.TotalMilliseconds, preset);
            }

            _currentPresetName = Path.GetFileNameWithoutExtension(preset);
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "preset load failed: {Preset}", preset);
        }
    }

    private int NextRandomIndex()
    {
        var next = _random.Next(_presets.Length);
        if (next == _presetIndex)
            next = (next + 1) % _presets.Length;
        return next;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _thread.Join(TimeSpan.FromSeconds(5)); }
        catch { /* best effort */ }
        _cts.Dispose();
    }
}
