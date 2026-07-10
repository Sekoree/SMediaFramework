using S.Media.Compositor;
using Silk.NET.OpenGL;
using StbImageSharp;

namespace S.Media.Source.MMD;

/// <summary>
/// GPU renderer for an MMD scene as a compositor layer surface. Renders the skinned model into its own
/// color/depth FBO, then draws that into the canvas with the layer transform and opacity. Implements
/// diffuse/sphere/toon textures, shared toon ramps, material and UV morphs, specular light, per-vertex
/// edges, ordered alpha blending, and PMX/VMD-controlled directional self shadows.
///
/// <para>Threading: everything GL runs on the compositor thread (<see cref="ConfigureGl"/>/<see cref="Render"/>
/// per the <see cref="IVideoCompositorLayerSurface"/> contract). The surface owns a PRIVATE
/// <see cref="MMDAnimator"/> over the same documents as its source, so the source's own (CPU-fallback)
/// animator is never shared across threads - poses are pure functions of time, so both stay identical.</para>
///
/// <para>GL resource lifetime: <see cref="Dispose"/> can be called off the GL thread (the session releases
/// clips on its dispatcher), so it only marks the surface dead; the GL objects live until the composition
/// retires its compositor (context teardown frees them) - bounded by the composition's lifetime.</para>
/// </summary>
internal sealed class MMDGlLayerSurface : IVideoCompositorLayerSurface
{
    private readonly PMXDocument _model;
    private readonly MMDAnimator? _animator;
    private readonly MMDPhysics? _physics;
    private MMDBakedPhysics? _bakedPhysics;
    private Task<MMDBakedPhysics?>? _pendingBake;
    private TimeSpan _lastPhysicsTime = TimeSpan.MinValue;
    private readonly Func<TimeSpan, VMDCameraFrame> _camera;
    private readonly Func<TimeSpan, VMDLightFrame> _light;
    private readonly Func<TimeSpan, VMDSelfShadowFrame> _selfShadow;
    private readonly Func<TimeSpan, bool> _visibility;
    private readonly string _modelDirectory;
    private readonly int _sceneWidth;
    private readonly int _sceneHeight;

    private readonly Vector3[] _positions;
    private readonly Vector3[] _normals;
    private readonly float[] _vertexUpload;   // interleaved pos(3) + normal(3)
    private readonly float[] _uvUpload;       // static uv(2)
    private readonly float[] _additionalUv1Upload;
    private readonly float[] _edgeScaleUpload;

    private GL? _gl;
    private uint _fbo, _colorTex, _depthRbo;
    private uint _shadowFbo, _shadowDepthTex, _shadowColorRbo;
    private uint _msaaFbo, _msaaColorRbo, _msaaDepthRbo; // multisampled scene target (resolved into _fbo)
    private uint _vao, _dynamicVbo, _uvVbo, _additionalUv1Vbo, _edgeScaleVbo, _ebo;
    private uint _blitVao; // attribute-less quad still needs a bound VAO in core profile
    private uint _mainProgram, _edgeProgram, _shadowProgram, _blitProgram;
    private uint _whiteTex, _blackTex;
    private uint[] _materialTextures = [];
    private uint[] _sphereTextures = [];
    private uint[] _toonTextures = [];
    private readonly int _msaaSamples;
    private int _canvasWidth, _canvasHeight;
    private volatile bool _disposed;

    internal MMDGlLayerSurface(
        PMXDocument model,
        VMDDocument? motion,
        Func<TimeSpan, VMDCameraFrame> camera,
        Func<TimeSpan, VMDLightFrame> light,
        Func<TimeSpan, VMDSelfShadowFrame> selfShadow,
        Func<TimeSpan, bool> visibility,
        string modelDirectory,
        int sceneWidth,
        int sceneHeight,
        int msaaSamples = 4,
        bool physics = true,
        MMDBakedPhysics? bakedPhysics = null,
        Task<MMDBakedPhysics?>? pendingBake = null)
    {
        _msaaSamples = Math.Clamp(msaaSamples, 0, 8);
        _model = model;
        _animator = motion is not null ? new MMDAnimator(model, motion) : null;
        _physics = physics && _animator is not null ? MMDPhysics.TryCreate(model) : null;
        _bakedPhysics = _animator is not null ? bakedPhysics : null;
        _pendingBake = _animator is not null && bakedPhysics is null ? pendingBake : null;
        _camera = camera;
        _light = light;
        _selfShadow = selfShadow;
        _visibility = visibility;
        _modelDirectory = modelDirectory;
        _sceneWidth = Math.Max(sceneWidth, 16);
        _sceneHeight = Math.Max(sceneHeight, 16);

        _positions = new Vector3[model.Vertices.Count];
        _normals = new Vector3[model.Vertices.Count];
        _vertexUpload = new float[model.Vertices.Count * 6];
        _uvUpload = new float[model.Vertices.Count * 2];
        _additionalUv1Upload = new float[model.Vertices.Count * 4];
        _edgeScaleUpload = new float[model.Vertices.Count];
        for (var i = 0; i < model.Vertices.Count; i++)
        {
            _positions[i] = model.Vertices[i].Position; // bind pose until the first Evaluate
            _normals[i] = model.Vertices[i].Normal;
            _uvUpload[i * 2] = model.Vertices[i].Uv.X;
            _uvUpload[i * 2 + 1] = model.Vertices[i].Uv.Y;
            var additionalUv = model.Vertices[i].AdditionalUvs.Count > 0
                ? model.Vertices[i].AdditionalUvs[0]
                : Vector4.Zero;
            _additionalUv1Upload[i * 4] = additionalUv.X;
            _additionalUv1Upload[i * 4 + 1] = additionalUv.Y;
            _additionalUv1Upload[i * 4 + 2] = additionalUv.Z;
            _additionalUv1Upload[i * 4 + 3] = additionalUv.W;
            _edgeScaleUpload[i] = model.Vertices[i].EdgeScale;
        }
    }

