using System.Collections.Concurrent;
using System.Reflection;
using S.Media.Core.Video;
using GlPixelFormat = Silk.NET.OpenGL.PixelFormat;
using GlPixelType = Silk.NET.OpenGL.PixelType;
using GlInternalFormat = Silk.NET.OpenGL.InternalFormat;
using CorePixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.OpenGL;

/// <summary>
/// Renders <see cref="VideoFrame"/>s into the currently-bound OpenGL framebuffer.
/// Format-aware via <see cref="GlVideoFormatSupport"/>.
/// </summary>
/// <remarks>
/// Threading: call only on the GL context owner thread. Pixel-store unpacked state is restored after <see cref="Upload"/>.
/// </remarks>
public sealed unsafe class YuvVideoRenderer : IDisposable
{
    private static readonly ConcurrentDictionary<string, string> ShaderSourceCache = new(StringComparer.Ordinal);

    private readonly GL _gl;
    private readonly VideoFormat _format;
    private readonly GlVideoFormatSupport.GlFormatRecipe _recipe;

    private uint _program;
    private uint _vao;
    private readonly uint[] _textures;
    private readonly int[] _samplerUniforms;
    private int _uBitScale = -1;
    private int _uYuvOffset = -1;
    private int _uYuvMatrix = -1;
    private int _uYuvFlip = -1;
    private int _uFrameWidth = -1;
    private int _uHalfTexWidth = -1;
    private int _uHdrTransfer = -1;
    private int _uHdrExposure = -1;

    private YuvColorSpace _colorSpace;
    private float _yUvFlip = 1f;
    private VideoHdrTransfer _hdrTransfer = VideoHdrTransfer.None;
    private float _hdrPreviewExposure = 400f;

    private int? _lastUnpackAlignment;
    private int? _lastUnpackRowLength;
    private int _savedUnpackAlignment = 4;
    private int _savedUnpackRowLength;
    private bool _unpackSession;

    private uint _samplerLinear;
    private uint _samplerYMipmap;

    private readonly bool _shareShaderPrograms;
    private readonly string? _shaderProgramCacheKey;
    private readonly bool _yPlaneMipmapsEnabled;

    private readonly Nv12DmabufGpuUploader? _nv12DmabufGpuUploader;
    private bool _suppressYPlaneMipForLastGlDmabufUpload;

    private readonly Action<VideoFrame> _uploadFromFrame;

    private bool _disposed;

    public VideoFormat Format => _format;

