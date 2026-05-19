using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using S.Media.Core.Video;
using GlPixelFormat = Silk.NET.OpenGL.PixelFormat;
using GlPixelType = Silk.NET.OpenGL.PixelType;
using GlInternalFormat = Silk.NET.OpenGL.InternalFormat;
using CorePixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.OpenGL;

/// <summary>
/// GL 3.3 implementation of <see cref="IVideoCompositor"/>. Renders each layer to an off-screen
/// FBO and reads back to a BGRA32 <see cref="VideoFrame"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Threading:</strong> the caller must make a GL context current on the same thread before
/// constructing the compositor and before every <see cref="Composite"/>. Disposal must run on that
/// thread too. <c>CompositorVideoSink.TryReadNextFrame</c> is what calls <see cref="Composite"/>, so
/// the downstream consumer of the sink's output must run on the GL thread.
/// </para>
/// <para>
/// <strong>First-cut limitations:</strong>
/// <list type="bullet">
/// <item>BGRA32 layers only (matches <c>CpuVideoCompositor</c>). YUV layers must be converted upstream
/// — the existing <c>VideoRouter</c> branch-converter path handles this transparently.</item>
/// <item>Output is BGRA32 via <c>glReadPixels</c>. GPU-resident output (e.g. a frame backed by a GL
/// texture handle) would let downstream GL sinks skip the readback; not in this batch.</item>
/// <item>Per-layer texture cache is keyed on <c>(Width, Height)</c>. Layers that vary in size every
/// frame will thrash; for steady-state UI this is fine.</item>
/// </list>
/// </para>
/// <para>
/// <strong>State hygiene:</strong> <see cref="Composite"/> saves and restores the current framebuffer
/// binding, viewport, program, VAO, blend enable/func, and scissor enable so it can be embedded
/// inside another sink's render path without trashing host state.
/// </para>
/// </remarks>
public sealed class GlVideoCompositor : IVideoCompositor
{
    private static readonly CorePixelFormat[] AcceptedFormatsArr = [CorePixelFormat.Bgra32];
    private static readonly ConcurrentDictionary<string, string> ShaderSourceCache = new(StringComparer.Ordinal);
    private const string ProgramCacheKey = "GlVideoCompositor:composite_layer";

    private readonly GL _gl;
    private VideoFormat _output;
    private int _outputStride;
    private int _outputByteCount;
    private uint _program;
    private int _uXformLoc = -1;
    private int _uOpacityLoc = -1;
    private int _uBlendKindLoc = -1;
    private int _uLayerLoc = -1;
    private uint _vao;
    private uint _vbo;
    private uint _fbo;
    private uint _fboTexture;
    private readonly Dictionary<(int W, int H), uint> _layerTextures = new();
    private bool _configured;
    private bool _disposed;

    /// <param name="gl">GL context already made current on the calling thread.</param>
    /// <param name="output">Initial output format. Pixel format must be BGRA32.</param>
    public GlVideoCompositor(GL gl, VideoFormat output)
    {
        ArgumentNullException.ThrowIfNull(gl);
        _gl = gl;
        BuildPipeline();
        Configure(output);
    }

    public VideoFormat OutputFormat => _output;
    public IReadOnlyList<CorePixelFormat> AcceptedLayerPixelFormats => AcceptedFormatsArr;

    public void Configure(VideoFormat output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (output.PixelFormat != CorePixelFormat.Bgra32)
            throw new ArgumentException(
                $"GlVideoCompositor only outputs BGRA32; got {output.PixelFormat}.", nameof(output));
        if (output.Width <= 0 || output.Height <= 0)
            throw new ArgumentException(
                $"output dimensions must be positive (got {output.Width}x{output.Height}).", nameof(output));

        if (_configured && _output.Width == output.Width && _output.Height == output.Height)
        {
            _output = output;
            return;
        }

        _output = output;
        _outputStride = output.Width * 4;
        _outputByteCount = _outputStride * output.Height;
        RecreateFbo();
        _configured = true;
    }

