using S.Media.Compositor;
using Silk.NET.OpenGL;
using StbImageSharp;

namespace S.Media.Source.MMD;

/// <summary>
/// GPU renderer for an MMD scene as a compositor layer surface (Gate-6 stage: "real MMD materials via
/// NXT-10"). Renders the skinned model into its OWN color+depth FBO (the compositor's canvas FBO has no
/// depth), then draws that as a textured quad into the canvas applying the layer transform + opacity.
/// Material model v1: per-material diffuse texture (StbImageSharp — png/jpg/bmp/tga), procedural two-tone
/// toon ramp, MMD inverted-hull edge pass (edge flag/color/size), double-sided flag, material-order alpha
/// blending. Sphere maps and per-material toon ramp textures are the known next slice.
///
/// <para>Threading: everything GL runs on the compositor thread (<see cref="ConfigureGl"/>/<see cref="Render"/>
/// per the <see cref="IVideoCompositorLayerSurface"/> contract). The surface owns a PRIVATE
/// <see cref="MmdAnimator"/> over the same documents as its source, so the source's own (CPU-fallback)
/// animator is never shared across threads — poses are pure functions of time, so both stay identical.</para>
///
/// <para>GL resource lifetime: <see cref="Dispose"/> can be called off the GL thread (the session releases
/// clips on its dispatcher), so it only marks the surface dead; the GL objects live until the composition
/// retires its compositor (context teardown frees them) — bounded by the composition's lifetime.</para>
/// </summary>
internal sealed class MmdGlLayerSurface : IVideoCompositorLayerSurface
{
    private readonly PmxDocument _model;
    private readonly MmdAnimator? _animator;
    private readonly MmdPhysics? _physics;
    private TimeSpan _lastPhysicsTime = TimeSpan.MinValue;
    private readonly Func<TimeSpan, VmdCameraFrame> _camera;
    private readonly string _modelDirectory;
    private readonly int _sceneWidth;
    private readonly int _sceneHeight;

    private readonly Vector3[] _positions;
    private readonly Vector3[] _normals;
    private readonly float[] _vertexUpload;   // interleaved pos(3) + normal(3)
    private readonly float[] _uvUpload;       // static uv(2)

    private GL? _gl;
    private uint _fbo, _colorTex, _depthRbo;
    private uint _msaaFbo, _msaaColorRbo, _msaaDepthRbo; // multisampled scene target (resolved into _fbo)
    private uint _vao, _dynamicVbo, _uvVbo, _ebo;
    private uint _blitVao; // attribute-less quad still needs a bound VAO in core profile
    private uint _mainProgram, _edgeProgram, _blitProgram;
    private uint _whiteTex, _blackTex;
    private uint[] _materialTextures = [];
    private uint[] _sphereTextures = [];
    private uint[] _toonTextures = [];
    private readonly int _msaaSamples;
    private int _canvasWidth, _canvasHeight;
    private volatile bool _disposed;