    public YuvColorSpace ColorSpace
    {
        get => _colorSpace;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _colorSpace = value;
            ApplyYuvColorUniforms();
        }
    }

    public float YAxisUvFlip
    {
        get => _yUvFlip;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (value is < 0f or > 1f)
                throw new ArgumentOutOfRangeException(nameof(value), "must be between 0 and 1.");
            _yUvFlip = value;
            ApplyYuvFlipUniform();
        }
    }

    public VideoHdrTransfer HdrTransfer
    {
        get => _hdrTransfer;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _hdrTransfer = value;
            ApplyHdrUniforms();
        }
    }

    public float HdrPreviewExposure
    {
        get => _hdrPreviewExposure;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!float.IsFinite(value) || value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value), "must be a finite positive number.");
            _hdrPreviewExposure = value;
            ApplyHdrUniforms();
        }
    }

    public YuvVideoRenderer(GL gl, VideoFormat format, YuvColorSpace? colorSpace = null,
        bool sharedShaderPrograms = false, bool yPlaneMipmaps = false, YuvDmabufEglInterop? eglDmabufInterop = null)
    {
        ArgumentNullException.ThrowIfNull(gl);
        if (format.Width <= 0 || format.Height <= 0)
            throw new ArgumentException("video format must have positive dimensions", nameof(format));
        if (!GlVideoFormatSupport.TryGetRecipe(format.PixelFormat, out var recipe))
            throw new NotSupportedException($"YuvVideoRenderer does not support pixel format {format.PixelFormat}");

        _gl = gl;
        _format = format;
        _recipe = recipe;
        _colorSpace = colorSpace
            ?? (recipe.NeedsYuvMatrix
                ? YuvColorSpace.DefaultForHeight(format.Height)
                : YuvColorSpace.Bt709Limited);

        _shareShaderPrograms = sharedShaderPrograms;
        _shaderProgramCacheKey = sharedShaderPrograms
            ? $"{recipe.VertexFile}\0{recipe.FragmentFile}"
            : null;
        _yPlaneMipmapsEnabled = yPlaneMipmaps && recipe.NeedsYuvMatrix && recipe.PlaneCount >= 2;

        _nv12DmabufGpuUploader =
            eglDmabufInterop != null ? Nv12DmabufGpuUploader.TryCreate(gl, eglDmabufInterop) : null;

        _textures = new uint[recipe.PlaneCount];
        _samplerUniforms = new int[_textures.Length];
        BuildPipeline();
        _uploadFromFrame = CreateUploadFromFrameDelegate(format.PixelFormat);
    }

    public static bool IsFormatSupported(CorePixelFormat format) =>
        GlVideoFormatSupport.TryGetRecipe(format, out _);

    public static IReadOnlyList<CorePixelFormat> SupportedPixelFormats =>
        GlVideoFormatSupport.SupportedPixelFormats;

    private static void ValidateStridesAgainstFormat(CorePixelFormat pixelFormat, int frameWidth, int frameHeight,
        ReadOnlySpan<int> strides, int expectedPlaneCount)
    {
        if (strides.Length != expectedPlaneCount)
            throw new ArgumentException($"expected {expectedPlaneCount} strides, got {strides.Length}.");
        for (var i = 0; i < strides.Length; i++)
        {
            var minStride = PixelFormatInfo.PlaneByteWidth(pixelFormat, frameWidth, i);
            if (strides[i] < minStride)
                throw new ArgumentOutOfRangeException(nameof(strides),
                    $"stride[{i}] ({strides[i]}) must be ≥ visible row bytes ({minStride}) for {pixelFormat}.");
            _ = PixelFormatInfo.PlanePitchBufferLength(pixelFormat, frameWidth, frameHeight, i, strides[i]);
        }
    }

    public void Upload(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Format.PixelFormat != _format.PixelFormat
            || frame.Format.Width != _format.Width
            || frame.Format.Height != _format.Height)
            throw new ArgumentException($"frame format {frame.Format} does not match renderer format {_format}", nameof(frame));

        _suppressYPlaneMipForLastGlDmabufUpload = false;
        if (frame.DmabufNv12 is not null)
        {
            if (_nv12DmabufGpuUploader is null)
                throw new InvalidOperationException(
                    "NV12 DRM PRIME frames require EGL DMA-BUF upload; GPU uploader is unavailable (extensions or GL context).");

            if (!_nv12DmabufGpuUploader.TryUpload(_textures[0], _textures[1], Format, frame.DmabufNv12))
                throw new InvalidOperationException(
                    "NV12 DRM PRIME EGL import failed (linear/modifier tiling, pitches, FOURCC, or driver EGL path). Clear RetainDmabufForGl if you need CPU-plane fallback.");

            _suppressYPlaneMipForLastGlDmabufUpload = true;
            RegenerateYPlaneMipmapsIfNeeded();
            return;
        }

        BeginUnpackSession();
        try { _uploadFromFrame(frame); }
        finally { EndUnpackSession(); }

        _suppressYPlaneMipForLastGlDmabufUpload = false;
        RegenerateYPlaneMipmapsIfNeeded();
    }

    /// <summary>Pinned-pixel upload for callers holding unmanaged plane pointers (must stay valid for the duration of the call).</summary>
    public unsafe void Upload(ReadOnlySpan<nint> planeBasePointers, ReadOnlySpan<int> strideBytesPerPlane)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (planeBasePointers.Length != _textures.Length || strideBytesPerPlane.Length != _textures.Length)
            throw new ArgumentException("must supply exactly one unmanaged pointer and stride per configured plane.");

        ValidateStridesAgainstFormat(_format.PixelFormat, _format.Width, _format.Height,
            strideBytesPerPlane, _textures.Length);

        BeginUnpackSession();
        try { DispatchUploadFromPointers(planeBasePointers, strideBytesPerPlane); }
        finally { EndUnpackSession(); }
        RegenerateYPlaneMipmapsIfNeeded();
    }

    private Action<VideoFrame> CreateUploadFromFrameDelegate(CorePixelFormat pixelFormat) =>
        pixelFormat switch
        {
            CorePixelFormat.Bgra32 => UploadBgraFromFrame,
            CorePixelFormat.Rgba32 or CorePixelFormat.Argb32 or CorePixelFormat.Abgr32 => UploadRgbaFromFrame,
            CorePixelFormat.Rgb24 => UploadRgb24FromFrame,
            CorePixelFormat.Bgr24 => UploadBgr24FromFrame,
            CorePixelFormat.I420 => UploadI420FromFrame,
            CorePixelFormat.Yv12 => UploadYv12FromFrame,
            CorePixelFormat.Yuv422P => UploadYuv422PFromFrame,
            CorePixelFormat.Yuv444P => UploadYuv444PFromFrame,
            CorePixelFormat.Yuv422P10Le => UploadYuv422P10LeFromFrame,
            CorePixelFormat.Nv12 => UploadNv12Adaptive,
            CorePixelFormat.Nv21 => UploadNv21FromFrame,
            CorePixelFormat.P010 or CorePixelFormat.P016 => UploadSemiPlanar16FromFrame,
            CorePixelFormat.Uyvy => UploadUyvyFromFrame,
            CorePixelFormat.Yuyv => UploadYuyvFromFrame,
            CorePixelFormat.Gray8 => UploadGray8FromFrame,
            CorePixelFormat.Gray16 => UploadGray16FromFrame,
            CorePixelFormat.Yuv420P10Le or CorePixelFormat.Yuv420P12Le => UploadPlanar420P16FromFrame,
            CorePixelFormat.Yuv444P10Le => UploadYuv444P10LeFromFrame,
            CorePixelFormat.Yuva420p => UploadYuva420FromFrame,
            _ => throw new NotSupportedException($"Upload: {pixelFormat}"),
        };

    private unsafe void DispatchUploadFromPointers(ReadOnlySpan<nint> planes, ReadOnlySpan<int> strides)
    {
        switch (_format.PixelFormat)
        {
            case CorePixelFormat.Bgra32:
                UploadBgraPtr((byte*)planes[0], strides[0]);
                break;
            case CorePixelFormat.Rgba32:
            case CorePixelFormat.Argb32:
            case CorePixelFormat.Abgr32:
                UploadRgbaPtr((byte*)planes[0], strides[0]);
                break;
            case CorePixelFormat.Rgb24:
                UploadRgb24Ptr((byte*)planes[0], strides[0]);
                break;
            case CorePixelFormat.Bgr24:
                UploadBgr24Ptr((byte*)planes[0], strides[0]);
                break;
            case CorePixelFormat.I420:
                UploadI420Ptr((byte*)planes[0], strides[0], (byte*)planes[1], strides[1], (byte*)planes[2], strides[2]);
                break;
            case CorePixelFormat.Yv12:
                UploadYv12Ptr((byte*)planes[0], strides[0], (byte*)planes[2], strides[2], (byte*)planes[1], strides[1]);
                break;
            case CorePixelFormat.Yuv422P:
                UploadYuv422PPtr((byte*)planes[0], strides[0], (byte*)planes[1], strides[1], (byte*)planes[2], strides[2]);
                break;
            case CorePixelFormat.Yuv444P:
                UploadYuv444PPtr((byte*)planes[0], strides[0], (byte*)planes[1], strides[1], (byte*)planes[2], strides[2]);
                break;
            case CorePixelFormat.Yuv422P10Le:
                UploadYuv422P10LePtr((byte*)planes[0], strides[0], (byte*)planes[1], strides[1], (byte*)planes[2], strides[2]);
                break;
            case CorePixelFormat.Nv12:
                UploadNv12Ptr((byte*)planes[0], strides[0], (byte*)planes[1], strides[1]);
                break;
            case CorePixelFormat.Nv21:
                UploadNv21Ptr((byte*)planes[0], strides[0], (byte*)planes[1], strides[1]);
                break;
            case CorePixelFormat.P010:
            case CorePixelFormat.P016:
                UploadSemiPlanar16Ptr((byte*)planes[0], strides[0], (byte*)planes[1], strides[1]);
                break;
            case CorePixelFormat.Uyvy:
                UploadUyvyPtr((byte*)planes[0], strides[0]);
                break;
            case CorePixelFormat.Yuyv:
                UploadYuyvPtr((byte*)planes[0], strides[0]);
                break;
            case CorePixelFormat.Gray8:
                UploadPlanarR8Ptr(0, (byte*)planes[0], strides[0], _format.Width, _format.Height);
                break;
            case CorePixelFormat.Gray16:
                UploadPlanarR16Ptr(0, (byte*)planes[0], strides[0], _format.Width, _format.Height);
                break;
            case CorePixelFormat.Yuv420P10Le:
            case CorePixelFormat.Yuv420P12Le:
                UploadPlanar420P16Ptr((byte*)planes[0], strides[0], (byte*)planes[1], strides[1],
                    (byte*)planes[2], strides[2]);
                break;
            case CorePixelFormat.Yuv444P10Le:
                UploadYuv444P10LePtr((byte*)planes[0], strides[0], (byte*)planes[1], strides[1],
                    (byte*)planes[2], strides[2]);
                break;
            case CorePixelFormat.Yuva420p:
                UploadYuva420Ptr((byte*)planes[0], strides[0], (byte*)planes[1], strides[1],
                    (byte*)planes[2], strides[2], (byte*)planes[3], strides[3]);
                break;
            default:
                throw new NotSupportedException($"Upload: {_format.PixelFormat}");
        }
    }

    public void Render(int viewportWidth, int viewportHeight) =>
        Render(0, 0, viewportWidth, viewportHeight, VideoViewportFit.Stretch);

    public void Render(int viewportWidth, int viewportHeight, VideoViewportFit fit) =>
        Render(0, 0, viewportWidth, viewportHeight, fit);

    public void Render(int x, int y, int viewportWidth, int viewportHeight) =>
        Render(x, y, viewportWidth, viewportHeight, VideoViewportFit.Stretch);

    public void Render(int x, int y, int viewportWidth, int viewportHeight, VideoViewportFit fit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var laid = VideoViewportLayout.Compute(x, y, viewportWidth, viewportHeight, _format.Width, _format.Height, fit);
        DrawFrame(laid.x, laid.y, laid.w, laid.h);
    }

    private void DrawFrame(int x, int y, int viewportWidth, int viewportHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gl.Viewport(x, y, (uint)viewportWidth, (uint)viewportHeight);
        _gl.UseProgram(_program);
        _gl.BindVertexArray(_vao);

        for (var i = 0; i < _textures.Length; i++)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + i);
            _gl.BindTexture(TextureTarget.Texture2D, _textures[i]);
            var samp = (_yPlaneMipmapsEnabled && _samplerYMipmap != 0 && i == 0) ? _samplerYMipmap : _samplerLinear;
            _gl.BindSampler((uint)i, samp);
        }

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        for (var i = 0; i < _textures.Length; i++)
            _gl.BindSampler((uint)i, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _nv12DmabufGpuUploader?.Dispose(); } catch { /* best-effort */ }

        for (var i = 0; i < _textures.Length; i++)
            if (_textures[i] != 0) { _gl.DeleteTexture(_textures[i]); _textures[i] = 0; }

        if (_samplerLinear != 0) { _gl.DeleteSampler(_samplerLinear); _samplerLinear = 0; }
        if (_samplerYMipmap != 0) { _gl.DeleteSampler(_samplerYMipmap); _samplerYMipmap = 0; }

        if (_shaderProgramCacheKey is not null)
        {
            SharedGlProgramCache.Release(_gl, _shaderProgramCacheKey);
            _program = 0;
        }
        else if (_program != 0)
        {
            _gl.DeleteProgram(_program);
            _program = 0;
        }

        if (_vao != 0) { _gl.DeleteVertexArray(_vao); _vao = 0; }
    }

    private void BuildPipeline()
    {
        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        var vertSrc = LoadShaderCached(_recipe.VertexFile);
        var fragSrc = LoadShaderCached(_recipe.FragmentFile);
        if (_shaderProgramCacheKey is not null)
            _program = SharedGlProgramCache.Acquire(_shaderProgramCacheKey, _gl, _ => LinkProgram(vertSrc, fragSrc));
        else
            _program = LinkProgram(vertSrc, fragSrc);
        _gl.UseProgram(_program);

        var samplerNames = _recipe.Samplers;
        for (var i = 0; i < samplerNames.Length; i++)
        {
            _samplerUniforms[i] = _gl.GetUniformLocation(_program, samplerNames[i]);
            if (_samplerUniforms[i] < 0)
                throw new InvalidOperationException($"shader missing sampler '{samplerNames[i]}'");
            _gl.Uniform1(_samplerUniforms[i], i);
        }

        _uYuvFlip = _gl.GetUniformLocation(_program, "yUvFlip");
        if (_uYuvFlip < 0)
            throw new InvalidOperationException("shader missing uniform 'yUvFlip'");
        ApplyYuvFlipUniform();

        _uBitScale = _gl.GetUniformLocation(_program, "bitScale");
        if (_uBitScale >= 0)
            _gl.Uniform1(_uBitScale, _recipe.DefaultBitScale);

        if (_recipe.NeedsYuvMatrix)
        {
            _uYuvOffset = _gl.GetUniformLocation(_program, "yuvOffset");
            _uYuvMatrix = _gl.GetUniformLocation(_program, "yuvMatrix");
            ApplyYuvColorUniforms();
        }

        _uFrameWidth = _gl.GetUniformLocation(_program, "frameWidth");
        _uHalfTexWidth = _gl.GetUniformLocation(_program, "halfTexWidth");
        if (_uFrameWidth >= 0 && _uHalfTexWidth >= 0)
        {
            _gl.Uniform1(_uFrameWidth, _format.Width);
            _gl.Uniform1(_uHalfTexWidth, PixelFormatInfo.ChromaWidth422(_format.Width));
        }

        _uHdrTransfer = _gl.GetUniformLocation(_program, "uHdrTransfer");
        _uHdrExposure = _gl.GetUniformLocation(_program, "uHdrExposure");
        ApplyHdrUniforms();

        var filter = _recipe.NearestSampling ? TextureMinFilter.Nearest : TextureMinFilter.Linear;
        var magFilter = _recipe.NearestSampling ? TextureMagFilter.Nearest : TextureMagFilter.Linear;

        for (var i = 0; i < _textures.Length; i++)
        {
            _textures[i] = _gl.GenTexture();
            _gl.ActiveTexture(TextureUnit.Texture0 + i);
            _gl.BindTexture(TextureTarget.Texture2D, _textures[i]);
            var (w, h) = _recipe.PlaneSize(_format, i);
            var (intFmt, fmt, type) = _recipe.PlaneGl(i);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, intFmt, (uint)w, (uint)h, 0, fmt, type, null);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)filter);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)magFilter);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }

        _samplerLinear = _gl.GenSampler();
        _gl.SamplerParameter(_samplerLinear, GLEnum.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.SamplerParameter(_samplerLinear, GLEnum.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        _gl.SamplerParameter(_samplerLinear, GLEnum.TextureMinFilter, (int)filter);
        _gl.SamplerParameter(_samplerLinear, GLEnum.TextureMagFilter, (int)magFilter);

        if (_yPlaneMipmapsEnabled)
        {
            _samplerYMipmap = _gl.GenSampler();
            var miniM = _recipe.NearestSampling
                ? TextureMinFilter.NearestMipmapNearest
                : TextureMinFilter.LinearMipmapLinear;
            _gl.SamplerParameter(_samplerYMipmap, GLEnum.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.SamplerParameter(_samplerYMipmap, GLEnum.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.SamplerParameter(_samplerYMipmap, GLEnum.TextureMinFilter, (int)miniM);
            _gl.SamplerParameter(_samplerYMipmap, GLEnum.TextureMagFilter, (int)magFilter);
        }
    }

    private void RegenerateYPlaneMipmapsIfNeeded()
    {
        if (_suppressYPlaneMipForLastGlDmabufUpload)
            return;
        if (!_yPlaneMipmapsEnabled || _textures.Length == 0 || _textures[0] == 0) return;
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _textures[0]);
        _gl.GenerateMipmap(TextureTarget.Texture2D);
    }

    private void ApplyHdrUniforms()
    {
        if (_program == 0) return;
        _gl.UseProgram(_program);
        if (_uHdrTransfer >= 0) _gl.Uniform1(_uHdrTransfer, (int)_hdrTransfer);
        if (_uHdrExposure >= 0) _gl.Uniform1(_uHdrExposure, _hdrPreviewExposure);
    }

    private void ApplyYuvFlipUniform()
    {
        if (_uYuvFlip < 0 || _program == 0) return;
        _gl.UseProgram(_program);
        _gl.Uniform1(_uYuvFlip, _yUvFlip);
    }

    private void ApplyYuvColorUniforms()
    {
        if (!_recipe.NeedsYuvMatrix || _program == 0) return;
        _gl.UseProgram(_program);

        if (_uYuvOffset >= 0)
        {
            var off = _colorSpace.Offset;
            _gl.Uniform3(_uYuvOffset, off[0], off[1], off[2]);
        }

        if (_uYuvMatrix >= 0)
        {
            fixed (float* p = _colorSpace.Matrix)
                _gl.UniformMatrix3(_uYuvMatrix, 1, transpose: true, p);
        }
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
                throw new InvalidOperationException($"shader program link failed: {log}");
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
            throw new InvalidOperationException($"{type} compile failed: {log}");
        }
        return s;
    }

    private void BeginUnpackSession()
    {
        _gl.GetInteger(GetPName.UnpackAlignment, out _savedUnpackAlignment);
        _gl.GetInteger(GetPName.UnpackRowLength, out _savedUnpackRowLength);
        _unpackSession = true;
        _lastUnpackAlignment = null;
        _lastUnpackRowLength = null;
    }

    private void EndUnpackSession()
    {
        if (!_unpackSession) return;
        _unpackSession = false;
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, _savedUnpackAlignment);
        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, _savedUnpackRowLength);
        _lastUnpackAlignment = null;
        _lastUnpackRowLength = null;
    }

    // --- Frame uploads (pin wrappers) ---
    private void UploadBgraFromFrame(VideoFrame f)
    {
        using var pin = f.Planes[0].Pin();
        UploadBgraPtr((byte*)pin.Pointer, f.Strides[0]);
    }

    /// RGBA‑ordered uploads for <see cref="CorePixelFormat.Rgba32"/> and FFmpeg‑compatible
    /// packed <see cref="CorePixelFormat.Argb32"/> / <see cref="CorePixelFormat.Abgr32"/> textures.
    private void UploadRgbaFromFrame(VideoFrame f)
    {
        using var pin = f.Planes[0].Pin();
        UploadRgbaPtr((byte*)pin.Pointer, f.Strides[0]);
    }

    private void UploadRgb24FromFrame(VideoFrame f)
    {
        using var pin = f.Planes[0].Pin();
        UploadRgb24Ptr((byte*)pin.Pointer, f.Strides[0]);
    }

    private void UploadBgr24FromFrame(VideoFrame f)
    {
        using var pin = f.Planes[0].Pin();
        UploadBgr24Ptr((byte*)pin.Pointer, f.Strides[0]);
    }

    private void UploadI420FromFrame(VideoFrame f)
    {
        using var p0 = f.Planes[0].Pin();
        using var p1 = f.Planes[1].Pin();
        using var p2 = f.Planes[2].Pin();
        UploadI420Ptr((byte*)p0.Pointer, f.Strides[0], (byte*)p1.Pointer, f.Strides[1],
            (byte*)p2.Pointer, f.Strides[2]);
    }

    private void UploadYv12FromFrame(VideoFrame f)
    {
        using var p0 = f.Planes[0].Pin();
        using var p1 = f.Planes[1].Pin();
        using var p2 = f.Planes[2].Pin();
        UploadYv12Ptr((byte*)p0.Pointer, f.Strides[0], (byte*)p2.Pointer, f.Strides[2],
            (byte*)p1.Pointer, f.Strides[1]);
    }

    private void UploadYuv422PFromFrame(VideoFrame f)
    {
        using var p0 = f.Planes[0].Pin();
        using var p1 = f.Planes[1].Pin();
        using var p2 = f.Planes[2].Pin();
        UploadYuv422PPtr((byte*)p0.Pointer, f.Strides[0], (byte*)p1.Pointer, f.Strides[1],
            (byte*)p2.Pointer, f.Strides[2]);
    }

    private void UploadYuv444PFromFrame(VideoFrame f)
    {
        using var p0 = f.Planes[0].Pin();
        using var p1 = f.Planes[1].Pin();
        using var p2 = f.Planes[2].Pin();
        UploadYuv444PPtr((byte*)p0.Pointer, f.Strides[0], (byte*)p1.Pointer, f.Strides[1],
            (byte*)p2.Pointer, f.Strides[2]);
    }

    private void UploadYuv422P10LeFromFrame(VideoFrame f)
    {
        using var p0 = f.Planes[0].Pin();
        using var p1 = f.Planes[1].Pin();
        using var p2 = f.Planes[2].Pin();
        UploadYuv422P10LePtr((byte*)p0.Pointer, f.Strides[0], (byte*)p1.Pointer, f.Strides[1],
            (byte*)p2.Pointer, f.Strides[2]);
    }

    private void UploadNv12Adaptive(VideoFrame frame) =>
        UploadNv12FromFrame(frame);

    private void UploadNv12FromFrame(VideoFrame f)
    {
        using var p0 = f.Planes[0].Pin();
        using var p1 = f.Planes[1].Pin();
        UploadNv12Ptr((byte*)p0.Pointer, f.Strides[0], (byte*)p1.Pointer, f.Strides[1]);
    }

    private void UploadNv21FromFrame(VideoFrame f) => UploadNv12FromFrame(f);

    private void UploadSemiPlanar16FromFrame(VideoFrame f)
    {
        using var p0 = f.Planes[0].Pin();
        using var p1 = f.Planes[1].Pin();
        UploadSemiPlanar16Ptr((byte*)p0.Pointer, f.Strides[0], (byte*)p1.Pointer, f.Strides[1]);
    }

    private void UploadUyvyFromFrame(VideoFrame f)
    {
        using var p = f.Planes[0].Pin();
        UploadUyvyPtr((byte*)p.Pointer, f.Strides[0]);
    }

    private void UploadYuyvFromFrame(VideoFrame f)
    {
        using var p = f.Planes[0].Pin();
        UploadYuyvPtr((byte*)p.Pointer, f.Strides[0]);
    }

    private void UploadGray8FromFrame(VideoFrame f)
    {
        using var p = f.Planes[0].Pin();
        UploadPlanarR8Ptr(0, (byte*)p.Pointer, f.Strides[0], _format.Width, _format.Height);
    }

    private void UploadGray16FromFrame(VideoFrame f)
    {
        using var p = f.Planes[0].Pin();
        UploadPlanarR16Ptr(0, (byte*)p.Pointer, f.Strides[0], _format.Width, _format.Height);
    }

    private void UploadPlanar420P16FromFrame(VideoFrame f)
    {
        using var p0 = f.Planes[0].Pin();
        using var p1 = f.Planes[1].Pin();
        using var p2 = f.Planes[2].Pin();
        UploadPlanar420P16Ptr((byte*)p0.Pointer, f.Strides[0], (byte*)p1.Pointer, f.Strides[1],
            (byte*)p2.Pointer, f.Strides[2]);
    }

    private void UploadYuv444P10LeFromFrame(VideoFrame f)
    {
        using var p0 = f.Planes[0].Pin();
        using var p1 = f.Planes[1].Pin();
        using var p2 = f.Planes[2].Pin();
        UploadYuv444P10LePtr((byte*)p0.Pointer, f.Strides[0], (byte*)p1.Pointer, f.Strides[1],
            (byte*)p2.Pointer, f.Strides[2]);
    }

    private void UploadYuva420FromFrame(VideoFrame f)
    {
        using var p0 = f.Planes[0].Pin();
        using var p1 = f.Planes[1].Pin();
        using var p2 = f.Planes[2].Pin();
        using var p3 = f.Planes[3].Pin();
        UploadYuva420Ptr((byte*)p0.Pointer, f.Strides[0], (byte*)p1.Pointer, f.Strides[1],
            (byte*)p2.Pointer, f.Strides[2], (byte*)p3.Pointer, f.Strides[3]);
    }

    // --- Native pointer uploads (shared paths) ---
    // Bgra: assumes little-endian 8-bit packing (memory order matches GL Bgra + UnsignedByte; big-endian hosts are rare/non-target).
    private void UploadBgraPtr(byte* basePtr, int stride)
    {
        BindUnit(0);
        SetUnpackAlignment(stride, _format.Width * 4);
        SetUnpackRowLength(stride / 4, _format.Width);
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)_format.Width, (uint)_format.Height,
            GlPixelFormat.Bgra, GlPixelType.UnsignedByte, basePtr);
    }

    private void UploadRgbaPtr(byte* basePtr, int stride)
    {
        BindUnit(0);
        SetUnpackAlignment(stride, _format.Width * 4);
        SetUnpackRowLength(stride / 4, _format.Width);
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)_format.Width, (uint)_format.Height,
            GlPixelFormat.Rgba, GlPixelType.UnsignedByte, basePtr);
    }

    private void UploadRgb24Ptr(byte* basePtr, int stride)
    {
        BindUnit(0);
        SetUnpackAlignment(stride, _format.Width * 3);
        SetUnpackRowLength(stride / 3, _format.Width);
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)_format.Width, (uint)_format.Height,
            GlPixelFormat.Rgb, GlPixelType.UnsignedByte, basePtr);
    }

    private void UploadBgr24Ptr(byte* basePtr, int stride)
    {
        BindUnit(0);
        SetUnpackAlignment(stride, _format.Width * 3);
        SetUnpackRowLength(stride / 3, _format.Width);
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)_format.Width, (uint)_format.Height,
            GlPixelFormat.Bgr, GlPixelType.UnsignedByte, basePtr);
    }

    private void UploadI420Ptr(byte* yPtr, int yStride, byte* uPtr, int uStride, byte* vPtr, int vStride)
    {
        var cw = PixelFormatInfo.ChromaWidth420(_format.Width);
        var ch = PixelFormatInfo.ChromaHeight420(_format.Height);
        UploadPlanarR8Ptr(0, yPtr, yStride, _format.Width, _format.Height);
        UploadPlanarR8Ptr(1, uPtr, uStride, cw, ch);
        UploadPlanarR8Ptr(2, vPtr, vStride, cw, ch);
    }

    private void UploadYv12Ptr(byte* yPtr, int yStride, byte* uSrcPtr, int uStride, byte* vSrcPtr, int vStride)
    {
        var cw = PixelFormatInfo.ChromaWidth420(_format.Width);
        var ch = PixelFormatInfo.ChromaHeight420(_format.Height);
        UploadPlanarR8Ptr(0, yPtr, yStride, _format.Width, _format.Height);
        UploadPlanarR8Ptr(1, uSrcPtr, uStride, cw, ch);
        UploadPlanarR8Ptr(2, vSrcPtr, vStride, cw, ch);
    }

    private void UploadYuv422PPtr(byte* yPtr, int ys, byte* uPtr, int us, byte* vPtr, int vs)
    {
        var cw = PixelFormatInfo.ChromaWidth422(_format.Width);
        var h = _format.Height;
        UploadPlanarR8Ptr(0, yPtr, ys, _format.Width, h);
        UploadPlanarR8Ptr(1, uPtr, us, cw, h);
        UploadPlanarR8Ptr(2, vPtr, vs, cw, h);
    }

    private void UploadYuv444PPtr(byte* yPtr, int ys, byte* uPtr, int us, byte* vPtr, int vs)
    {
        var w = _format.Width;
        var h = _format.Height;
        UploadPlanarR8Ptr(0, yPtr, ys, w, h);
        UploadPlanarR8Ptr(1, uPtr, us, w, h);
        UploadPlanarR8Ptr(2, vPtr, vs, w, h);
    }

    private void UploadYuv422P10LePtr(byte* yPtr, int ys, byte* uPtr, int us, byte* vPtr, int vs)
    {
        var cw = PixelFormatInfo.ChromaWidth422(_format.Width);
        var h = _format.Height;
        UploadPlanarR16Ptr(0, yPtr, ys, _format.Width, h);
        UploadPlanarR16Ptr(1, uPtr, us, cw, h);
        UploadPlanarR16Ptr(2, vPtr, vs, cw, h);
    }

    private void UploadPlanar420P16Ptr(byte* yPtr, int ys, byte* uPtr, int us, byte* vPtr, int vs)
    {
        var cw = PixelFormatInfo.ChromaWidth420(_format.Width);
        var ch = PixelFormatInfo.ChromaHeight420(_format.Height);
        UploadPlanarR16Ptr(0, yPtr, ys, _format.Width, _format.Height);
        UploadPlanarR16Ptr(1, uPtr, us, cw, ch);
        UploadPlanarR16Ptr(2, vPtr, vs, cw, ch);
    }

    private void UploadYuv444P10LePtr(byte* yPtr, int ys, byte* uPtr, int us, byte* vPtr, int vs)
    {
        var w = _format.Width;
        var h = _format.Height;
        UploadPlanarR16Ptr(0, yPtr, ys, w, h);
        UploadPlanarR16Ptr(1, uPtr, us, w, h);
        UploadPlanarR16Ptr(2, vPtr, vs, w, h);
    }

    private void UploadYuva420Ptr(byte* yPtr, int ys, byte* uPtr, int us, byte* vPtr, int vs,
        byte* aPtr, int astride)
    {
        var cw = PixelFormatInfo.ChromaWidth420(_format.Width);
        var ch = PixelFormatInfo.ChromaHeight420(_format.Height);
        UploadPlanarR8Ptr(0, yPtr, ys, _format.Width, _format.Height);
        UploadPlanarR8Ptr(1, uPtr, us, cw, ch);
        UploadPlanarR8Ptr(2, vPtr, vs, cw, ch);
        UploadPlanarR8Ptr(3, aPtr, astride, _format.Width, _format.Height);
    }

    private void UploadNv12Ptr(byte* yPtr, int yStride, byte* uvPtr, int uvStride)
    {
        var cw = PixelFormatInfo.ChromaWidth420(_format.Width);
        var ch = PixelFormatInfo.ChromaHeight420(_format.Height);
        UploadPlanarR8Ptr(0, yPtr, yStride, _format.Width, _format.Height);
        BindUnit(1);
        SetUnpackAlignment(uvStride, cw * 2);
        SetUnpackRowLength(uvStride / 2, cw);
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)cw, (uint)ch,
            GlPixelFormat.RG, GlPixelType.UnsignedByte, uvPtr);
    }

    private void UploadNv21Ptr(byte* yPtr, int yStride, byte* vuPtr, int vuStride) =>
        UploadNv12Ptr(yPtr, yStride, vuPtr, vuStride);

    private void UploadSemiPlanar16Ptr(byte* yPtr, int yStride, byte* uvPtr, int uvStride)
    {
        var cw = PixelFormatInfo.ChromaWidth420(_format.Width);
        var ch = PixelFormatInfo.ChromaHeight420(_format.Height);
        UploadPlanarR16Ptr(0, yPtr, yStride, _format.Width, _format.Height);
        BindUnit(1);
        SetUnpackAlignment(uvStride, cw * 4);
        SetUnpackRowLength(uvStride / 4, cw);
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)cw, (uint)ch,
            GlPixelFormat.RG, GlPixelType.UnsignedShort, uvPtr);
    }

    private void UploadUyvyPtr(byte* basePtr, int stride)
    {
        BindUnit(0);
        var texW = PixelFormatInfo.ChromaWidth422(_format.Width);
        SetUnpackAlignment(stride, _format.Width * 2);
        SetUnpackRowLength(stride / 4, texW);
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)texW, (uint)_format.Height,
            GlPixelFormat.Rgba, GlPixelType.UnsignedByte, basePtr);
    }

    private void UploadYuyvPtr(byte* basePtr, int stride)
    {
        BindUnit(0);
        var texW = PixelFormatInfo.ChromaWidth422(_format.Width);
        SetUnpackAlignment(stride, _format.Width * 2);
        SetUnpackRowLength(stride / 4, texW);
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)texW, (uint)_format.Height,
            GlPixelFormat.Rgba, GlPixelType.UnsignedByte, basePtr);
    }

    private void UploadPlanarR8Ptr(int textureUnit, byte* ptr, int strideBytes, int planeWidth, int planeHeight)
    {
        BindUnit(textureUnit);
        SetUnpackAlignment(strideBytes, planeWidth);
        SetUnpackRowLength(strideBytes, planeWidth);
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)planeWidth, (uint)planeHeight,
            GlPixelFormat.Red, GlPixelType.UnsignedByte, ptr);
    }

    private void UploadPlanarR16Ptr(int textureUnit, byte* ptr, int strideBytes, int planeWidth, int planeHeight)
    {
        if (strideBytes % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(strideBytes), "stride must be even for R16 uploads.");
        BindUnit(textureUnit);
        SetUnpackAlignment(strideBytes, planeWidth * 2);
        SetUnpackRowLength(strideBytes / 2, planeWidth);
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)planeWidth, (uint)planeHeight,
            GlPixelFormat.Red, GlPixelType.UnsignedShort, ptr);
    }

    private void BindUnit(int unit)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, _textures[unit]);
    }

    private void SetUnpackRowLength(int rowLengthPixels, int visiblePixelsPerRow)
    {
        var value = rowLengthPixels == visiblePixelsPerRow ? 0 : rowLengthPixels;
        if (_lastUnpackRowLength == value) return;
        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, value);
        _lastUnpackRowLength = value;
    }

    private void SetUnpackAlignment(int strideBytes, int visibleBytesPerRow)
    {
        var alignment = 1;
        if (strideBytes % 8 == 0) alignment = 8;
        else if (strideBytes % 4 == 0) alignment = 4;
        else if (strideBytes % 2 == 0) alignment = 2;
        alignment = Math.Min(alignment, 8);
        if (visibleBytesPerRow > 0 && strideBytes < visibleBytesPerRow)
            throw new ArgumentException("row stride shorter than visible row bytes", nameof(strideBytes));

        if (_lastUnpackAlignment == alignment) return;
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, alignment);
        _lastUnpackAlignment = alignment;
    }

    private static string LoadShaderCached(string fileName) =>
        ShaderSourceCache.GetOrAdd(fileName, LoadShaderUncached);

    private static string LoadShaderUncached(string fileName)
    {
        var asm = typeof(YuvVideoRenderer).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".Shaders.{fileName}", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"shader resource '{fileName}' not embedded");
        using var s = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"shader '{resourceName}' unavailable");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
