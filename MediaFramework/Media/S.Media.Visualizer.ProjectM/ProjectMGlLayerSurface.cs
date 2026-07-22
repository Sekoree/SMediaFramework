using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ProjectMLib;
using S.Media.Compositor;
using Silk.NET.OpenGL;

namespace S.Media.Visualizer.ProjectM;

/// <summary>
/// Renders projectM into a private FBO on the compositor's GL thread, then blits into the canvas with
/// the layer transform + opacity (the same blit approach as MMDGlLayerSurface). projectM clobbers GL
/// state freely (program/VAO/blend/viewport via its internal GLAD loader), so this surface re-binds
/// everything its own blit needs after <c>projectm_opengl_render_frame</c> and leaves depth/scissor
/// off for the host's subsequent passes; the compositor restores its full state set around the whole
/// composite.
///
/// <para>Lifetime: <see cref="Dispose"/> can run off the GL thread (session dispatcher), so it only
/// marks the surface dead. The owning compositor invokes <see cref="ReleaseGl"/> on its GL thread,
/// which destroys the projectM instance and every GL object before the context is torn down.</para>
/// </summary>
internal sealed class ProjectMGlLayerSurface : IVideoCompositorLayerSurface, IVideoCompositorGlResource
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Visualizer.ProjectM.Surface");

    private readonly ProjectMVisualSource _source;
    private readonly ProjectMOptions _options;
    private readonly float[] _pcmScratch = new float[4096];
    private string[] _presets = [];
    private PresetRotation? _rotation;
    // Written by the native switch-failed callback, which fires synchronously inside
    // projectm_load_preset_file on the compositor thread - the same thread that reads it.
    private int _presetSwitchFailed;
    private string? _lastFailureMessage;
    private bool _allFailedLogged;
    private GCHandle _failureCallbackHandle;
    private long _lastPresetSwitchTimestamp;

    private GL? _gl;
    private nint _projectM;
    private uint _fbo, _colorTex, _depthRbo;
    private uint _blitProgram, _blitVao;
    private int _cornersLocation = -1, _opacityLocation = -1, _sceneLocation = -1;
    private int _sceneWidth, _sceneHeight;
    private int _canvasWidth, _canvasHeight;
    private volatile bool _disposed;
    private bool _failed;

    internal ProjectMGlLayerSurface(ProjectMVisualSource source)
    {
        _source = source;
        _options = source.Options;
    }

    public unsafe void ConfigureGl(GL gl, VideoFormat canvas)
    {
        _gl = gl;
        _canvasWidth = canvas.Width;
        _canvasHeight = canvas.Height;
        if (_projectM != 0 || _failed)
            return; // canvas re-configure: the scene keeps its size, only the blit target changed

        try
        {
            // Defensive pixel-store reset (belt & braces beside the host's own reset): projectM's
            // texture preloads assume GL defaults; a stale UNPACK_ROW_LENGTH from a host's frame
            // uploads makes the driver read past SOIL's source buffers - an uncatchable segfault.
            gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);

            // projectm_create requires a CURRENT GL context (it resolves GL entry points through it) -
            // guaranteed here by the IVideoCompositorLayerSurface contract.
            _projectM = Native.projectm_create();
            if (_projectM == 0)
            {
                _failed = true;
                Trace.LogError("projectm_create returned NULL - visualizer renders nothing (GL 3.3+ context required)");
                return;
            }

            var texturePaths = ProjectMTextureSearchPaths.Configure(_projectM, _options.PresetDirectory);

            // Render at the configured resolution (decoupled from the canvas) when set, else follow the
            // canvas. The blit into the canvas scales, so a high internal res stays crisp even on a
            // small canvas, and vice-versa.
            _sceneWidth = _options.RenderWidth > 0 ? _options.RenderWidth : canvas.Width;
            _sceneHeight = _options.RenderHeight > 0 ? _options.RenderHeight : canvas.Height;
            Native.projectm_set_window_size(_projectM, (nuint)_sceneWidth, (nuint)_sceneHeight);
            var fps = _options.Fps > 0
                ? _options.Fps
                : canvas.FrameRate.Numerator > 0 && canvas.FrameRate.Denominator > 0
                    ? canvas.FrameRate.Numerator / canvas.FrameRate.Denominator
                    : 0;
            if (fps > 0)
                Native.projectm_set_fps(_projectM, Math.Max(1, fps));
            Native.projectm_set_preset_duration(_projectM, double.MaxValue); // rotation is ours (no playlist lib)
            Native.projectm_set_soft_cut_duration(_projectM, Math.Max(0, _options.TransitionSeconds));
            Native.projectm_set_beat_sensitivity(_projectM, (float)Math.Clamp(_options.BeatSensitivity, 0.0, 5.0));
            Native.projectm_set_aspect_correction(_projectM, true);
            Native.projectm_set_preset_locked(_projectM, true); // no self-switching; NextPreset drives rotation

            // Same auto-skip contract as the continuous renderer: a preset that fails to load is
            // blocklisted and rotation advances immediately instead of showing the stale scene.
            _failureCallbackHandle = GCHandle.Alloc(this);
            unsafe
            {
                Native.projectm_set_preset_switch_failed_event_callback(
                    _projectM,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnPresetSwitchFailed,
                    GCHandle.ToIntPtr(_failureCallbackHandle));
            }

            _presets = EnumeratePresets(_options.PresetDirectory);
            _rotation = new PresetRotation(_presets, _options.Shuffle);
            _source.ReportLegacyPresets(_presets.Length);
            if (_presets.Length > 0)
                LoadNextPreset(smooth: false);
            Trace.LogInformation("projectM {Version} surface ready ({Presets} presets, {Textures} texture paths, {W}x{H})",
                ProjectMRuntime.Version, _presets.Length, texturePaths.Length, _sceneWidth, _sceneHeight);

            CreateSceneFbo(gl);
            CreateBlitProgram(gl);
            _lastPresetSwitchTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        catch (Exception ex)
        {
            _failed = true;
            Trace.LogError(ex, "projectM surface configuration failed - visualizer renders nothing");
        }
    }

    public unsafe void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity)
    {
        if (_disposed || _failed || _projectM == 0 || _fbo == 0)
            return;

        // Feed the tapped PCM (drained from the source's ring; interleaved stereo floats).
        while (true)
        {
            var read = _source.DrainPcm(_pcmScratch);
            if (read <= 0)
                break;
            fixed (float* pcm = _pcmScratch)
                Native.projectm_pcm_add_float(_projectM, pcm, (uint)(read / 2), ProjectMChannels.Stereo);
            if (read < _pcmScratch.Length)
                break;
        }

        // Manual preset rotation (the playlist library stays off) + operator "next preset" requests.
        var operatorSkip = _source.ConsumeNextPresetRequest();
        if (_presets.Length > 0
            && (operatorSkip
                || (_presets.Length > 1
                    && System.Diagnostics.Stopwatch.GetElapsedTime(_lastPresetSwitchTimestamp).TotalSeconds
                       >= Math.Max(5, _options.PresetDurationSeconds))))
        {
            LoadNextPreset(smooth: !operatorSkip); // a manual skip cuts hard - the operator wants it NOW
            _lastPresetSwitchTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        // --- projectM pass into the private FBO ---------------------------------
        // Same defensive reset as ConfigureGl: presets can load textures lazily on ANY frame.
        gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.Viewport(0, 0, (uint)_sceneWidth, (uint)_sceneHeight);
        gl.Disable(EnableCap.ScissorTest);
        gl.ClearColor(0f, 0f, 0f, 1f);
        gl.Clear((uint)ClearBufferMask.ColorBufferBit);
        try
        {
            Native.projectm_opengl_render_frame(_projectM);
        }
        catch (Exception ex)
        {
            _failed = true;
            Trace.LogError(ex, "projectm_opengl_render_frame failed - visualizer disabled for this composition");
            return;
        }

        // projectM leaves arbitrary state behind - rebind everything the blit needs, and leave the
        // depth/scissor switches the host's 2D passes expect.
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.CullFace);
        gl.Enable(EnableCap.Blend);
        gl.BlendFuncSeparate(
            BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha,
            BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

        // --- blit into the canvas with the layer transform + opacity -------------
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
        gl.Viewport(0, 0, (uint)_canvasWidth, (uint)_canvasHeight);
        gl.UseProgram(_blitProgram);
        Span<float> ndc = stackalloc float[8];
        WriteCornerNdc(transform, ndc);
        fixed (float* c = ndc)
            gl.Uniform2(_cornersLocation, 4, c);
        gl.Uniform1(_opacityLocation, Math.Clamp(opacity, 0f, 1f));
        gl.Uniform1(_sceneLocation, 0);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _colorTex);
        gl.BindVertexArray(_blitVao);
        gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        gl.BindVertexArray(0);
    }

    /// <summary>Advance to the next LOADABLE preset (shuffle or rotation): failed loads are
    /// blocklisted and skipped in the same call, so a broken preset costs milliseconds instead of
    /// an empty visualizer slot. Also exposed for a UI "next" action later.</summary>
    internal void LoadNextPreset(bool smooth)
    {
        if (_projectM == 0 || _rotation is not { } rotation)
            return;

        while (rotation.TryAdvance(out var preset))
        {
            _source.ReportLegacyPresetName(Path.GetFileNameWithoutExtension(preset)); // visible even if the native load stalls
            Volatile.Write(ref _presetSwitchFailed, 0);
            var loadThrew = false;
            try
            {
                Native.projectm_load_preset_file(_projectM, preset, smooth);
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "preset load failed: {Preset}", preset);
                loadThrew = true;
            }

            if (!loadThrew && Volatile.Read(ref _presetSwitchFailed) == 0)
                return;

            if (rotation.MarkFailed(preset))
            {
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

    /// <summary>Native projectM switch-failed callback (compositor thread, inside the load call).
    /// Must not call back into projectM - just records the failure for LoadNextPreset's loop.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnPresetSwitchFailed(nint presetFilename, nint message, nint userData)
    {
        _ = presetFilename;
        if (GCHandle.FromIntPtr(userData).Target is ProjectMGlLayerSurface self)
        {
            self._lastFailureMessage = Marshal.PtrToStringUTF8(message);
            Volatile.Write(ref self._presetSwitchFailed, 1);
        }
    }

    internal static string[] EnumeratePresets(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return [];
        try
        {
            var presets = Directory
                .EnumerateFiles(directory, "*.milk", SearchOption.AllDirectories)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return presets;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void CreateSceneFbo(GL gl)
    {
        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _colorTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _colorTex);
        unsafe
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)_sceneWidth, (uint)_sceneHeight, 0, GLEnum.Rgba, GLEnum.UnsignedByte, null);
        }
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTex, 0);

        // Depth: some presets enable depth ops; give the FBO a depth attachment so they are complete.
        _depthRbo = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRbo);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
            (uint)_sceneWidth, (uint)_sceneHeight);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _depthRbo);

        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            _failed = true;
            Trace.LogError("projectM scene FBO incomplete: {Status}", status);
        }
    }

    private void CreateBlitProgram(GL gl)
    {
        _blitProgram = CompileProgram(gl, BlitVs, BlitFs);
        _blitVao = gl.GenVertexArray(); // attribute-less quad still needs a bound VAO in core profile
        _cornersLocation = gl.GetUniformLocation(_blitProgram, "uCorners");
        _opacityLocation = gl.GetUniformLocation(_blitProgram, "uOpacity");
        _sceneLocation = gl.GetUniformLocation(_blitProgram, "uScene");
    }

    private static uint CompileProgram(GL gl, string vs, string fs)
    {
        var vertex = gl.CreateShader(ShaderType.VertexShader);
        gl.ShaderSource(vertex, vs);
        gl.CompileShader(vertex);
        var fragment = gl.CreateShader(ShaderType.FragmentShader);
        gl.ShaderSource(fragment, fs);
        gl.CompileShader(fragment);
        var program = gl.CreateProgram();
        gl.AttachShader(program, vertex);
        gl.AttachShader(program, fragment);
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var linked);
        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);
        if (linked == 0)
            throw new InvalidOperationException($"projectM blit program link failed: {gl.GetProgramInfoLog(program)}");
        return program;
    }

    /// <summary>Scene-corner NDC for the blit strip (TL, TR, BL, BR): the layer transform maps scene
    /// pixels (nominal source = canvas size) into canvas pixels, top-left origin; NDC flips Y. The
    /// blit shader's V-flip handles the FBO's Y-down texture orientation.</summary>
    private void WriteCornerNdc(LayerTransform2D transform, Span<float> ndc)
    {
        Span<(float X, float Y)> corners =
        [
            transform.Apply(0, 0),
            transform.Apply(_canvasWidth, 0),
            transform.Apply(0, _canvasHeight),
            transform.Apply(_canvasWidth, _canvasHeight),
        ];
        for (var i = 0; i < 4; i++)
        {
            ndc[i * 2] = corners[i].X / _canvasWidth * 2f - 1f;
            ndc[i * 2 + 1] = 1f - corners[i].Y / _canvasHeight * 2f;
        }
    }

    public void Dispose() => _disposed = true;

    public void ReleaseGl(GL gl)
    {
        _disposed = true;
        if (_projectM != 0)
        {
            Native.projectm_set_preset_switch_failed_event_callback(_projectM, 0, 0);
            Native.projectm_destroy(_projectM);
            _projectM = 0;
            if (_failureCallbackHandle.IsAllocated)
                _failureCallbackHandle.Free();
        }
        if (_fbo != 0) { gl.DeleteFramebuffer(_fbo); _fbo = 0; }
        if (_colorTex != 0) { gl.DeleteTexture(_colorTex); _colorTex = 0; }
        if (_depthRbo != 0) { gl.DeleteRenderbuffer(_depthRbo); _depthRbo = 0; }
        if (_blitProgram != 0) { gl.DeleteProgram(_blitProgram); _blitProgram = 0; }
        if (_blitVao != 0) { gl.DeleteVertexArray(_blitVao); _blitVao = 0; }
    }

    private const string BlitVs = """
        #version 330 core
        uniform vec2 uCorners[4];
        out vec2 vUv;
        void main()
        {
            // Strip order TL,TR,BL,BR; the scene FBO is Y-down relative to GL texture space, so flip V.
            vec2 uvs[4] = vec2[4](vec2(0.0, 1.0), vec2(1.0, 1.0), vec2(0.0, 0.0), vec2(1.0, 0.0));
            gl_Position = vec4(uCorners[gl_VertexID], 0.0, 1.0);
            vUv = uvs[gl_VertexID];
        }
        """;

    private const string BlitFs = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uScene;
        uniform float uOpacity;
        out vec4 fragColor;
        void main()
        {
            vec4 scene = texture(uScene, vUv);
            fragColor = vec4(scene.rgb, scene.a * uOpacity);
        }
        """;
}
