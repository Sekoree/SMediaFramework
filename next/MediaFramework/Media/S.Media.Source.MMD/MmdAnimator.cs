namespace S.Media.Source.MMD;

/// <summary>
/// Evaluates a PMX model's pose at a point in time from a VMD motion (review Gate-6 stage 2): bone
/// tracks sampled with the MMD Bezier curves, FK world transforms with append/inherit rotation and
/// translation, CCD IK solving with per-link Euler angle limits (stage 6 — the dance-feet artifact
/// source), vertex morphs, and linear-blend CPU skinning. Deliberately NOT evaluated in the
/// prototype: SDEF-exact skinning and physics (rigid bodies stay at their bind pose) — the review's
/// staged plan defers both.
/// </summary>
public sealed class MmdAnimator
{
    private readonly PmxDocument _model;
    private readonly VmdDocument _motion;
    private readonly int[] _evaluationOrder;      // parents before children
    private readonly Matrix4x4[] _world;          // per-bone world transform (bind-relative animation applied)
    private readonly Matrix4x4[] _skin;           // world * inverse-bind — what vertices multiply by
    private readonly Vector3[] _morphedPositions; // bind positions + active vertex-morph offsets
    private readonly Quaternion[] _ikRotations;   // per-bone IK delta (identity outside solved chains), reset per Evaluate
    private readonly int[] _ikBones;              // bones with IsIk, in evaluation order (leg IK before dependent toe IK)

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

