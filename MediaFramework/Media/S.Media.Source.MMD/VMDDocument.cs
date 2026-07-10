using System.Text;

namespace S.Media.Source.MMD;

/// <summary>One VMD bone keyframe. Interpolation is the MMD Bezier (x1,y1,x2,y2 per channel, 0..127).</summary>
public readonly record struct VMDBoneFrame(
    uint Frame,
    Vector3 Translation,
    Quaternion Rotation,
    byte XInterp0, byte XInterp1, byte XInterp2, byte XInterp3,
    byte YInterp0, byte YInterp1, byte YInterp2, byte YInterp3,
    byte ZInterp0, byte ZInterp1, byte ZInterp2, byte ZInterp3,
    byte RInterp0, byte RInterp1, byte RInterp2, byte RInterp3)
{
    /// <summary>MMD's per-bone physics toggle hidden in bytes 2/3 of the interpolation block.</summary>
    public bool PhysicsEnabled { get; init; } = true;
}

public readonly record struct VMDMorphFrame(uint Frame, float Weight);

/// <summary>One IK on/off key for a single IK bone (from the VMD show/IK section).</summary>
public readonly record struct VMDIkFrame(uint Frame, bool Enabled);

/// <summary>One VMD camera keyframe (MMD conventions: camera orbits <see cref="Target"/> at
/// <see cref="Distance"/> with XYZ euler <see cref="RotationRadians"/>; <see cref="FovDegrees"/> vertical).</summary>
public readonly record struct VMDCameraFrame(
    uint Frame,
    float Distance,
    Vector3 Target,
    Vector3 RotationRadians,
    float FovDegrees,
    bool Perspective)
{
    /// <summary>Six Bezier channels × (x1,x2,y1,y2): target XYZ, rotation, distance, FOV.</summary>
    public IReadOnlyList<byte> Interpolation { get; init; } = [];
}

public readonly record struct VMDLightFrame(uint Frame, Vector3 Color, Vector3 Direction);

/// <summary>VMD self-shadow key. Mode 0 disables shadows; modes 1/2 select MMD's two shadow-map
/// variants. Distance is exposed in MMD scene units (the packed VMD value is converted on load).</summary>
public readonly record struct VMDSelfShadowFrame(uint Frame, byte Mode, float Distance);

public readonly record struct VMDVisibilityFrame(uint Frame, bool Visible);

/// <summary>
/// Parsed VMD motion (review Gate-6 stage 1): bone tracks (grouped by Shift-JIS bone name), morph
/// tracks, camera/light/self-shadow tracks, and visibility/per-IK enable tracks from the show/IK
/// section. Frames are the MMD 30 fps timeline; conversion to session time is the animator's job.
/// </summary>
public sealed class VMDDocument
{
    public const double FramesPerSecond = 30.0;