    internal MmdGlLayerSurface(
        PmxDocument model,
        VmdDocument? motion,
        Func<TimeSpan, VmdCameraFrame> camera,
        string modelDirectory,
        int sceneWidth,
        int sceneHeight,
        int msaaSamples = 4,
        bool physics = true)
    {
        _msaaSamples = Math.Clamp(msaaSamples, 0, 8);
        _model = model;
        _animator = motion is not null ? new MmdAnimator(model, motion) : null;
        _physics = physics && _animator is not null ? MmdPhysics.TryCreate(model) : null;
        _camera = camera;
        _modelDirectory = modelDirectory;
        _sceneWidth = Math.Max(sceneWidth, 16);
        _sceneHeight = Math.Max(sceneHeight, 16);

        _positions = new Vector3[model.Vertices.Count];
        _normals = new Vector3[model.Vertices.Count];
        _vertexUpload = new float[model.Vertices.Count * 6];
        _uvUpload = new float[model.Vertices.Count * 2];
        for (var i = 0; i < model.Vertices.Count; i++)
        {
            _positions[i] = model.Vertices[i].Position; // bind pose until the first Evaluate
            _normals[i] = model.Vertices[i].Normal;
            _uvUpload[i * 2] = model.Vertices[i].Uv.X;
            _uvUpload[i * 2 + 1] = model.Vertices[i].Uv.Y;
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
            throw new InvalidOperationException("MmdGlLayerSurface: scene framebuffer incomplete");

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
                _msaaFbo = 0; // MSAA unsupported here — fall back to the aliased path silently
        }

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
        _blitProgram = CompileProgram(gl, BlitVs, BlitFs);
        _blitVao = gl.GenVertexArray();

        // 1×1 white/black fallbacks + per-material diffuse/sphere/toon textures. The sphere (.spa Add)
        // and toon ramps ARE the visible detail on many materials — YYB eyes are almost entirely their
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
            _sphereTextures[m] = material.SphereMode == PmxSphereMode.None
                ? 0
                : LoadMaterialTexture(gl, material.SphereTextureIndex, fallback: 0);
            _toonTextures[m] = LoadMaterialTexture(gl, material.ToonTextureIndex, fallback: 0);
        }
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
            // A missing texture degrades that material to white — loud in the log, because a model whose
            // textures ALL miss (a wrong folder, or case mismatches) renders "black and white".
            S.Media.Core.Diagnostics.MediaDiagnostics.LogWarning(
                "MMD: texture '{0}' not found under '{1}' — material renders untextured", relative, _modelDirectory);
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
                "MMD: texture '{0}' failed to decode ({1}) — material renders untextured", path, ex.Message);
            return fallback; // undecodable texture — the material still draws with its diffuse color
        }
    }

    /// <summary>Resolves a model-relative texture path, falling back to a CASE-INSENSITIVE per-segment
    /// walk when the exact path misses — MMD models are authored on Windows, where `tex\Body.PNG` happily
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

        // Pose at the transport's source time (the physics layer is stateful: it advances by the
        // wall delta between rendered frames and resets on seeks/jumps — the documented MMD behavior).
        if (_animator is not null)
        {
            var physicsDelta = _lastPhysicsTime == TimeSpan.MinValue
                ? -1f
                : (float)(masterTime - _lastPhysicsTime).TotalSeconds;
            _lastPhysicsTime = masterTime;
            _animator.Evaluate(masterTime, _positions, _normals, _physics, physicsDelta);
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
        var view = SceneView(camera);
        var viewProjection = view * SceneProjection(camera, (float)_sceneWidth / _sceneHeight);

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

        gl.Enable(EnableCap.Blend);
        gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha,
            BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

        // Pass 1 — main shading in material order (MMD's transparency convention).
        // (MFP_MMD_GL_NOBLEND=1 disables blending — a render-debug knob.)
        if (Environment.GetEnvironmentVariable("MFP_MMD_GL_NOBLEND") == "1")
            gl.Disable(EnableCap.Blend);
        gl.UseProgram(_mainProgram);
        gl.UniformMatrix4(gl.GetUniformLocation(_mainProgram, "uViewProj"), 1, false, (float*)&viewProjection);
        gl.UniformMatrix4(gl.GetUniformLocation(_mainProgram, "uView"), 1, false, (float*)&view);
        gl.Uniform1(gl.GetUniformLocation(_mainProgram, "uTexture"), 0);
        gl.Uniform1(gl.GetUniformLocation(_mainProgram, "uSphere"), 1);
        gl.Uniform1(gl.GetUniformLocation(_mainProgram, "uToon"), 2);
        gl.CullFace(TriangleFace.Back);
        var offset = 0;
        for (var m = 0; m < _model.Materials.Count; m++)
        {
            var material = _model.Materials[m];
            if (material.FaceVertexCount <= 0 || material.Diffuse.W <= 0f)
            {
                offset += material.FaceVertexCount;
                continue;
            }

            if (material.DoubleSided) gl.Disable(EnableCap.CullFace);
            else gl.Enable(EnableCap.CullFace);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D,
                m < _materialTextures.Length && _materialTextures[m] != 0 ? _materialTextures[m] : _whiteTex);
            // Sphere map (.sph multiply / .spa add): the eye/face/hair detail on many models. Neutral
            // fallbacks (white for multiply, black for add) keep un-sphered materials unchanged.
            var sphereMode = m < _sphereTextures.Length && _sphereTextures[m] != 0
                ? material.SphereMode
                : PmxSphereMode.None;
            gl.ActiveTexture(TextureUnit.Texture1);
            gl.BindTexture(TextureTarget.Texture2D, sphereMode switch
            {
                PmxSphereMode.Multiply => _sphereTextures[m],
                PmxSphereMode.Add => _sphereTextures[m],
                _ => _whiteTex,
            });
            gl.Uniform1(gl.GetUniformLocation(_mainProgram, "uSphereMode"), (int)sphereMode);
            // Toon ramp texture (shade tint); 0 = procedural two-tone fallback.
            var hasToon = m < _toonTextures.Length && _toonTextures[m] != 0;
            gl.ActiveTexture(TextureUnit.Texture2);
            gl.BindTexture(TextureTarget.Texture2D, hasToon ? _toonTextures[m] : _whiteTex);
            gl.Uniform1(gl.GetUniformLocation(_mainProgram, "uHasToon"), hasToon ? 1 : 0);
            var d = material.Diffuse;
            gl.Uniform4(gl.GetUniformLocation(_mainProgram, "uDiffuse"), d.X, d.Y, d.Z, d.W);
            var a = material.Ambient;
            gl.Uniform3(gl.GetUniformLocation(_mainProgram, "uAmbient"), a.X, a.Y, a.Z);
            gl.DrawElements(PrimitiveType.Triangles, (uint)material.FaceVertexCount,
                DrawElementsType.UnsignedInt, (void*)(offset * sizeof(uint)));
            offset += material.FaceVertexCount;
        }
        gl.ActiveTexture(TextureUnit.Texture0);

        // Pass 2 — MMD inverted-hull edges, AFTER the body so the shell can never occlude it: vertices
        // expanded along normals, and only the AWAY-facing hull kept. The scene's Z-flip inverts winding,
        // so away-facing here means culling GL-BACK faces (culling FRONT kept the camera-facing shell and
        // painted it OVER the model — the 2026-07-03 "see-through, wrong colors" report).
        // (MFP_MMD_GL_NOEDGE=1 skips the pass — a render-debug knob.)
        if (Environment.GetEnvironmentVariable("MFP_MMD_GL_NOEDGE") != "1")
        {
            gl.UseProgram(_edgeProgram);
            gl.UniformMatrix4(gl.GetUniformLocation(_edgeProgram, "uViewProj"), 1, false, (float*)&viewProjection);
            gl.Enable(EnableCap.CullFace);
            gl.CullFace(TriangleFace.Back);
            offset = 0;
            for (var m = 0; m < _model.Materials.Count; m++)
            {
                var material = _model.Materials[m];
                if (material.HasEdge && material.EdgeSize > 0f && material.EdgeColor.W > 0f)
                {
                    gl.Uniform1(gl.GetUniformLocation(_edgeProgram, "uEdgeScale"), material.EdgeSize * 0.03f);
                    var edge = material.EdgeColor;
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

    /// <summary>The same MMD camera convention as <see cref="MmdSoftwareRenderer"/> (orbit target, Z-flip
    /// to right-handed happens per-vertex in the shaders). View and projection stay separate so the
    /// sphere-map pass can compute view-space normals.</summary>
    internal static Matrix4x4 SceneView(VmdCameraFrame camera)
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

    internal static Matrix4x4 SceneProjection(VmdCameraFrame camera, float aspect)
    {
        var fov = Math.Clamp(camera.FovDegrees, 1f, 170f) * MathF.PI / 180f;
        return Matrix4x4.CreatePerspectiveFieldOfView(fov, aspect, 0.5f, 2000f);
    }

    internal static Matrix4x4 SceneViewProjection(VmdCameraFrame camera, float aspect) =>
        SceneView(camera) * SceneProjection(camera, aspect);

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
            throw new InvalidOperationException($"MmdGlLayerSurface: program link failed: {gl.GetProgramInfoLog(program)}");
        return program;
    }

    private static uint CompileShader(GL gl, ShaderType type, string source)
    {
        var shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out var ok);
        if (ok == 0)
            throw new InvalidOperationException($"MmdGlLayerSurface: {type} compile failed: {gl.GetShaderInfoLog(shader)}");
        return shader;
    }

    public void Dispose() => _disposed = true; // GL objects follow the compositor's context (see class doc)

    // MMD is left-handed (+Z away); the shaders flip Z into GL's right-handed clip space — the same
    // conversion the software rasterizer applies. Visible faces then wind CCW (GL default front).
    private const string MainVs = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        uniform mat4 uViewProj;
        uniform mat4 uView;
        out vec3 vNormal;
        out vec3 vNormalView;
        out vec2 vUv;
        void main()
        {
            gl_Position = uViewProj * vec4(aPos.x, aPos.y, -aPos.z, 1.0);
            vNormal = vec3(aNormal.x, aNormal.y, -aNormal.z);
            vNormalView = mat3(uView) * vNormal;
            vUv = aUv;
        }
        """;

    private const string MainFs = """
        #version 330 core
        in vec3 vNormal;
        in vec3 vNormalView;
        in vec2 vUv;
        uniform sampler2D uTexture;
        uniform sampler2D uSphere;
        uniform sampler2D uToon;
        uniform int uSphereMode; // 0 none, 1 multiply, 2 add, 3 sub-texture (treated as multiply)
        uniform int uHasToon;
        uniform vec4 uDiffuse;
        uniform vec3 uAmbient;
        out vec4 fragColor;
        void main()
        {
            vec4 tex = texture(uTexture, vUv);
            // MMD default key light, direction in the flipped (RH) space.
            vec3 light = normalize(vec3(-0.5, -1.0, -0.5));
            float ndl = dot(normalize(vNormal), -light);
            vec3 base = clamp(uDiffuse.rgb * 0.8 + uAmbient * 0.6, 0.0, 1.0);
            vec3 color = tex.rgb * base;
            // Toon: the ramp texture's V axis encodes lit(0) → shadow(1); procedural two-tone fallback.
            if (uHasToon == 1)
                color *= texture(uToon, vec2(0.5, clamp(0.5 - 0.5 * ndl, 0.01, 0.99))).rgb;
            else
                color *= mix(0.62, 1.0, smoothstep(0.02, 0.28, ndl));
            // Sphere map (matcap): view-space normal xy → texture coords. Multiply (.sph) darkens/tints,
            // Add (.spa) is the highlight/iris detail layer.
            vec2 sphereUv = normalize(vNormalView).xy * 0.5 + 0.5;
            vec3 sphere = texture(uSphere, vec2(sphereUv.x, 1.0 - sphereUv.y)).rgb;
            if (uSphereMode == 1 || uSphereMode == 3) color *= sphere;
            else if (uSphereMode == 2) color += sphere;
            float alpha = tex.a * uDiffuse.a;
            if (alpha < 0.005) discard;
            fragColor = vec4(clamp(color, 0.0, 1.0), alpha);
        }
        """;

    private const string EdgeVs = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        uniform mat4 uViewProj;
        uniform float uEdgeScale;
        void main()
        {
            vec3 p = aPos + aNormal * uEdgeScale;
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
