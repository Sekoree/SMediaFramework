namespace S.Media.Source.MMD;

/// <summary>
/// Evaluates a PMX model's pose at a point in time from a VMD motion: bone tracks sampled with the
/// MMD Bezier curves, then bones processed ONE AT A TIME in MMD's transform order — stable-sorted by
/// (deform layer, index), split into before-/after-physics groups — where each bone folds fixed-axis
/// projection and append/inherit FROM THE DONOR'S CURRENT STATE (including its IK rotation — the
/// D-bone rigs that carry the leg skin weights inherit from IK-solved bones), chains its world matrix,
/// and runs its IK solve in place when it is an IK bone. The IK solver is a faithful port of
/// babylon-mmd's <c>ikSolver.ts</c> (Saba lineage): per-link step scaling, limit-adaptive Euler order,
/// the first-half-iterations limit REFLECTION that bootstraps knee bends, and fixed-link skipping.
/// Vertex/UV/material morphs and BDEF/SDEF/QDEF CPU skinning follow.
/// </summary>
public sealed class MMDAnimator
{
    private readonly PMXDocument _model;
    private readonly VMDDocument _motion;
    private readonly int[] _beforePhysics;        // (layer, index)-sorted bones evaluated before physics
    private readonly int[] _afterPhysics;         // ... and the TransformAfterPhysics group
    private readonly Matrix4x4[] _world;          // per-bone world transform (bind-relative animation applied)
    private readonly Matrix4x4[] _skin;           // world * inverse-bind — what vertices multiply by
    private readonly Vector3[] _morphedPositions; // bind positions + active vertex-morph offsets
    private readonly Vector2[] _morphedUvs;
    private readonly Vector4[] _morphedAdditionalUv1;
    private readonly MMDMaterialState[] _materialStates;
    private readonly Quaternion[] _ikRotations;   // per-bone IK delta (identity outside solved chains), reset per Evaluate
    private readonly Quaternion[] _animRotations; // folded local rotation (sample + fixed axis + append), EXCLUDING the IK delta
    private readonly Vector3[] _animTranslations; // folded local translation (sample + append)
    private readonly Vector3[] _fixedAxes;        // normalized fixed axis per bone (Zero = none)
    private readonly IkSetup?[] _ikSetups;        // per-bone solver setup; non-null only for solvable IK bones
    private readonly IReadOnlyList<VMDMorphFrame>?[] _morphTracks; // per-morph VMD track (null = unanimated)
    private readonly float[] _morphWeights;       // per-morph weight this evaluation (groups fanned out)
    private readonly bool _hasBoneMorphs;
    private readonly Quaternion[] _boneMorphRotations;   // accumulated bone-morph deltas (empty when unused)
    private readonly Vector3[] _boneMorphTranslations;
    private readonly IReadOnlyList<VMDIkFrame>?[] _ikEnableTracks; // per-IK-bone on/off keys (null = always on)
    private readonly bool[] _bonePhysicsEnabled;

    public MMDAnimator(PMXDocument model, VMDDocument motion)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _motion = motion ?? throw new ArgumentNullException(nameof(motion));
        var count = model.Bones.Count;
        _world = new Matrix4x4[count];
        _skin = new Matrix4x4[count];
        _morphedPositions = new Vector3[model.Vertices.Count];
        _morphedUvs = new Vector2[model.Vertices.Count];
        _morphedAdditionalUv1 = new Vector4[model.Vertices.Count];
        _materialStates = new MMDMaterialState[model.Materials.Count];
        for (var i = 0; i < _morphedUvs.Length; i++)
        {
            _morphedUvs[i] = model.Vertices[i].Uv;
            _morphedAdditionalUv1[i] = model.Vertices[i].AdditionalUvs.Count > 0
                ? model.Vertices[i].AdditionalUvs[0]
                : Vector4.Zero;
        }
        for (var i = 0; i < _materialStates.Length; i++)
            _materialStates[i] = MMDMaterialState.From(model.Materials[i]);
        _ikRotations = new Quaternion[count];
        _animRotations = new Quaternion[count];
        _animTranslations = new Vector3[count];

        // MMD transform order: stable sort by (deform layer, file index) — NOT a parent-topological
        // order. Append donors and world parents are expected to precede their dependents in this
        // order in any PMXEditor-produced file; a malformed forward reference degrades to reading the
        // donor's previous state, exactly as the reference runtimes do.
        var order = Enumerable.Range(0, count)
            .OrderBy(i => model.Bones[i].DeformLayer)
            .ThenBy(i => i)
            .ToArray();
        _beforePhysics = [.. order.Where(i => !model.Bones[i].TransformAfterPhysics)];
        _afterPhysics = [.. order.Where(i => model.Bones[i].TransformAfterPhysics)];

        _fixedAxes = new Vector3[count];
        for (var i = 0; i < count; i++)
            if (model.Bones[i].FixedAxis is { } axis)
                _fixedAxes[i] = axis.LengthSquared() > 1e-12f ? Vector3.Normalize(axis) : Vector3.Zero;

        _ikSetups = new IkSetup?[count];
        var orderPosition = new int[count]; // bone index → position in transform order (chain refresh sorting)
        for (var p = 0; p < order.Length; p++)
            orderPosition[order[p]] = p;
        for (var i = 0; i < count; i++)
            if (IsSolvableIkBone(model, i))
                _ikSetups[i] = new IkSetup(model, i, orderPosition);

        _morphTracks = new IReadOnlyList<VMDMorphFrame>?[model.Morphs.Count];
        for (var m = 0; m < model.Morphs.Count; m++)
            _morphTracks[m] = motion.MorphTracks.TryGetValue(model.Morphs[m].Name, out var track) && track.Count > 0
                ? track
                : null;
        _morphWeights = new float[model.Morphs.Count];
        _hasBoneMorphs = model.Morphs.Any(m => m.BoneOffsets.Count > 0);
        _boneMorphRotations = new Quaternion[_hasBoneMorphs ? count : 0];
        _boneMorphTranslations = new Vector3[_hasBoneMorphs ? count : 0];

