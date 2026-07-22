using System.Diagnostics;
using System.Runtime.InteropServices;
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
/// the configured resolution, read back as BGRA, and swap-published under a short lock with no producer
/// copy; consumers copy out under the same lock. Row order matches the GL FBO layout, so the blit
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
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private int _disposeStarted;

    private readonly object _frameGate = new();
    private byte[] _latestFrame;
    private long _frameVersion; // under _frameGate; 0 = nothing rendered yet

    private string[] _presets = [];
    private PresetRotation? _rotation;
    // Written by the native switch-failed callback, which fires SYNCHRONOUSLY inside
    // projectm_load_preset_file on the render thread - same thread that reads it right after.
    private int _presetSwitchFailed;
    private string? _lastFailureMessage;
    private volatile int _failedPresetCount;
    private bool _allFailedLogged;
    private GCHandle _failureCallbackHandle;
    private volatile int _presetCount = -1; // -1 = not enumerated yet
    private volatile string? _currentPresetName;
    private string? _lastSlowPresetLogged; // render thread only
    private string? _lastSlowReadbackPresetLogged; // render thread only
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

    /// <summary>Presets blocklisted after a projectM load failure (auto-skipped by rotation).</summary>
    public int FailedPresetCount => _failedPresetCount;

    public string? CurrentPresetName => _currentPresetName;

    /// <summary>True when GL/projectM never came up - the blit surface then renders nothing (logged).</summary>
    public bool Failed => _failed;

    /// <summary>Pixel layout of published frames: BGRA32 on desktop GL (readback is native there),
    /// RGBA32 on GLES (its native readback - avoids the driver's BGRA slow path AND a CPU swizzle;
    /// the NDI sender takes RGBA directly). Set on the render thread before the first publish.</summary>
    public S.Media.Core.Video.PixelFormat PublishedPixelFormat { get; private set; } = S.Media.Core.Video.PixelFormat.Bgra32;

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
        var pbos = new uint[2];
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

            var texturePaths = ProjectMTextureSearchPaths.Configure(projectM, _options.PresetDirectory);

            Native.projectm_set_window_size(projectM, (nuint)_width, (nuint)_height);
            Native.projectm_set_fps(projectM, _fps);
            Native.projectm_set_preset_duration(projectM, double.MaxValue); // rotation is ours
            Native.projectm_set_soft_cut_duration(projectM, Math.Max(0, _options.TransitionSeconds));
            Native.projectm_set_beat_sensitivity(projectM, (float)Math.Clamp(_options.BeatSensitivity, 0.0, 5.0));
            Native.projectm_set_aspect_correction(projectM, true);
            Native.projectm_set_preset_locked(projectM, true);

            // Failed preset loads (parse/shader errors) fire this callback synchronously inside
            // projectm_load_preset_file; LoadNextPreset checks the flag and auto-advances so a bad
            // preset never leaves the output stuck on the previous/idle scene for a whole slot.
            _failureCallbackHandle = GCHandle.Alloc(this);
            unsafe
            {
                Native.projectm_set_preset_switch_failed_event_callback(
                    projectM,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnPresetSwitchFailed,
                    GCHandle.ToIntPtr(_failureCallbackHandle));
            }

            _presets = ProjectMGlLayerSurface.EnumeratePresets(_options.PresetDirectory);
            _rotation = new PresetRotation(_presets, _options.Shuffle);
            _presetCount = _presets.Length;
            if (_presets.Length > 0)
                LoadNextPreset(projectM, smooth: false);
            Trace.LogInformation(
                "continuous projectM {Version} renderer running ({Presets} presets, {Textures} texture paths, {W}x{H}@{Fps})",
                ProjectMRuntime.Version, _presets.Length, texturePaths.Length, _width, _height, _fps);

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

            // GLES contexts (Android) only support BGRA readback via GL_EXT_read_format_bgra;
            // without it, read RGBA and swizzle in place so published frames stay BGRA either way.
            // GLES: read back the FBO's native RGBA and publish RGBA32 - the BGRA extension path
            // routes through a driver swizzle (measurably slower on mobile) and every Android
            // consumer (NDI) takes RGBA directly. Desktop GL keeps BGRA (native there, and the
            // composition blit path historically expects it).
            var glVersion = gl.GetStringS(StringName.Version) ?? string.Empty;
            var isGles = glVersion.StartsWith("OpenGL ES", StringComparison.Ordinal);
            PublishedPixelFormat = isGles ? S.Media.Core.Video.PixelFormat.Rgba32 : S.Media.Core.Video.PixelFormat.Bgra32;
            // One-shot markers around the first iteration: a driver-level hang (seen on mobile GLES)
            // freezes this thread silently - these pin down WHERE without a debugger on the device.
            Trace.LogInformation("renderer FBO ready on '{Version}' (publish={Format})", glVersion, PublishedPixelFormat);
            var firstFramePublished = false;

            var readback = new byte[_width * _height * 4];
            var frameBytes = (uint)(_width * _height * 4);

            // Async readback ring: glReadPixels into a pixel-pack buffer returns without draining
            // the GPU pipeline; the PREVIOUS frame's PBO is mapped instead, which by then has
            // (usually) finished its DMA. Costs one frame of latency and roughly halves-to-quarters
            // the per-frame stall on mobile GLES (the sync path measured 40-130 ms at 720p).
            // Falls back to the synchronous path when buffer setup fails.
            var usePbo = true;
            try
            {
                for (var i = 0; i < pbos.Length; i++)
                {
                    pbos[i] = gl.GenBuffer();
                    gl.BindBuffer(BufferTargetARB.PixelPackBuffer, pbos[i]);
                    gl.BufferData(BufferTargetARB.PixelPackBuffer, frameBytes, null, BufferUsageARB.StreamRead);
                }

                gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
            }
            catch (Exception ex)
            {
                usePbo = false;
                Trace.LogWarning(ex, "pixel-pack buffers unavailable - using synchronous readback");
            }

            var pboIndex = 0;
            var pboPendingFrames = 0; // PBOs holding an issued-but-unmapped readback
            var intervalStopwatchTicks = Stopwatch.Frequency / (double)_fps;
            var nextDeadline = Stopwatch.GetTimestamp();
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

                // Read the frame back (FBO row order - the blit shader's V-flip shows it upright).
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
                gl.PixelStore(PixelStoreParameter.PackRowLength, 0);
                var readFormat = isGles ? GLEnum.Rgba : GLEnum.Bgra;
                var readbackStarted = Stopwatch.GetTimestamp();
                var framePublishable = true;
                if (usePbo)
                {
                    // Issue this frame's readback (async into the current PBO)...
                    gl.BindBuffer(BufferTargetARB.PixelPackBuffer, pbos[pboIndex]);
                    gl.ReadPixels(0, 0, (uint)_width, (uint)_height, readFormat, GLEnum.UnsignedByte, (void*)0);
                    pboIndex = 1 - pboIndex;
                    if (pboPendingFrames < 1)
                    {
                        // Ring not primed: nothing to map yet, publish starts next iteration.
                        pboPendingFrames++;
                        framePublishable = false;
                    }
                    else
                    {
                        // ...and map the PREVIOUS frame's PBO, whose transfer had a frame to finish.
                        gl.BindBuffer(BufferTargetARB.PixelPackBuffer, pbos[pboIndex]);
                        var mapped = gl.MapBufferRange(
                            BufferTargetARB.PixelPackBuffer, 0, frameBytes, (uint)MapBufferAccessMask.ReadBit);
                        if (mapped is not null)
                        {
                            new ReadOnlySpan<byte>(mapped, (int)frameBytes).CopyTo(readback);
                            gl.UnmapBuffer(BufferTargetARB.PixelPackBuffer);
                        }
                        else
                        {
                            // Map failure (context loss etc.): drop to the sync path for good.
                            usePbo = false;
                            framePublishable = false;
                            Trace.LogWarning("pixel-pack map failed - switching to synchronous readback");
                        }
                    }

                    gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
                }
                else
                {
                    fixed (byte* dst = readback)
                        gl.ReadPixels(0, 0, (uint)_width, (uint)_height, readFormat, GLEnum.UnsignedByte, dst);
                }

                var readbackElapsed = Stopwatch.GetElapsedTime(readbackStarted);
                if (readbackElapsed.TotalMilliseconds > 500 && _currentPresetName != _lastSlowReadbackPresetLogged)
                {
                    _lastSlowReadbackPresetLogged = _currentPresetName;
                    Trace.LogWarning(
                        "SLOW projectM readback: {Ms:0}ms on '{Preset}' - GPU/driver transfer stalled",
                        readbackElapsed.TotalMilliseconds, _currentPresetName ?? "(idle)");
                }

                if (framePublishable)
                {
                    lock (_frameGate)
                    {
                        // Double-buffer publication: the renderer never copies a full frame while
                        // holding the lock; it swaps ownership with the previously published buffer.
                        var reusable = _latestFrame;
                        _latestFrame = readback;
                        readback = reusable;
                        _frameVersion++;
                    }

                    if (!firstFramePublished)
                    {
                        firstFramePublished = true;
                        Trace.LogInformation(
                            "first frame published (render {RenderMs:0.0}ms, readback {ReadbackMs:0.0}ms, pbo={Pbo})",
                            renderElapsed.TotalMilliseconds, readbackElapsed.TotalMilliseconds, usePbo);
                    }
                }

                nextDeadline += (long)Math.Round(intervalStopwatchTicks);
                var now = Stopwatch.GetTimestamp();
                if (nextDeadline <= now)
                {
                    // Behind schedule (slow preset, or oversleeping waits - Android applies tens of
                    // ms of timer slack to background threads): render back-to-back to catch up
                    // instead of waiting, resyncing only when hopelessly behind. Re-anchoring the
                    // deadline every time capped the loop at ~1/(interval+slack) fps on device.
                    if (now - nextDeadline > Stopwatch.Frequency)
                        nextDeadline = now;
                    if (token.IsCancellationRequested)
                        break;
                    continue;
                }

                var delay = TimeSpan.FromSeconds((nextDeadline - now) / (double)Stopwatch.Frequency);
                if (token.WaitHandle.WaitOne(delay))
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
                    foreach (var pbo in pbos)
                    {
                        if (pbo != 0)
                            gl.DeleteBuffer(pbo);
                    }
                }
            }
            catch (Exception ex) { Trace.LogWarning(ex, "renderer GL teardown"); }
            try
            {
                if (projectM != 0)
                    Native.projectm_set_preset_switch_failed_event_callback(projectM, 0, 0);
                    Native.projectm_destroy(projectM);
            }
            catch (Exception ex) { Trace.LogWarning(ex, "projectm_destroy"); }
            if (_failureCallbackHandle.IsAllocated)
                _failureCallbackHandle.Free();
            try { context?.Dispose(); }
            catch (Exception ex) { Trace.LogWarning(ex, "offscreen context dispose"); }
            _completion.TrySetResult();
        }
    }

    private void LoadNextPreset(nint projectM, bool smooth)
    {
        if (_rotation is not { } rotation)
            return;

        // Advance until a preset LOADS: the switch-failed callback (or a managed load exception)
        // blocklists the offender and the loop moves straight to the next candidate, so a broken
        // preset costs milliseconds instead of an "empty" slot. Bounded by the pack size via
        // TryAdvance's blocklist; fully-broken packs stop rotating (projectM idle stays up).
        while (rotation.TryAdvance(out var preset))
        {
            _currentPresetName = Path.GetFileNameWithoutExtension(preset);
            Volatile.Write(ref _presetSwitchFailed, 0);
            var loadThrew = false;
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
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "preset load failed: {Preset}", preset);
                loadThrew = true;
            }

            if (!loadThrew && Volatile.Read(ref _presetSwitchFailed) == 0)
                return; // loaded (or transitioning) - done

            if (rotation.MarkFailed(preset))
            {
                _failedPresetCount = rotation.FailedCount;
                Trace.LogWarning(
                    "preset failed to load - skipping it from rotation ({Failed}/{Total}): '{Preset}' ({Message})",
                    rotation.FailedCount, rotation.Count, preset, _lastFailureMessage ?? "load threw");
            }

            smooth = false; // a failed smooth switch can leave a half-started blend; cut hard on retry
        }

        if (rotation.AllFailed && !_allFailedLogged)
        {
            _allFailedLogged = true;
            Trace.LogError(
                "every preset in the pack failed to load ({Total}) - staying on the projectM idle preset",
                rotation.Count);
        }
    }

    /// <summary>Native projectM switch-failed callback (render thread, inside the load call).
    /// Must not call back into projectM - just records the failure for LoadNextPreset's loop.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnPresetSwitchFailed(nint presetFilename, nint message, nint userData)
    {
        _ = presetFilename;
        if (GCHandle.FromIntPtr(userData).Target is ProjectMOffscreenRenderer self)
        {
            self._lastFailureMessage = Marshal.PtrToStringUTF8(message);
            Volatile.Write(ref self._presetSwitchFailed, 1);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;

        // Native preset compilation/rendering is not cancellable. Dispose is routinely initiated by the
        // Avalonia dispatcher (toggle/settings/deck teardown), so joining this thread here can freeze the UI
        // for the full timeout. Signal cancellation and let owner-thread teardown complete asynchronously.
        // The CTS must remain alive until the render loop exits because that loop waits on Token.WaitHandle.
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* completion cleanup won the race */ }
        _ = DisposeCancellationWhenStoppedAsync();
    }

    private async Task DisposeCancellationWhenStoppedAsync()
    {
        await _completion.Task.ConfigureAwait(false);
        _cts.Dispose();
    }
}
