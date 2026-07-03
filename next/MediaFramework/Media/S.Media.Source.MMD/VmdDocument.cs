using System.Text;

namespace S.Media.Source.MMD;

/// <summary>One VMD bone keyframe. Interpolation is the MMD Bezier (x1,y1,x2,y2 per channel, 0..127).</summary>
public readonly record struct VmdBoneFrame(
    uint Frame,
    Vector3 Translation,
    Quaternion Rotation,
    byte XInterp0, byte XInterp1, byte XInterp2, byte XInterp3,
    byte YInterp0, byte YInterp1, byte YInterp2, byte YInterp3,
    byte ZInterp0, byte ZInterp1, byte ZInterp2, byte ZInterp3,
    byte RInterp0, byte RInterp1, byte RInterp2, byte RInterp3);

public readonly record struct VmdMorphFrame(uint Frame, float Weight);

/// <summary>One VMD camera keyframe (MMD conventions: camera orbits <see cref="Target"/> at
/// <see cref="Distance"/> with XYZ euler <see cref="RotationRadians"/>; <see cref="FovDegrees"/> vertical).</summary>
public readonly record struct VmdCameraFrame(
    uint Frame,
    float Distance,
    Vector3 Target,
    Vector3 RotationRadians,
    float FovDegrees,
    bool Perspective);

/// <summary>
/// Parsed VMD motion (review Gate-6 stage 1): bone tracks (grouped by Shift-JIS bone name), morph
/// tracks, and the camera track. Light/self-shadow/IK-enable sections are ignored (out of prototype
/// scope). Frames are the MMD 30 fps timeline; conversion to session time is the animator's job.
/// </summary>
public sealed class VmdDocument
{
    public const double FramesPerSecond = 30.0;

    public required string ModelName { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<VmdBoneFrame>> BoneTracks { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<VmdMorphFrame>> MorphTracks { get; init; }
    public required IReadOnlyList<VmdCameraFrame> CameraTrack { get; init; }

    /// <summary>Last keyframe across all tracks, as a 30 fps frame number.</summary>
    public uint LastFrame { get; init; }

    public TimeSpan Duration => TimeSpan.FromSeconds(LastFrame / FramesPerSecond);

    public static VmdDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static VmdDocument Load(Stream stream)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var shiftJis = Encoding.GetEncoding(932);
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var header = ReadFixedString(r, 30, shiftJis);
        if (!header.StartsWith("Vocaloid Motion Data", StringComparison.Ordinal))
            throw new PmxFormatException("not a VMD file (bad signature)");
        var modelName = ReadFixedString(r, 20, shiftJis);

        uint lastFrame = 0;

        // Bone frames
        var boneCount = ReadCount(r, "bone-frame");
        var boneTracks = new Dictionary<string, List<VmdBoneFrame>>(StringComparer.Ordinal);
        for (var i = 0; i < boneCount; i++)
        {
            var name = ReadFixedString(r, 15, shiftJis);
            var frame = r.ReadUInt32();
            var translation = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var rotation = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var interp = r.ReadBytes(64);
            if (interp.Length != 64)
                throw new PmxFormatException("truncated VMD bone frame");
            lastFrame = Math.Max(lastFrame, frame);
            if (!boneTracks.TryGetValue(name, out var track))
                boneTracks[name] = track = [];
            // The packed 64-byte block interleaves the four channels' x1/y1/x2/y2 with stride 4.
            track.Add(new VmdBoneFrame(
                frame, translation, rotation,
                interp[0], interp[4], interp[8], interp[12],
                interp[1], interp[5], interp[9], interp[13],
                interp[2], interp[6], interp[10], interp[14],
                interp[3], interp[7], interp[11], interp[15]));
        }

        foreach (var track in boneTracks.Values)
            track.Sort(static (a, b) => a.Frame.CompareTo(b.Frame));

        // Morph frames
        var morphCount = ReadCount(r, "morph-frame");
        var morphTracks = new Dictionary<string, List<VmdMorphFrame>>(StringComparer.Ordinal);
        for (var i = 0; i < morphCount; i++)
        {
            var name = ReadFixedString(r, 15, shiftJis);
            var frame = r.ReadUInt32();
            var weight = r.ReadSingle();
            lastFrame = Math.Max(lastFrame, frame);
            if (!morphTracks.TryGetValue(name, out var track))
                morphTracks[name] = track = [];
            track.Add(new VmdMorphFrame(frame, weight));
        }

        foreach (var track in morphTracks.Values)
            track.Sort(static (a, b) => a.Frame.CompareTo(b.Frame));

        // Camera frames (absent in model motions — the section may be missing entirely at EOF)
        var camera = new List<VmdCameraFrame>();
        if (stream.Position < stream.Length)
        {
            var cameraCount = ReadCount(r, "camera-frame");
            for (var i = 0; i < cameraCount; i++)
            {
                var frame = r.ReadUInt32();
                var distance = r.ReadSingle();
                var target = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var rotation = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                _ = r.ReadBytes(24); // interpolation (linear evaluation in the prototype)
                var fov = r.ReadUInt32();
                var perspective = r.ReadByte() == 0; // 0 = perspective on in VMD
                lastFrame = Math.Max(lastFrame, frame);
                camera.Add(new VmdCameraFrame(frame, distance, target, rotation, fov, perspective));
            }

            camera.Sort(static (a, b) => a.Frame.CompareTo(b.Frame));
        }

        return new VmdDocument
        {
            ModelName = modelName,
            BoneTracks = boneTracks.ToDictionary(
                kv => kv.Key, IReadOnlyList<VmdBoneFrame> (kv) => kv.Value, StringComparer.Ordinal),
            MorphTracks = morphTracks.ToDictionary(
                kv => kv.Key, IReadOnlyList<VmdMorphFrame> (kv) => kv.Value, StringComparer.Ordinal),
            CameraTrack = camera,
            LastFrame = lastFrame,
        };
    }

    private static int ReadCount(BinaryReader r, string what)
    {
        var n = r.ReadUInt32();
        if (n > 20_000_000)
            throw new PmxFormatException($"implausible VMD {what} count {n}");
        return (int)n;
    }

    /// <summary>Fixed-width Shift-JIS field, terminated by NUL (garbage after the NUL is normal in VMD).</summary>
    private static string ReadFixedString(BinaryReader r, int bytes, Encoding encoding)
    {
        var raw = r.ReadBytes(bytes);
        if (raw.Length != bytes)
            throw new PmxFormatException("unexpected end of VMD file");
        var nul = Array.IndexOf(raw, (byte)0);
        return encoding.GetString(raw, 0, nul < 0 ? raw.Length : nul);
    }
}