    public unsafe VideoFrame Composite(IReadOnlyList<CompositorLayer> layersBackToFront, TimeSpan presentationTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(layersBackToFront);
        if (!_configured)
            throw new InvalidOperationException("GlVideoCompositor must be Configure()d before Composite.");

        // --- Save host GL state we'll touch. ---
        _gl.GetInteger(GetPName.DrawFramebufferBinding, out var savedFbo);
        _gl.GetInteger(GetPName.CurrentProgram, out var savedProgram);
        _gl.GetInteger(GetPName.VertexArrayBinding, out var savedVao);
        _gl.GetInteger(GetPName.ActiveTexture, out var savedActiveTexture);
        Span<int> savedViewport = stackalloc int[4];
        _gl.GetInteger(GetPName.Viewport, savedViewport);
        var savedBlendEnabled = _gl.IsEnabled(EnableCap.Blend);
        _gl.GetInteger(GetPName.BlendSrcRgb, out var savedBlendSrcRgb);
        _gl.GetInteger(GetPName.BlendDstRgb, out var savedBlendDstRgb);
        _gl.GetInteger(GetPName.BlendSrcAlpha, out var savedBlendSrcAlpha);
        _gl.GetInteger(GetPName.BlendDstAlpha, out var savedBlendDstAlpha);
        var savedScissor = _gl.IsEnabled(EnableCap.ScissorTest);
        _gl.GetInteger(GetPName.UnpackAlignment, out var savedUnpackAlignment);
        _gl.GetInteger(GetPName.UnpackRowLength, out var savedUnpackRowLength);

        try
        {
            // --- Bind compositor state. ---
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            _gl.Viewport(0, 0, (uint)_output.Width, (uint)_output.Height);
            if (savedScissor) _gl.Disable(EnableCap.ScissorTest);
            _gl.UseProgram(_program);
            _gl.BindVertexArray(_vao);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.Uniform1(_uLayerLoc, 0);

            // Clear to transparent black.
            _gl.ClearColor(0f, 0f, 0f, 0f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);

            for (var i = 0; i < layersBackToFront.Count; i++)
            {
                var layer = layersBackToFront[i];
                if (layer.Frame.Format.PixelFormat != CorePixelFormat.Bgra32)
                    throw new InvalidOperationException(
                        $"GlVideoCompositor layer {i}: only BGRA32 accepted, got {layer.Frame.Format.PixelFormat}.");
                var opacity = Math.Clamp(layer.Opacity, 0f, 1f);
                if (opacity <= 0f) continue;
                DrawLayer(layer, opacity);
            }

            // --- Readback. ---
            var buffer = ArrayPool<byte>.Shared.Rent(_outputByteCount);
            _gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
            _gl.PixelStore(PixelStoreParameter.PackRowLength, 0);
            fixed (byte* p = buffer)
                _gl.ReadPixels(0, 0, (uint)_output.Width, (uint)_output.Height, GlPixelFormat.Bgra, GlPixelType.UnsignedByte, p);

            var plane = new ReadOnlyMemory<byte>(buffer, 0, _outputByteCount);
            var owned = buffer;
            return new VideoFrame(
                presentationTime,
                _output,
                plane,
                _outputStride,
                release: () => ArrayPool<byte>.Shared.Return(owned, clearArray: false));
        }
        finally
        {
            // --- Restore host state. ---
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, savedUnpackAlignment);
            _gl.PixelStore(PixelStoreParameter.UnpackRowLength, savedUnpackRowLength);
            if (!savedBlendEnabled) _gl.Disable(EnableCap.Blend);
            _gl.BlendFuncSeparate((BlendingFactor)savedBlendSrcRgb, (BlendingFactor)savedBlendDstRgb,
                (BlendingFactor)savedBlendSrcAlpha, (BlendingFactor)savedBlendDstAlpha);
            if (savedScissor) _gl.Enable(EnableCap.ScissorTest);
            _gl.BindVertexArray((uint)savedVao);
            _gl.UseProgram((uint)savedProgram);
            _gl.ActiveTexture((TextureUnit)savedActiveTexture);
            _gl.Viewport(savedViewport[0], savedViewport[1], (uint)savedViewport[2], (uint)savedViewport[3]);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)savedFbo);
        }
    }

    private unsafe void DrawLayer(CompositorLayer layer, float opacity)
    {
        var src = layer.Frame;
        var srcW = src.Format.Width;
        var srcH = src.Format.Height;
        var srcStride = src.Strides[0];

        // Acquire / refresh the per-layer texture.
        if (!_layerTextures.TryGetValue((srcW, srcH), out var tex))
        {
            tex = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, tex);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, GlInternalFormat.Rgba8, (uint)srcW, (uint)srcH, 0,
                GlPixelFormat.Bgra, GlPixelType.UnsignedByte, null);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _layerTextures[(srcW, srcH)] = tex;
        }
        else
        {
            _gl.BindTexture(TextureTarget.Texture2D, tex);
        }

        // Upload BGRA32 plane. Match alignment and row length to the source's stride.
        var rowLenPixels = srcStride / 4;
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, rowLenPixels);
        var span = src.Planes[0].Span;
        fixed (byte* p = span)
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)srcW, (uint)srcH,
                GlPixelFormat.Bgra, GlPixelType.UnsignedByte, p);

        // Bake the per-layer 3x3 transform: uv ∈ [0,1] → ndc ∈ [-1,1] with Y flipped so glReadPixels
        // produces a top-down output buffer.
        var t = layer.Transform;
        var outW = (float)_output.Width;
        var outH = (float)_output.Height;
        // matrix is column-major when sent via UniformMatrix3, but we use *transpose=false convention
        // by laying out per-column. Easier: assemble the 9-float array explicitly.
        // m[col,row]: column-major storage.
        Span<float> m = stackalloc float[9];
        // Col 0: derivative w.r.t. uv.x — (M11*srcW)*(2/outW), -(M21*srcW)*(2/outH), 0
        m[0] = (t.M11 * srcW) * (2f / outW);
        m[1] = -(t.M21 * srcW) * (2f / outH);
        m[2] = 0f;
        // Col 1: derivative w.r.t. uv.y — (M12*srcH)*(2/outW), -(M22*srcH)*(2/outH), 0
        m[3] = (t.M12 * srcH) * (2f / outW);
        m[4] = -(t.M22 * srcH) * (2f / outH);
        m[5] = 0f;
        // Col 2: translation — (2*Tx/outW) - 1, 1 - (2*Ty/outH), 1
        m[6] = (2f * t.Tx / outW) - 1f;
        m[7] = 1f - (2f * t.Ty / outH);
        m[8] = 1f;
        _gl.UniformMatrix3(_uXformLoc, 1, false, m);
        _gl.Uniform1(_uOpacityLoc, opacity);

        switch (layer.BlendMode)
        {
            case BlendMode.Source:
                _gl.Disable(EnableCap.Blend);
                _gl.Uniform1(_uBlendKindLoc, 0);
                break;
            case BlendMode.SourceOver:
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
                _gl.Uniform1(_uBlendKindLoc, 0);
                break;
            case BlendMode.Multiply:
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
                _gl.Uniform1(_uBlendKindLoc, 1);
                break;
            default:
                throw new NotSupportedException($"BlendMode {layer.BlendMode} not supported.");
        }

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    private void BuildPipeline()
    {
        var vertSrc = LoadShaderCached("composite_layer.vert.glsl");
        var fragSrc = LoadShaderCached("composite_layer.frag.glsl");
        _program = SharedGlProgramCache.Acquire(ProgramCacheKey, _gl, _ => LinkProgram(vertSrc, fragSrc));

        _uXformLoc = _gl.GetUniformLocation(_program, "uXform");
        _uOpacityLoc = _gl.GetUniformLocation(_program, "uOpacity");
        _uBlendKindLoc = _gl.GetUniformLocation(_program, "uBlendKind");
        _uLayerLoc = _gl.GetUniformLocation(_program, "uLayer");
        if (_uXformLoc < 0 || _uOpacityLoc < 0 || _uBlendKindLoc < 0 || _uLayerLoc < 0)
            throw new InvalidOperationException("composite_layer program missing required uniforms.");

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);
        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        Span<float> verts = stackalloc float[12]
        {
            0f, 0f,
            1f, 0f,
            1f, 1f,
            0f, 0f,
            1f, 1f,
            0f, 1f,
        };
        unsafe
        {
            fixed (float* p = verts)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        }
        _gl.EnableVertexAttribArray(0);
        unsafe { _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, (uint)(2 * sizeof(float)), (void*)0); }
        _gl.BindVertexArray(0);
    }

    private unsafe void RecreateFbo()
    {
        if (_fboTexture != 0) { _gl.DeleteTexture(_fboTexture); _fboTexture = 0; }
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _fbo = 0; }

        _fboTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _fboTexture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, GlInternalFormat.Rgba8,
            (uint)_output.Width, (uint)_output.Height, 0,
            GlPixelFormat.Rgba, GlPixelType.UnsignedByte, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _fboTexture, 0);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
            throw new InvalidOperationException($"GlVideoCompositor FBO incomplete: {status}");
    }

    private uint LinkProgram(string vertSrc, string fragSrc)
    {
        var vs = CompileShader(ShaderType.VertexShader, vertSrc);
        var fs = CompileShader(ShaderType.FragmentShader, fragSrc);
        try
        {
            var program = _gl.CreateProgram();
            _gl.AttachShader(program, vs);
            _gl.AttachShader(program, fs);
            _gl.LinkProgram(program);
            _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var status);
            if (status == 0)
            {
                var log = _gl.GetProgramInfoLog(program);
                _gl.DeleteProgram(program);
                throw new InvalidOperationException($"composite_layer link failed: {log}");
            }
            _gl.DetachShader(program, vs);
            _gl.DetachShader(program, fs);
            return program;
        }
        finally
        {
            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);
        }
    }

    private uint CompileShader(ShaderType type, string source)
    {
        var s = _gl.CreateShader(type);
        _gl.ShaderSource(s, source);
        _gl.CompileShader(s);
        _gl.GetShader(s, ShaderParameterName.CompileStatus, out var status);
        if (status == 0)
        {
            var log = _gl.GetShaderInfoLog(s);
            _gl.DeleteShader(s);
            throw new InvalidOperationException($"composite_layer {type} compile failed: {log}");
        }
        return s;
    }

    private static string LoadShaderCached(string fileName) =>
        ShaderSourceCache.GetOrAdd(fileName, LoadShaderUncached);

    private static string LoadShaderUncached(string fileName)
    {
        var asm = typeof(GlVideoCompositor).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".Shaders.{fileName}", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"shader resource '{fileName}' not embedded");
        using var s = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"shader '{resourceName}' unavailable");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (_, tex) in _layerTextures)
            _gl.DeleteTexture(tex);
        _layerTextures.Clear();
        if (_fboTexture != 0) { _gl.DeleteTexture(_fboTexture); _fboTexture = 0; }
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _fbo = 0; }
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
        if (_program != 0) { SharedGlProgramCache.Release(_gl, ProgramCacheKey); _program = 0; }
    }
}
