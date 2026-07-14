using Microsoft.Extensions.Logging;
using S.Media.Compositor;
using Silk.NET.OpenGL;

namespace S.Media.Visualizer.ProjectM;

/// <summary>
/// The composition-side face of the CONTINUOUS visualizer: a thin surface that uploads the
/// <see cref="ProjectMOffscreenRenderer"/>'s latest frame into a texture and blits it into the canvas
/// with the layer transform + opacity. No projectM calls happen in the composition's GL context at all,
/// so compositions can be torn down and rebuilt per track while the renderer runs on uninterrupted -
/// a fresh surface simply picks the stream up mid-flow. Same blit shader/orientation as the legacy
/// in-composition surface (the readback preserves FBO row order; the shader's V-flip shows it upright).
///
/// <para>Lifetime: <see cref="Dispose"/> can run off the GL thread and stops rendering. The owning
/// compositor later invokes <see cref="ReleaseGl"/> on its GL thread before tearing down the context.</para>
/// </summary>
internal sealed class ProjectMFrameBlitSurface : IVideoCompositorLayerSurface, IVideoCompositorGlResource
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Visualizer.ProjectM.BlitSurface");

    private readonly ProjectMVisualSource _source;
    private readonly ProjectMOffscreenRenderer _renderer;
    private ProjectMGlLayerSurface? _fallback;
    private VideoFormat _canvasFormat;
    private byte[]? _upload;
    private long _seenVersion;
    private uint _texture, _blitProgram, _blitVao;
    private int _cornersLocation = -1, _opacityLocation = -1, _sceneLocation = -1;
    private int _canvasWidth, _canvasHeight;
    private bool _hasFrame;
    private bool _loggedFirstRender;
    private volatile bool _disposed;
    private bool _failed;

    internal ProjectMFrameBlitSurface(ProjectMVisualSource source, ProjectMOffscreenRenderer renderer)
    {
        _source = source;
        _renderer = renderer;
    }

    public unsafe void ConfigureGl(GL gl, VideoFormat canvas)
    {
        _canvasWidth = canvas.Width;
        _canvasHeight = canvas.Height;
        _canvasFormat = canvas;
        if (_renderer.Failed)
        {
            EnsureFallback(gl);
            return;
        }
        if (_texture != 0 || _failed)
            return; // canvas re-configure: only the blit target changed

        try
        {
            _upload = new byte[_renderer.Width * _renderer.Height * 4];

            gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);

            _texture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _texture);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)_renderer.Width, (uint)_renderer.Height, 0, GLEnum.Bgra, GLEnum.UnsignedByte, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            _blitProgram = CompileProgram(gl, BlitVs, BlitFs);
            _blitVao = gl.GenVertexArray();
            _cornersLocation = gl.GetUniformLocation(_blitProgram, "uCorners");
            _opacityLocation = gl.GetUniformLocation(_blitProgram, "uOpacity");
            _sceneLocation = gl.GetUniformLocation(_blitProgram, "uScene");
        }
        catch (Exception ex)
        {
            _failed = true;
            Trace.LogError(ex, "frame-blit surface configuration failed - visualizer renders nothing");
        }
    }

    public unsafe void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity)
    {
        if (_renderer.Failed)
        {
            EnsureFallback(gl);
            _fallback?.Render(gl, targetFbo, masterTime, transform, opacity);
            return;
        }
        if (_disposed || _failed || _texture == 0 || _upload is null)
            return;
        if (!_loggedFirstRender)
        {
            // One line per surface instance: proves the composition pump IS compositing this layer
            // (its absence after an attach = the pump never rendered the surface at all).
            _loggedFirstRender = true;
            Trace.LogInformation("frame-blit surface: first composite (canvas {W}x{H}, renderer failed={Failed})",
                _canvasWidth, _canvasHeight, _renderer.Failed);
        }

        // Pull the renderer's newest frame (cheap copy under its lock); keep showing the previous
        // texture content when nothing new landed this tick - the stream stays visually continuous.
        if (_renderer.TryCopyLatestFrame(_upload, ref _seenVersion))
        {
            gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
            gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
            gl.BindTexture(TextureTarget.Texture2D, _texture);
            fixed (byte* src = _upload)
            {
                gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0,
                    (uint)_renderer.Width, (uint)_renderer.Height, GLEnum.Bgra, GLEnum.UnsignedByte, src);
            }

            _hasFrame = true;
        }

        if (!_hasFrame)
            return; // renderer hasn't produced anything yet - draw nothing rather than garbage

        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.CullFace);
        gl.Enable(EnableCap.Blend);
        gl.BlendFuncSeparate(
            BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha,
            BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

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
        gl.BindTexture(TextureTarget.Texture2D, _texture);
        gl.BindVertexArray(_blitVao);
        gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        gl.BindVertexArray(0);
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
            throw new InvalidOperationException($"frame-blit program link failed: {gl.GetProgramInfoLog(program)}");
        return program;
    }

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

    public void Dispose()
    {
        _disposed = true;
        _fallback?.Dispose();
    }

    private void EnsureFallback(GL gl)
    {
        if (_disposed || _fallback is not null)
            return;
        Trace.LogWarning("continuous projectM renderer unavailable - falling back to compositor-context rendering");
        _fallback = new ProjectMGlLayerSurface(_source);
        _fallback.ConfigureGl(gl, _canvasFormat);
    }

    public void ReleaseGl(GL gl)
    {
        _disposed = true;
        _fallback?.ReleaseGl(gl);
        _fallback = null;
        if (_texture != 0) { gl.DeleteTexture(_texture); _texture = 0; }
        if (_blitProgram != 0) { gl.DeleteProgram(_blitProgram); _blitProgram = 0; }
        if (_blitVao != 0) { gl.DeleteVertexArray(_blitVao); _blitVao = 0; }
    }

    private const string BlitVs = """
        #version 330 core
        uniform vec2 uCorners[4];
        out vec2 vUv;
        void main()
        {
            // Strip order TL,TR,BL,BR; the frame keeps GL FBO row order (Y-down vs texture space), so flip V.
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
