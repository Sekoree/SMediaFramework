using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using S.Media.Effects;
using S.Media.Core.Video;
using S.Media.OpenGL;
using Silk.NET.OpenGL;
using GlPixelFormat = Silk.NET.OpenGL.PixelFormat;
using GlPixelType = Silk.NET.OpenGL.PixelType;
using GlInternalFormat = Silk.NET.OpenGL.InternalFormat;
using CorePixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Effects.OpenGL;

/// <summary>
/// GL 3.3 implementation of <see cref="IVideoCompositor"/>. Renders each layer to an off-screen
/// FBO and reads back to a BGRA32 <see cref="VideoFrame"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Threading:</strong> the caller must make a GL context current on the same thread before
/// constructing the compositor and before every <see cref="Composite"/>. Disposal must run on that
/// thread too. <c>VideoCompositorSource.TryReadNextFrame</c> is what calls <see cref="Composite"/>, so
/// the downstream consumer of the output's output must run on the GL thread.
/// </para>
/// <para>
/// <strong>Layer pixel-format support:</strong> accepts every format <see cref="YuvVideoRenderer"/> accepts
/// (BGRA32 / RGBA32 / RGB24 / BGR24 / I420 / Yv12 / NV12 / NV21 / Yuv422P / Yuv422P10Le / Yuv422P12Le /
/// Yuv444P / Yuv444P10Le / Yuv444P12Le / Yuv420P10Le / Yuv420P12Le / P010 / P016 / Uyvy / Yuyv / Argb32 /
/// Abgr32 / Gray8 / Gray16, plus the full YUVA family at 8 / 10 / 12 / 16 bit). BGRA32 layers run a direct
/// upload to an RGBA8 texture; every other format runs a per-layer YUV → RGB pre-pass into a cached RGBA16F
/// intermediate texture so 10 / 12 / 16-bit precision survives through blending. The composite shader samples
/// the intermediate identically to a BGRA32 layer — transform, opacity, and blend mode are unchanged.
/// </para>
/// <para>
/// <strong>Output:</strong> BGRA32 via <c>glReadPixels</c>. A single 8-bit truncation lands at the very end —
/// the intermediate working space stays 16-bit per channel until the final readback. GPU-resident output is
/// not in this batch.
/// </para>
/// <para>
/// <strong>Per-layer caches:</strong>
/// <list type="bullet">
/// <item>BGRA32 textures keyed on <c>(Width, Height)</c>.</item>
/// <item>YUV intermediate textures + FBOs keyed on <c>(Width, Height)</c> (any YUV format at the same size shares one).</item>
/// <item>YUV renderers keyed on <c>(PixelFormat, Width, Height)</c>.</item>
/// </list>
/// Layers that vary in size every frame will thrash; steady-state UI is fine.
/// </para>
/// <para>
/// <strong>State hygiene:</strong> <see cref="Composite"/> saves and restores the current framebuffer
/// binding, viewport, program, VAO, blend enable/func, and scissor enable so it can be embedded
/// inside another output's render path without trashing host state.
/// </para>
/// </remarks>
public sealed class GlVideoCompositor : IVideoCompositor
{
    private static readonly ConcurrentDictionary<string, string> ShaderSourceCache = new(StringComparer.Ordinal);
    private const string ProgramCacheKey = "GlVideoCompositor:composite_layer";

    private readonly GL _gl;
    private readonly GlCompositorOutputPrecision _outputPrecision;
    private VideoFormat _output;
    private int _outputStride;
    private int _outputByteCount;
    private GlInternalFormat _fboInternalFormat;
    private GlPixelFormat _readPixelFormat;
    private GlPixelType _readPixelType;
    private uint _program;
    private int _uXformLoc = -1;
    private int _uOpacityLoc = -1;
    private int _uBlendKindLoc = -1;
    private int _uLayerLoc = -1;
    private uint _vao;
    private uint _vbo;
    private uint _fbo;
    private uint _fboTexture;
    /// <summary>Per-(W, H) RGBA8 textures for BGRA32 layers.</summary>
    private readonly Dictionary<(int W, int H), uint> _layerTextures = new();
    /// <summary>Per-(PixelFormat, W, H) YUV → RGB pre-pass renderers for non-BGRA32 layers.</summary>
    private readonly Dictionary<(CorePixelFormat Fmt, int W, int H), YuvVideoRenderer> _yuvRenderers = new();
    /// <summary>Per-(W, H) RGBA16F intermediate texture + FBO that holds the YUV-converted layer.</summary>
    private readonly Dictionary<(int W, int H), (uint Tex, uint Fbo)> _yuvIntermediates = new();
    /// <summary>Cached accepted-formats array — mirrors <see cref="YuvVideoRenderer.SupportedPixelFormats"/>.</summary>
    private static readonly CorePixelFormat[] AcceptedFormatsArr = YuvVideoRenderer.SupportedPixelFormats.ToArray();
    private bool _configured;
    private bool _disposed;