    public unsafe void ConfigureGl(GL gl, S.Media.Core.Video.VideoFormat canvas)
    {
        _gl = gl;
        _canvasWidth = canvas.Width;
        _canvasHeight = canvas.Height;
        if (_fbo != 0)
            return; // canvas re-configure: scene FBO keeps its fixed scene size; only the blit target changed

        // Scene FBO: color texture + depth renderbuffer at the scene resolution.
        _colorTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _colorTex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
            (uint)_sceneWidth, (uint)_sceneHeight, 0, GLEnum.Rgba, GLEnum.UnsignedByte, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _depthRbo = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRbo);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24,
            (uint)_sceneWidth, (uint)_sceneHeight);
        _fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _colorTex, 0);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _depthRbo);
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new InvalidOperationException("MMDGlLayerSurface: scene framebuffer incomplete");

        // MSAA (operator request, toggleable via the mmd:// msaa param): the scene renders into
        // multisampled renderbuffers and resolves into the plain color texture the blit samples.
        if (_msaaSamples > 1)
        {
            _msaaColorRbo = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msaaColorRbo);
            gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)_msaaSamples,
                InternalFormat.Rgba8, (uint)_sceneWidth, (uint)_sceneHeight);
            _msaaDepthRbo = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _msaaDepthRbo);
            gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)_msaaSamples,
                InternalFormat.DepthComponent24, (uint)_sceneWidth, (uint)_sceneHeight);
            _msaaFbo = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _msaaFbo);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                RenderbufferTarget.Renderbuffer, _msaaColorRbo);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, _msaaDepthRbo);
            if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
                _msaaFbo = 0; // MSAA unsupported here - fall back to the aliased path silently
        }

        // Directional self-shadow map. A tiny color renderbuffer keeps the FBO portable across core
        // drivers that require an explicit draw attachment; only the sampled depth texture matters.
        const uint shadowSize = 1024;
        _shadowDepthTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _shadowDepthTex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24,
            shadowSize, shadowSize, 0, GLEnum.DepthComponent, GLEnum.UnsignedInt, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _shadowColorRbo = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _shadowColorRbo);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.R8, shadowSize, shadowSize);
        _shadowFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, _shadowDepthTex, 0);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            RenderbufferTarget.Renderbuffer, _shadowColorRbo);
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            _shadowFbo = 0;

        // Geometry: interleaved dynamic pos+normal, static uv, uint indices.
        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _dynamicVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _dynamicVbo);
        gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_vertexUpload.Length * sizeof(float)), null, BufferUsageARB.StreamDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)(3 * sizeof(float)));
        _uvVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _uvVbo);
        fixed (float* uv = _uvUpload)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_uvUpload.Length * sizeof(float)), uv, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        _additionalUv1Vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _additionalUv1Vbo);
        fixed (float* uv = _additionalUv1Upload)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_additionalUv1Upload.Length * sizeof(float)), uv, BufferUsageARB.StreamDraw);
        gl.EnableVertexAttribArray(3);
        gl.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
        _edgeScaleVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _edgeScaleVbo);
        fixed (float* scale = _edgeScaleUpload)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_edgeScaleUpload.Length * sizeof(float)), scale, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(4);
        gl.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, sizeof(float), (void*)0);
        _ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        var indices = new uint[_model.Indices.Count];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = (uint)_model.Indices[i];
        fixed (uint* idx = indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), idx, BufferUsageARB.StaticDraw);
        gl.BindVertexArray(0);

        _mainProgram = CompileProgram(gl, MainVs, MainFs);
        _edgeProgram = CompileProgram(gl, EdgeVs, EdgeFs);
        _shadowProgram = CompileProgram(gl, ShadowVs, ShadowFs);
        _blitProgram = CompileProgram(gl, BlitVs, BlitFs);
        _blitVao = gl.GenVertexArray();

        // 1×1 white/black fallbacks + per-material diffuse/sphere/toon textures. The sphere (.spa Add)
        // and toon ramps ARE the visible detail on many materials - YYB eyes are almost entirely their
        // additive sphere maps (operator report: "the eyes have no textures").
        _whiteTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _whiteTex);
        var white = stackalloc byte[] { 255, 255, 255, 255 };
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0, GLEnum.Rgba, GLEnum.UnsignedByte, white);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _blackTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _blackTex);
        var black = stackalloc byte[] { 0, 0, 0, 255 };
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0, GLEnum.Rgba, GLEnum.UnsignedByte, black);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);

        _materialTextures = new uint[_model.Materials.Count];
        _sphereTextures = new uint[_model.Materials.Count];
        _toonTextures = new uint[_model.Materials.Count];
        for (var m = 0; m < _model.Materials.Count; m++)
        {
            var material = _model.Materials[m];
            _materialTextures[m] = LoadMaterialTexture(gl, material.TextureIndex);
            _sphereTextures[m] = material.SphereMode == PMXSphereMode.None
                ? 0
                : LoadMaterialTexture(gl, material.SphereTextureIndex, fallback: 0);
            _toonTextures[m] = LoadMaterialTexture(gl, material.ToonTextureIndex, fallback: 0);
            if (_toonTextures[m] == 0 && material.SharedToonIndex >= 0)
                _toonTextures[m] = CreateSharedToonTexture(gl, material.SharedToonIndex);
        }
    }

    /// <summary>MMD's ten built-in shared toon slots. Slots 1-4 are hard two-tone ramps, 5-6 use
    /// a softer skin/gold transition, and 7-10 are neutral white. Keeping these in code avoids an
    /// external MMD installation just to render PMX materials that select a shared toon.</summary>
    private static unsafe uint CreateSharedToonTexture(GL gl, int index)
    {
        var dark = index switch
        {
            0 => new byte[] { 205, 205, 205 },
            1 => new byte[] { 245, 225, 225 },
            2 => new byte[] { 154, 154, 154 },
            3 => new byte[] { 248, 239, 235 },
            4 => new byte[] { 254, 231, 222 },
            5 => new byte[] { 195, 172, 3 },
            _ => new byte[] { 255, 255, 255 },
        };
        var pixels = new byte[32 * 4];
        for (var y = 0; y < 32; y++)
        {
            var transition = index switch
            {
                4 => Math.Clamp((y - 16f) / 12f, 0f, 1f),
                5 => Math.Clamp((y - 19f) / 7f, 0f, 1f),
                _ => y < 15 ? 0f : 1f,
            };
            var bright = index == 5 ? new Vector3(255, 237, 97) : new Vector3(255);
            pixels[y * 4] = (byte)float.Lerp(bright.X, dark[0], transition);
            pixels[y * 4 + 1] = (byte)float.Lerp(bright.Y, dark[1], transition);
            pixels[y * 4 + 2] = (byte)float.Lerp(bright.Z, dark[2], transition);
            pixels[y * 4 + 3] = 255;
        }
        var texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, texture);
        fixed (byte* data = pixels)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 32, 0,
                GLEnum.Rgba, GLEnum.UnsignedByte, data);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return texture;
    }

    private unsafe uint LoadMaterialTexture(GL gl, int textureIndex) =>
        LoadMaterialTexture(gl, textureIndex, _whiteTex);

    private unsafe uint LoadMaterialTexture(GL gl, int textureIndex, uint fallback)
    {
        if (textureIndex < 0 || textureIndex >= _model.Textures.Count)
            return fallback;
        var relative = _model.Textures[textureIndex].Replace('\\', Path.DirectorySeparatorChar);
        var path = ResolveTexturePath(_modelDirectory, relative);
        if (path is null)
        {
            // A missing texture degrades that material to white - loud in the log, because a model whose
            // textures ALL miss (a wrong folder, or case mismatches) renders "black and white".
            S.Media.Core.Diagnostics.MediaDiagnostics.LogWarning(
                "MMD: texture '{0}' not found under '{1}' - material renders untextured", relative, _modelDirectory);
            return fallback;
        }

        try
        {
            var image = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
            var tex = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, tex);
            fixed (byte* data = image.Data)
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                    (uint)image.Width, (uint)image.Height, 0, GLEnum.Rgba, GLEnum.UnsignedByte, data);
            gl.GenerateMipmap(TextureTarget.Texture2D);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
            return tex;
        }
        catch (Exception ex)
        {
            S.Media.Core.Diagnostics.MediaDiagnostics.LogWarning(
                "MMD: texture '{0}' failed to decode ({1}) - material renders untextured", path, ex.Message);
            return fallback; // undecodable texture - the material still draws with its diffuse color
        }
    }

    /// <summary>Resolves a model-relative texture path, falling back to a CASE-INSENSITIVE per-segment
    /// walk when the exact path misses - MMD models are authored on Windows, where `tex\Body.PNG` happily
    /// matches `tex/body.png`; on Linux it doesn't, and every miss used to silently render white.</summary>
    internal static string? ResolveTexturePath(string root, string relative)
    {
        var direct = Path.Combine(root, relative);
        if (File.Exists(direct))
            return direct;

        var current = root;
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var want = segments[i];
            var match = i == segments.Length - 1
                ? Directory.EnumerateFiles(current)
                    .FirstOrDefault(f => string.Equals(Path.GetFileName(f), want, StringComparison.OrdinalIgnoreCase))
                : Directory.EnumerateDirectories(current)
                    .FirstOrDefault(d => string.Equals(Path.GetFileName(d), want, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return null;
            current = match;
        }

        return current;
    }

    public unsafe void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity)
    {
        if (_disposed || _fbo == 0)
            return;

        // Pose at the transport's source time. With a BAKE the pose is a pure function of time
        // (seek-exact, cadence-immune - the pre-rendered physics the reference MMD videos have);
        // otherwise the stateful live solver advances by the wall delta and resets on seeks/jumps.
        if (_animator is not null)
        {
            if (_bakedPhysics is null && _pendingBake is { IsCompletedSuccessfully: true } landed)
            {
                _bakedPhysics = landed.Result;
                _pendingBake = null;
            }

            if (_bakedPhysics is not null)
            {
                _animator.Evaluate(masterTime, _positions, _normals, _bakedPhysics);
            }
            else
            {
                var physicsDelta = _lastPhysicsTime == TimeSpan.MinValue
                    ? -1f
                    : (float)(masterTime - _lastPhysicsTime).TotalSeconds;
                _lastPhysicsTime = masterTime;
                _animator.Evaluate(masterTime, _positions, _normals, _physics, physicsDelta);
            }
        }
        for (var i = 0; i < _positions.Length; i++)
        {
            _vertexUpload[i * 6] = _positions[i].X;
            _vertexUpload[i * 6 + 1] = _positions[i].Y;
            _vertexUpload[i * 6 + 2] = _positions[i].Z;
            _vertexUpload[i * 6 + 3] = _normals[i].X;
            _vertexUpload[i * 6 + 4] = _normals[i].Y;
            _vertexUpload[i * 6 + 5] = _normals[i].Z;
        }

        var camera = _camera(masterTime);
        var light = _light(masterTime);
        var selfShadow = _selfShadow(masterTime);
        var visible = _visibility(masterTime);
        var view = SceneView(camera);
        var viewProjection = view * SceneProjection(camera, (float)_sceneWidth / _sceneHeight);
        var lightViewProjection = SceneShadowMatrix(light, selfShadow);

        // --- scene pass (own FBO with depth; multisampled target when MSAA is on) --------------------
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _msaaFbo != 0 ? _msaaFbo : _fbo);
        gl.Viewport(0, 0, (uint)_sceneWidth, (uint)_sceneHeight);
        gl.Disable(EnableCap.ScissorTest);
        gl.ClearColor(0f, 0f, 0f, 0f);
        gl.ClearDepth(1.0);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.DepthMask(true); // the hosting compositor's 2D passes may leave depth writes off
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _dynamicVbo);
        fixed (float* v = _vertexUpload)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_vertexUpload.Length * sizeof(float)), v, BufferUsageARB.StreamDraw);
        if (_animator is not null)
        {
            var uvs = _animator.CurrentUvs;
            for (var i = 0; i < uvs.Count; i++)
            {
                _uvUpload[i * 2] = uvs[i].X;
                _uvUpload[i * 2 + 1] = uvs[i].Y;
            }
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _uvVbo);
            fixed (float* uv = _uvUpload)
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_uvUpload.Length * sizeof(float)), uv, BufferUsageARB.StreamDraw);
            var additionalUvs = _animator.CurrentAdditionalUv1;
            for (var i = 0; i < additionalUvs.Count; i++)
            {
                _additionalUv1Upload[i * 4] = additionalUvs[i].X;
                _additionalUv1Upload[i * 4 + 1] = additionalUvs[i].Y;
                _additionalUv1Upload[i * 4 + 2] = additionalUvs[i].Z;
                _additionalUv1Upload[i * 4 + 3] = additionalUvs[i].W;
            }
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, _additionalUv1Vbo);
            fixed (float* uv = _additionalUv1Upload)
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_additionalUv1Upload.Length * sizeof(float)), uv, BufferUsageARB.StreamDraw);
        }

        var shadowEnabled = visible && selfShadow.Mode != 0 && _shadowFbo != 0;
        if (shadowEnabled)
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
            gl.Viewport(0, 0, 1024, 1024);
            gl.ClearDepth(1.0);
            gl.Enable(EnableCap.DepthTest);
            gl.DepthMask(true);
            gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
            gl.UseProgram(_shadowProgram);
            gl.UniformMatrix4(gl.GetUniformLocation(_shadowProgram, "uLightViewProj"), 1, false,
                (float*)&lightViewProjection);
            gl.Uniform1(gl.GetUniformLocation(_shadowProgram, "uTexture"), 0);
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);
            gl.FrontFace(FrontFaceDirection.CW); // Z-flipped model winding (see the main pass note)
            var shadowOffset = 0;
            for (var m = 0; m < _model.Materials.Count; m++)
            {
                var material = _model.Materials[m];
                var state = _animator is not null
                    ? _animator.MaterialStates[m]
                    : MMDMaterialState.From(material);
                if (material.CastsSelfShadow && material.FaceVertexCount > 0 && state.Diffuse.W > 0f)
                {
                    if (material.DoubleSided) gl.Disable(EnableCap.CullFace);
                    else gl.Enable(EnableCap.CullFace);
                    gl.ActiveTexture(TextureUnit.Texture0);
                    gl.BindTexture(TextureTarget.Texture2D,
                        m < _materialTextures.Length && _materialTextures[m] != 0
                            ? _materialTextures[m]
                            : _whiteTex);
                    gl.Uniform1(gl.GetUniformLocation(_shadowProgram, "uAlpha"), state.Diffuse.W);
                    gl.DrawElements(PrimitiveType.Triangles, (uint)material.FaceVertexCount,
                        DrawElementsType.UnsignedInt, (void*)(shadowOffset * sizeof(uint)));
                }
                shadowOffset += material.FaceVertexCount;
            }
        }

        // The shadow pass temporarily owns the framebuffer and viewport; restore the already-cleared
        // scene target before regular material rendering.
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _msaaFbo != 0 ? _msaaFbo : _fbo);
        gl.Viewport(0, 0, (uint)_sceneWidth, (uint)_sceneHeight);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.DepthMask(true);

        gl.Enable(EnableCap.Blend);
        gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha,
            BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

        // Pass 1 - main shading in material order (MMD's transparency convention).
        // (MFP_MMD_GL_NOBLEND=1 disables blending - a render-debug knob.)
        if (Environment.GetEnvironmentVariable("MFP_MMD_GL_NOBLEND") == "1")
            gl.Disable(EnableCap.Blend);
        gl.UseProgram(_mainProgram);
        gl.UniformMatrix4(gl.GetUniformLocation(_mainProgram, "uViewProj"), 1, false, (float*)&viewProjection);
        gl.UniformMatrix4(gl.GetUniformLocation(_mainProgram, "uView"), 1, false, (float*)&view);
        gl.Uniform1(gl.GetUniformLocation(_mainProgram, "uTexture"), 0);
        gl.Uniform1(gl.GetUniformLocation(_mainProgram, "uSphere"), 1);
        gl.Uniform1(gl.GetUniformLocation(_mainProgram, "uToon"), 2);
        gl.Uniform1(gl.GetUniformLocation(_mainProgram, "uShadowMap"), 3);
        gl.Uniform1(gl.GetUniformLocation(_mainProgram, "uSphereDebug"),
            Environment.GetEnvironmentVariable("MFP_MMD_GL_SPHEREDEBUG") == "1" ? 1 : 0);
        gl.UniformMatrix4(gl.GetUniformLocation(_mainProgram, "uLightViewProj"), 1, false,
            (float*)&lightViewProjection);
        gl.ActiveTexture(TextureUnit.Texture3);
        gl.BindTexture(TextureTarget.Texture2D, _shadowDepthTex);
        var lightDirection = light.Direction;
        gl.Uniform3(gl.GetUniformLocation(_mainProgram, "uLightDirection"),
            lightDirection.X, lightDirection.Y, -lightDirection.Z);
        gl.Uniform3(gl.GetUniformLocation(_mainProgram, "uLightColor"),
            light.Color.X, light.Color.Y, light.Color.Z);
        gl.CullFace(TriangleFace.Back);
        // PMX winding is authored for MMD's left-handed space; the per-vertex Z-flip mirrors the
        // geometry and reverses winding, so the model's FRONT faces arrive CLOCKWISE in GL. Nearly
        // every YYB material is double-sided (masking this), but single-sided meshes - the classic
        // inset iris/eye-highlight shells - vanish under the default CCW convention (the "eyes have
        // no texture" report).
        gl.FrontFace(FrontFaceDirection.CW);
        // MFP_MMD_GL_ONLYMAT=<index>: draw a single material (render-debug isolation).
        var onlyMaterial = int.TryParse(
            Environment.GetEnvironmentVariable("MFP_MMD_GL_ONLYMAT"), out var om) ? om : -1;
        var offset = 0;
        for (var m = 0; visible && m < _model.Materials.Count; m++)
        {
            var material = _model.Materials[m];
            var materialState = _animator is not null
                ? _animator.MaterialStates[m]
                : MMDMaterialState.From(material);
            if (material.FaceVertexCount <= 0 || materialState.Diffuse.W <= 0f
                || (onlyMaterial >= 0 && m != onlyMaterial))
            {
                offset += material.FaceVertexCount;
                continue;
            }

            // MFP_MMD_GL_NOCULL=1: draw everything double-sided (render-debug knob).
            if (material.DoubleSided || Environment.GetEnvironmentVariable("MFP_MMD_GL_NOCULL") == "1")
                gl.Disable(EnableCap.CullFace);
            else gl.Enable(EnableCap.CullFace);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D,
                m < _materialTextures.Length && _materialTextures[m] != 0 ? _materialTextures[m] : _whiteTex);
            // Sphere map (.sph multiply / .spa add): the eye/face/hair detail on many models. Neutral
            // fallbacks (white for multiply, black for add) keep un-sphered materials unchanged.
            var sphereMode = m < _sphereTextures.Length && _sphereTextures[m] != 0
                ? material.SphereMode
                : PMXSphereMode.None;
            gl.ActiveTexture(TextureUnit.Texture1);
            gl.BindTexture(TextureTarget.Texture2D, sphereMode switch
            {
                PMXSphereMode.Multiply => _sphereTextures[m],
                PMXSphereMode.Add => _sphereTextures[m],
                PMXSphereMode.SubTexture => _sphereTextures[m],
                _ => _whiteTex,
            });
            gl.Uniform1(gl.GetUniformLocation(_mainProgram, "uSphereMode"), (int)sphereMode);
            // Toon ramp texture (shade tint); 0 = procedural two-tone fallback.
            var hasToon = m < _toonTextures.Length && _toonTextures[m] != 0;
            gl.ActiveTexture(TextureUnit.Texture2);
            gl.BindTexture(TextureTarget.Texture2D, hasToon ? _toonTextures[m] : _whiteTex);
            gl.Uniform1(gl.GetUniformLocation(_mainProgram, "uHasToon"), hasToon ? 1 : 0);
            SetColorPair("uTextureMultiply", "uTextureAdd", materialState.TextureMultiply, materialState.TextureAdd);
            SetColorPair("uSphereMultiply", "uSphereAdd", materialState.SphereMultiply, materialState.SphereAdd);
            SetColorPair("uToonMultiply", "uToonAdd", materialState.ToonMultiply, materialState.ToonAdd);
            var d = materialState.Diffuse;
            gl.Uniform4(gl.GetUniformLocation(_mainProgram, "uDiffuse"), d.X, d.Y, d.Z, d.W);
            var a = materialState.Ambient;
            gl.Uniform3(gl.GetUniformLocation(_mainProgram, "uAmbient"), a.X, a.Y, a.Z);
            gl.Uniform1(gl.GetUniformLocation(_mainProgram, "uShadowEnabled"),
                shadowEnabled && material.ReceivesSelfShadow ? 1 : 0);
            gl.Uniform1(gl.GetUniformLocation(_mainProgram, "uShadowStrength"),
                selfShadow.Mode == 2 ? 0.72f : 0.55f);
            var specular = materialState.Specular;
            gl.Uniform3(gl.GetUniformLocation(_mainProgram, "uSpecular"), specular.X, specular.Y, specular.Z);
            gl.Uniform1(gl.GetUniformLocation(_mainProgram, "uSpecularPower"), Math.Max(materialState.SpecularPower, 0.001f));
            gl.DrawElements(PrimitiveType.Triangles, (uint)material.FaceVertexCount,
                DrawElementsType.UnsignedInt, (void*)(offset * sizeof(uint)));
            offset += material.FaceVertexCount;

            void SetColorPair(string multiplyName, string addName, Vector4 multiply, Vector4 add)
            {
                gl.Uniform4(gl.GetUniformLocation(_mainProgram, multiplyName),
                    multiply.X, multiply.Y, multiply.Z, multiply.W);
                gl.Uniform4(gl.GetUniformLocation(_mainProgram, addName), add.X, add.Y, add.Z, add.W);
            }
        }
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.FrontFace(FrontFaceDirection.Ccw); // edge pass + host compositor expect the GL default

        // Pass 2 - MMD inverted-hull edges, AFTER the body so the shell can never occlude it: vertices
        // expanded along normals, and only the AWAY-facing hull kept. The scene's Z-flip inverts winding,
        // so away-facing here means culling GL-BACK faces (culling FRONT kept the camera-facing shell and
        // painted it OVER the model - the 2026-07-03 "see-through, wrong colors" report).
        // (MFP_MMD_GL_NOEDGE=1 skips the pass - a render-debug knob.)
        if (visible && Environment.GetEnvironmentVariable("MFP_MMD_GL_NOEDGE") != "1")
        {
            gl.UseProgram(_edgeProgram);
            gl.UniformMatrix4(gl.GetUniformLocation(_edgeProgram, "uViewProj"), 1, false, (float*)&viewProjection);
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);
            offset = 0;
            for (var m = 0; m < _model.Materials.Count; m++)
            {
                var material = _model.Materials[m];
                var materialState = _animator is not null
                    ? _animator.MaterialStates[m]
                    : MMDMaterialState.From(material);
                if (material.HasEdge && materialState.EdgeSize > 0f && materialState.EdgeColor.W > 0f)
                {
                    gl.Uniform1(gl.GetUniformLocation(_edgeProgram, "uEdgeScale"), materialState.EdgeSize * 0.03f);
                    var edge = materialState.EdgeColor;
                    gl.Uniform4(gl.GetUniformLocation(_edgeProgram, "uEdgeColor"), edge.X, edge.Y, edge.Z, edge.W);
                    gl.DrawElements(PrimitiveType.Triangles, (uint)material.FaceVertexCount,
                        DrawElementsType.UnsignedInt, (void*)(offset * sizeof(uint)));
                }
                offset += material.FaceVertexCount;
            }
        }

        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.DepthTest);
        gl.BindVertexArray(0);

        // MSAA resolve: blit the multisampled scene into the plain color texture the canvas blit samples.
        if (_msaaFbo != 0)
        {
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _msaaFbo);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _fbo);
            gl.BlitFramebuffer(0, 0, _sceneWidth, _sceneHeight, 0, 0, _sceneWidth, _sceneHeight,
                (uint)ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        }

        // --- blit into the canvas: layer transform maps scene rect → canvas pixels ------------------
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);
        gl.Viewport(0, 0, (uint)_canvasWidth, (uint)_canvasHeight);
        gl.UseProgram(_blitProgram);
        Span<float> ndc = stackalloc float[8];
        WriteCornerNdc(transform, ndc);
        fixed (float* c = ndc)
            gl.Uniform2(gl.GetUniformLocation(_blitProgram, "uCorners"), 4, c);
        gl.Uniform1(gl.GetUniformLocation(_blitProgram, "uOpacity"), Math.Clamp(opacity, 0f, 1f));
        gl.Uniform1(gl.GetUniformLocation(_blitProgram, "uScene"), 0);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _colorTex);
        gl.BindVertexArray(_blitVao);
        gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        gl.BindVertexArray(0);
    }

    /// <summary>Scene-corner NDC positions for the blit strip (order: TL, TR, BL, BR). The layer transform
    /// maps SCENE pixels (the surface's nominal source = canvas size, per the placement contract) into
    /// canvas pixels, top-left origin Y-down; NDC flips Y.</summary>
    private void WriteCornerNdc(LayerTransform2D transform, Span<float> ndc)
    {
        // Empirically the scene lands in the FBO rotated 180° relative to the software reference (the
        // System.Numerics row-vector clip path × GL's sampling conventions); the blit un-rotates by
        // mapping the strip's TL/TR/BL/BR to the transform of the OPPOSITE scene corners.
        Span<(float X, float Y)> corners =
        [
            transform.Apply(_canvasWidth, _canvasHeight),
            transform.Apply(0, _canvasHeight),
            transform.Apply(_canvasWidth, 0),
            transform.Apply(0, 0),
        ];
        for (var i = 0; i < 4; i++)
        {
            ndc[i * 2] = corners[i].X / _canvasWidth * 2f - 1f;
            ndc[i * 2 + 1] = 1f - corners[i].Y / _canvasHeight * 2f;
        }
    }

    /// <summary>The same MMD camera convention as <see cref="MMDSoftwareRenderer"/> (orbit target, Z-flip
    /// to right-handed happens per-vertex in the shaders). View and projection stay separate so the
    /// sphere-map pass can compute view-space normals.</summary>
    internal static Matrix4x4 SceneView(VMDCameraFrame camera)
    {
        var rotation =
            Matrix4x4.CreateRotationY(camera.RotationRadians.Y) *
            Matrix4x4.CreateRotationX(-camera.RotationRadians.X) *
            Matrix4x4.CreateRotationZ(-camera.RotationRadians.Z);
        var back = Vector3.TransformNormal(new Vector3(0, 0, 1), rotation);
        var eye = camera.Target + back * MathF.Abs(camera.Distance);
        var up = Vector3.TransformNormal(new Vector3(0, 1, 0), rotation);
        return Matrix4x4.CreateLookAt(eye, camera.Target, up);
    }

    internal static Matrix4x4 SceneProjection(VMDCameraFrame camera, float aspect)
    {
        var fov = Math.Clamp(camera.FovDegrees, 1f, 170f) * MathF.PI / 180f;
        return Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, 0.5f, 2000f);
    }

    internal static Matrix4x4 SceneViewProjection(VMDCameraFrame camera, float aspect) =>
        SceneView(camera) * SceneProjection(camera, aspect);

    private Matrix4x4 SceneShadowMatrix(VMDLightFrame light, VMDSelfShadowFrame shadow)
    {
        if (_positions.Length == 0)
            return Matrix4x4.Identity;
        var first = _positions[0];
        var min = new Vector3(first.X, first.Y, -first.Z);
        var max = min;
        for (var i = 1; i < _positions.Length; i++)
        {
            var p = new Vector3(_positions[i].X, _positions[i].Y, -_positions[i].Z);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        var center = (min + max) * 0.5f;
        var radius = Math.Max((max - min).Length() * 0.6f, 5f);
        var direction = new Vector3(light.Direction.X, light.Direction.Y, -light.Direction.Z);
        direction = direction.LengthSquared() > 1e-8f
            ? Vector3.Normalize(direction)
            : Vector3.Normalize(new Vector3(-0.5f, -1f, -0.5f));
        var depthRange = Math.Clamp(shadow.Distance, radius * 3f, 20_000f);
        var eye = center - direction * (depthRange * 0.5f + radius);
        var up = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.98f
            ? Vector3.UnitZ
            : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(eye, center, up);
        var projection = Matrix4x4.CreateOrthographic(radius * 2.4f, radius * 2.4f,
            0.1f, depthRange + radius * 3f);
        return view * projection;
    }

    private static uint CompileProgram(GL gl, string vsSource, string fsSource)
    {
        var vs = CompileShader(gl, ShaderType.VertexShader, vsSource);
        var fs = CompileShader(gl, ShaderType.FragmentShader, fsSource);
        var program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var ok);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        if (ok == 0)
            throw new InvalidOperationException($"MMDGlLayerSurface: program link failed: {gl.GetProgramInfoLog(program)}");
        return program;
    }

    private static uint CompileShader(GL gl, ShaderType type, string source)
    {
        var shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out var ok);
        if (ok == 0)
            throw new InvalidOperationException($"MMDGlLayerSurface: {type} compile failed: {gl.GetShaderInfoLog(shader)}");
        return shader;
    }

    public void Dispose()
    {
        _disposed = true; // GL objects follow the compositor's context (see class doc)
        _physics?.Dispose(); // ...but the native Bullet world is ours to free
    }

    // MMD is left-handed (+Z away); the shaders flip Z into GL's right-handed clip space - the same
    // conversion the software rasterizer applies. Visible faces then wind CCW (GL default front).
    private const string MainVs = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        layout(location = 3) in vec4 aAdditionalUv1;
        uniform mat4 uViewProj;
        uniform mat4 uView;
        uniform mat4 uLightViewProj;
        out vec3 vNormal;
        out vec3 vNormalView;
        out vec2 vUv;
        out vec2 vAdditionalUv1;
        out vec3 vPositionView;
        out vec4 vShadowCoord;
        void main()
        {
            vec4 worldPosition = vec4(aPos.x, aPos.y, -aPos.z, 1.0);
            gl_Position = uViewProj * worldPosition;
            vNormal = vec3(aNormal.x, aNormal.y, -aNormal.z);
            vNormalView = mat3(uView) * vNormal;
            vUv = aUv;
            vAdditionalUv1 = aAdditionalUv1.xy;
            vPositionView = (uView * worldPosition).xyz;
            vShadowCoord = uLightViewProj * worldPosition;
        }
        """;

    private const string MainFs = """
        #version 330 core
        in vec3 vNormal;
        in vec3 vNormalView;
        in vec2 vUv;
        in vec2 vAdditionalUv1;
        in vec3 vPositionView;
        in vec4 vShadowCoord;
        uniform sampler2D uTexture;
        uniform sampler2D uSphere;
        uniform sampler2D uToon;
        uniform sampler2D uShadowMap;
        uniform int uSphereMode; // 0 none, 1 multiply, 2 add, 3 sub-texture via additional UV1
        uniform int uHasToon;
        uniform vec4 uDiffuse;
        uniform vec3 uAmbient;
        uniform vec3 uSpecular;
        uniform float uSpecularPower;
        uniform vec4 uTextureMultiply;
        uniform vec4 uTextureAdd;
        uniform vec4 uSphereMultiply;
        uniform vec4 uSphereAdd;
        uniform vec4 uToonMultiply;
        uniform vec4 uToonAdd;
        uniform vec3 uLightDirection;
        uniform vec3 uLightColor;
        uniform mat4 uView;
        uniform int uShadowEnabled;
        uniform float uShadowStrength;
        uniform int uSphereDebug; // MFP_MMD_GL_SPHEREDEBUG=1: show only the sphere-map layer
        out vec4 fragColor;
        vec3 applyTextureColor(vec3 sampled, vec4 multiplyColor, vec4 additiveColor)
        {
            sampled = mix(vec3(1.0), sampled * multiplyColor.rgb, multiplyColor.a);
            return clamp(sampled + (sampled - vec3(1.0)) * additiveColor.a, 0.0, 1.0)
                + additiveColor.rgb;
        }
        float sampleShadow()
        {
            if (uShadowEnabled == 0) return 1.0;
            vec3 projected = vShadowCoord.xyz / vShadowCoord.w;
            projected = projected * 0.5 + 0.5;
            if (projected.x < 0.0 || projected.x > 1.0 || projected.y < 0.0 ||
                projected.y > 1.0 || projected.z < 0.0 || projected.z > 1.0)
                return 1.0;
            vec2 texel = 1.0 / vec2(textureSize(uShadowMap, 0));
            float visibility = 0.0;
            float bias = 0.0012;
            for (int y = -1; y <= 1; ++y)
                for (int x = -1; x <= 1; ++x)
                    visibility += projected.z - bias <= texture(uShadowMap,
                        projected.xy + vec2(x, y) * texel).r ? 1.0 : 0.0;
            return mix(1.0 - uShadowStrength, 1.0, visibility / 9.0);
        }
        void main()
        {
            vec4 rawTex = texture(uTexture, vUv);
            vec4 tex = vec4(applyTextureColor(rawTex.rgb, uTextureMultiply, uTextureAdd), rawTex.a);
            vec3 light = normalize(uLightDirection);
            vec3 normal = normalize(vNormal);
            float ndl = dot(normal, -light);
            // Lighting term (babylon-mmd's diffuseBase): the toon ramp - whose V axis encodes
            // lit(0) → shadow(1) - or a procedural two-tone fallback, then self-shadow × light colour.
            // BOTH the surface colour and the sphere reflection are modulated by this (see below).
            vec3 diffuseBase;
            if (uHasToon == 1)
                diffuseBase = applyTextureColor(
                    texture(uToon, vec2(0.5, clamp(0.5 - 0.5 * ndl, 0.01, 0.99))).rgb,
                    uToonMultiply, uToonAdd);
            else
                diffuseBase = vec3(mix(0.62, 1.0, smoothstep(0.02, 0.28, ndl)));
            diffuseBase *= sampleShadow() * uLightColor;
            // MMD/babylon-mmd composite: AMBIENT is added to the shaded diffuse and clamped, THEN modulates
            // the texture (finalDiffuse = clamp(diffuseBase*diffuse + ambient) * baseColor). The ambient is
            // NOT itself shaded - so high-ambient materials (the EYES) stay bright. The old model folded
            // ambient into the shaded product (tex * clamp(diffuse*0.8 + ambient*0.6) * diffuseBase), which
            // darkened the eyes below the MMD editor.
            vec3 color = clamp(diffuseBase * uDiffuse.rgb + uAmbient, 0.0, 1.0) * tex.rgb;
            // Sphere map (matcap): view-space normal xy → texture coords. Multiply (.sph) darkens/tints,
            // Add (.spa) is the highlight/iris detail layer. babylon-mmd modulates the reflection by the
            // lighting term before blending (sphereReflectionColor.rgb *= diffuseBase) so an additive eye
            // matcap is LIT and integrates with the shade - without it the .spa layer blasted a
            // full-bright, unlit "bullseye" over the iris (the wrong-looking eyes).
            vec2 sphereUv = normalize(vNormalView).xy * 0.5 + 0.5;
            // babylon-mmd samples the matcap un-flipped (viewSpaceNormal.xy*0.5+0.5), but it loads
            // textures invertY; our loader does not, so the V flip here reproduces the same orientation
            // (matcap highlight at the TOP of the eye).
            vec2 sphereSampleUv = uSphereMode == 3
                ? vAdditionalUv1
                : vec2(sphereUv.x, 1.0 - sphereUv.y);
            vec3 sphere = applyTextureColor(texture(uSphere, sphereSampleUv).rgb,
                uSphereMultiply, uSphereAdd);
            if (uSphereDebug == 1)
            {
                if (tex.a * uDiffuse.a < 0.005) discard;
                fragColor = vec4(uSphereMode == 0 ? vec3(1,0,1) : sphere, 1.0);
                return;
            }
            sphere *= diffuseBase;
            if (uSphereMode == 1 || uSphereMode == 3) color *= sphere;
            else if (uSphereMode == 2) color += sphere;
            vec3 lightView = normalize(mat3(uView) * light);
            vec3 normalView = normalize(vNormalView);
            vec3 viewDirection = normalize(-vPositionView);
            float specular = pow(max(dot(reflect(lightView, normalView), viewDirection), 0.0),
                max(uSpecularPower, 0.001));
            color += uSpecular * specular * uLightColor;
            float alpha = tex.a * uDiffuse.a;
            if (alpha < 0.005) discard;
            fragColor = vec4(clamp(color, 0.0, 1.0), alpha);
        }
        """;

    private const string ShadowVs = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 2) in vec2 aUv;
        uniform mat4 uLightViewProj;
        out vec2 vUv;
        void main()
        {
            gl_Position = uLightViewProj * vec4(aPos.x, aPos.y, -aPos.z, 1.0);
            vUv = aUv;
        }
        """;

    private const string ShadowFs = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uTexture;
        uniform float uAlpha;
        out vec4 fragColor;
        void main()
        {
            if (texture(uTexture, vUv).a * uAlpha < 0.005) discard;
            fragColor = vec4(1.0);
        }
        """;

    private const string EdgeVs = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 4) in float aEdgeScale;
        uniform mat4 uViewProj;
        uniform float uEdgeScale;
        void main()
        {
            vec3 p = aPos + aNormal * uEdgeScale * aEdgeScale;
            gl_Position = uViewProj * vec4(p.x, p.y, -p.z, 1.0);
        }
        """;

    private const string EdgeFs = """
        #version 330 core
        uniform vec4 uEdgeColor;
        out vec4 fragColor;
        void main() { fragColor = uEdgeColor; }
        """;

    private const string BlitVs = """
        #version 330 core
        uniform vec2 uCorners[4];
        out vec2 vUv;
        void main()
        {
            // Strip order TL,TR,BL,BR; scene texture is Y-down relative to GL texture space, so flip V.
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
