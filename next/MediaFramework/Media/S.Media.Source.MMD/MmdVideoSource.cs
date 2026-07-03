using System.Diagnostics.CodeAnalysis;
using S.Media.Core.Audio;

namespace S.Media.Source.MMD;

/// <summary>The parsed pieces of an <c>mmd://</c> URI (see <see cref="MmdSourceUri"/>).</summary>
public sealed record MmdSourceRequest(
    string ModelPath,
    string? MotionPath,
    string? CameraMotionPath,
    int Width,
    int Height,
    // Manual camera override (used when no camera VMD is given — the HaPlay camera-placement controls).
    float? CameraDistance,
    Vector3? CameraTarget,
    Vector3? CameraRotationDegrees,
    float? CameraFovDegrees);

/// <summary>
/// URI codec for MMD sources: <c>mmd://?model=…&amp;motion=…&amp;camera=…&amp;w=…&amp;h=…</c> plus manual
/// camera-override params (<c>dist</c>, <c>tx/ty/tz</c>, <c>rx/ry/rz</c> degrees, <c>fov</c>) for the
/// rudimentary camera-placement preview. Paths are escaped query values, so any file path round-trips.
/// </summary>
public static class MmdSourceUri
{
    public const string Scheme = "mmd";

    public static string Build(MmdSourceRequest request)
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
        return $"{Scheme}://?{string.Join('&', query)}";
    }

    public static bool TryParse(string? uri, [NotNullWhen(true)] out MmdSourceRequest? request)
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
            }
        }

        if (string.IsNullOrEmpty(model))
            return false;

        request = new MmdSourceRequest(
            model, motion, camera,
            Math.Clamp(width, 16, 7680), Math.Clamp(height, 16, 4320),
            dist,
            tx is not null && ty is not null && tz is not null ? new Vector3(tx.Value, ty.Value, tz.Value) : null,
            rx is not null && ry is not null && rz is not null ? new Vector3(rx.Value, ry.Value, rz.Value) : null,
            fov);
        return true;

        static float? ParseF(string s) =>
            float.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : null;
    }
}

/// <summary>
/// Pull-based BGRA video source rendering the animated MMD scene at 30 fps (the VMD timeline rate)
/// through the software renderer. Finite when a motion is present (its duration), else a 1-hour
/// hold of the bind pose for camera placement. Seekable — frames are pure functions of time, so a
/// seek is just a playhead move (deterministic; no physics in the prototype).
/// </summary>
public sealed class MmdVideoSource : IVideoSource, ISeekableSource, IDisposable
{
    private readonly MmdAnimator? _animator;
    private readonly PmxDocument _model;
    private readonly VmdDocument? _cameraMotion;
    private readonly MmdSourceRequest _request;
    private readonly MmdSoftwareRenderer _renderer;
    private readonly Vector3[] _positions;
    private readonly byte[] _pixels;
    private long _frameIndex;

    public MmdVideoSource(MmdSourceRequest request)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _model = PmxDocument.Load(request.ModelPath);
        var motion = request.MotionPath is { Length: > 0 } m ? VmdDocument.Load(m) : null;
        _cameraMotion = request.CameraMotionPath is { Length: > 0 } c ? VmdDocument.Load(c) : null;
        _animator = motion is not null ? new MmdAnimator(_model, motion) : null;
        Duration = motion?.Duration ?? TimeSpan.FromHours(1);
        _renderer = new MmdSoftwareRenderer(request.Width, request.Height);
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
            throw new NotSupportedException("MMD prototype renders BGRA32 only");
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

        _animator?.Evaluate(time, _positions);
        var camera = ResolveCamera(time);
        _renderer.Render(_model, _positions, camera, _pixels);
        _frameIndex++;

        // Copy out: the renderer's buffer is reused per frame, the emitted frame must own its pixels.
        var copy = new byte[_pixels.Length];
        Buffer.BlockCopy(_pixels, 0, copy, 0, _pixels.Length);
        frame = new VideoFrame(time, Format, copy, _request.Width * 4);
        return true;
    }

    private VmdCameraFrame ResolveCamera(TimeSpan time)
    {
        if (_cameraMotion is not null && _cameraMotion.CameraTrack.Count > 0)
            return MmdAnimator.SampleCamera(_cameraMotion, time);

        // Manual placement (HaPlay's rudimentary camera controls) or a sensible default framing.
        var rotation = _request.CameraRotationDegrees ?? Vector3.Zero;
        return new VmdCameraFrame(
            0,
            _request.CameraDistance ?? -35f,
            _request.CameraTarget ?? new Vector3(0, 12, 0),
            rotation * (MathF.PI / 180f),
            _request.CameraFovDegrees ?? 30f,
            true);
    }

    public void Dispose()
    {
        // All managed; nothing to release. (Kept for symmetry with other sources.)
    }
}

/// <summary>Registers the <c>mmd://</c> provider (video-only; MMD audio is the show's own audio cue —
/// the review's design keeps music as a normal source in the same transport group).</summary>
public sealed class MmdSourceModule : IMediaModule
{
    public string Name => "MMD";

    public void Register(IMediaRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddDecoder(new MmdDecoderProvider());
    }
}

public sealed class MmdDecoderProvider : IMediaDecoderProvider
{
    public string Name => "MMD";

    public double Probe(string uri, MediaKind kind) =>
        kind == MediaKind.Video && MmdSourceUri.TryParse(uri, out _) ? 1.0 : 0.0;

    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options)
    {
        if (!MmdSourceUri.TryParse(uri, out var request))
            throw new ArgumentException($"not an mmd:// source: '{uri}'", nameof(uri));
        if (!File.Exists(request.ModelPath))
            throw new FileNotFoundException($"PMX model not found: '{request.ModelPath}'", request.ModelPath);
        return new MmdVideoSource(request);
    }

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) =>
        throw new NotSupportedException("MMD sources are video-only; pair audio as its own cue in the same group");
}
