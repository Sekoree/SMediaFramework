using System.Diagnostics.CodeAnalysis;
using S.Media.Core.Audio;

namespace S.Media.Source.MMD;

/// <summary>The parsed pieces of an <c>mmd://</c> URI (see <see cref="MMDSourceUri"/>).</summary>
public sealed record MMDSourceRequest(
    string ModelPath,
    string? MotionPath,
    string? CameraMotionPath,
    int Width,
    int Height,
    // Manual camera override (used when no camera VMD is given — the HaPlay camera-placement controls).
    float? CameraDistance,
    Vector3? CameraTarget,
    Vector3? CameraRotationDegrees,
    float? CameraFovDegrees)
{
    /// <summary>MSAA for the GL renderer (URI <c>aa=0</c> disables — the operator toggle).</summary>
    public bool Antialias { get; init; } = true;

    /// <summary>Stage-5 physics (hair/skirt secondary motion; URI <c>phys=0</c> disables).</summary>
    public bool Physics { get; init; } = true;
}

/// <summary>
/// URI codec for MMD sources: <c>mmd://?model=…&amp;motion=…&amp;camera=…&amp;w=…&amp;h=…</c> plus manual
/// camera-override params (<c>dist</c>, <c>tx/ty/tz</c>, <c>rx/ry/rz</c> degrees, <c>fov</c>) for the
/// rudimentary camera-placement preview. Paths are escaped query values, so any file path round-trips.
/// </summary>
public static class MMDSourceUri
{
    public const string Scheme = "mmd";

    public static string Build(MMDSourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = new List<string> { "model=" + Uri.EscapeDataString(request.ModelPath) };
        if (request.MotionPath is { Length: > 0 } m) query.Add("motion=" + Uri.EscapeDataString(m));
        if (request.CameraMotionPath is { Length: > 0 } c) query.Add("camera=" + Uri.EscapeDataString(c));
        if (request.Width > 0) query.Add($"w={request.Width}");
        if (request.Height > 0) query.Add($"h={request.Height}");
        if (request.CameraDistance is { } d) query.Add($"dist={d:R}");
        if (request.CameraTarget is { } t)
        {
            query.Add($"tx={t.X:R}");
            query.Add($"ty={t.Y:R}");
            query.Add($"tz={t.Z:R}");
        }

        if (request.CameraRotationDegrees is { } r)
        {
            query.Add($"rx={r.X:R}");
            query.Add($"ry={r.Y:R}");
            query.Add($"rz={r.Z:R}");
        }

        if (request.CameraFovDegrees is { } f) query.Add($"fov={f:R}");
        if (!request.Antialias) query.Add("aa=0");
        if (!request.Physics) query.Add("phys=0");
        return $"{Scheme}://?{string.Join('&', query)}";
    }

    public static bool TryParse(string? uri, [NotNullWhen(true)] out MMDSourceRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(uri) || !uri.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase))
            return false;

        var q = uri.IndexOf('?');
        if (q < 0)
            return false;

        string? model = null, motion = null, camera = null;
        var width = 1280;
        var height = 720;
        float? dist = null, fov = null;
        var antialias = true;
        var physics = true;
        float? tx = null, ty = null, tz = null, rx = null, ry = null, rz = null;
        foreach (var pair in uri[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = pair[..eq];
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            switch (key)
            {
                case "model": model = value; break;
                case "motion": motion = value; break;
                case "camera": camera = value; break;
                case "w": _ = int.TryParse(value, out width); break;
                case "h": _ = int.TryParse(value, out height); break;
                case "dist": dist = ParseF(value); break;
                case "fov": fov = ParseF(value); break;
                case "tx": tx = ParseF(value); break;
                case "ty": ty = ParseF(value); break;
                case "tz": tz = ParseF(value); break;
                case "rx": rx = ParseF(value); break;
                case "ry": ry = ParseF(value); break;
                case "rz": rz = ParseF(value); break;
                case "aa": antialias = value != "0"; break;
                case "phys": physics = value != "0"; break;
            }
        }

        if (string.IsNullOrEmpty(model))
            return false;

        request = new MMDSourceRequest(
            model, motion, camera,
            Math.Clamp(width, 16, 7680), Math.Clamp(height, 16, 4320),
            dist,
            tx is not null && ty is not null && tz is not null ? new Vector3(tx.Value, ty.Value, tz.Value) : null,
            rx is not null && ry is not null && rz is not null ? new Vector3(rx.Value, ry.Value, rz.Value) : null,
            fov)
        {
            Antialias = antialias,
            Physics = physics,
        };
        return true;

        static float? ParseF(string s) =>
            float.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : null;
    }
}

