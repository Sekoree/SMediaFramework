using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using S.Media.Compositor;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Gpu;
using Silk.NET.OpenGL;
using GlPixelFormat = Silk.NET.OpenGL.PixelFormat;
using GlPixelType = Silk.NET.OpenGL.PixelType;
using GlInternalFormat = Silk.NET.OpenGL.InternalFormat;
using CorePixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Compositor.OpenGL;

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
/// binding, viewport, program, VAO, pixel-pack buffer, blend enable/func, and scissor enable so it can
/// be embedded inside another output's render path without trashing host state.
/// </para>
/// </remarks>
public sealed class GlVideoCompositor : IWarpPassVideoCompositor
{
    /// <summary>Immutable warp-pass snapshot (swapped atomically by <see cref="SetWarpPass"/>;
    /// read once per <see cref="Composite"/> so a mid-frame swap can't tear).</summary>
    private sealed record WarpPassState(VideoFormat Output, WarpSection[] Sections);

    private sealed class PboReadbackException(string message) : InvalidOperationException(message);

    private sealed class PboReadbackState(WarpOutputRequest[] shape)
    {
        public WarpOutputRequest[] Shape { get; } = shape;
        public PboOutputSlot[] Outputs { get; } = CreateSlots(shape.Length);
        public PboReadback[]? Pending { get; set; }

        private static PboOutputSlot[] CreateSlots(int count)
        {
            var slots = new PboOutputSlot[count];
            for (var i = 0; i < slots.Length; i++)
                slots[i] = new PboOutputSlot();
            return slots;
        }
    }

    private sealed class PboOutputSlot
    {
        public readonly uint[] Buffers = new uint[2];
        public readonly int[] Capacities = new int[2];
        public int NextBufferIndex;
    }

    private sealed class PboReadback
    {
        public required uint Buffer { get; init; }
        public required nint Sync { get; set; }
        public required VideoFormat Format { get; init; }
        public required int Stride { get; init; }
        public required int ByteCount { get; init; }
    }

    private volatile WarpPassState? _warpPass;
    private uint _warpFbo;
    private uint _warpFboTexture;
    private (int W, int H) _warpFboSize;

    /// <summary>One mesh section's uploaded tessellation, keyed back to its warp-section index.</summary>
    private readonly record struct MeshDraw(int SectionIndex, uint Vao, uint Vbo, uint Ebo, int IndexCount);

    // Mesh warp (Phase 4): a lazily linked second program (composite_mesh.vert + the shared fragment
    // shader) plus per-section tessellated buffers. Buffers are rebuilt only when the warp snapshot
    // changes (control-point edits), never per frame; all GL work stays on the Composite thread.
    private const string MeshProgramCacheKey = "GlVideoCompositor:composite_mesh";
    private uint _meshProgram;
    private int _meshXformLoc = -1;
    private int _meshCropLoc = -1;
    private int _meshOpacityLoc = -1;
    private int _meshBlendKindLoc = -1;
    private int _meshLayerLoc = -1;
    private int _meshLayerFlipVLoc = -1;
    private WarpSection[]? _meshBuffersSections;
    private readonly List<MeshDraw> _meshDraws = new();

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
    private int _uCropLoc = -1;
    private int _uOpacityLoc = -1;
    private int _uBlendKindLoc = -1;
    private int _uLayerLoc = -1;
    private int _uLayerFlipVLoc = -1;
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
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly ConcurrentQueue<Action> _deferredExternalImageReleases = new();
    private PboReadbackState? _multiPboReadback;
    private bool _multiPboReadbackUnavailable;
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
        DrainDeferredExternalImageReleases();
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
        DrainDeferredExternalImageReleases();
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
        _gl.GetInteger(GetPName.PixelPackBufferBinding, out var savedPixelPackBuffer);

