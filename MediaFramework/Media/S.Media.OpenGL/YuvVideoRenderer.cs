using System.Reflection;
using S.Media.Core.Video;
using GlPixelFormat = Silk.NET.OpenGL.PixelFormat;
using GlPixelType = Silk.NET.OpenGL.PixelType;
using GlInternalFormat = Silk.NET.OpenGL.InternalFormat;
using CorePixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.OpenGL;

/// <summary>
/// Renders <see cref="VideoFrame"/>s into the currently-bound OpenGL
/// framebuffer. Format-aware: picks the right shader + texture layout
/// based on the configured <see cref="VideoFormat"/>.
/// </summary>
/// <remarks>
/// <para>
/// The renderer does <strong>not</strong> own a window or GL context — the
/// caller (a windowing layer like SDL3, or Avalonia's GL surface) creates
/// the context, makes it current, then calls into the renderer. This lets
/// the same renderer power both a stand-alone SDL window and an embedded
/// Avalonia surface from one shader/texture pipeline.
/// </para>
/// <para>
/// Supported formats: <see cref="CorePixelFormat.Bgra32"/>,
/// <see cref="CorePixelFormat.I420"/>, <see cref="CorePixelFormat.Nv12"/>,
/// <see cref="CorePixelFormat.Yuv422P10Le"/>. The 10-bit format uploads
/// each sample as a 16-bit unsigned short into an R16 texture; the shader
/// scales by <c>65535/1023</c> to bring the 10 valid bits back to [0, 1].
/// </para>
/// <para>
/// Threading: every method must be called on the thread that owns the GL
/// context (the standard GL contract). The renderer holds no internal
/// synchronization.
/// </para>
/// </remarks>
public sealed unsafe class YuvVideoRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly VideoFormat _format;
    private readonly YuvColorSpace _colorSpace;

    private uint _program;
    private uint _vao;
    private readonly uint[] _textures;
    private readonly int[] _samplerUniforms;
    private int _uBitScale = -1;
    private int _uYuvOffset = -1;
    private int _uYuvMatrix = -1;
    private bool _disposed;

    /// <summary>The negotiated frame format the renderer is configured for.</summary>
    public VideoFormat Format => _format;
    /// <summary>The active YUV → RGB colour space (irrelevant for BGRA pass-through).</summary>
    public YuvColorSpace ColorSpace => _colorSpace;

    public YuvVideoRenderer(GL gl, VideoFormat format, YuvColorSpace? colorSpace = null)
    {
        ArgumentNullException.ThrowIfNull(gl);
        if (format.Width <= 0 || format.Height <= 0)
            throw new ArgumentException("video format must have positive dimensions", nameof(format));
        if (PlanesNeeded(format.PixelFormat) == 0)
            throw new NotSupportedException(
                $"YuvVideoRenderer does not support pixel format {format.PixelFormat}");

        _gl = gl;
        _format = format;
        _colorSpace = colorSpace
            ?? (format.PixelFormat == CorePixelFormat.Bgra32
                ? default
                : YuvColorSpace.DefaultForHeight(format.Height));
        _textures = new uint[PlanesNeeded(format.PixelFormat)];
        _samplerUniforms = new int[_textures.Length];

        BuildPipeline();
    }

    /// <summary>
    /// Upload <paramref name="frame"/>'s planes into the renderer's
    /// textures. Caller retains ownership of the frame — does not
    /// <see cref="VideoFrame.Dispose"/> it (the host typically does so
    /// after upload returns).
    /// </summary>
    public void Upload(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Format.PixelFormat != _format.PixelFormat
            || frame.Format.Width != _format.Width
            || frame.Format.Height != _format.Height)
            throw new ArgumentException(
                $"frame format {frame.Format} does not match renderer format {_format}", nameof(frame));

        switch (_format.PixelFormat)
        {
            case CorePixelFormat.Bgra32:
                UploadBgra(frame);
                break;
            case CorePixelFormat.I420:
                UploadI420(frame);
                break;
            case CorePixelFormat.Nv12:
                UploadNv12(frame);
                break;
            case CorePixelFormat.Yuv422P10Le:
                UploadYuv422P10Le(frame);
                break;
            default:
                throw new NotSupportedException($"Upload: {_format.PixelFormat}");
        }
    }

    /// <summary>
    /// Draw the most recently uploaded frame to the bound framebuffer.
    /// <paramref name="viewportWidth"/> and <paramref name="viewportHeight"/>
    /// set the GL viewport (caller can letter/pillar-box by passing a
    /// smaller-than-window region — for now we fill).
    /// </summary>
    public void Render(int viewportWidth, int viewportHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gl.Viewport(0, 0, (uint)viewportWidth, (uint)viewportHeight);
        _gl.Clear((uint)ClearBufferMask.ColorBufferBit);
        _gl.UseProgram(_program);
        _gl.BindVertexArray(_vao);

        for (var i = 0; i < _textures.Length; i++)
        {
            _gl.ActiveTexture(TextureUnit.Texture0 + i);
            _gl.BindTexture(TextureTarget.Texture2D, _textures[i]);
        }

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (var i = 0; i < _textures.Length; i++)
            if (_textures[i] != 0) { _gl.DeleteTexture(_textures[i]); _textures[i] = 0; }
        if (_program != 0) { _gl.DeleteProgram(_program); _program = 0; }
        if (_vao != 0)     { _gl.DeleteVertexArray(_vao); _vao = 0; }
    }

    // ---------- pipeline construction --------------------------------------

    private void BuildPipeline()
    {
        // VAO is required by Core profile even though we have no attributes.
        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        var (vsName, fsName) = ShaderNames(_format.PixelFormat);
        var vertSrc = LoadShader(vsName);
        var fragSrc = LoadShader(fsName);
        _program = LinkProgram(vertSrc, fragSrc);
        _gl.UseProgram(_program);

        // Sampler uniforms — bind each texture unit.
        var samplerNames = SamplerUniformNames(_format.PixelFormat);
        for (var i = 0; i < samplerNames.Length; i++)
        {
            _samplerUniforms[i] = _gl.GetUniformLocation(_program, samplerNames[i]);
            if (_samplerUniforms[i] < 0)
                throw new InvalidOperationException($"shader missing sampler uniform '{samplerNames[i]}'");
            _gl.Uniform1(_samplerUniforms[i], i);
        }

        if (_format.PixelFormat != CorePixelFormat.Bgra32)
        {
            _uBitScale  = _gl.GetUniformLocation(_program, "bitScale");
            _uYuvOffset = _gl.GetUniformLocation(_program, "yuvOffset");
            _uYuvMatrix = _gl.GetUniformLocation(_program, "yuvMatrix");

            if (_uBitScale >= 0)
            {
                var scale = _format.PixelFormat == CorePixelFormat.Yuv422P10Le
                    ? 65535f / 1023f
                    : 1f;
                _gl.Uniform1(_uBitScale, scale);
            }

            if (_uYuvOffset >= 0)
            {
                var off = _colorSpace.Offset;
                _gl.Uniform3(_uYuvOffset, off[0], off[1], off[2]);
            }

            if (_uYuvMatrix >= 0)
            {
                // Row-major in source; GLSL mat3 is column-major, so transpose
                // when uploading.
                fixed (float* p = _colorSpace.Matrix)
                    _gl.UniformMatrix3(_uYuvMatrix, 1, transpose: true, p);
            }
        }

        // Textures — sized once, content goes in via TexSubImage in Upload.
        for (var i = 0; i < _textures.Length; i++)
        {
            _textures[i] = _gl.GenTexture();
            _gl.ActiveTexture(TextureUnit.Texture0 + i);
            _gl.BindTexture(TextureTarget.Texture2D, _textures[i]);
            var (w, h) = PlaneTextureSize(_format, i);
            var (intFmt, fmt, type) = PlaneTextureFormat(_format.PixelFormat, i);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, intFmt, (uint)w, (uint)h, 0, fmt, type, null);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }
    }

    private uint LinkProgram(string vertSrc, string fragSrc)
    {
        var vs = CompileShader(ShaderType.VertexShader,   vertSrc);
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

    // ---------- per-format upload paths ------------------------------------

    private void UploadBgra(VideoFrame frame)
    {
        BindUnit(0);
        var width = _format.Width;
        var height = _format.Height;
        var stride = frame.Strides[0];
        SetUnpackAlignment(stride, width * 4);
        SetUnpackRowLength(stride / 4, width);
        using var pin = frame.Planes[0].Pin();
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)width, (uint)height,
            GlPixelFormat.Bgra, GlPixelType.UnsignedByte, pin.Pointer);
    }

    private void UploadI420(VideoFrame frame)
    {
        UploadPlanarR8(frame, 0, _format.Width,     _format.Height);
        UploadPlanarR8(frame, 1, _format.Width / 2, _format.Height / 2);
        UploadPlanarR8(frame, 2, _format.Width / 2, _format.Height / 2);
    }

    private void UploadNv12(VideoFrame frame)
    {
        UploadPlanarR8(frame, 0, _format.Width, _format.Height);

        BindUnit(1);
        var width = _format.Width / 2;
        var height = _format.Height / 2;
        var stride = frame.Strides[1];          // bytes; UV row is width*2 bytes
        SetUnpackAlignment(stride, width * 2);
        SetUnpackRowLength(stride / 2, width);  // pixels = bytes/2 (RG = 2bpp)
        using var pin = frame.Planes[1].Pin();
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)width, (uint)height,
            GlPixelFormat.RG, GlPixelType.UnsignedByte, pin.Pointer);
    }

    private void UploadYuv422P10Le(VideoFrame frame)
    {
        UploadPlanarR16(frame, 0, _format.Width,     _format.Height);
        UploadPlanarR16(frame, 1, _format.Width / 2, _format.Height);
        UploadPlanarR16(frame, 2, _format.Width / 2, _format.Height);
    }

    private void UploadPlanarR8(VideoFrame frame, int plane, int planeWidth, int planeHeight)
    {
        BindUnit(plane);
        var stride = frame.Strides[plane];
        SetUnpackAlignment(stride, planeWidth);
        SetUnpackRowLength(stride, planeWidth);
        using var pin = frame.Planes[plane].Pin();
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)planeWidth, (uint)planeHeight,
            GlPixelFormat.Red, GlPixelType.UnsignedByte, pin.Pointer);
    }

    private void UploadPlanarR16(VideoFrame frame, int plane, int planeWidth, int planeHeight)
    {
        BindUnit(plane);
        var strideBytes = frame.Strides[plane];
        var visibleBytes = planeWidth * 2;
        SetUnpackAlignment(strideBytes, visibleBytes);
        SetUnpackRowLength(strideBytes / 2, planeWidth);   // pixels = bytes/2 (16-bit per pixel)
        using var pin = frame.Planes[plane].Pin();
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)planeWidth, (uint)planeHeight,
            GlPixelFormat.Red, GlPixelType.UnsignedShort, pin.Pointer);
    }

    private void BindUnit(int unit)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, _textures[unit]);
    }

    private void SetUnpackRowLength(int rowLength, int visiblePixels)
    {
        // 0 = "row length equals width"; only set when stride differs.
        _gl.PixelStore(PixelStoreParameter.UnpackRowLength, rowLength == visiblePixels ? 0 : rowLength);
    }

    private void SetUnpackAlignment(int strideBytes, int visibleBytes)
    {
        // GL_UNPACK_ALIGNMENT default is 4. For non-multiples we need 1.
        // Codecs typically pad to 16/32 bytes which is fine at alignment 4
        // — but UV planes at half-width can land odd, so we pick the
        // largest power-of-two divisor of strideBytes (1, 2, 4, 8).
        int alignment = 1;
        if (strideBytes % 8 == 0) alignment = 8;
        else if (strideBytes % 4 == 0) alignment = 4;
        else if (strideBytes % 2 == 0) alignment = 2;
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, alignment);
        _ = visibleBytes; // reserved for future right-edge cropping
    }

    // ---------- format → shader / texture metadata -------------------------

    private static (string vert, string frag) ShaderNames(CorePixelFormat fmt) => fmt switch
    {
        CorePixelFormat.Bgra32      => ("fullscreen.vert.glsl", "bgra.frag.glsl"),
        CorePixelFormat.I420        => ("fullscreen.vert.glsl", "yuv_planar.frag.glsl"),
        CorePixelFormat.Nv12        => ("fullscreen.vert.glsl", "yuv_nv12.frag.glsl"),
        CorePixelFormat.Yuv422P10Le => ("fullscreen.vert.glsl", "yuv_planar.frag.glsl"),
        _ => throw new NotSupportedException($"ShaderNames: {fmt}"),
    };

    private static string[] SamplerUniformNames(CorePixelFormat fmt) => fmt switch
    {
        CorePixelFormat.Bgra32      => ["image"],
        CorePixelFormat.I420        => ["yPlane", "uPlane", "vPlane"],
        CorePixelFormat.Nv12        => ["yPlane", "uvPlane"],
        CorePixelFormat.Yuv422P10Le => ["yPlane", "uPlane", "vPlane"],
        _ => throw new NotSupportedException($"SamplerUniformNames: {fmt}"),
    };

    private static int PlanesNeeded(CorePixelFormat fmt) => fmt switch
    {
        CorePixelFormat.Bgra32      => 1,
        CorePixelFormat.I420        => 3,
        CorePixelFormat.Nv12        => 2,
        CorePixelFormat.Yuv422P10Le => 3,
        _ => 0,
    };

    private static (int width, int height) PlaneTextureSize(VideoFormat fmt, int plane) => fmt.PixelFormat switch
    {
        CorePixelFormat.Bgra32                         => (fmt.Width, fmt.Height),
        CorePixelFormat.I420 when plane == 0           => (fmt.Width, fmt.Height),
        CorePixelFormat.I420                           => (fmt.Width / 2, fmt.Height / 2),
        CorePixelFormat.Nv12 when plane == 0           => (fmt.Width, fmt.Height),
        CorePixelFormat.Nv12                           => (fmt.Width / 2, fmt.Height / 2),
        CorePixelFormat.Yuv422P10Le when plane == 0    => (fmt.Width, fmt.Height),
        CorePixelFormat.Yuv422P10Le                    => (fmt.Width / 2, fmt.Height),
        _ => throw new NotSupportedException($"PlaneTextureSize: {fmt.PixelFormat}"),
    };

    private static (GlInternalFormat intFmt, GlPixelFormat fmt, GlPixelType type) PlaneTextureFormat(
        CorePixelFormat fmt, int plane) => fmt switch
    {
        CorePixelFormat.Bgra32      => (GlInternalFormat.Rgba8, GlPixelFormat.Bgra, GlPixelType.UnsignedByte),
        CorePixelFormat.I420        => (GlInternalFormat.R8,    GlPixelFormat.Red,  GlPixelType.UnsignedByte),
        CorePixelFormat.Nv12 when plane == 0 => (GlInternalFormat.R8, GlPixelFormat.Red, GlPixelType.UnsignedByte),
        CorePixelFormat.Nv12        => (GlInternalFormat.RG8,   GlPixelFormat.RG,   GlPixelType.UnsignedByte),
        CorePixelFormat.Yuv422P10Le => (GlInternalFormat.R16,   GlPixelFormat.Red,  GlPixelType.UnsignedShort),
        _ => throw new NotSupportedException($"PlaneTextureFormat: {fmt}"),
    };

    // ---------- shader source loading --------------------------------------

    private static string LoadShader(string fileName)
    {
        var asm = typeof(YuvVideoRenderer).Assembly;
        // Embedded resource path is "<rootnamespace>.Shaders.<file>".
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".Shaders.{fileName}", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"shader resource '{fileName}' not embedded");
        using var s = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"shader resource '{resourceName}' could not be opened");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