    public required string ModelName { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<VMDBoneFrame>> BoneTracks { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<VMDMorphFrame>> MorphTracks { get; init; }
    public required IReadOnlyList<VMDCameraFrame> CameraTrack { get; init; }
    public IReadOnlyList<VMDLightFrame> LightTrack { get; init; } = [];
    public IReadOnlyList<VMDSelfShadowFrame> SelfShadowTrack { get; init; } = [];
    public IReadOnlyList<VMDVisibilityFrame> VisibilityTrack { get; init; } = [];

    /// <summary>IK on/off keys per IK bone name (step-sampled; a bone with no track is always on).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<VMDIkFrame>> IkEnableTracks { get; init; } =
        new Dictionary<string, IReadOnlyList<VMDIkFrame>>(StringComparer.Ordinal);

    /// <summary>Last keyframe across all tracks, as a 30 fps frame number.</summary>
    public uint LastFrame { get; init; }

    public TimeSpan Duration => TimeSpan.FromSeconds(LastFrame / FramesPerSecond);

    public static VMDDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static VMDDocument Load(Stream stream)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var shiftJis = Encoding.GetEncoding(932);
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var header = ReadFixedString(r, 30, shiftJis);
        if (!header.StartsWith("Vocaloid Motion Data", StringComparison.Ordinal))
            throw new PMXFormatException("not a VMD file (bad signature)");
        var modelName = ReadFixedString(r, 20, shiftJis);

        uint lastFrame = 0;

        // Bone frames
        var boneCount = ReadCount(r, "bone-frame");
        var boneTracks = new Dictionary<string, List<VMDBoneFrame>>(StringComparer.Ordinal);
        for (var i = 0; i < boneCount; i++)
        {
            var name = ReadFixedString(r, 15, shiftJis);
            var frame = r.ReadUInt32();
            var translation = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var rotation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var interp = r.ReadBytes(64);
            if (interp.Length != 64)
                throw new PMXFormatException("truncated VMD bone frame");
            lastFrame = Math.Max(lastFrame, frame);
            if (!boneTracks.TryGetValue(name, out var track))
                boneTracks[name] = track = [];
            // The packed 64-byte block interleaves the four channels' x1/y1/x2/y2 with stride 4.
            track.Add(new VMDBoneFrame(
                frame, translation, rotation,
                interp[0], interp[4], interp[8], interp[12],
                interp[1], interp[5], interp[9], interp[13],
                interp[2], interp[6], interp[10], interp[14],
                interp[3], interp[7], interp[11], interp[15])
            {
                // 0x0000 = on; 0x630f = off. Unknown/non-MMD blocks retain the compatible default on.
                PhysicsEnabled = interp[2] != 0x63 || interp[3] != 0x0f,
            });
        }

        foreach (var track in boneTracks.Values)
            track.Sort(static (a, b) => a.Frame.CompareTo(b.Frame));

        // Morph frames
        var morphCount = ReadCount(r, "morph-frame");
        var morphTracks = new Dictionary<string, List<VMDMorphFrame>>(StringComparer.Ordinal);
        for (var i = 0; i < morphCount; i++)
        {
            var name = ReadFixedString(r, 15, shiftJis);
            var frame = r.ReadUInt32();
            var weight = r.ReadSingle();
            lastFrame = Math.Max(lastFrame, frame);
            if (!morphTracks.TryGetValue(name, out var track))
                morphTracks[name] = track = [];
            track.Add(new VMDMorphFrame(frame, weight));
        }

        foreach (var track in morphTracks.Values)
            track.Sort(static (a, b) => a.Frame.CompareTo(b.Frame));

        // Camera frames (absent in model motions - the section may be missing entirely at EOF)
        var camera = new List<VMDCameraFrame>();
        if (stream.Position < stream.Length)
        {
            var cameraCount = ReadCount(r, "camera-frame");
            for (var i = 0; i < cameraCount; i++)
            {
                var frame = r.ReadUInt32();
                var distance = r.ReadSingle();
                var target = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var rotation = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var interpolation = r.ReadBytes(24);
                if (interpolation.Length != 24)
                    throw new PMXFormatException("truncated VMD camera interpolation");
                var fov = r.ReadUInt32();
                var perspective = r.ReadByte() == 0; // 0 = perspective on in VMD
                lastFrame = Math.Max(lastFrame, frame);
                camera.Add(new VMDCameraFrame(frame, distance, target, rotation, fov, perspective)
                {
                    Interpolation = interpolation,
                });
            }

            camera.Sort(static (a, b) => a.Frame.CompareTo(b.Frame));
        }

        var lights = new List<VMDLightFrame>();
        if (stream.Position < stream.Length)
        {
            var lightCount = ReadCount(r, "light-frame");
            for (var i = 0; i < lightCount; i++)
            {
                var frame = r.ReadUInt32();
                var color = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var direction = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                lights.Add(new VMDLightFrame(frame, color, direction));
                lastFrame = Math.Max(lastFrame, frame);
            }
            lights.Sort(static (a, b) => a.Frame.CompareTo(b.Frame));
        }

        var shadows = new List<VMDSelfShadowFrame>();
        if (stream.Position < stream.Length)
        {
            var shadowCount = ReadCount(r, "self-shadow-frame");
            for (var i = 0; i < shadowCount; i++)
            {
                var frame = r.ReadUInt32();
                var mode = r.ReadByte();
                if (mode > 2)
                    throw new PMXFormatException($"invalid VMD self-shadow mode {mode}");
                var packedDistance = r.ReadSingle();
                shadows.Add(new VMDSelfShadowFrame(frame, mode, 10000f - packedDistance * 100000f));
                lastFrame = Math.Max(lastFrame, frame);
            }
            shadows.Sort(static (a, b) => a.Frame.CompareTo(b.Frame));
        }

        // Show/IK frames: each key carries the model-visible flag plus per-IK-bone on/off.
        var ikTracks = new Dictionary<string, List<VMDIkFrame>>(StringComparer.Ordinal);
        var visibility = new List<VMDVisibilityFrame>();
        if (stream.Position < stream.Length)
        {
            var ikFrameCount = ReadCount(r, "show-ik-frame");
            for (var i = 0; i < ikFrameCount; i++)
            {
                var frame = r.ReadUInt32();
                visibility.Add(new VMDVisibilityFrame(frame, r.ReadByte() != 0));
                var ikCount = ReadCount(r, "ik-state");
                for (var k = 0; k < ikCount; k++)
                {
                    var boneName = ReadFixedString(r, 20, shiftJis);
                    var enabled = r.ReadByte() != 0;
                    if (!ikTracks.TryGetValue(boneName, out var track))
                        ikTracks[boneName] = track = [];
                    track.Add(new VMDIkFrame(frame, enabled));
                }

                lastFrame = Math.Max(lastFrame, frame);
            }

            foreach (var track in ikTracks.Values)
                track.Sort(static (a, b) => a.Frame.CompareTo(b.Frame));
            visibility.Sort(static (a, b) => a.Frame.CompareTo(b.Frame));
        }

        return new VMDDocument
        {
            ModelName = modelName,
            BoneTracks = boneTracks.ToDictionary(
                kv => kv.Key, IReadOnlyList<VMDBoneFrame> (kv) => kv.Value, StringComparer.Ordinal),
            MorphTracks = morphTracks.ToDictionary(
                kv => kv.Key, IReadOnlyList<VMDMorphFrame> (kv) => kv.Value, StringComparer.Ordinal),
            CameraTrack = camera,
            LightTrack = lights,
            SelfShadowTrack = shadows,
            VisibilityTrack = visibility,
            IkEnableTracks = ikTracks.ToDictionary(
                kv => kv.Key, IReadOnlyList<VMDIkFrame> (kv) => kv.Value, StringComparer.Ordinal),
            LastFrame = lastFrame,
        };
    }

    private static int ReadCount(BinaryReader r, string what)
    {
        var n = r.ReadUInt32();
        if (n > 20_000_000)
            throw new PMXFormatException($"implausible VMD {what} count {n}");
        return (int)n;
    }

    /// <summary>Fixed-width Shift-JIS field, terminated by NUL (garbage after the NUL is normal in VMD).</summary>
    private static string ReadFixedString(BinaryReader r, int bytes, Encoding encoding)
    {
        var raw = r.ReadBytes(bytes);
        if (raw.Length != bytes)
            throw new PMXFormatException("unexpected end of VMD file");
        var nul = Array.IndexOf(raw, (byte)0);
        return encoding.GetString(raw, 0, nul < 0 ? raw.Length : nul);
    }
}
