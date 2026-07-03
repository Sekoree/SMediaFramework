namespace S.Media.Source.MMD;

/// <summary>
/// Evaluates a PMX model's pose at a point in time from a VMD motion (review Gate-6 stage 2): bone
/// tracks sampled with the MMD Bezier curves, FK world transforms with append/inherit rotation and
/// translation, vertex morphs, and linear-blend CPU skinning. Deliberately NOT evaluated in the
/// prototype: IK solving (feet/skirt bones show artifacts on dance motions), SDEF-exact skinning,
/// and physics (rigid bodies stay at their bind pose) — the review's staged plan defers all three.
/// </summary>
public sealed class MmdAnimator
{
    private readonly PmxDocument _model;
    private readonly VmdDocument _motion;
    private readonly int[] _evaluationOrder;      // parents before children
    private readonly Matrix4x4[] _world;          // per-bone world transform (bind-relative animation applied)
    private readonly Matrix4x4[] _skin;           // world * inverse-bind — what vertices multiply by
    private readonly Vector3[] _morphedPositions; // bind positions + active vertex-morph offsets

    public MmdAnimator(PmxDocument model, VmdDocument motion)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _motion = motion ?? throw new ArgumentNullException(nameof(motion));
        _world = new Matrix4x4[model.Bones.Count];
        _skin = new Matrix4x4[model.Bones.Count];
        _morphedPositions = new Vector3[model.Vertices.Count];

        // Topological order: bones whose parent (and append parent) precede them. PMX files are usually
        // already ordered, but append parents can point anywhere — a simple repeated pass settles it.
        var order = new List<int>(model.Bones.Count);
        var placed = new bool[model.Bones.Count];
        var safety = 0;
        while (order.Count < model.Bones.Count && safety++ <= model.Bones.Count)
        {
            for (var i = 0; i < model.Bones.Count; i++)
            {
                if (placed[i])
                    continue;
                var bone = model.Bones[i];
                var parentReady = bone.ParentIndex < 0 || (uint)bone.ParentIndex >= (uint)model.Bones.Count || placed[bone.ParentIndex];
                var appendReady = bone.AppendParentIndex < 0 || (uint)bone.AppendParentIndex >= (uint)model.Bones.Count || placed[bone.AppendParentIndex];
                if (parentReady && appendReady)
                {
                    placed[i] = true;
                    order.Add(i);
                }
            }
        }