        _ikEnableTracks = new IReadOnlyList<VMDIkFrame>?[count];
        _bonePhysicsEnabled = new bool[count];
        for (var i = 0; i < count; i++)
            if (_ikSetups[i] is not null
                && motion.IkEnableTracks.TryGetValue(model.Bones[i].Name, out var ikTrack) && ikTrack.Count > 0)
                _ikEnableTracks[i] = ikTrack;
    }

    /// <summary>An IK bone this solver can run: a valid effector target and at least one valid link.</summary>
    private static bool IsSolvableIkBone(PMXDocument model, int index)
    {
        var bone = model.Bones[index];
        return bone.IsIk
               && (uint)bone.IkTargetIndex < (uint)model.Bones.Count
               && bone.IkLoopCount > 0
               && bone.IkLinks.Count > 0
               && bone.IkLinks.All(l => (uint)l.BoneIndex < (uint)model.Bones.Count);
    }

    public PMXDocument Model => _model;

    public TimeSpan Duration => _motion.Duration;

    public IReadOnlyList<Vector2> CurrentUvs => _morphedUvs;

    public IReadOnlyList<Vector4> CurrentAdditionalUv1 => _morphedAdditionalUv1;

    public IReadOnlyList<MMDMaterialState> MaterialStates => _materialStates;

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

    /// <summary>Full evaluation with the optional physics step: the before-physics bones (FK + append +
    /// IK) run first, then <paramref name="physics"/> advances by <paramref name="physicsDeltaSeconds"/>
    /// and writes the dynamic bodies' transforms back into the bone worlds, the NON-physics bones
    /// re-chain under their (possibly moved) parents, the after-physics bones evaluate, and morphs +
    /// skinning read the result.</summary>
    public void Evaluate(TimeSpan time, Vector3[] positions, Vector3[]? normals, MMDPhysics? physics, float physicsDeltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (positions.Length < _model.Vertices.Count)
            throw new ArgumentException("positions buffer too small", nameof(positions));
        if (normals is not null && normals.Length < _model.Vertices.Count)
            throw new ArgumentException("normals buffer too small", nameof(normals));

        var frame = (float)(time.TotalSeconds * VMDDocument.FramesPerSecond);
        EvaluatePose(frame, physics, physicsDeltaSeconds);
        FinishSkinning(positions, normals);
    }

    /// <summary>Skin matrices + vertex morphs + skinning from the current bone worlds.</summary>
    private void FinishSkinning(Vector3[] positions, Vector3[]? normals)
    {
        // Skin matrix maps a BIND-space point: subtract the bind position, then apply world.
        for (var i = 0; i < _model.Bones.Count; i++)
            _skin[i] = Matrix4x4.CreateTranslation(-_model.Bones[i].Position) * _world[i];

        // Vertex morphs (weights sampled above, group morphs already fanned out).
        var vertices = _model.Vertices;
        for (var v = 0; v < vertices.Count; v++)
            _morphedPositions[v] = vertices[v].Position;
        for (var m = 0; m < _morphWeights.Length; m++)
        {
            var weight = _morphWeights[m];
            if (weight == 0f)
                continue;
            foreach (var offset in _model.Morphs[m].VertexOffsets)
                _morphedPositions[offset.VertexIndex] += offset.Offset * weight;
        }

        // Linear-blend skinning.
        for (var v = 0; v < vertices.Count; v++)
        {
            var vertex = vertices[v];
            var p = _morphedPositions[v];
            if (vertex.DeformType == PMXDeformType.Sdef
                && IsValidBone(vertex.Bone0) && IsValidBone(vertex.Bone1))
            {
                SkinSdef(vertex, p, out positions[v], out var sdefNormal);
                if (normals is not null)
                    normals[v] = sdefNormal;
                continue;
            }

            if (vertex.DeformType == PMXDeformType.Qdef)
            {
                SkinQdef(vertex, p, out positions[v], out var qdefNormal);
                if (normals is not null)
                    normals[v] = qdefNormal;
                continue;
            }

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

        bool IsValidBone(int bone) => (uint)bone < (uint)_skin.Length;
    }

    /// <summary>SDEF as implemented by Saba/babylon-mmd: slerped bone rotation around the authored
    /// center plus the two corrected radius points transformed by their respective skin matrices.</summary>
    private void SkinSdef(in PMXVertex vertex, Vector3 position, out Vector3 result, out Vector3 normal)
    {
        var w0 = vertex.Weight0;
        var w1 = vertex.Weight1;
        var m0 = _skin[vertex.Bone0];
        var m1 = _skin[vertex.Bone1];
        var q0 = RotationPartOf(m0);
        var q1 = RotationPartOf(m1);
        if (Quaternion.Dot(q0, q1) < 0f)
            q1 = Negated(q1);
        var rotation = Quaternion.Normalize(Quaternion.Slerp(q0, q1, w1));

        var rw = vertex.SdefR0 * w0 + vertex.SdefR1 * w1;
        var correctedR0 = vertex.SdefC + (vertex.SdefR0 - rw) * 0.5f;
        var correctedR1 = vertex.SdefC + (vertex.SdefR1 - rw) * 0.5f;
        result = Vector3.Transform(position - vertex.SdefC, rotation)
                 + Vector3.Transform(correctedR0, m0) * w0
                 + Vector3.Transform(correctedR1, m1) * w1;
        normal = SafeNormal(Vector3.Transform(vertex.Normal, rotation), vertex.Normal);
    }

    /// <summary>PMX 2.1 QDEF dual-quaternion blending. MMD itself does not author QDEF, but evaluating
    /// it here avoids collapsing third-party PMX 2.1 models back to linear skinning.</summary>
    private void SkinQdef(in PMXVertex vertex, Vector3 position, out Vector3 result, out Vector3 normal)
    {
        Quaternion real = default, dual = default, reference = default;
        var hasReference = false;
        AccumulateDual(vertex.Bone0, vertex.Weight0);
        AccumulateDual(vertex.Bone1, vertex.Weight1);
        AccumulateDual(vertex.Bone2, vertex.Weight2);
        AccumulateDual(vertex.Bone3, vertex.Weight3);

        var length = MathF.Sqrt(Quaternion.Dot(real, real));
        if (length < 1e-7f)
        {
            result = position;
            normal = vertex.Normal;
            return;
        }

        real = Scale(real, 1f / length);
        dual = Scale(dual, 1f / length);
        // Keep the dual part orthogonal to the normalized real part before extracting translation.
        dual = Add(dual, Scale(real, -Quaternion.Dot(real, dual)));
        var translationQ = dual * Quaternion.Conjugate(real);
        var translation = new Vector3(translationQ.X, translationQ.Y, translationQ.Z) * 2f;
        result = Vector3.Transform(position, real) + translation;
        normal = SafeNormal(Vector3.Transform(vertex.Normal, real), vertex.Normal);

        void AccumulateDual(int bone, float weight)
        {
            if ((uint)bone >= (uint)_skin.Length || weight <= 0f)
                return;
            var matrix = _skin[bone];
            var qr = RotationPartOf(matrix);
            if (!hasReference)
            {
                reference = qr;
                hasReference = true;
            }
            else if (Quaternion.Dot(reference, qr) < 0f)
            {
                qr = Negated(qr);
            }
            var t = matrix.Translation;
            var qd = Scale(new Quaternion(t, 0f) * qr, 0.5f);
            real = Add(real, Scale(qr, weight));
            dual = Add(dual, Scale(qd, weight));
        }
    }

    private static Quaternion Add(Quaternion a, Quaternion b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);

    private static Quaternion Scale(Quaternion q, float scale) =>
        new(q.X * scale, q.Y * scale, q.Z * scale, q.W * scale);

    private static Quaternion Negated(Quaternion q) => new(-q.X, -q.Y, -q.Z, -q.W);

    private static Vector3 SafeNormal(Vector3 value, Vector3 fallback)
    {
        var length = value.Length();
        return length > 1e-7f ? value / length : fallback;
    }

    /// <summary>One bone's turn in transform order: sample FK, project onto a fixed axis, fold append
    /// from the donor's CURRENT state (its folded rotation AND its IK delta — babylon-mmd
    /// appendTransformSolver semantics), chain the world matrix, and solve IK if this is an IK bone.</summary>
    /// <summary>The shared pose pipeline (morph weights → before-physics bones → physics → re-chain →
    /// after-physics bones), without the skinning tail.</summary>
    private void EvaluatePose(float frame, MMDPhysics? physics, float physicsDeltaSeconds)
    {
        // Morph weights first — bone morphs feed the transform passes below, vertex morphs the skinning.
        SampleMorphWeights(frame);
        AccumulateUvAndMaterialMorphs();
        if (_hasBoneMorphs)
            AccumulateBoneMorphs();

        // IK deltas reset every evaluation — the pose stays a pure function of time (seek-back determinism).
        Array.Fill(_ikRotations, Quaternion.Identity);

        for (var p = 0; p < _beforePhysics.Length; p++)
            ProcessBone(_beforePhysics, p, frame);

        if (physics is not null)
        {
            for (var i = 0; i < _bonePhysicsEnabled.Length; i++)
                _bonePhysicsEnabled[i] = PhysicsEnabledAt(_model.Bones[i].Name, frame);
            physics.SetBonePhysicsEnabled(_bonePhysicsEnabled);
            physics.Step(_world, physicsDeltaSeconds);
            // Re-chain the non-physics bones under their (possibly physics-moved) parents — tip/hem
            // bones carry skin weights and would otherwise stay at the rigid FK pose where a chain ends.
            foreach (var i in _beforePhysics)
                if (!physics.DrivesBone(i))
                    ComputeWorld(i);
        }

        for (var p = 0; p < _afterPhysics.Length; p++)
            ProcessBone(_afterPhysics, p, frame);
    }

    /// <summary>Evaluation with PRE-BAKED physics (see <see cref="MMDBakedPhysics"/>): the FK/IK pass
    /// runs as usual, then every physics-driven bone chains its baked parent-relative transform in one
    /// transform-order sweep — a pure function of time, immune to render cadence, seeks and stalls.</summary>
    public void Evaluate(TimeSpan time, Vector3[] positions, Vector3[]? normals, MMDBakedPhysics baked)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(baked);
        if (positions.Length < _model.Vertices.Count)
            throw new ArgumentException("positions buffer too small", nameof(positions));
        if (normals is not null && normals.Length < _model.Vertices.Count)
            throw new ArgumentException("normals buffer too small", nameof(normals));

        var frame = (float)(time.TotalSeconds * VMDDocument.FramesPerSecond);
        EvaluatePose(frame, physics: null, physicsDeltaSeconds: 0f);

        // One transform-order sweep: driven bones take their baked pose relative to the CURRENT parent
        // world; everything else re-chains under the (possibly baked) parents. Interleaving in order
        // handles chains where driven and FK bones alternate.
        var bakedFrame = MMDBakedPhysics.FrameOf(time);
        foreach (var i in _beforePhysics)
        {
            if (baked.DrivesBone(i))
            {
                baked.Sample(i, bakedFrame, out var rotation, out var translation);
                var local = Matrix4x4.CreateFromQuaternion(rotation) with { Translation = translation };
                var parent = _model.Bones[i].ParentIndex;
                _world[i] = parent >= 0 && parent < _model.Bones.Count ? local * _world[parent] : local;
            }
            else
            {
                ComputeWorld(i);
            }
        }

        for (var p = 0; p < _afterPhysics.Length; p++)
            ProcessBone(_afterPhysics, p, frame);

        FinishSkinning(positions, normals);
    }

    /// <summary>Pose-only evaluation for the offline physics baker — the full transform pipeline
    /// including the live physics step, without the vertex-morph/skinning tail.</summary>
    internal void EvaluatePoseForBake(TimeSpan time, MMDPhysics physics, float physicsDeltaSeconds) =>
        EvaluatePose((float)(time.TotalSeconds * VMDDocument.FramesPerSecond), physics, physicsDeltaSeconds);

    /// <summary>FK/IK-only pose (no physics step) for offline probes — the rigid pose a bone would have
    /// if physics never ran, used as the "driven by the animation only" reference.</summary>
    internal void EvaluatePoseFKOnly(TimeSpan time) =>
        EvaluatePose((float)(time.TotalSeconds * VMDDocument.FramesPerSecond), physics: null, physicsDeltaSeconds: 0f);

    /// <summary>The last evaluated world transform of a bone (baker capture).</summary>
    internal Matrix4x4 BoneWorldForBake(int index) => _world[index];

    private void ProcessBone(int[] group, int position, float frame)
    {
        var i = group[position];
        var bone = _model.Bones[i];
        var pose = SampleBone(bone.Name, frame);
        var rotation = pose.Rotation;
        var translation = pose.Translation;

        // Fixed-axis (twist) bones: keep only the rotation about the authored axis.
        if (bone.FixedAxis is not null)
            rotation = ProjectToAxis(rotation, _fixedAxes[i]);

        // Bone morphs compose on top of the sampled animation (babylon: morph ⊗ anim).
        if (_hasBoneMorphs)
        {
            if (_boneMorphRotations[i] != Quaternion.Identity)
                rotation = Quaternion.Normalize(_boneMorphRotations[i] * rotation);
            translation += _boneMorphTranslations[i];
        }

        if (bone.AppendParentIndex >= 0 && bone.AppendParentIndex < _model.Bones.Count)
        {
            var donor = bone.AppendParentIndex;
            if (bone.AppendRotation)
            {
                // Donor's effective local rotation: its folded animation (which already contains the
                // donor's OWN append — the recursion) with its IK delta composed on top. This is what
                // makes the D-bone legs follow the IK solve. LOCAL append (0x0080) reads the donor's
                // WORLD deformation instead (babylon's isLocal branch).
                var source = bone.LocalAppend
                    ? RotationPartOf(_world[donor])
                    : _ikRotations[donor] == Quaternion.Identity
                        ? _animRotations[donor]
                        : Quaternion.Normalize(_ikRotations[donor] * _animRotations[donor]);
                if (bone.AppendRatio != 1f)
                    source = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, source, bone.AppendRatio));
                rotation = Quaternion.Normalize(rotation * source);
            }

            if (bone.AppendTranslation)
            {
                var source = bone.LocalAppend
                    ? _world[donor].Translation - _model.Bones[donor].Position // world displacement of the donor
                    : _animTranslations[donor];
                translation += source * bone.AppendRatio;
            }
        }

        _animRotations[i] = Quaternion.Normalize(rotation);
        _animTranslations[i] = translation;
        ComputeWorld(i);

        if (_ikSetups[i] is { } setup && IkEnabledAt(i, frame))
        {
            SolveIk(setup);
            // Bones already processed this pass whose ancestors include a moved link (the toe chain
            // under a solved leg, skirt roots under the hip) re-chain here; bones later in the order
            // pick the refreshed parents up naturally.
            for (var p = 0; p < position; p++)
                ComputeWorld(group[p]);
        }
    }

    /// <summary>Step-samples the VMD IK-enable track for a bone. As MMD does for every keyed track,
    /// times before its first key clamp to that first value; a bone with no track remains enabled.</summary>
    private bool IkEnabledAt(int bone, float frame)
    {
        if (_ikEnableTracks[bone] is not { } track)
            return true;
        var enabled = track[0].Enabled;
        for (var k = 0; k < track.Count && track[k].Frame <= frame; k++)
            enabled = track[k].Enabled;
        return enabled;
    }

    private bool PhysicsEnabledAt(string boneName, float frame)
    {
        if (!_motion.BoneTracks.TryGetValue(boneName, out var track) || track.Count == 0)
            return true;
        var enabled = track[0].PhysicsEnabled;
        for (var i = 1; i < track.Count && track[i].Frame <= frame; i++)
            enabled = track[i].PhysicsEnabled;
        return enabled;
    }

    /// <summary>Per-morph weights at <paramref name="frame"/>: direct tracks sampled, then group morphs
    /// fan their weight out ratio-scaled onto their members (PMX forbids nested groups; guarded anyway).</summary>
    private void SampleMorphWeights(float frame)
    {
        for (var m = 0; m < _morphWeights.Length; m++)
            _morphWeights[m] = _morphTracks[m] is { } track ? SampleMorph(track, frame) : 0f;

        for (var m = 0; m < _morphWeights.Length; m++)
        {
            var groups = _model.Morphs[m].GroupOffsets;
            var weight = _morphWeights[m];
            if (groups.Count == 0 || weight == 0f)
                continue;
            foreach (var member in groups)
                if ((uint)member.MorphIndex < (uint)_morphWeights.Length
                    && member.MorphIndex != m
                    && _model.Morphs[member.MorphIndex].GroupOffsets.Count == 0)
                    _morphWeights[member.MorphIndex] += weight * member.Ratio;
        }
    }

    /// <summary>Folds every active bone morph into per-bone rotation/translation deltas.</summary>
    private void AccumulateBoneMorphs()
    {
        Array.Fill(_boneMorphRotations, Quaternion.Identity);
        Array.Fill(_boneMorphTranslations, Vector3.Zero);
        for (var m = 0; m < _morphWeights.Length; m++)
        {
            var weight = _morphWeights[m];
            var offsets = _model.Morphs[m].BoneOffsets;
            if (weight == 0f || offsets.Count == 0)
                continue;
            foreach (var offset in offsets)
            {
                if ((uint)offset.BoneIndex >= (uint)_boneMorphRotations.Length)
                    continue;
                _boneMorphTranslations[offset.BoneIndex] += offset.Translation * weight;
                var delta = Quaternion.Slerp(Quaternion.Identity, Quaternion.Normalize(offset.Rotation), weight);
                _boneMorphRotations[offset.BoneIndex] =
                    Quaternion.Normalize(delta * _boneMorphRotations[offset.BoneIndex]);
            }
        }
    }

    private void AccumulateUvAndMaterialMorphs()
    {
        for (var i = 0; i < _morphedUvs.Length; i++)
        {
            _morphedUvs[i] = _model.Vertices[i].Uv;
            _morphedAdditionalUv1[i] = _model.Vertices[i].AdditionalUvs.Count > 0
                ? _model.Vertices[i].AdditionalUvs[0]
                : Vector4.Zero;
        }
        for (var i = 0; i < _materialStates.Length; i++)
            _materialStates[i] = MMDMaterialState.From(_model.Materials[i]);

        for (var m = 0; m < _morphWeights.Length; m++)
        {
            var weight = _morphWeights[m];
            if (weight == 0f)
                continue;
            var morph = _model.Morphs[m];
            if (morph.Type == 3)
            {
                foreach (var offset in morph.UvOffsets)
                    if ((uint)offset.VertexIndex < (uint)_morphedUvs.Length)
                        _morphedUvs[offset.VertexIndex] += new Vector2(offset.Offset.X, offset.Offset.Y) * weight;
            }
            else if (morph.Type == 4)
            {
                foreach (var offset in morph.UvOffsets)
                    if ((uint)offset.VertexIndex < (uint)_morphedAdditionalUv1.Length)
                        _morphedAdditionalUv1[offset.VertexIndex] += offset.Offset * weight;
            }

            foreach (var offset in morph.MaterialOffsets)
            {
                if (offset.MaterialIndex < 0)
                {
                    for (var material = 0; material < _materialStates.Length; material++)
                        ApplyMaterialMorph(material, offset, weight);
                }
                else if ((uint)offset.MaterialIndex < (uint)_materialStates.Length)
                {
                    ApplyMaterialMorph(offset.MaterialIndex, offset, weight);
                }
            }
        }
    }

    private void ApplyMaterialMorph(int index, in PMXMaterialMorphOffset offset, float weight)
    {
        var state = _materialStates[index];
        if (offset.Operation == PMXMaterialMorphOperation.Multiply)
        {
            state = state with
            {
                Diffuse = MultiplyTowards(state.Diffuse, offset.Diffuse, weight),
                Specular = MultiplyTowards3(state.Specular, offset.Specular, weight),
                SpecularPower = float.Lerp(state.SpecularPower, state.SpecularPower * offset.SpecularPower, weight),
                Ambient = MultiplyTowards3(state.Ambient, offset.Ambient, weight),
                EdgeColor = MultiplyTowards(state.EdgeColor, offset.EdgeColor, weight),
                EdgeSize = float.Lerp(state.EdgeSize, state.EdgeSize * offset.EdgeSize, weight),
                TextureMultiply = MultiplyTowards(state.TextureMultiply, offset.TextureColor, weight),
                SphereMultiply = MultiplyTowards(state.SphereMultiply, offset.SphereTextureColor, weight),
                ToonMultiply = MultiplyTowards(state.ToonMultiply, offset.ToonTextureColor, weight),
            };
        }
        else
        {
            state = state with
            {
                Diffuse = state.Diffuse + offset.Diffuse * weight,
                Specular = state.Specular + offset.Specular * weight,
                SpecularPower = state.SpecularPower + offset.SpecularPower * weight,
                Ambient = state.Ambient + offset.Ambient * weight,
                EdgeColor = state.EdgeColor + offset.EdgeColor * weight,
                EdgeSize = state.EdgeSize + offset.EdgeSize * weight,
                TextureAdd = state.TextureAdd + offset.TextureColor * weight,
                SphereAdd = state.SphereAdd + offset.SphereTextureColor * weight,
                ToonAdd = state.ToonAdd + offset.ToonTextureColor * weight,
            };
        }
        _materialStates[index] = state;

        static Vector4 MultiplyTowards(Vector4 value, Vector4 factor, float amount) =>
            Vector4.Lerp(value, value * factor, amount);
        static Vector3 MultiplyTowards3(Vector3 value, Vector3 factor, float amount) =>
            Vector3.Lerp(value, value * factor, amount);
    }

    private static Quaternion RotationPartOf(in Matrix4x4 world) =>
        Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(world with { Translation = Vector3.Zero }));

    /// <summary>Chains one bone's world matrix from its stored folded locals + IK delta and its
    /// parent's CURRENT world. Parents precede children in transform order, so passes that sweep the
    /// order settle the whole skeleton.</summary>
    private void ComputeWorld(int i)
    {
        var bone = _model.Bones[i];
        var rotation = _ikRotations[i] == Quaternion.Identity
            ? _animRotations[i]
            : Quaternion.Normalize(_ikRotations[i] * _animRotations[i]);
        var parentBind = bone.ParentIndex >= 0 && bone.ParentIndex < _model.Bones.Count
            ? _model.Bones[bone.ParentIndex].Position
            : Vector3.Zero;
        var local = Matrix4x4.CreateFromQuaternion(rotation) with
        {
            Translation = _animTranslations[i] + bone.Position - parentBind,
        };
        _world[i] = bone.ParentIndex >= 0 && bone.ParentIndex < _model.Bones.Count
            ? local * _world[bone.ParentIndex]
            : local;
    }

    /// <summary>Projects a sampled rotation onto a bone's fixed axis (babylon-mmd's runtime axis-limit
    /// handling): extract axis-angle, sign the angle by the axis dot, rebuild about the fixed axis.</summary>
    internal static Quaternion ProjectToAxis(Quaternion rotation, Vector3 axis)
    {
        if (axis == Vector3.Zero)
            return Quaternion.Identity; // authored zero axis — MMD treats the bone as rotation-less

        rotation = Quaternion.Normalize(rotation);
        var w = Math.Clamp(rotation.W, -1f, 1f);
        var sin = MathF.Sqrt(1f - w * w);
        if (sin < 1e-6f)
            return Quaternion.Identity;
        var angle = 2f * MathF.Acos(w);
        var rotationAxis = new Vector3(rotation.X, rotation.Y, rotation.Z) / sin;
        if (Vector3.Dot(rotationAxis, axis) < 0f)
            angle = -angle;
        return Quaternion.CreateFromAxisAngle(axis, angle);
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

    // ── IK (port of babylon-mmd ikSolver.ts, Saba lineage) ────────────────────────────────────────

    private enum EulerOrder { Yxz, Zyx, Xzy }

    private enum SolveAxis { None, Fixed, X, Y, Z }

    private readonly struct IkLinkSetup
    {
        public readonly int BoneIndex;
        public readonly bool HasLimit;
        public readonly Vector3 Min;
        public readonly Vector3 Max;
        public readonly EulerOrder Order;
        public readonly SolveAxis Axis;

        public IkLinkSetup(PMXIkLink link)
        {
            BoneIndex = link.BoneIndex;
            HasLimit = link.HasLimit;
            if (!link.HasLimit)
                return;

            Min = Vector3.Min(link.LimitMin, link.LimitMax);
            Max = Vector3.Max(link.LimitMin, link.LimitMax);

            const float halfPi = MathF.PI * 0.5f;
            Order = -halfPi < Min.X && Max.X < halfPi ? EulerOrder.Yxz
                : -halfPi < Min.Y && Max.Y < halfPi ? EulerOrder.Zyx
                : EulerOrder.Xzy;

            Axis = Min == Vector3.Zero && Max == Vector3.Zero ? SolveAxis.Fixed
                : Min.Y == 0 && Max.Y == 0 && Min.Z == 0 && Max.Z == 0 ? SolveAxis.X
                : Min.X == 0 && Max.X == 0 && Min.Z == 0 && Max.Z == 0 ? SolveAxis.Y
                : Min.X == 0 && Max.X == 0 && Min.Y == 0 && Max.Y == 0 ? SolveAxis.Z
                : SolveAxis.None;
        }
    }

    private sealed class IkSetup
    {
        public readonly int IkBone;
        public readonly int Effector;
        public readonly int Iterations;
        public readonly float UnitAngle;
        public readonly IkLinkSetup[] Links;
        public readonly int[] RefreshOrder; // links + effector ancestor path, in transform order

        public IkSetup(PMXDocument model, int ikIndex, int[] orderPosition)
        {
            var bone = model.Bones[ikIndex];
            IkBone = ikIndex;
            Effector = bone.IkTargetIndex;
            Iterations = Math.Min(bone.IkLoopCount, 256);
            UnitAngle = MathF.Max(bone.IkLimitRadians, 1e-3f);
            Links = [.. bone.IkLinks.Select(l => new IkLinkSetup(l))];

            // The worlds that move during a solve: every link plus the effector's ancestor path up to
            // the outermost link — a chain-only refresh per adjustment instead of a full skeleton pass.
            var set = new HashSet<int>(Links.Length + 4);
            foreach (var link in Links)
                set.Add(link.BoneIndex);
            var outermost = Links[^1].BoneIndex;
            for (var walk = Effector;
                 walk >= 0 && walk < model.Bones.Count && set.Count < model.Bones.Count;
                 walk = model.Bones[walk].ParentIndex)
            {
                set.Add(walk);
                if (walk == outermost)
                    break;
            }

            RefreshOrder = [.. set.OrderBy(i => orderPosition[i])];
        }
    }

    /// <summary>CCD IK for one IK bone: each link chases the IK bone's own world position (the goal the
    /// motion animates) with the effector (<see cref="PMXBone.IkTargetIndex"/>, e.g. the ankle). Fixed
    /// links are skipped; limited links clamp their combined rotation per-axis in the limit-adaptive
    /// Euler order, REFLECTING past-limit angles during the first half of the iterations — the
    /// straight-leg knee bootstrap. Mutates only <see cref="_ikRotations"/> plus the chain worlds.</summary>
    private void SolveIk(IkSetup ik)
    {
        var links = ik.Links;
        foreach (var link in links)
            _ikRotations[link.BoneIndex] = Quaternion.Identity;

        var ikPosition = _world[ik.IkBone].Translation;
        var targetPosition = _world[ik.Effector].Translation;
        if (Vector3.DistanceSquared(ikPosition, targetPosition) < 1e-8f)
            return;

        var half = ik.Iterations >> 1;
        for (var iteration = 0; iteration < ik.Iterations; iteration++)
        {
            for (var l = 0; l < links.Length; l++)
            {
                if (links[l].Axis == SolveAxis.Fixed)
                    continue;
                SolveChainLink(ik, l, ikPosition, ref targetPosition, useAxis: iteration < half);
            }

            if (Vector3.DistanceSquared(ikPosition, targetPosition) < 1e-8f)
                break;
        }
    }

    private void SolveChainLink(IkSetup ik, int chainIndex, Vector3 ikPosition, ref Vector3 targetPosition, bool useAxis)
    {
        var link = ik.Links[chainIndex];
        var linkIndex = link.BoneIndex;

        var chainPosition = _world[linkIndex].Translation;
        var toEffector = chainPosition - targetPosition;
        var toGoal = chainPosition - ikPosition;
        if (toEffector.LengthSquared() < 1e-10f || toGoal.LengthSquared() < 1e-10f)
            return;
        toEffector = Vector3.Normalize(toEffector);
        toGoal = Vector3.Normalize(toGoal);

        var axis = Vector3.Cross(toEffector, toGoal);
        if (axis.LengthSquared() < 1e-8f)
            return;

        // The delta's axis lives in the link's pre-rotation frame ≡ the parent's world orientation
        // (PMX bones carry no bind rotation). Hinge links snap it to the signed local hinge axis.
        var parent = _model.Bones[linkIndex].ParentIndex;
        var parentRotation = parent >= 0 && parent < _model.Bones.Count
            ? _world[parent] with { Translation = Vector3.Zero }
            : Matrix4x4.Identity;
        if (link.HasLimit && useAxis)
        {
            switch (link.Axis)
            {
                case SolveAxis.X:
                    axis = new Vector3(
                        Vector3.Dot(axis, new Vector3(parentRotation.M11, parentRotation.M12, parentRotation.M13)) >= 0 ? 1f : -1f, 0f, 0f);
                    break;
                case SolveAxis.Y:
                    axis = new Vector3(0f,
                        Vector3.Dot(axis, new Vector3(parentRotation.M21, parentRotation.M22, parentRotation.M23)) >= 0 ? 1f : -1f, 0f);
                    break;
                case SolveAxis.Z:
                    axis = new Vector3(0f, 0f,
                        Vector3.Dot(axis, new Vector3(parentRotation.M31, parentRotation.M32, parentRotation.M33)) >= 0 ? 1f : -1f);
                    break;
                default:
                    axis = Vector3.Normalize(Vector3.TransformNormal(axis, Matrix4x4.Transpose(parentRotation)));
                    break;
            }
        }
        else
        {
            axis = Vector3.Normalize(Vector3.TransformNormal(axis, Matrix4x4.Transpose(parentRotation)));
        }

        var dot = Math.Clamp(Vector3.Dot(toEffector, toGoal), -1f, 1f);
        // Deeper links may take larger per-step turns (babylon: limitAngle * (chainIndex + 1)).
        var angle = MathF.Min(ik.UnitAngle * (chainIndex + 1), MathF.Acos(dot));
        if (angle < 1e-7f)
            return;

        var delta = Quaternion.CreateFromAxisAngle(axis, angle);
        _ikRotations[linkIndex] = Quaternion.Normalize(delta * _ikRotations[linkIndex]);

        if (link.HasLimit)
            ClampLinkRotation(link, linkIndex, useAxis);

        RefreshChainWorlds(ik);
        targetPosition = _world[ik.Effector].Translation;
    }

    /// <summary>Clamps the link's COMBINED local rotation (IK delta ∘ folded animation) per-axis in the
    /// link's Euler order and stores the surviving delta. Past-limit angles REFLECT off the limit while
    /// <paramref name="useAxis"/> holds (first half of the iterations) — MMD's knee-bend bootstrap.</summary>
    private void ClampLinkRotation(in IkLinkSetup link, int linkIndex, bool useAxis)
    {
        var combined = Quaternion.Normalize(_ikRotations[linkIndex] * _animRotations[linkIndex]);
        var m = Matrix4x4.CreateFromQuaternion(combined);
        const float threshold = 88f * MathF.PI / 180f;
        float rX, rY, rZ, cos;
        Quaternion result;
        switch (link.Order)
        {
            case EulerOrder.Yxz:
                rX = ClampAbs(MathF.Asin(-m.M32), threshold);
                cos = InverseCos(rX);
                rY = MathF.Atan2(m.M31 * cos, m.M33 * cos);
                rZ = MathF.Atan2(m.M12 * cos, m.M22 * cos);
                rX = LimitAngle(rX, link.Min.X, link.Max.X, useAxis);
                rY = LimitAngle(rY, link.Min.Y, link.Max.Y, useAxis);
                rZ = LimitAngle(rZ, link.Min.Z, link.Max.Z, useAxis);
                result = Quaternion.CreateFromAxisAngle(Vector3.UnitY, rY)
                         * Quaternion.CreateFromAxisAngle(Vector3.UnitX, rX)
                         * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rZ);
                break;
            case EulerOrder.Zyx:
                rY = ClampAbs(MathF.Asin(-m.M13), threshold);
                cos = InverseCos(rY);
                rX = MathF.Atan2(m.M23 * cos, m.M33 * cos);
                rZ = MathF.Atan2(m.M12 * cos, m.M11 * cos);
                rX = LimitAngle(rX, link.Min.X, link.Max.X, useAxis);
                rY = LimitAngle(rY, link.Min.Y, link.Max.Y, useAxis);
                rZ = LimitAngle(rZ, link.Min.Z, link.Max.Z, useAxis);
                result = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rZ)
                         * Quaternion.CreateFromAxisAngle(Vector3.UnitY, rY)
                         * Quaternion.CreateFromAxisAngle(Vector3.UnitX, rX);
                break;
            default: // XZY
                rZ = ClampAbs(MathF.Asin(-m.M21), threshold);
                cos = InverseCos(rZ);
                rX = MathF.Atan2(m.M23 * cos, m.M22 * cos);
                rY = MathF.Atan2(m.M31 * cos, m.M11 * cos);
                rX = LimitAngle(rX, link.Min.X, link.Max.X, useAxis);
                rY = LimitAngle(rY, link.Min.Y, link.Max.Y, useAxis);
                rZ = LimitAngle(rZ, link.Min.Z, link.Max.Z, useAxis);
                result = Quaternion.CreateFromAxisAngle(Vector3.UnitX, rX)
                         * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rZ)
                         * Quaternion.CreateFromAxisAngle(Vector3.UnitY, rY);
                break;
        }

        _ikRotations[linkIndex] = Quaternion.Normalize(result * Quaternion.Inverse(_animRotations[linkIndex]));

        static float ClampAbs(float value, float limit) =>
            MathF.Abs(value) > limit ? (value < 0 ? -limit : limit) : value;

        static float InverseCos(float angle)
        {
            var c = MathF.Cos(angle);
            return c != 0f ? 1f / c : c;
        }
    }

    /// <summary>The angle clamp with MMD's reflection: an angle past a limit bounces to its mirror image
    /// inside the range while <paramref name="useAxis"/> holds (and the mirror is itself in range) —
    /// this is what starts a knee bending when CCD first pushes it the wrong way off a straight leg.</summary>
    internal static float LimitAngle(float angle, float min, float max, bool useAxis)
    {
        if (angle < min)
        {
            var diff = 2f * min - angle;
            return diff <= max && useAxis ? diff : min;
        }

        if (angle > max)
        {
            var diff = 2f * max - angle;
            return diff >= min && useAxis ? diff : max;
        }

        return angle;
    }

    /// <summary>Recomputes world matrices for the solve chain only (transform-ordered, parents first —
    /// each member's parent world is either untouched or refreshed earlier in the same array).</summary>
    private void RefreshChainWorlds(IkSetup ik)
    {
        foreach (var i in ik.RefreshOrder)
            ComputeWorld(i);
    }

    /// <summary>Clamps a rotation per-axis in Euler XYZ to the given limits (radians). Kept for the
    /// physics joint solver; the IK path uses the limit-adaptive orders above.</summary>
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
        // VMD stores the Bezier handles on the destination keyframe for the segment leading into it.
        var tx = Bezier(n.XInterp0, n.XInterp1, n.XInterp2, n.XInterp3, t);
        var ty = Bezier(n.YInterp0, n.YInterp1, n.YInterp2, n.YInterp3, t);
        var tz = Bezier(n.ZInterp0, n.ZInterp1, n.ZInterp2, n.ZInterp3, t);
        var tr = Bezier(n.RInterp0, n.RInterp1, n.RInterp2, n.RInterp3, t);
        var translation = new Vector3(
            float.Lerp(previous.Translation.X, n.Translation.X, tx),
            float.Lerp(previous.Translation.Y, n.Translation.Y, ty),
            float.Lerp(previous.Translation.Z, n.Translation.Z, tz));
        var rotation = Quaternion.Normalize(Quaternion.Slerp(
            Quaternion.Normalize(previous.Rotation), Quaternion.Normalize(n.Rotation), tr));
        return new LocalPose(rotation, translation);
    }

    private static float SampleMorph(IReadOnlyList<VMDMorphFrame> track, float frame)
    {
        var (previous, next, t) = Locate(track, frame, static f => f.Frame);
        return next is null ? previous.Weight : float.Lerp(previous.Weight, next.Value.Weight, t);
    }

    /// <summary>Camera parameters at <paramref name="time"/>, using all six authored VMD Bezier
    /// channels (target XYZ, rotation, distance and FOV).</summary>
    public static VMDCameraFrame SampleCamera(VMDDocument motion, TimeSpan time)
    {
        var track = motion.CameraTrack;
        if (track.Count == 0)
            return new VMDCameraFrame(0, -45f, new Vector3(0, 10, 0), Vector3.Zero, 30f, true);

        var frame = (float)(time.TotalSeconds * VMDDocument.FramesPerSecond);
        var (previous, next, t) = Locate(track, frame, static f => f.Frame);
        if (next is null)
            return previous;
        var n = next.Value;
        var interpolation = n.Interpolation;
        float Ease(int channel) => interpolation.Count == 24
            ? Bezier(
                interpolation[channel * 4], interpolation[channel * 4 + 2],
                interpolation[channel * 4 + 1], interpolation[channel * 4 + 3], t)
            : t;
        return new VMDCameraFrame(
            previous.Frame,
            float.Lerp(previous.Distance, n.Distance, Ease(4)),
            new Vector3(
                float.Lerp(previous.Target.X, n.Target.X, Ease(0)),
                float.Lerp(previous.Target.Y, n.Target.Y, Ease(1)),
                float.Lerp(previous.Target.Z, n.Target.Z, Ease(2))),
            Vector3.Lerp(previous.RotationRadians, n.RotationRadians, Ease(3)),
            float.Lerp(previous.FovDegrees, n.FovDegrees, Ease(5)),
            t >= 1f ? n.Perspective : previous.Perspective);
    }

    public static VMDLightFrame SampleLight(VMDDocument motion, TimeSpan time)
    {
        var track = motion.LightTrack;
        if (track.Count == 0)
            return new VMDLightFrame(0, Vector3.One, new Vector3(-0.5f, -1f, -0.5f));
        var frame = (float)(time.TotalSeconds * VMDDocument.FramesPerSecond);
        var (previous, next, t) = Locate(track, frame, static f => f.Frame);
        return next is null
            ? previous
            : new VMDLightFrame(previous.Frame,
                Vector3.Lerp(previous.Color, next.Value.Color, t),
                Vector3.Lerp(previous.Direction, next.Value.Direction, t));
    }

    public static VMDSelfShadowFrame SampleSelfShadow(VMDDocument motion, TimeSpan time)
    {
        var track = motion.SelfShadowTrack;
        if (track.Count == 0)
            return new VMDSelfShadowFrame(0, 0, 0f);
        var frame = (float)(time.TotalSeconds * VMDDocument.FramesPerSecond);
        var (previous, next, t) = Locate(track, frame, static f => f.Frame);
        return next is not null && t >= 1f ? next.Value : previous;
    }

    public static bool SampleVisibility(VMDDocument motion, TimeSpan time)
    {
        var track = motion.VisibilityTrack;
        if (track.Count == 0)
            return true;
        var frame = (float)(time.TotalSeconds * VMDDocument.FramesPerSecond);
        var (previous, next, t) = Locate(track, frame, static f => f.Frame);
        return next is not null && t >= 1f ? next.Value.Visible : previous.Visible;
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