    /// <param name="gl">GL context already made current on the calling thread.</param>
    /// <param name="output">Initial output format (BGRA32, Rgba16, or Rgba16F).</param>
    /// <param name="outputPrecision">FBO internal format / readback type when <paramref name="output"/> is high precision.</param>
    public GlVideoCompositor(GL gl, VideoFormat output, GlCompositorOutputPrecision outputPrecision = GlCompositorOutputPrecision.Rgba8)
    {
        ArgumentNullException.ThrowIfNull(gl);
        _gl = gl;
        _outputPrecision = outputPrecision;
        BuildPipeline();
        Configure(output);
    }

    public VideoFormat OutputFormat => _output;
    public IReadOnlyList<CorePixelFormat> AcceptedLayerPixelFormats => AcceptedFormatsArr;

    public void Configure(VideoFormat output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var expectedPf = OutputPixelFormatForPrecision(_outputPrecision);
        if (output.PixelFormat != expectedPf)
            throw new ArgumentException(
                $"GlVideoCompositor output precision {_outputPrecision} requires {expectedPf}; got {output.PixelFormat}.",
                nameof(output));
        ApplyOutputPrecisionLayout(_outputPrecision);
        if (output.Width <= 0 || output.Height <= 0)
            throw new ArgumentException(
                $"output dimensions must be positive (got {output.Width}x{output.Height}).", nameof(output));

        if (_configured && _output.Width == output.Width && _output.Height == output.Height)
        {
            _output = output;
            return;
        }

        _output = output;
        ApplyOutputPrecisionLayout(_outputPrecision);
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
        _gl.GetInteger(GetPName.PackAlignment, out var savedPackAlignment);
        _gl.GetInteger(GetPName.PackRowLength, out var savedPackRowLength);

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
                var fmt = layer.Frame.Format.PixelFormat;
                if (!GlVideoFormatSupport.TryGetRecipe(fmt, out _))
                    throw new InvalidOperationException(
                        $"GlVideoCompositor layer {i}: pixel format {fmt} has no GL recipe — accepted set is YuvVideoRenderer.SupportedPixelFormats.");
                var opacity = Math.Clamp(layer.Opacity, 0f, 1f);
                if (opacity <= 0f) continue;
                DrawLayer(layer, opacity);
            }

            // --- Readback. ---
            var buffer = ArrayPool<byte>.Shared.Rent(_outputByteCount);
            _gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
            _gl.PixelStore(PixelStoreParameter.PackRowLength, 0);
            fixed (byte* p = buffer)
                _gl.ReadPixels(0, 0, (uint)_output.Width, (uint)_output.Height, _readPixelFormat, _readPixelType, p);