        try
        {
            RenderLayersToCanvas(layersBackToFront, savedScissor);

            // --- Optional warp pass: re-render the composited canvas texture into the warp FBO
            // as N warped sections, so output mapping never leaves the GPU (the chained-compositor
            // alternative costs an extra readback + re-upload per frame). ---
            var warp = _warpPass;
            var frameFormat = _output;
            var readW = _output.Width;
            var readH = _output.Height;
            var readStride = _outputStride;
            var readBytes = _outputByteCount;
            if (warp is not null)
            {
                RunWarpPass(warp);
                frameFormat = warp.Output;
                readW = warp.Output.Width;
                readH = warp.Output.Height;
                readStride = readW * (_outputStride / _output.Width);
                readBytes = readStride * readH;
            }
            else if (_meshDraws.Count > 0)
            {
                // Warp cleared since the last frame — free the stale mesh buffers here, on the GL
                // thread (SetWarpPass may run on any thread and must not touch GL objects).
                ReleaseMeshBuffers();
            }

            return ReadCurrentFramebuffer(frameFormat, readW, readH, readStride, readBytes, presentationTime);
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
            _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, (uint)savedPixelPackBuffer);
            if (savedBlendEnabled) _gl.Enable(EnableCap.Blend);
            else _gl.Disable(EnableCap.Blend);
            _gl.BlendFuncSeparate((BlendingFactor)savedBlendSrcRgb, (BlendingFactor)savedBlendDstRgb,
                (BlendingFactor)savedBlendSrcAlpha, (BlendingFactor)savedBlendDstAlpha);
            if (savedScissor) _gl.Enable(EnableCap.ScissorTest);
            _gl.BindVertexArray((uint)savedVao);
            _gl.UseProgram((uint)savedProgram);
            _gl.ActiveTexture((TextureUnit)savedActiveTexture);
            _gl.Viewport(savedViewport[0], savedViewport[1], (uint)savedViewport[2], (uint)savedViewport[3]);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)savedFbo);
            DrainDeferredExternalImageReleases();
        }
    }

    /// <inheritdoc />
    public unsafe IReadOnlyList<VideoFrame> CompositeMulti(
        IReadOnlyList<CompositorLayer> layersBackToFront,
        IReadOnlyList<WarpOutputRequest> outputs,
        TimeSpan presentationTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DrainDeferredExternalImageReleases();
        ArgumentNullException.ThrowIfNull(layersBackToFront);
        ArgumentNullException.ThrowIfNull(outputs);
        if (!_configured)
            throw new InvalidOperationException("GlVideoCompositor must be Configure()d before CompositeMulti.");
        if (outputs.Count == 0)
            return Array.Empty<VideoFrame>();

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
        _gl.GetInteger(GetPName.PixelPackBufferBinding, out var savedPixelPackBuffer);

        try
        {
            RenderLayersToCanvas(layersBackToFront, savedScissor);
            if (_multiPboReadbackUnavailable)
                return CompositeMultiSynchronousAfterCanvas(outputs, presentationTime);

            try
            {
                return CompositeMultiPboAfterCanvas(outputs, presentationTime);
            }
            catch (PboReadbackException)
            {
                // Driver variance is the main PBO risk. Disable it for this compositor instance and
                // re-run the already-correct sync path so playback degrades instead of failing.
                _multiPboReadbackUnavailable = true;
                DisposeMultiPboReadback();
                RenderLayersToCanvas(layersBackToFront, savedScissor);
                return CompositeMultiSynchronousAfterCanvas(outputs, presentationTime);
            }
        }
        finally
        {
            // --- Restore host state. ---
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, savedUnpackAlignment);
            _gl.PixelStore(PixelStoreParameter.UnpackRowLength, savedUnpackRowLength);
            _gl.PixelStore(PixelStoreParameter.PackAlignment, savedPackAlignment);
            _gl.PixelStore(PixelStoreParameter.PackRowLength, savedPackRowLength);
            _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, (uint)savedPixelPackBuffer);
            if (savedBlendEnabled) _gl.Enable(EnableCap.Blend);
            else _gl.Disable(EnableCap.Blend);
            _gl.BlendFuncSeparate((BlendingFactor)savedBlendSrcRgb, (BlendingFactor)savedBlendDstRgb,
                (BlendingFactor)savedBlendSrcAlpha, (BlendingFactor)savedBlendDstAlpha);
            if (savedScissor) _gl.Enable(EnableCap.ScissorTest);
            _gl.BindVertexArray((uint)savedVao);
            _gl.UseProgram((uint)savedProgram);
            _gl.ActiveTexture((TextureUnit)savedActiveTexture);
            _gl.Viewport(savedViewport[0], savedViewport[1], (uint)savedViewport[2], (uint)savedViewport[3]);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)savedFbo);
            DrainDeferredExternalImageReleases();
        }
    }

    public unsafe void CompositeMultiToTargets(
        IReadOnlyList<CompositorLayer> layersBackToFront,
        IReadOnlyList<TargetedWarpOutput> targets,
        TimeSpan presentationTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DrainDeferredExternalImageReleases();
        ArgumentNullException.ThrowIfNull(layersBackToFront);
        ArgumentNullException.ThrowIfNull(targets);
        if (!_configured)
            throw new InvalidOperationException("GlVideoCompositor must be Configure()d before CompositeMultiToTargets.");
        if (targets.Count == 0)
            return;

        // --- Save host GL state we'll touch (same set as CompositeMulti). ---
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
        _gl.GetInteger(GetPName.PixelPackBufferBinding, out var savedPixelPackBuffer);

        try
        {
            RenderLayersToCanvas(layersBackToFront, savedScissor);

            for (var i = 0; i < targets.Count; i++)
            {
                var targeted = targets[i];
                ArgumentNullException.ThrowIfNull(targeted.Target);

                // Warp this output; the result is left in the bound draw framebuffer (_fbo for a
                // full-canvas passthrough, else _warpFbo) at OutputFormat size.
                RenderOutputRequest(targeted.Request, nameof(targets));
                _gl.GetInteger(GetPName.DrawFramebufferBinding, out var srcFbo);
                var w = targeted.Request.OutputFormat.Width;
                var h = targeted.Request.OutputFormat.Height;

                switch (targeted.Target)
                {
                    case GlCompositeTarget glTarget:
                        BlitToGlTarget((uint)srcFbo, w, h, glTarget);
                        break;

                    case CpuFrameCompositeTarget cpuTarget:
                        var stride = OutputStrideForWidth(w);
                        var frame = ReadCurrentFramebuffer(
                            targeted.Request.OutputFormat, w, h, stride, stride * h, presentationTime);
                        try
                        {
                            cpuTarget.OnFrameReady(frame);
                            frame = null!;
                        }
                        finally
                        {
                            frame?.Dispose();
                        }
                        break;

                    case ExternalImageCompositeTarget externalTarget:
                        ExportExternalImage((uint)srcFbo, targeted.Request.OutputFormat, externalTarget);
                        break;

                    default:
                        throw new NotSupportedException(
                            $"Unknown composite target '{targeted.Target.GetType().Name}'.");
                }
            }
        }
        finally
        {
            // --- Restore host state. ---
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, savedUnpackAlignment);
            _gl.PixelStore(PixelStoreParameter.UnpackRowLength, savedUnpackRowLength);
            _gl.PixelStore(PixelStoreParameter.PackAlignment, savedPackAlignment);
            _gl.PixelStore(PixelStoreParameter.PackRowLength, savedPackRowLength);
            _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, (uint)savedPixelPackBuffer);
            if (savedBlendEnabled) _gl.Enable(EnableCap.Blend);
            else _gl.Disable(EnableCap.Blend);
            _gl.BlendFuncSeparate((BlendingFactor)savedBlendSrcRgb, (BlendingFactor)savedBlendDstRgb,
                (BlendingFactor)savedBlendSrcAlpha, (BlendingFactor)savedBlendDstAlpha);
            if (savedScissor) _gl.Enable(EnableCap.ScissorTest);
            _gl.BindVertexArray((uint)savedVao);
            _gl.UseProgram((uint)savedProgram);
            _gl.ActiveTexture((TextureUnit)savedActiveTexture);
            _gl.Viewport(savedViewport[0], savedViewport[1], (uint)savedViewport[2], (uint)savedViewport[3]);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)savedFbo);
            DrainDeferredExternalImageReleases();
        }
    }

    /// <summary>Zero-copy GL→GL: blit the warped result in <paramref name="srcFbo"/> into the caller's
    /// framebuffer. The canvas is stored top-down, so the destination Y is flipped to land upright in the
    /// GL (bottom-up) target.</summary>
    private void BlitToGlTarget(uint srcFbo, int w, int h, GlCompositeTarget target)
    {
        var vp = target.Viewport;
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, srcFbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, target.Framebuffer);
        _gl.BlitFramebuffer(
            0, 0, w, h,
            vp.X, vp.Y + vp.Height, vp.X + vp.Width, vp.Y,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
    }

    private void ExportExternalImage(uint srcFbo, VideoFormat format, ExternalImageCompositeTarget target)
    {
        if (GlExternalImageExport.TryExportDmabuf(
                _gl,
                srcFbo,
                format,
                target.AcceptedHandleTypes,
                ReleaseExternalImageOnOwnerThread,
                out var handle))
        {
            try
            {
                target.OnImageReady(handle);
            }
            catch
            {
                handle.Release();
                throw;
            }
            return;
        }

        throw new NotSupportedException(
            "ExternalImageCompositeTarget: no requested handle type can be exported on this platform/context " +
            $"(requested: {string.Join(", ", target.AcceptedHandleTypes)}). dmabuf needs EGL_MESA_image_dma_buf_export " +
            "(Linux/Mesa); D3D11 NT-handle export is the Windows path. Use a CpuFrameCompositeTarget as the fallback.");
    }

    public unsafe VideoFrame CompositeWithSurfaces(
        IReadOnlyList<CompositorLayer> frameLayers,
        IReadOnlyList<CompositorSurfaceLayer> surfaceLayers,
        TimeSpan presentationTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DrainDeferredExternalImageReleases();
        ArgumentNullException.ThrowIfNull(frameLayers);
        ArgumentNullException.ThrowIfNull(surfaceLayers);
        if (!_configured)
            throw new InvalidOperationException("GlVideoCompositor must be Configure()d before CompositeWithSurfaces.");

        // --- Save host GL state (same set as CompositeMulti). ---
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
        _gl.GetInteger(GetPName.PixelPackBufferBinding, out var savedPixelPackBuffer);

        try
        {
            RenderLayersToCanvas(frameLayers, savedScissor);

            // Surface layers render on top of the frame layers, directly into the canvas FBO, each in the
            // compositor's GL context/thread (the "3D object layer" seam, Doc 04 §5).
            for (var i = 0; i < surfaceLayers.Count; i++)
            {
                var surfaceLayer = surfaceLayers[i];
                ArgumentNullException.ThrowIfNull(surfaceLayer.Surface);
                var opacity = Math.Clamp(surfaceLayer.Opacity, 0f, 1f);
                if (opacity <= 0f)
                    continue;

                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
                _gl.Viewport(0, 0, (uint)_output.Width, (uint)_output.Height);
                surfaceLayer.Surface.Render(_gl, _fbo, presentationTime, surfaceLayer.Transform, opacity);
            }

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            return ReadCurrentFramebuffer(_output, _output.Width, _output.Height, _outputStride, _outputByteCount, presentationTime);
        }
        finally
        {
            // --- Restore host state. ---
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, savedUnpackAlignment);
            _gl.PixelStore(PixelStoreParameter.UnpackRowLength, savedUnpackRowLength);
            _gl.PixelStore(PixelStoreParameter.PackAlignment, savedPackAlignment);
            _gl.PixelStore(PixelStoreParameter.PackRowLength, savedPackRowLength);
            _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, (uint)savedPixelPackBuffer);
            if (savedBlendEnabled) _gl.Enable(EnableCap.Blend);
            else _gl.Disable(EnableCap.Blend);
            _gl.BlendFuncSeparate((BlendingFactor)savedBlendSrcRgb, (BlendingFactor)savedBlendDstRgb,
                (BlendingFactor)savedBlendSrcAlpha, (BlendingFactor)savedBlendDstAlpha);
            if (savedScissor) _gl.Enable(EnableCap.ScissorTest);
            else _gl.Disable(EnableCap.ScissorTest);
            _gl.BindVertexArray((uint)savedVao);
            _gl.UseProgram((uint)savedProgram);
            _gl.ActiveTexture((TextureUnit)savedActiveTexture);
            _gl.Viewport(savedViewport[0], savedViewport[1], (uint)savedViewport[2], (uint)savedViewport[3]);
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)savedFbo);
            DrainDeferredExternalImageReleases();
        }
    }

    private void ReleaseExternalImageOnOwnerThread(Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            RunExternalImageRelease(release);
            return;
        }

        _deferredExternalImageReleases.Enqueue(release);
    }

    private void DrainDeferredExternalImageReleases()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            return;

        while (_deferredExternalImageReleases.TryDequeue(out var release))
            RunExternalImageRelease(release);
    }

    private static void RunExternalImageRelease(Action release)
    {
        try
        {
            release();
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogWarning("GlVideoCompositor external image release failed: {0}", ex.Message);
        }
    }

    private VideoFrame[] CompositeMultiSynchronousAfterCanvas(
        IReadOnlyList<WarpOutputRequest> outputs,
        TimeSpan presentationTime)
    {
        var frames = new VideoFrame[outputs.Count];
        var created = 0;
        try
        {
            for (var i = 0; i < outputs.Count; i++)
            {
                var request = outputs[i];
                RenderOutputRequest(request, nameof(outputs));

                var stride = OutputStrideForWidth(request.OutputFormat.Width);
                frames[i] = ReadCurrentFramebuffer(
                    request.OutputFormat,
                    request.OutputFormat.Width,
                    request.OutputFormat.Height,
                    stride,
                    stride * request.OutputFormat.Height,
                    presentationTime);
                created++;
            }

            return frames;
        }
        catch
        {
            for (var i = 0; i < created; i++)
                frames[i]?.Dispose();
            throw;
        }
    }

    private unsafe VideoFrame[] CompositeMultiPboAfterCanvas(
        IReadOnlyList<WarpOutputRequest> outputs,
        TimeSpan presentationTime)
    {
        var state = EnsureMultiPboReadback(outputs);
        var previousPending = state.Pending;
        state.Pending = null;
        var returnPrevious = previousPending is { Length: > 0 };
        var currentPending = new PboReadback[outputs.Count];
        var currentPendingCount = 0;
        var frames = new VideoFrame[outputs.Count];
        var created = 0;

        try
        {
            for (var i = 0; i < outputs.Count; i++)
            {
                var request = outputs[i];
                RenderOutputRequest(request, nameof(outputs));

                var stride = OutputStrideForWidth(request.OutputFormat.Width);
                var byteCount = stride * request.OutputFormat.Height;

                if (!returnPrevious)
                {
                    frames[i] = ReadCurrentFramebuffer(
                        request.OutputFormat,
                        request.OutputFormat.Width,
                        request.OutputFormat.Height,
                        stride,
                        byteCount,
                        presentationTime);
                    created++;
                }

                currentPending[i] = IssuePboReadback(
                    state.Outputs[i],
                    request.OutputFormat,
                    stride,
                    byteCount);
                currentPendingCount++;
            }

            if (returnPrevious)
            {
                frames = CompletePboReadbacks(previousPending!, presentationTime);
                created = frames.Length;
            }

            state.Pending = currentPending;
            currentPending = [];
            currentPendingCount = 0;
            return frames;
        }
        catch
        {
            for (var i = 0; i < created; i++)
                frames[i]?.Dispose();
            DisposePendingReadbacks(previousPending);
            DisposePendingReadbacks(currentPending, 0, currentPendingCount);
            throw;
        }
    }

    private void RenderOutputRequest(WarpOutputRequest request, string paramName)
    {
        ValidateWarpOutput(request.OutputFormat, paramName);

        if (request.Sections is null
            && request.OutputFormat.Width == _output.Width
            && request.OutputFormat.Height == _output.Height
            && request.OutputFormat.PixelFormat == _output.PixelFormat)
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        }
        else
        {
            RunWarpPass(CreateWarpPassState(request));
        }
    }

    private PboReadbackState EnsureMultiPboReadback(IReadOnlyList<WarpOutputRequest> outputs)
    {
        if (_multiPboReadback is { } state && MultiPboShapeMatches(state.Shape, outputs))
            return state;

        DisposeMultiPboReadback();
        _multiPboReadback = new PboReadbackState(SnapshotOutputRequests(outputs));
        return _multiPboReadback;
    }

    private static bool MultiPboShapeMatches(WarpOutputRequest[] shape, IReadOnlyList<WarpOutputRequest> outputs)
    {
        if (shape.Length != outputs.Count)
            return false;

        for (var i = 0; i < shape.Length; i++)
        {
            if (!shape[i].Equals(outputs[i]))
                return false;
        }

        return true;
    }

    private static WarpOutputRequest[] SnapshotOutputRequests(IReadOnlyList<WarpOutputRequest> outputs)
    {
        var snapshot = new WarpOutputRequest[outputs.Count];
        for (var i = 0; i < snapshot.Length; i++)
            snapshot[i] = outputs[i];
        return snapshot;
    }

    private void RenderLayersToCanvas(IReadOnlyList<CompositorLayer> layersBackToFront, bool savedScissor)
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
    }

    private unsafe VideoFrame ReadCurrentFramebuffer(
        VideoFormat frameFormat,
        int readW,
        int readH,
        int readStride,
        int readBytes,
        TimeSpan presentationTime)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(readBytes);
        _gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
        _gl.PixelStore(PixelStoreParameter.PackRowLength, 0);
        _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
        fixed (byte* p = buffer)
            _gl.ReadPixels(0, 0, (uint)readW, (uint)readH, _readPixelFormat, _readPixelType, p);

        var plane = new ReadOnlyMemory<byte>(buffer, 0, readBytes);
        var owned = buffer;
        return new VideoFrame(
            presentationTime,
            frameFormat,
            plane,
            readStride,
            release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(owned, clearArray: false)));
    }

    private unsafe PboReadback IssuePboReadback(
        PboOutputSlot slot,
        VideoFormat frameFormat,
        int readStride,
        int readBytes)
    {
        var bufferIndex = slot.NextBufferIndex;
        slot.NextBufferIndex ^= 1;

        var pbo = slot.Buffers[bufferIndex];
        if (pbo == 0)
        {
            pbo = _gl.GenBuffer();
            slot.Buffers[bufferIndex] = pbo;
        }

        _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, pbo);
        // Orphan even at equal size so the DMA target never aliases a buffer the CPU may map next frame.
        _gl.BufferData(BufferTargetARB.PixelPackBuffer, (nuint)readBytes, null, BufferUsageARB.StreamRead);
        slot.Capacities[bufferIndex] = Math.Max(slot.Capacities[bufferIndex], readBytes);

        _gl.PixelStore(PixelStoreParameter.PackAlignment, 4);
        _gl.PixelStore(PixelStoreParameter.PackRowLength, 0);
        _gl.ReadPixels(0, 0, (uint)frameFormat.Width, (uint)frameFormat.Height,
            _readPixelFormat, _readPixelType, (void*)0);

        var sync = _gl.FenceSync(GLEnum.SyncGpuCommandsComplete, 0u);
        if (sync == nint.Zero)
            throw new PboReadbackException("glFenceSync returned null for compositor PBO readback.");

        return new PboReadback
        {
            Buffer = pbo,
            Sync = sync,
            Format = frameFormat,
            Stride = readStride,
            ByteCount = readBytes,
        };
    }

    private VideoFrame[] CompletePboReadbacks(PboReadback[] pending, TimeSpan presentationTime)
    {
        var frames = new VideoFrame[pending.Length];
        var created = 0;
        try
        {
            for (var i = 0; i < pending.Length; i++)
            {
                frames[i] = CompletePboReadback(pending[i], presentationTime);
                created++;
            }

            return frames;
        }
        catch
        {
            for (var i = 0; i < created; i++)
                frames[i]?.Dispose();
            DisposePendingReadbacks(pending, created, pending.Length - created);
            throw;
        }
    }

    private unsafe VideoFrame CompletePboReadback(PboReadback pending, TimeSpan presentationTime)
    {
        byte[]? buffer = null;
        var mapped = false;
        try
        {
            var wait = _gl.ClientWaitSync(pending.Sync, 1u, ulong.MaxValue);
            if (wait != GLEnum.AlreadySignaled && wait != GLEnum.ConditionSatisfied)
                throw new PboReadbackException($"glClientWaitSync failed for compositor PBO readback: {wait}.");

            _gl.DeleteSync(pending.Sync);
            pending.Sync = nint.Zero;

            _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, pending.Buffer);
            var ptr = (byte*)_gl.MapBufferRange(
                BufferTargetARB.PixelPackBuffer,
                0,
                (nuint)pending.ByteCount,
                1u);
            if (ptr is null)
                throw new PboReadbackException("glMapBufferRange returned null for compositor PBO readback.");

            mapped = true;
            buffer = ArrayPool<byte>.Shared.Rent(pending.ByteCount);
            new ReadOnlySpan<byte>(ptr, pending.ByteCount).CopyTo(buffer.AsSpan(0, pending.ByteCount));

            var unmapOk = _gl.UnmapBuffer(BufferTargetARB.PixelPackBuffer);
            mapped = false;
            if (!unmapOk)
                throw new PboReadbackException("glUnmapBuffer reported corrupted compositor PBO readback data.");

            var owned = buffer;
            buffer = null;
            return new VideoFrame(
                presentationTime,
                pending.Format,
                new ReadOnlyMemory<byte>(owned, 0, pending.ByteCount),
                pending.Stride,
                release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(owned, clearArray: false)));
        }
        finally
        {
            if (mapped)
                _gl.UnmapBuffer(BufferTargetARB.PixelPackBuffer);
            if (pending.Sync != nint.Zero)
            {
                _gl.DeleteSync(pending.Sync);
                pending.Sync = nint.Zero;
            }
            if (buffer is not null)
                ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    private void DisposeMultiPboReadback()
    {
        var state = _multiPboReadback;
        if (state is null)
            return;

        DisposePendingReadbacks(state.Pending);
        state.Pending = null;
        foreach (var slot in state.Outputs)
        {
            for (var i = 0; i < slot.Buffers.Length; i++)
            {
                var buffer = slot.Buffers[i];
                if (buffer == 0)
                    continue;
                _gl.DeleteBuffer(buffer);
                slot.Buffers[i] = 0;
                slot.Capacities[i] = 0;
            }
        }

        _multiPboReadback = null;
    }

    private void DisposePendingReadbacks(PboReadback[]? pending)
    {
        if (pending is null)
            return;
        DisposePendingReadbacks(pending, 0, pending.Length);
    }

    private void DisposePendingReadbacks(PboReadback[]? pending, int start, int count)
    {
        if (pending is null)
            return;

        var end = Math.Min(pending.Length, start + count);
        for (var i = Math.Max(0, start); i < end; i++)
        {
            var readback = pending[i];
            if (readback?.Sync is { } sync && sync != nint.Zero)
            {
                _gl.DeleteSync(sync);
                readback.Sync = nint.Zero;
            }
        }
    }

    private WarpPassState CreateWarpPassState(WarpOutputRequest request)
    {
        if (request.Sections is { } sections)
            return new WarpPassState(
                request.OutputFormat,
                sections as WarpSection[] ?? sections.ToArray());

        var transform = LayerTransform2D.Scale(
            request.OutputFormat.Width / (float)_output.Width,
            request.OutputFormat.Height / (float)_output.Height);
        return new WarpPassState(
            request.OutputFormat,
            [new WarpSection(RectNormalized.Full, transform, 1f)]);
    }

    private int OutputStrideForWidth(int width) => width * (_outputStride / _output.Width);

    private void DrawLayer(CompositorLayer layer, float opacity)
    {
        var src = layer.Frame;
        var srcW = src.Format.Width;
        var srcH = src.Format.Height;

        // Pick the per-layer source texture: BGRA32 uploads direct to RGBA8; everything else runs the YUV
        // pre-pass into a cached RGBA16F intermediate so high-bit precision survives into the composite.
        // The pre-pass bakes a vertical flip (YuvVideoRenderer's yUvFlip) into its intermediate; the direct
        // BGRA32 upload does not, so flag it so the fragment shader flips V to keep both paths upright.
        var directBgraUpload = src.Format.PixelFormat == CorePixelFormat.Bgra32;
        if (directBgraUpload)
        {
            PrepareBgra32LayerTexture(src, srcW, srcH);
        }
        else
        {
            PrepareYuvLayerIntermediate(src, srcW, srcH);
        }

        if (layer.Mesh is { } mesh)
        {
            DrawLayerMesh(mesh, layer.SourceCrop, _output.Width, _output.Height,
                opacity, flipV: directBgraUpload, layer.BlendMode);
            _gl.UseProgram(_program);
            _gl.BindVertexArray(_vao);
        }
        else
        {
            DrawQuad(srcW, srcH, _output.Width, _output.Height,
                layer.Transform, layer.SourceCrop, opacity, flipV: directBgraUpload, layer.BlendMode);
        }
    }

    /// <summary>Core textured-quad draw shared by the layer pass and the warp pass: bakes the
    /// source-pixels → dest-NDC transform and issues the draw with the bound texture-unit-0 source.</summary>
    /// <param name="mirrorDestY">The layer pass paints the framebuffer vertically MIRRORED (Y flip
    /// in the bake, paired with the sampling V-flip) so bottom-up glReadPixels yields a top-down
    /// buffer. The warp pass samples the canvas FBO texture, which is already stored top-down —
    /// it must paint unmirrored (false) with no sampling flip, or the output arrives upside down.</param>
    private void DrawQuad(
        int srcW, int srcH, int destW, int destH,
        LayerTransform2D t, RectNormalized sourceCrop, float opacity, bool flipV, BlendMode blendMode,
        bool mirrorDestY = true)
    {
        var outW = (float)destW;
        var outH = (float)destH;
        var ySign = mirrorDestY ? -1f : 1f;
        // matrix is column-major when sent via UniformMatrix3, but we use *transpose=false convention
        // by laying out per-column. Easier: assemble the 9-float array explicitly.
        // m[col,row]: column-major storage.
        Span<float> m = stackalloc float[9];
        // Col 0: derivative w.r.t. uv.x — (M11*srcW)*(2/outW), ±(M21*srcW)*(2/outH), 0
        m[0] = (t.M11 * srcW) * (2f / outW);
        m[1] = ySign * (t.M21 * srcW) * (2f / outH);
        m[2] = 0f;
        // Col 1: derivative w.r.t. uv.y — (M12*srcH)*(2/outW), ±(M22*srcH)*(2/outH), 0
        m[3] = (t.M12 * srcH) * (2f / outW);
        m[4] = ySign * (t.M22 * srcH) * (2f / outH);
        m[5] = 0f;
        // Col 2: translation — (2*Tx/outW) - 1, ∓(1 - (2*Ty/outH)), 1
        m[6] = (2f * t.Tx / outW) - 1f;
        m[7] = mirrorDestY ? 1f - (2f * t.Ty / outH) : (2f * t.Ty / outH) - 1f;
        m[8] = 1f;
        _gl.UniformMatrix3(_uXformLoc, 1, false, m);
        var crop = sourceCrop.Clamped();
        _gl.Uniform4(_uCropLoc, crop.X0, crop.Y0, crop.X1, crop.Y1);
        _gl.Uniform1(_uOpacityLoc, opacity);
        _gl.Uniform1(_uLayerFlipVLoc, flipV ? 1f : 0f);

        switch (blendMode)
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
                throw new NotSupportedException($"BlendMode {blendMode} not supported.");
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
        _uCropLoc = _gl.GetUniformLocation(_program, "uCrop");
        _uOpacityLoc = _gl.GetUniformLocation(_program, "uOpacity");
        _uBlendKindLoc = _gl.GetUniformLocation(_program, "uBlendKind");
        _uLayerLoc = _gl.GetUniformLocation(_program, "uLayer");
        _uLayerFlipVLoc = _gl.GetUniformLocation(_program, "uLayerFlipV");
        if (_uXformLoc < 0 || _uCropLoc < 0 || _uOpacityLoc < 0 || _uBlendKindLoc < 0 || _uLayerLoc < 0 || _uLayerFlipVLoc < 0)
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

    /// <inheritdoc />
    public void SetWarpPass(VideoFormat warpOutput, IReadOnlyList<WarpSection>? sections)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sections is null)
        {
            _warpPass = null;
            return;
        }

        ValidateWarpOutput(warpOutput, nameof(warpOutput));

        _warpPass = new WarpPassState(warpOutput, sections.ToArray());
    }

    private void ValidateWarpOutput(VideoFormat warpOutput, string paramName)
    {
        var expectedPf = OutputPixelFormatForPrecision(_outputPrecision);
        if (warpOutput.PixelFormat != expectedPf)
            throw new ArgumentException(
                $"warp output must match compositor precision pixel format {expectedPf}; got {warpOutput.PixelFormat}.",
                paramName);
        if (warpOutput.Width <= 0 || warpOutput.Height <= 0)
            throw new ArgumentException(
                $"warp output dimensions must be positive (got {warpOutput.Width}x{warpOutput.Height}).",
                paramName);
    }

    /// <summary>GL thread (inside Composite). Draws the warp sections from the canvas FBO texture
    /// into the warp FBO; the caller reads back from the warp FBO afterwards.</summary>
    private void RunWarpPass(WarpPassState warp)
    {
        EnsureWarpFbo(warp.Output.Width, warp.Output.Height);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _warpFbo);
        _gl.Viewport(0, 0, (uint)warp.Output.Width, (uint)warp.Output.Height);
        _gl.ClearColor(0f, 0f, 0f, 0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        // The composited canvas texture is the source for every section. Linear so scaled/rotated
        // sections sample smoothly (the FBO texture defaults to Nearest because it is normally
        // only read back, never sampled).
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _fboTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

        EnsureMeshBuffers(warp);

        var meshIdx = 0; // _meshDraws is ordered by ascending SectionIndex
        for (var i = 0; i < warp.Sections.Length; i++)
        {
            var section = warp.Sections[i];
            var opacity = Math.Clamp(section.Opacity, 0f, 1f);

            while (meshIdx < _meshDraws.Count && _meshDraws[meshIdx].SectionIndex < i)
                meshIdx++;
            var hasMesh = meshIdx < _meshDraws.Count && _meshDraws[meshIdx].SectionIndex == i;

            if (opacity <= 0f)
                continue;

            if (hasMesh)
            {
                DrawMeshSection(_meshDraws[meshIdx], section, warp.Output.Width, warp.Output.Height, opacity);
                // The mesh draw switched program + VAO — restore the layer pipeline for any affine
                // sections that follow (and for state-restore symmetry at the end of Composite).
                _gl.UseProgram(_program);
                _gl.BindVertexArray(_vao);
            }
            else
            {
                // Canvas texture is stored top-down (it IS the readback content) — no sampling flip and
                // no dest mirroring, unlike the layer pass (see DrawQuad's mirrorDestY remarks).
                DrawQuad(_output.Width, _output.Height, warp.Output.Width, warp.Output.Height,
                    section.Transform, section.SourceCrop, opacity, flipV: false, BlendMode.SourceOver,
                    mirrorDestY: false);
            }
        }
    }

    /// <summary>GL thread. (Re)builds the per-section mesh buffers when the warp snapshot changed;
    /// tessellation runs on the CPU here, the per-frame cost is just the indexed draws.</summary>
    private unsafe void EnsureMeshBuffers(WarpPassState warp)
    {
        if (ReferenceEquals(_meshBuffersSections, warp.Sections))
            return;

        ReleaseMeshBuffers();
        for (var i = 0; i < warp.Sections.Length; i++)
        {
            if (warp.Sections[i].Mesh is not { } mesh)
                continue;

            EnsureMeshPipeline();
            WarpMeshTessellator.Tessellate(mesh, out var vertices, out var indices);

            var vao = _gl.GenVertexArray();
            _gl.BindVertexArray(vao);
            var vbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            fixed (float* p = vertices)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
            var ebo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
            fixed (uint* p = indices)
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StaticDraw);
            // Interleaved (s, t, x, y): location 0 = section parameter (UV via uCrop), 1 = warped
            // destination position in output pixels.
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, (uint)(4 * sizeof(float)), (void*)0);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, (uint)(4 * sizeof(float)), (void*)(2 * sizeof(float)));
            _gl.BindVertexArray(0);

            _meshDraws.Add(new MeshDraw(i, vao, vbo, ebo, indices.Length));
        }

        // Mesh-buffer setup unbound the VAO — restore the layer-pass binding the caller expects.
        _gl.BindVertexArray(_vao);
        _meshBuffersSections = warp.Sections;
    }

    private unsafe void DrawMeshSection(MeshDraw draw, WarpSection section, int destW, int destH, float opacity)
    {
        _gl.UseProgram(_meshProgram);
        _gl.BindVertexArray(draw.Vao);

        // Output pixels → NDC, no Y mirror — identical convention to the affine warp draw
        // (mirrorDestY: false); the mesh positions are already absolute output pixels.
        Span<float> m = stackalloc float[9];
        m[0] = 2f / destW; m[1] = 0f; m[2] = 0f;
        m[3] = 0f; m[4] = 2f / destH; m[5] = 0f;
        m[6] = -1f; m[7] = -1f; m[8] = 1f;
        _gl.UniformMatrix3(_meshXformLoc, 1, false, m);
        var crop = section.SourceCrop.Clamped();
        _gl.Uniform4(_meshCropLoc, crop.X0, crop.Y0, crop.X1, crop.Y1);
        _gl.Uniform1(_meshOpacityLoc, opacity);
        _gl.Uniform1(_meshLayerFlipVLoc, 0f);
        _gl.Uniform1(_meshLayerLoc, 0);
        _gl.Uniform1(_meshBlendKindLoc, 0);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

        _gl.DrawElements(PrimitiveType.Triangles, (uint)draw.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
    }

    private unsafe void DrawLayerMesh(
        WarpMesh mesh,
        RectNormalized sourceCrop,
        int destW,
        int destH,
        float opacity,
        bool flipV,
        BlendMode blendMode)
    {
        EnsureMeshPipeline();
        WarpMeshTessellator.Tessellate(mesh, out var vertices, out var indices);

        var vao = _gl.GenVertexArray();
        var vbo = _gl.GenBuffer();
        var ebo = _gl.GenBuffer();
        try
        {
            _gl.BindVertexArray(vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            fixed (float* p = vertices)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), p, BufferUsageARB.StreamDraw);
            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
            fixed (uint* p = indices)
                _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), p, BufferUsageARB.StreamDraw);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, (uint)(4 * sizeof(float)), (void*)0);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, (uint)(4 * sizeof(float)), (void*)(2 * sizeof(float)));

            _gl.UseProgram(_meshProgram);
            _gl.BindVertexArray(vao);

            // Layer pass convention: write mirrored in Y so bottom-up glReadPixels returns top-down frames.
            Span<float> m = stackalloc float[9];
            m[0] = 2f / destW; m[1] = 0f; m[2] = 0f;
            m[3] = 0f; m[4] = -2f / destH; m[5] = 0f;
            m[6] = -1f; m[7] = 1f; m[8] = 1f;
            _gl.UniformMatrix3(_meshXformLoc, 1, false, m);
            var crop = sourceCrop.Clamped();
            _gl.Uniform4(_meshCropLoc, crop.X0, crop.Y0, crop.X1, crop.Y1);
            _gl.Uniform1(_meshOpacityLoc, opacity);
            _gl.Uniform1(_meshLayerFlipVLoc, flipV ? 1f : 0f);
            _gl.Uniform1(_meshLayerLoc, 0);

            switch (blendMode)
            {
                case BlendMode.Source:
                    _gl.Disable(EnableCap.Blend);
                    _gl.Uniform1(_meshBlendKindLoc, 0);
                    break;
                case BlendMode.SourceOver:
                    _gl.Enable(EnableCap.Blend);
                    _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
                    _gl.Uniform1(_meshBlendKindLoc, 0);
                    break;
                case BlendMode.Multiply:
                    _gl.Enable(EnableCap.Blend);
                    _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
                    _gl.Uniform1(_meshBlendKindLoc, 1);
                    break;
                default:
                    throw new NotSupportedException($"BlendMode {blendMode} not supported.");
            }

            _gl.DrawElements(PrimitiveType.Triangles, (uint)indices.Length, DrawElementsType.UnsignedInt, (void*)0);
        }
        finally
        {
            if (ebo != 0) _gl.DeleteBuffer(ebo);
            if (vbo != 0) _gl.DeleteBuffer(vbo);
            if (vao != 0) _gl.DeleteVertexArray(vao);
        }
    }

    private void EnsureMeshPipeline()
    {
        if (_meshProgram != 0)
            return;

        var vertSrc = LoadShaderCached("composite_mesh.vert.glsl");
        var fragSrc = LoadShaderCached("composite_layer.frag.glsl");
        _meshProgram = SharedGlProgramCache.Acquire(MeshProgramCacheKey, _gl, _ => LinkProgram(vertSrc, fragSrc));

        _meshXformLoc = _gl.GetUniformLocation(_meshProgram, "uXform");
        _meshCropLoc = _gl.GetUniformLocation(_meshProgram, "uCrop");
        _meshOpacityLoc = _gl.GetUniformLocation(_meshProgram, "uOpacity");
        _meshBlendKindLoc = _gl.GetUniformLocation(_meshProgram, "uBlendKind");
        _meshLayerLoc = _gl.GetUniformLocation(_meshProgram, "uLayer");
        _meshLayerFlipVLoc = _gl.GetUniformLocation(_meshProgram, "uLayerFlipV");
        if (_meshXformLoc < 0 || _meshCropLoc < 0 || _meshOpacityLoc < 0 || _meshBlendKindLoc < 0
            || _meshLayerLoc < 0 || _meshLayerFlipVLoc < 0)
            throw new InvalidOperationException("composite_mesh program missing required uniforms.");
    }

    private void ReleaseMeshBuffers()
    {
        foreach (var md in _meshDraws)
        {
            _gl.DeleteVertexArray(md.Vao);
            _gl.DeleteBuffer(md.Vbo);
            _gl.DeleteBuffer(md.Ebo);
        }
        _meshDraws.Clear();
        _meshBuffersSections = null;
    }

    private unsafe void EnsureWarpFbo(int width, int height)
    {
        if (_warpFbo != 0 && _warpFboSize == (width, height))
            return;

        if (_warpFboTexture != 0) { _gl.DeleteTexture(_warpFboTexture); _warpFboTexture = 0; }
        if (_warpFbo != 0) { _gl.DeleteFramebuffer(_warpFbo); _warpFbo = 0; }

        _warpFboTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _warpFboTexture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, _fboInternalFormat,
            (uint)width, (uint)height, 0,
            GlPixelFormat.Rgba, PixelTypeForInternalFormat(_fboInternalFormat), null);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

        _warpFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _warpFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _warpFboTexture, 0);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            _gl.DeleteFramebuffer(_warpFbo);
            _gl.DeleteTexture(_warpFboTexture);
            _warpFbo = 0;
            _warpFboTexture = 0;
            throw new InvalidOperationException($"GlVideoCompositor warp FBO incomplete: {status}");
        }

        _warpFboSize = (width, height);
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
        if (_disposed)
        {
            DrainDeferredExternalImageReleases();
            return;
        }
        DrainDeferredExternalImageReleases();
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
        DisposeMultiPboReadback();
        if (_fboTexture != 0) { _gl.DeleteTexture(_fboTexture); _fboTexture = 0; }
        if (_fbo != 0) { _gl.DeleteFramebuffer(_fbo); _fbo = 0; }
        if (_warpFboTexture != 0) { _gl.DeleteTexture(_warpFboTexture); _warpFboTexture = 0; }
        if (_warpFbo != 0) { _gl.DeleteFramebuffer(_warpFbo); _warpFbo = 0; }
        ReleaseMeshBuffers();
        if (_meshProgram != 0) { SharedGlProgramCache.Release(_gl, MeshProgramCacheKey); _meshProgram = 0; }
        if (_vbo != 0) { _gl.DeleteBuffer(_vbo); _vbo = 0; }
        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
        if (_program != 0) { SharedGlProgramCache.Release(_gl, ProgramCacheKey); _program = 0; }
        DrainDeferredExternalImageReleases();
    }
}