        _ikRotations = new Quaternion[model.Bones.Count];
        _ikBones = [.. _evaluationOrder.Where(i => IsSolvableIkBone(model, i))];
    }

    /// <summary>An IK bone this solver can run: a valid effector target and at least one valid link.</summary>
    private static bool IsSolvableIkBone(PmxDocument model, int index)
    {
        var bone = model.Bones[index];
        return bone.IsIk
               && (uint)bone.IkTargetIndex < (uint)model.Bones.Count
               && bone.IkLoopCount > 0
               && bone.IkLinks.Count > 0
               && bone.IkLinks.All(l => (uint)l.BoneIndex < (uint)model.Bones.Count);
    }

    public PmxDocument Model => _model;

    public TimeSpan Duration => _motion.Duration;

    /// <summary>Per-bone LOCAL animation (sampled rotation/translation) captured for pose queries.</summary>
    private readonly record struct LocalPose(Quaternion Rotation, Vector3 Translation);

    /// <summary>Evaluates the skeleton + morphs at <paramref name="time"/> and returns skinned vertex
    /// positions (into <paramref name="positions"/>, sized to the vertex count).</summary>
    public void Evaluate(TimeSpan time, Vector3[] positions) => Evaluate(time, positions, normals: null);

    /// <summary>Evaluates like <see cref="Evaluate(TimeSpan, Vector3[])"/> and ALSO skins vertex normals
    /// (same blended bone transform, linear part only, renormalized) — the GL renderer's lighting/edge
    /// input. Pass <c>null</c> to skip the normal work.</summary>
    public void Evaluate(TimeSpan time, Vector3[] positions, Vector3[]? normals) =>
        Evaluate(time, positions, normals, physics: null, physicsDeltaSeconds: 0f);

    /// <summary>Full evaluation with the optional stage-5 physics step: FK + IK pose first, then
    /// <paramref name="physics"/> advances by <paramref name="physicsDeltaSeconds"/> and writes the
    /// dynamic bodies' transforms back into the bone worlds, then morphs + skinning read the result.</summary>
    public void Evaluate(TimeSpan time, Vector3[] positions, Vector3[]? normals, MmdPhysics? physics, float physicsDeltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (positions.Length < _model.Vertices.Count)
            throw new ArgumentException("positions buffer too small", nameof(positions));
        if (normals is not null && normals.Length < _model.Vertices.Count)
            throw new ArgumentException("normals buffer too small", nameof(normals));

        var frame = (float)(time.TotalSeconds * VmdDocument.FramesPerSecond);
        var locals = new LocalPose[_model.Bones.Count];
        for (var i = 0; i < _model.Bones.Count; i++)
            locals[i] = SampleBone(_model.Bones[i].Name, frame);

        // Append/inherit folds the append parent's SAMPLED local pose in (MMD semantics, ratio-scaled).
        // Folded once, before any world pass — the IK solver re-runs the world pass but not this.
        for (var idx = 0; idx < _evaluationOrder.Length; idx++)
        {
            var i = _evaluationOrder[idx];
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
        }

        // IK deltas reset every evaluation — the pose stays a pure function of time (seek-back determinism).
        Array.Fill(_ikRotations, Quaternion.Identity);
        ComputeWorldPass(locals);
        foreach (var ikBone in _ikBones)
            SolveIkChain(ikBone, locals);

        // Stage-5 physics: hair/skirt bodies simulate against the posed skeleton and overwrite their
        // bones' world transforms. The NON-physics bones then re-chain under their (possibly moved)
        // parents — tip/hem bones carry skin weights and would otherwise stay at the rigid FK pose,
        // tearing the mesh where a chain ends — and skin matrices rebuild from the final worlds.
        if (physics is not null)
        {
            physics.Step(_world, physicsDeltaSeconds);
            foreach (var i in _evaluationOrder)
            {
                var parent = _model.Bones[i].ParentIndex;
                if (!physics.DrivesBone(i) && parent >= 0 && parent < _model.Bones.Count)
                    _world[i] = LocalMatrix(i, locals) * _world[parent];
            }

            ComputeSkinFromWorld();
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

            if (normals is not null)
            {
                var n = vertex.Normal;
                var blended = Vector3.Zero;
                var nTotal = 0f;
                AccumulateNormal(vertex.Bone0, vertex.Weight0, n, ref blended, ref nTotal);
                AccumulateNormal(vertex.Bone1, vertex.Weight1, n, ref blended, ref nTotal);
                AccumulateNormal(vertex.Bone2, vertex.Weight2, n, ref blended, ref nTotal);
                AccumulateNormal(vertex.Bone3, vertex.Weight3, n, ref blended, ref nTotal);
                var skinned = nTotal > 0.0001f ? blended : n;
                var len = skinned.Length();
                normals[v] = len > 1e-6f ? skinned / len : n;
            }
        }
    }

    private void AccumulateNormal(int bone, float weight, Vector3 n, ref Vector3 result, ref float total)
    {
        if (bone < 0 || bone >= _skin.Length || weight <= 0f)
            return;
        // Linear part only (TransformNormal ignores translation); non-uniform scale is absent in MMD rigs.
        result += Vector3.TransformNormal(n, _skin[bone]) * weight;
        total += weight;
    }

    private void Accumulate(int bone, float weight, Vector3 p, ref Vector3 result, ref float total)
    {
        if (bone < 0 || bone >= _skin.Length || weight <= 0f)
            return;
        result += Vector3.Transform(p, _skin[bone]) * weight;
        total += weight;
    }

    /// <summary>Recomputes every bone's world + skin matrix from the folded locals, applying the current
    /// IK deltas on top of the FK rotations. Cheap enough (one matrix chain per bone) that the IK solver
    /// simply re-runs it after each link adjustment — correctness over micro-optimization in the prototype.</summary>
    private void ComputeWorldPass(LocalPose[] locals)
    {
        foreach (var i in _evaluationOrder)
        {
            var bone = _model.Bones[i];
            _world[i] = bone.ParentIndex >= 0 && bone.ParentIndex < _model.Bones.Count
                ? LocalMatrix(i, locals) * _world[bone.ParentIndex]
                : LocalMatrix(i, locals);
            // Skin matrix maps a BIND-space point: subtract the bind position, then apply world.
            _skin[i] = Matrix4x4.CreateTranslation(-bone.Position) * _world[i];
        }
    }

    /// <summary>The bone's parent-relative matrix: sampled local pose (+ IK delta) about its bind offset.</summary>
    private Matrix4x4 LocalMatrix(int i, LocalPose[] locals)
    {
        var bone = _model.Bones[i];
        var local = locals[i];
        var rotation = _ikRotations[i] == Quaternion.Identity
            ? local.Rotation
            : Quaternion.Normalize(_ikRotations[i] * local.Rotation);
        var parentBind = bone.ParentIndex >= 0 && bone.ParentIndex < _model.Bones.Count
            ? _model.Bones[bone.ParentIndex].Position
            : Vector3.Zero;
        return Matrix4x4.CreateFromQuaternion(rotation) with
        {
            Translation = local.Translation + bone.Position - parentBind,
        };
    }

    /// <summary>Rebuilds every skin matrix from the CURRENT world transforms (after physics mutated them).</summary>
    private void ComputeSkinFromWorld()
    {
        for (var i = 0; i < _model.Bones.Count; i++)
            _skin[i] = Matrix4x4.CreateTranslation(-_model.Bones[i].Position) * _world[i];
    }

    /// <summary>Distance below which an IK chain counts as converged (MMD model units — 1 ≈ 8 cm).</summary>
    private const float IkConvergenceEpsilon = 1e-3f;

    /// <summary>
    /// CCD IK for one IK bone (stage 6): rotate each link so the effector (<see cref="PmxBone.IkTargetIndex"/>,
    /// e.g. the ankle) chases the IK bone's own world position (the goal the motion animates). Every step's
    /// rotation is clamped to the bone's unit angle (<see cref="PmxBone.IkLimitRadians"/>) and limited links
    /// clamp their COMBINED local rotation per-axis in Euler XYZ (the knee's X-only hinge), matching MMD
    /// semantics. The solve mutates only <see cref="_ikRotations"/> and leaves the world pass consistent.
    /// </summary>
    private void SolveIkChain(int ikIndex, LocalPose[] locals)
    {
        var ik = _model.Bones[ikIndex];
        var links = ik.IkLinks;
        var effector = ik.IkTargetIndex;
        var target = _world[ikIndex].Translation; // fixed during the solve — links never move the IK bone itself

        // The bones whose worlds move during the solve: every link plus the effector's ancestor path up to
        // the outermost link, in evaluation order — a chain-only refresh per adjustment instead of a full
        // skeleton pass (an 800-bone model with loop count 40 would otherwise do ~100k matrix chains per
        // solve). Everything downstream of the chain is settled by ONE full pass when the solve ends.
        var chainSet = new HashSet<int>(links.Count + 4);
        foreach (var link in links)
            chainSet.Add(link.BoneIndex);
        for (var walk = effector;
             walk >= 0 && walk < _model.Bones.Count && chainSet.Count < _model.Bones.Count;
             walk = _model.Bones[walk].ParentIndex)
        {
            chainSet.Add(walk);
            if (walk == links[^1].BoneIndex)
                break; // reached the outermost link — ancestors above it don't move
        }
        var chain = _evaluationOrder.Where(chainSet.Contains).ToArray();

        var anyMoved = false;
        try
        {
            for (var iteration = 0; iteration < ik.IkLoopCount; iteration++)
            {
                var moved = false;
                for (var l = 0; l < links.Count; l++)
                {
                    var effectorPos = _world[effector].Translation;
                    if (Vector3.DistanceSquared(effectorPos, target) < IkConvergenceEpsilon * IkConvergenceEpsilon)
                        return;

                    var link = links[l];
                    var linkIndex = link.BoneIndex;
                    if (!Matrix4x4.Invert(_world[linkIndex], out var toLocal))
                        continue;

                    var localEffector = Vector3.Transform(effectorPos, toLocal);
                    var localTarget = Vector3.Transform(target, toLocal);
                    if (localEffector.LengthSquared() < 1e-8f || localTarget.LengthSquared() < 1e-8f)
                        continue;
                    localEffector = Vector3.Normalize(localEffector);
                    localTarget = Vector3.Normalize(localTarget);

                    var dot = Math.Clamp(Vector3.Dot(localEffector, localTarget), -1f, 1f);
                    var angle = MathF.Acos(dot);
                    if (angle < 1e-5f)
                        continue;
                    angle = MathF.Min(angle, MathF.Max(ik.IkLimitRadians, 1e-3f)); // per-step unit angle

                    var axis = Vector3.Cross(localEffector, localTarget);
                    if (axis.LengthSquared() < 1e-10f)
                        continue;
                    axis = Vector3.Normalize(axis);

                    // Single-axis hinge (the MMD knee: Y and Z pinned to 0) — project the corrective
                    // rotation onto the hinge axis instead of letting the Euler clamp mangle an off-axis
                    // turn. The classic knee treatment; keeps CCD progress ON the hinge.
                    if (link.HasLimit
                        && link.LimitMin.Y == 0 && link.LimitMax.Y == 0
                        && link.LimitMin.Z == 0 && link.LimitMax.Z == 0)
                        axis = new Vector3(axis.X >= 0 ? 1f : -1f, 0f, 0f);

                    // The delta acts in the link's CURRENT local frame (world[link] already contains its
                    // rotation), so it composes on the effective-rotation side: new = old-effective * delta.
                    var delta = Quaternion.CreateFromAxisAngle(axis, angle);
                    var fk = locals[linkIndex].Rotation;
                    var effective = Quaternion.Normalize(
                        Quaternion.Normalize(_ikRotations[linkIndex] * fk) * delta);
                    if (link.HasLimit)
                        effective = ClampEulerXyz(effective, link.LimitMin, link.LimitMax);
                    _ikRotations[linkIndex] = Quaternion.Normalize(effective * Quaternion.Inverse(fk));
                    moved = true;
                    anyMoved = true;

                    RefreshChainWorlds(chain, locals);
                }

                if (!moved)
                    return; // every link hit its limit / degenerate geometry — more iterations change nothing
            }
        }
        finally
        {
            // Settle the whole skeleton (the moved links' subtrees — toes, skirt children — plus skin
            // matrices) exactly once per solve.
            if (anyMoved)
                ComputeWorldPass(locals);
        }
    }

    /// <summary>Recomputes world matrices for the solve chain only (evaluation-ordered, parents first —
    /// each member's parent world is either untouched or refreshed earlier in the same array).</summary>
    private void RefreshChainWorlds(int[] chain, LocalPose[] locals)
    {
        foreach (var i in chain)
        {
            var bone = _model.Bones[i];
            var local = locals[i];
            var rotation = _ikRotations[i] == Quaternion.Identity
                ? local.Rotation
                : Quaternion.Normalize(_ikRotations[i] * local.Rotation);
            var bind = bone.Position;
            var parentBind = bone.ParentIndex >= 0 && bone.ParentIndex < _model.Bones.Count
                ? _model.Bones[bone.ParentIndex].Position
                : Vector3.Zero;
            var localMatrix =
                Matrix4x4.CreateFromQuaternion(rotation) with
                {
                    Translation = local.Translation + bind - parentBind,
                };
            _world[i] = bone.ParentIndex >= 0 && bone.ParentIndex < _model.Bones.Count
                ? localMatrix * _world[bone.ParentIndex]
                : localMatrix;
        }
    }

    /// <summary>Clamps a rotation per-axis in Euler XYZ to the PMX link limits (radians). The knee's
    /// canonical limit (X ∈ [-π, 0], Y = Z = 0) collapses this to a pure X hinge.</summary>
    internal static Quaternion ClampEulerXyz(Quaternion rotation, Vector3 min, Vector3 max)
    {
        rotation = Quaternion.Normalize(rotation);
        var m = Matrix4x4.CreateFromQuaternion(rotation);

        // Decompose R = Rx·Ry·Rz (System.Numerics row-vector convention: v' = v·Rx·Ry·Rz, so Rx acts
        // first). Expanding that product: M13 = −sinY, M23 = sinX·cosY, M33 = cosX·cosY,
        // M12 = cosY·sinZ, M11 = cosY·cosZ — the extraction below inverts it.
        float x, y, z;
        var sinY = Math.Clamp(-m.M13, -1f, 1f);
        y = MathF.Asin(sinY);
        if (MathF.Abs(sinY) < 0.99999f)
        {
            x = MathF.Atan2(m.M23, m.M33);
            z = MathF.Atan2(m.M12, m.M11);
        }
        else
        {
            // Gimbal lock (cosY = 0): with Z folded to 0, M22 = cosX and M32 = −sinX.
            x = MathF.Atan2(-m.M32, m.M22);
            z = 0f;
        }

        x = Math.Clamp(x, min.X, max.X);
        y = Math.Clamp(y, min.Y, max.Y);
        z = Math.Clamp(z, min.Z, max.Z);

        return Quaternion.Normalize(
            Quaternion.CreateFromRotationMatrix(
                Matrix4x4.CreateRotationX(x) * Matrix4x4.CreateRotationY(y) * Matrix4x4.CreateRotationZ(z)));
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