            var plane = new ReadOnlyMemory<byte>(buffer, 0, _outputByteCount);
            var owned = buffer;
            return new VideoFrame(
                presentationTime,
                _output,
                plane,
                _outputStride,
                release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(owned, clearArray: false)));
        }
        finally
        {
            // --- Restore host state. ---
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, savedUnpackAlignment);
            _gl.PixelStore(PixelStoreParameter.UnpackRowLength, savedUnpackRowLength);
            // glReadPixels above mutated GL_PACK_* — restore so embedding the compositor
            // in another GL renderer can't corrupt that renderer's later readbacks.
            _gl.PixelStore(PixelStoreParameter.PackAlignment, savedPackAlignment);
            _gl.PixelStore(PixelStoreParameter.PackRowLength, savedPackRowLength);
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

        // Pick the per-layer source texture: BGRA32 uploads direct to RGBA8; everything else runs the YUV
        // pre-pass into a cached RGBA16F intermediate so high-bit precision survives into the composite.
        if (src.Format.PixelFormat == CorePixelFormat.Bgra32)
        {
            PrepareBgra32LayerTexture(src, srcW, srcH);
        }
        else
        {
            PrepareYuvLayerIntermediate(src, srcW, srcH);
        }

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

    /// <summary>
    /// Direct BGRA32 upload into a cached RGBA8 texture, then bind it to texture unit 0 so the
    /// composite shader samples it as the layer source.
    /// </summary>
    private unsafe void PrepareBgra32LayerTexture(VideoFrame src, int srcW, int srcH)
    {
        var srcStride = src.Strides[0];
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

        var rowLenPixels = srcStride / 4;
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, rowLenPixels);
        var span = src.Planes[0].Span;
        fixed (byte* p = span)
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)srcW, (uint)srcH,
                GlPixelFormat.Bgra, GlPixelType.UnsignedByte, p);
    }

    /// <summary>
    /// Run a per-layer <see cref="YuvVideoRenderer"/> pre-pass into a cached RGBA16F intermediate so the
    /// composite stage works in float precision regardless of the source's bit depth. Rebinds the compositor's
    /// FBO + viewport + program + VAO + texture-unit-0 to the intermediate so the caller can continue with the
    /// transform / opacity / blend setup as if this were a BGRA32 layer.
    /// </summary>
    private unsafe void PrepareYuvLayerIntermediate(VideoFrame src, int srcW, int srcH)
    {
        var fmt = src.Format.PixelFormat;
        var key = (fmt, srcW, srcH);
        if (!_yuvRenderers.TryGetValue(key, out var renderer))
        {
            renderer = new YuvVideoRenderer(_gl, src.Format);
            _yuvRenderers[key] = renderer;
        }

        // Pick the YUV → RGB matrix from the per-frame hint (Phase 5 metadata).
        renderer.ColorSpace = YuvColorSpace.FromHint(
            src.Metadata.ColorSpace,
            src.Metadata.ColorRange,
            srcH);

        if (!_yuvIntermediates.TryGetValue((srcW, srcH), out var intermediate))
        {
            intermediate = CreateRgba16fIntermediate(srcW, srcH);
            _yuvIntermediates[(srcW, srcH)] = intermediate;
        }

        // Pre-pass: bind the intermediate FBO, set viewport to source size, clear, upload + render YUV → RGB.
        // YuvVideoRenderer's Render() changes program / VAO / viewport — we restore them below.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, intermediate.Fbo);
        _gl.Viewport(0, 0, (uint)srcW, (uint)srcH);
        _gl.Disable(EnableCap.Blend);
        _gl.ClearColor(0f, 0f, 0f, 0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        renderer.Upload(src);
        renderer.Render(srcW, srcH);

        // Rebind compositor state. Blend / scissor / pixel-store stay as the outer Composite set them; we just
        // restore framebuffer, viewport, program, VAO, and the layer texture binding.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)_output.Width, (uint)_output.Height);
        _gl.UseProgram(_program);
        _gl.BindVertexArray(_vao);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, intermediate.Tex);
    }

    private unsafe (uint Tex, uint Fbo) CreateRgba16fIntermediate(int width, int height)
    {
        var tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, GlInternalFormat.Rgba16f, (uint)width, (uint)height, 0,
            GlPixelFormat.Rgba, GlPixelType.HalfFloat, null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        var fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, tex, 0);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            _gl.DeleteFramebuffer(fbo);
            _gl.DeleteTexture(tex);
            throw new InvalidOperationException(
                $"GlVideoCompositor RGBA16F intermediate FBO incomplete: {status}");
        }
        return (tex, fbo);
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
        _gl.TexImage2D(TextureTarget.Texture2D, 0, _fboInternalFormat,
            (uint)_output.Width, (uint)_output.Height, 0,
            GlPixelFormat.Rgba, PixelTypeForInternalFormat(_fboInternalFormat), null);
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

    private static CorePixelFormat OutputPixelFormatForPrecision(GlCompositorOutputPrecision precision) => precision switch
    {
        GlCompositorOutputPrecision.Rgba8 => CorePixelFormat.Bgra32,
        GlCompositorOutputPrecision.Rgba16 => CorePixelFormat.Rgba16,
        GlCompositorOutputPrecision.Rgba16F => CorePixelFormat.Rgba16F,
        _ => CorePixelFormat.Bgra32,
    };

    private void ApplyOutputPrecisionLayout(GlCompositorOutputPrecision precision)
    {
        switch (precision)
        {
            case GlCompositorOutputPrecision.Rgba16:
                _fboInternalFormat = GlInternalFormat.Rgba16;
                _readPixelFormat = GlPixelFormat.Rgba;
                _readPixelType = GlPixelType.UnsignedShort;
                _outputStride = _output.Width * 8;
                break;
            case GlCompositorOutputPrecision.Rgba16F:
                _fboInternalFormat = GlInternalFormat.Rgba16f;
                _readPixelFormat = GlPixelFormat.Rgba;
                _readPixelType = GlPixelType.HalfFloat;
                _outputStride = _output.Width * 8;
                break;
            default:
                _fboInternalFormat = GlInternalFormat.Rgba8;
                _readPixelFormat = GlPixelFormat.Bgra;
                _readPixelType = GlPixelType.UnsignedByte;
                _outputStride = _output.Width * 4;
                break;
        }

        _outputByteCount = _outputStride * _output.Height;
    }

    private static GlPixelType PixelTypeForInternalFormat(GlInternalFormat internalFormat) => internalFormat switch
    {
        GlInternalFormat.Rgba16 => GlPixelType.UnsignedShort,
        GlInternalFormat.Rgba16f => GlPixelType.HalfFloat,
        _ => GlPixelType.UnsignedByte,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var (_, tex) in _layerTextures)
            _gl.DeleteTexture(tex);
        _layerTextures.Clear();
        foreach (var (_, renderer) in _yuvRenderers)
            renderer.Dispose();
        _yuvRenderers.Clear();
        foreach (var (_, inter) in _yuvIntermediates)
        {
            _gl.DeleteFramebuffer(inter.Fbo);
            _gl.DeleteTexture(inter.Tex);
        }
        _yuvIntermediates.Clear();
        if (_fboTexture != 0) { _gl.DeleteTexture(_fboTexture); _fboTexture = 0; }
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _fbo = 0; }
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
        if (_program != 0) { SharedGlProgramCache.Release(_gl, ProgramCacheKey); _program = 0; }
    }
}