        // A cyclic append chain (malformed file) — fall back to file order so evaluation still terminates.
        for (var i = 0; i < model.Bones.Count; i++)
            if (!placed[i])
                order.Add(i);
        _evaluationOrder = [.. order];
    }

    public PmxDocument Model => _model;

    public TimeSpan Duration => _motion.Duration;

    /// <summary>Per-bone LOCAL animation (sampled rotation/translation) captured for pose queries.</summary>
    private readonly record struct LocalPose(Quaternion Rotation, Vector3 Translation);

    /// <summary>Evaluates the skeleton + morphs at <paramref name="time"/> and returns skinned vertex
    /// positions (into <paramref name="positions"/>, sized to the vertex count).</summary>
    public void Evaluate(TimeSpan time, Vector3[] positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (positions.Length < _model.Vertices.Count)
            throw new ArgumentException("positions buffer too small", nameof(positions));

        var frame = (float)(time.TotalSeconds * VmdDocument.FramesPerSecond);
        var locals = new LocalPose[_model.Bones.Count];
        for (var i = 0; i < _model.Bones.Count; i++)
            locals[i] = SampleBone(_model.Bones[i].Name, frame);

        // Append/inherit folds the append parent's SAMPLED local pose in (MMD semantics, ratio-scaled).
        foreach (var i in _evaluationOrder)
        {
            var bone = _model.Bones[i];
            var local = locals[i];
            if (bone.AppendParentIndex >= 0 && bone.AppendParentIndex < locals.Length)
            {
                var source = locals[bone.AppendParentIndex];
                if (bone.AppendRotation)
                    local = local with
                    {
                        Rotation = Quaternion.Normalize(Quaternion.Slerp(
                            Quaternion.Identity, source.Rotation, bone.AppendRatio) * local.Rotation),
                    };
                if (bone.AppendTranslation)
                    local = local with { Translation = local.Translation + source.Translation * bone.AppendRatio };
                locals[i] = local;
            }

            // World = local (about the bone's bind position) chained through the parent.
            var bind = bone.Position;
            var parentBind = bone.ParentIndex >= 0 && bone.ParentIndex < _model.Bones.Count
                ? _model.Bones[bone.ParentIndex].Position
                : Vector3.Zero;
            var localMatrix =
                Matrix4x4.CreateFromQuaternion(local.Rotation) with
                {
                    Translation = local.Translation + bind - parentBind,
                };
            _world[i] = bone.ParentIndex >= 0 && bone.ParentIndex < _model.Bones.Count
                ? localMatrix * _world[bone.ParentIndex]
                : localMatrix;
            // Skin matrix maps a BIND-space point: subtract the bind position, then apply world.
            _skin[i] = Matrix4x4.CreateTranslation(-bind) * _world[i];
        }

        // Vertex morphs (weights sampled at the same time).
        var vertices = _model.Vertices;
        for (var v = 0; v < vertices.Count; v++)
            _morphedPositions[v] = vertices[v].Position;
        foreach (var morph in _model.Morphs)
        {
            if (morph.VertexOffsets.Count == 0 ||
                !_motion.MorphTracks.TryGetValue(morph.Name, out var track) || track.Count == 0)
                continue;
            var weight = SampleMorph(track, frame);
            if (weight <= 0f)
                continue;
            foreach (var offset in morph.VertexOffsets)
                _morphedPositions[offset.VertexIndex] += offset.Offset * weight;
        }

        // Linear-blend skinning.
        for (var v = 0; v < vertices.Count; v++)
        {
            var vertex = vertices[v];
            var p = _morphedPositions[v];
            var result = Vector3.Zero;
            var total = 0f;
            Accumulate(vertex.Bone0, vertex.Weight0, p, ref result, ref total);
            Accumulate(vertex.Bone1, vertex.Weight1, p, ref result, ref total);
            Accumulate(vertex.Bone2, vertex.Weight2, p, ref result, ref total);
            Accumulate(vertex.Bone3, vertex.Weight3, p, ref result, ref total);
            positions[v] = total > 0.0001f ? result / total : p;
        }
    }

    private void Accumulate(int bone, float weight, Vector3 p, ref Vector3 result, ref float total)
    {
        if (bone < 0 || bone >= _skin.Length || weight <= 0f)
            return;
        result += Vector3.Transform(p, _skin[bone]) * weight;
        total += weight;
    }

    /// <summary>World position of a named bone at the LAST evaluated pose (camera targets, tests).</summary>
    public Vector3? TryGetBoneWorldPosition(string name)
    {
        for (var i = 0; i < _model.Bones.Count; i++)
            if (_model.Bones[i].Name == name)
                return _world[i].Translation;
        return null;
    }

    private LocalPose SampleBone(string name, float frame)
    {
        if (!_motion.BoneTracks.TryGetValue(name, out var track) || track.Count == 0)
            return new LocalPose(Quaternion.Identity, Vector3.Zero);

        var (previous, next, t) = Locate(track, frame, static f => f.Frame);
        if (next is null)
            return new LocalPose(Quaternion.Normalize(previous.Rotation), previous.Translation);

        var n = next.Value;
        // Per-channel MMD Bezier easing between the two keyframes.
        var tx = Bezier(previous.XInterp0, previous.XInterp1, previous.XInterp2, previous.XInterp3, t);
        var ty = Bezier(previous.YInterp0, previous.YInterp1, previous.YInterp2, previous.YInterp3, t);
        var tz = Bezier(previous.ZInterp0, previous.ZInterp1, previous.ZInterp2, previous.ZInterp3, t);
        var tr = Bezier(previous.RInterp0, previous.RInterp1, previous.RInterp2, previous.RInterp3, t);
        var translation = new Vector3(
            float.Lerp(previous.Translation.X, n.Translation.X, tx),
            float.Lerp(previous.Translation.Y, n.Translation.Y, ty),
            float.Lerp(previous.Translation.Z, n.Translation.Z, tz));
        var rotation = Quaternion.Normalize(Quaternion.Slerp(
            Quaternion.Normalize(previous.Rotation), Quaternion.Normalize(n.Rotation), tr));
        return new LocalPose(rotation, translation);
    }

    private static float SampleMorph(IReadOnlyList<VmdMorphFrame> track, float frame)
    {
        var (previous, next, t) = Locate(track, frame, static f => f.Frame);
        return next is null ? previous.Weight : float.Lerp(previous.Weight, next.Value.Weight, t);
    }

    /// <summary>Camera parameters at <paramref name="time"/> (linear between keyframes; identity default).</summary>
    public static VmdCameraFrame SampleCamera(VmdDocument motion, TimeSpan time)
    {
        var track = motion.CameraTrack;
        if (track.Count == 0)
            return new VmdCameraFrame(0, -45f, new Vector3(0, 10, 0), Vector3.Zero, 30f, true);

        var frame = (float)(time.TotalSeconds * VmdDocument.FramesPerSecond);
        var (previous, next, t) = Locate(track, frame, static f => f.Frame);
        if (next is null)
            return previous;
        var n = next.Value;
        return new VmdCameraFrame(
            previous.Frame,
            float.Lerp(previous.Distance, n.Distance, t),
            Vector3.Lerp(previous.Target, n.Target, t),
            Vector3.Lerp(previous.RotationRadians, n.RotationRadians, t),
            float.Lerp(previous.FovDegrees, n.FovDegrees, t),
            previous.Perspective);
    }

    /// <summary>Finds the keyframe pair bracketing <paramref name="frame"/> (binary search).</summary>
    private static (T Previous, T? Next, float T) Locate<T>(
        IReadOnlyList<T> track, float frame, Func<T, uint> frameOf) where T : struct
    {
        if (frame <= frameOf(track[0]))
            return (track[0], null, 0f);
        if (frame >= frameOf(track[^1]))
            return (track[^1], null, 0f);

        int lo = 0, hi = track.Count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (frameOf(track[mid]) <= frame) lo = mid;
            else hi = mid;
        }

        var f0 = frameOf(track[lo]);
        var f1 = frameOf(track[hi]);
        var t = f1 == f0 ? 0f : (frame - f0) / (f1 - f0);
        return (track[lo], track[hi], Math.Clamp(t, 0f, 1f));
    }

    /// <summary>MMD Bezier easing: control points (x1,y1,x2,y2)/127 on a unit curve; returns y for time-x.</summary>
    internal static float Bezier(byte x1, byte y1, byte x2, byte y2, float x)
    {
        // Linear curve shortcut (the overwhelmingly common 20/20/107/107 default is near-linear too).
        if (x1 == y1 && x2 == y2)
            return x;

        float cx1 = x1 / 127f, cy1 = y1 / 127f, cx2 = x2 / 127f, cy2 = y2 / 127f;
        // Solve Bezier parameter s for the given x by bisection (curve x(s) is monotonic).
        float lo = 0f, hi = 1f;
        for (var i = 0; i < 16; i++)
        {
            var mid = (lo + hi) * 0.5f;
            if (CubicAxis(cx1, cx2, mid) < x) lo = mid;
            else hi = mid;
        }

        var s = (lo + hi) * 0.5f;
        return CubicAxis(cy1, cy2, s);

        static float CubicAxis(float c1, float c2, float s)
        {
            var inv = 1f - s;
            return 3f * inv * inv * s * c1 + 3f * inv * s * s * c2 + s * s * s;
        }
    }
}