/// <summary>
/// Pull-based BGRA video source rendering the animated MMD scene at 30 fps (the VMD timeline rate)
/// through the software renderer. Finite when a motion is present (its duration), else a 1-hour
/// hold of the bind pose for camera placement. Seekable — frames are pure functions of time, so a
/// seeks reset and re-simulate physics from the requested pose.
///
/// <para>NXT-10: also an <see cref="S.Media.Compositor.ILayerSurfaceVideoSource"/> — on a surface-hosting
/// (GL) compositor the session asks for an <see cref="MMDGlLayerSurface"/> and the scene renders GPU-side
/// with real materials/toon/edges; this source then stops software-rasterizing (its frames become a cheap
/// cached transparent buffer so priming/clock plumbing stays alive). On a CPU compositor the software
/// raster below remains the playback path.</para>
/// </summary>
public sealed class MMDVideoSource : IVideoSource, ISeekableSource, IDisposable,
    S.Media.Compositor.ILayerSurfaceVideoSource
{
    private readonly MMDAnimator? _animator;
    private readonly PMXDocument _model;
    private readonly VMDDocument? _motion;
    private readonly VMDDocument? _cameraMotion;
    private readonly MMDSourceRequest _request;
    private readonly MMDSoftwareRenderer _renderer;
    private readonly Vector3[] _positions;
    private readonly byte[] _pixels;
    private byte[]? _surfaceModeFrame; // cached transparent frame while the GL surface renders the scene
    private bool _cpuTexturesLoaded;
    private MMDPhysics? _physics;
    private MMDBakedPhysics? _bakedPhysics;
    private Task<MMDBakedPhysics?>? _pendingBake;
    private TimeSpan _lastPhysicsTime = TimeSpan.MinValue;
    private long _frameIndex;

    public MMDVideoSource(MMDSourceRequest request)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _model = PMXDocument.Load(request.ModelPath);
        _motion = request.MotionPath is { Length: > 0 } m ? VMDDocument.Load(m) : null;
        _cameraMotion = request.CameraMotionPath is { Length: > 0 } c ? VMDDocument.Load(c) : null;
        _animator = _motion is not null ? new MMDAnimator(_model, _motion) : null;
        _physics = request.Physics ? MMDPhysics.TryCreate(_model) : null;

        // PRE-BAKED physics (the way MMD's own renders are produced — an offline forward simulation):
        // a cached bake plays deterministically and seek-exactly; the first-ever open of this pair
        // starts one background bake and plays live physics meanwhile.
        if (_physics is not null && _motion is not null && _request.MotionPath is { Length: > 0 } motionPath)
        {
            var (ready, pending) = MMDPhysicsBakeCache.LoadOrStart(request.ModelPath, motionPath, _model, _motion);
            _bakedPhysics = ready;
            _pendingBake = ready is null ? pending : null;
        }

        Duration = _motion?.Duration ?? TimeSpan.FromHours(1);
        _renderer = new MMDSoftwareRenderer(request.Width, request.Height);
        _positions = new Vector3[_model.Vertices.Count];
        _pixels = new byte[request.Width * request.Height * 4];
        Format = new VideoFormat(request.Width, request.Height, PixelFormat.Bgra32, new Rational(30, 1));

        // No motion: bind pose (vertices as authored).
        if (_animator is null)
            for (var i = 0; i < _model.Vertices.Count; i++)
                _positions[i] = _model.Vertices[i].Position;
    }

    public VideoFormat Format { get; }

    public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];

    public bool IsExhausted => Position >= Duration;

    public TimeSpan Duration { get; }

    public TimeSpan Position => TimeSpan.FromTicks(_frameIndex * TimeSpan.TicksPerSecond / 30);

    public void SelectOutputFormat(PixelFormat format)
    {
        if (format != PixelFormat.Bgra32)
            throw new NotSupportedException("MMD software rendering outputs BGRA32 only");
    }

    public void Seek(TimeSpan position)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        _frameIndex = (long)(position.TotalSeconds * 30);
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        frame = null!;
        var time = Position;
        if (time >= Duration)
            return false;

        // Surface mode (NXT-10): the GL layer surface renders the scene; keep the frame stream alive for
        // priming/clock plumbing without rasterizing — one cached transparent buffer, correct PTS cadence.
        if (Volatile.Read(ref _surfaceModeFrame) is { } transparent)
        {
            _frameIndex++;
            frame = new VideoFrame(time, Format, transparent, _request.Width * 4);
            return true;
        }

        // Hot-swap to the bake the moment the background run lands (one-time pose blend; from then on
        // the pose is a pure function of time).
        if (_bakedPhysics is null && _pendingBake is { IsCompletedSuccessfully: true } landed)
        {
            _bakedPhysics = landed.Result;
            _pendingBake = null;
        }

        if (_animator is not null && _bakedPhysics is not null)
            _animator.Evaluate(time, _positions, normals: null, _bakedPhysics);
        else
            _animator?.Evaluate(time, _positions, normals: null, _physics, PhysicsDelta(time));
        var camera = ResolveCamera(time);
        EnsureCpuTextures();
        _renderer.Render(
            _model, _positions, camera, _pixels, ResolveLight(time), ResolveVisibility(time),
            _animator?.CurrentUvs, _animator?.MaterialStates);
        _frameIndex++;

        // Copy out: the renderer's buffer is reused per frame, the emitted frame must own its pixels.
        var copy = new byte[_pixels.Length];
        Buffer.BlockCopy(_pixels, 0, copy, 0, _pixels.Length);
        frame = new VideoFrame(time, Format, copy, _request.Width * 4);
        return true;
    }

    /// <summary>NXT-10: mint the GL renderer for this scene (its own animator over the same documents —
    /// never shares this source's mutable animator across threads) and switch this source's frame stream
    /// to the cheap surface-mode path.</summary>
    public S.Media.Compositor.IVideoCompositorLayerSurface CreateLayerSurface()
    {
        var surface = new MMDGlLayerSurface(
            _model,
            _motion,
            ResolveCamera,
            ResolveLight,
            ResolveSelfShadow,
            ResolveVisibility,
            Path.GetDirectoryName(Path.GetFullPath(_request.ModelPath)) ?? ".",
            _request.Width,
            _request.Height,
            msaaSamples: _request.Antialias ? 4 : 0,
            physics: _request.Physics,
            bakedPhysics: _bakedPhysics,
            pendingBake: _pendingBake);
        Volatile.Write(ref _surfaceModeFrame, new byte[_pixels.Length]); // all-zero BGRA = transparent
        return surface;
    }

    /// <summary>Elapsed simulation time since the previous rendered frame (negative/huge values make the
    /// physics reset — the seek/jump contract).</summary>
    private float PhysicsDelta(TimeSpan time)
    {
        var delta = _lastPhysicsTime == TimeSpan.MinValue ? -1f : (float)(time - _lastPhysicsTime).TotalSeconds;
        _lastPhysicsTime = time;
        return delta;
    }

    /// <summary>Loads the per-material diffuse textures for the CPU raster path once (preview dialog +
    /// CPU-compositor fallback) — shares the GL renderer's case-insensitive path resolution.</summary>
    private void EnsureCpuTextures()
    {
        if (_cpuTexturesLoaded)
            return;
        _cpuTexturesLoaded = true;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_request.ModelPath)) ?? ".";
            var set = new MMDSoftwareRenderer.MMDCpuTexture?[_model.Materials.Count];
            for (var m = 0; m < _model.Materials.Count; m++)
            {
                var texIndex = _model.Materials[m].TextureIndex;
                if (texIndex < 0 || texIndex >= _model.Textures.Count)
                    continue;
                var relative = _model.Textures[texIndex].Replace('\\', Path.DirectorySeparatorChar);
                if (MMDGlLayerSurface.ResolveTexturePath(dir, relative) is not { } path)
                    continue;
                try
                {
                    var image = StbImageSharp.ImageResult.FromMemory(
                        File.ReadAllBytes(path), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                    set[m] = new MMDSoftwareRenderer.MMDCpuTexture(image.Width, image.Height, image.Data);
                }
                catch
                {
                    // undecodable → flat diffuse color for that material
                }
            }

            _renderer.SetTextures(set);
        }
        catch
        {
            // texture loading is best-effort — the flat-shaded raster is still a valid preview
        }
    }

    private VMDCameraFrame ResolveCamera(TimeSpan time)
    {
        if (_cameraMotion is not null && _cameraMotion.CameraTrack.Count > 0)
            return MMDAnimator.SampleCamera(_cameraMotion, time);

        // Manual placement (HaPlay's rudimentary camera controls) or the MMD editor's default framing
        // (distance 45, target (0,10,0), fov 30 — operator feedback: the old −35/(0,12,0) sat too close).
        var rotation = _request.CameraRotationDegrees ?? Vector3.Zero;
        return new VMDCameraFrame(
            0,
            _request.CameraDistance ?? -45f,
            _request.CameraTarget ?? new Vector3(0, 10, 0),
            rotation * (MathF.PI / 180f),
            _request.CameraFovDegrees ?? 30f,
            true);
    }

    private VMDLightFrame ResolveLight(TimeSpan time)
    {
        var motion = _cameraMotion?.LightTrack.Count > 0 ? _cameraMotion : _motion;
        return motion is not null
            ? MMDAnimator.SampleLight(motion, time)
            : new VMDLightFrame(0, Vector3.One, new Vector3(-0.5f, -1f, 0.5f));
    }

    private bool ResolveVisibility(TimeSpan time) =>
        _motion is null || MMDAnimator.SampleVisibility(_motion, time);

    private VMDSelfShadowFrame ResolveSelfShadow(TimeSpan time)
    {
        var motion = _cameraMotion?.SelfShadowTrack.Count > 0 ? _cameraMotion : _motion;
        return motion is not null
            ? MMDAnimator.SampleSelfShadow(motion, time)
            : new VMDSelfShadowFrame(0, 0, 0f);
    }

    public void Dispose()
    {
        // All managed; nothing to release. (Kept for symmetry with other sources.)
    }
}

/// <summary>Registers the <c>mmd://</c> provider (video-only; MMD audio is the show's own audio cue —
/// the review's design keeps music as a normal source in the same transport group).</summary>
public sealed class MMDSourceModule : IMediaModule
{
    public string Name => "MMD";

    public void Register(IMediaRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddDecoder(new MMDDecoderProvider());
    }
}

public sealed class MMDDecoderProvider : IMediaDecoderProvider
{
    public string Name => "MMD";

    public double Probe(string uri, MediaKind kind) =>
        kind == MediaKind.Video && MMDSourceUri.TryParse(uri, out _) ? 1.0 : 0.0;

    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options)
    {
        if (!MMDSourceUri.TryParse(uri, out var request))
            throw new ArgumentException($"not an mmd:// source: '{uri}'", nameof(uri));
        if (!File.Exists(request.ModelPath))
            throw new FileNotFoundException($"PMX model not found: '{request.ModelPath}'", request.ModelPath);
        return new MMDVideoSource(request);
    }

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) =>
        throw new NotSupportedException("MMD sources are video-only; pair audio as its own cue in the same group");
}
