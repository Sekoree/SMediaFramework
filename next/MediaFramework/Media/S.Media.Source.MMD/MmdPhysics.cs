namespace S.Media.Source.MMD;

/// <summary>
/// Stage-5 MMD physics: hair/skirt/accessory secondary motion from the PMX rigid bodies + spring joints.
/// A deliberately compact position-based (XPBD-style) solver — predict under gravity/damping, then solve
/// the joints (swing orientation → spring pull → hard angular-limit clamp → position derived from the
/// clamped frame, since MMD joints lock the linear DOF) and shape collisions positionally, then derive
/// velocities — chosen over an impulse solver because it stays STABLE with the file's Bullet-tuned
/// parameters instead of exploding. The frame-derived position is what makes AUTHORED STIFFNESS real:
/// YYB-style tails lock their inner joints to ±0.1°, so the links must track the parent rigidly instead
/// of dangling on an arm-length rope. Kinematic bodies (<see cref="PmxPhysicsMode.FollowBone"/>) follow
/// the animated bones and push the dynamic chain around; dynamic bodies write their transforms back to
/// their bones between IK and skinning.
///
/// <para>STATEFUL by nature (unlike the pure-function-of-time animator): the owner steps it monotonically
/// and calls it once per rendered frame. Backward seeks or large jumps reset the chain to the animated
/// pose and warm a few substeps — the same behavior MMD itself has on seek.</para>
/// </summary>
public sealed class MmdPhysics
{
    private const float Gravity = -98f;         // model units/s² (1 unit ≈ 8 cm ⇒ 9.8 m/s²)
    private const float SubstepSeconds = 1f / 120f;
    private const int MaxSubstepsPerFrame = 8;
    private const int SolverIterations = 4;

    private struct Body
    {
        public int BoneIndex;
        public bool Dynamic;
        public bool AlignBonePosition;   // PhysicsWithBonePosition: rotation from physics, position from bone
        public ushort Group;             // bit (1 << group)
        public ushort Mask;
        public float InvMass;
        public float LinearDamping;
        public float AngularDamping;
        public float Radius;             // collision radius (sphere/capsule; box approximated)
        public float HalfHeight;         // capsule half-length along local Y (0 ⇒ sphere)
        public Matrix4x4 BoneToBody;     // bind: bodyWorld = BoneToBody · boneWorld (row-vector)
        public Matrix4x4 BodyToBone;
        public Quaternion BindRotation;  // body bind orientation (model space)
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 PrevPosition;
        public Quaternion PrevRotation;
        public Vector3 Velocity;
    }

    private readonly struct Joint(
        int a, int b, Vector3 anchorLocalA, Vector3 anchorLocalB,
        Vector3 angularLower, Vector3 angularUpper, float springRate, bool drives)
    {
        public readonly int A = a;
        public readonly int B = b;
        public readonly Vector3 AnchorLocalA = anchorLocalA; // joint anchor in each body's local frame
        public readonly Vector3 AnchorLocalB = anchorLocalB;
        public readonly Vector3 AngularLower = angularLower;
        public readonly Vector3 AngularUpper = angularUpper;
        public readonly float SpringRate = springRate;       // 1/s pull toward the bind pose (PMX angular spring)
        public readonly bool Drives = drives;                // B's structural joint (first per body in file order)
    }

    /// <summary>Blend for the EXTRA (non-driving) lattice links — skirt ring joints etc. They pull the
    /// anchors back together mass-weighted instead of snapping, so they AVERAGE against the structural
    /// joint over the iterations rather than overriding it (a full snap collapses the skirt).</summary>
    private const float SoftLinkRelaxation = 0.5f;

    /// <summary>Free-swing rate cap. Short-armed bodies (skirt plates, arm ≈ 0.5 units) read a garbage
    /// arm direction for a few substeps when their kinematic anchor teleports through a fast dance move;
    /// uncapped they flip 180° and wedge inside the torso colliders. Real cloth swings ~10 rad/s; a flip
    /// needs 60+. The angular-limit clamp is NOT capped — near-locked chains must track a whipping head
    /// rigidly.</summary>
    private const float MaxSwingRadiansPerSecond = 12f;

    /// <summary>Contact penetration recovery rate cap (units/s — Bullet's split-impulse analogue). A leg
    /// capsule sweeping through the skirt ring in a crouch is a deep overlap; resolving it in ONE substep
    /// blasts the plate outward far faster than gravity can ever restore, which is what ratcheted plates
    /// into the flipped-and-wedged state. Bounded recovery lets deep contacts resolve over a few frames
    /// (transient clipping, same as MMD) and keeps contact response comparable to the other forces.</summary>
    private const float MaxContactRecoveryPerSecond = 8f;

    /// <summary>Baseline pull toward the authored bind pose for free-swing joints (1/s; authored springs
    /// override when stronger — the bangs are 5). It stands in for the shape-holding that the authored
    /// ANGULAR damping produces in Bullet (this solver carries no angular velocity), so it is scaled by
    /// damping⁴: the 0.99999-damped skirt gets the full rate (holds its A-line at rest ≈8° sag, un-flips
    /// plates a crouch shoved past horizontal in ~⅓ s), while a lightly damped 0.5 body keeps true
    /// pendulum dynamics (rate ≈ 0.2/s).</summary>
    private const float ShapeRestoreRate = 3f;

    private readonly PmxDocument _model;
    private readonly Body[] _bodies;
    private readonly Joint[] _joints;
    private readonly bool[] _drivenBones;   // bones whose world transform WriteBack overwrites
    private float _pendingSeconds;
    private bool _primed;

    private MmdPhysics(PmxDocument model, Body[] bodies, Joint[] joints)
    {
        _model = model;
        _bodies = bodies;
        _joints = joints;
        _drivenBones = new bool[model.Bones.Count];
        foreach (var body in bodies)
            if (body.Dynamic && (uint)body.BoneIndex < (uint)_drivenBones.Length)
                _drivenBones[body.BoneIndex] = true;
    }

    /// <summary>True when the simulation overwrites this bone's world transform — the animator re-chains
    /// the OTHER bones under their (possibly physics-moved) parents after <see cref="Step"/>.</summary>
    public bool DrivesBone(int boneIndex) =>
        (uint)boneIndex < (uint)_drivenBones.Length && _drivenBones[boneIndex];

    /// <summary>Builds the simulation, or null when the model carries no usable dynamic bodies.</summary>
    public static MmdPhysics? TryCreate(PmxDocument model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.RigidBodies.Count == 0 || model.RigidBodies.All(b => b.Mode == PmxPhysicsMode.FollowBone))
            return null;

        var bodies = new Body[model.RigidBodies.Count];
        for (var i = 0; i < bodies.Length; i++)
        {
            var spec = model.RigidBodies[i];
            var bindRotation = BindRotationOf(spec);
            var bindBody = Matrix4x4.CreateFromQuaternion(bindRotation) with { Translation = spec.Position };
            var boneBind = spec.BoneIndex >= 0 && spec.BoneIndex < model.Bones.Count
                ? Matrix4x4.CreateTranslation(model.Bones[spec.BoneIndex].Position)
                : Matrix4x4.Identity;
            Matrix4x4.Invert(boneBind, out var boneBindInv);
            Matrix4x4.Invert(bindBody, out var bindBodyInv);
            var dynamic = spec.Mode != PmxPhysicsMode.FollowBone && spec.Mass > 0f;

            // Collision primitive: sphere/capsule as authored; a box becomes a capsule along its longest
            // axis with the SMALLEST half-extent as radius. Skirt plates are thin boxes riding just off
            // the hip/leg capsules — an averaged (fat) radius puts them in permanent contact at rest and
            // the constant pushes ratchet them over the waist; the thin radius keeps rest contacts clear.
            float radius, halfHeight;
            switch (spec.Shape)
            {
                case PmxRigidShape.Sphere:
                    radius = spec.Size.X;
                    halfHeight = 0f;
                    break;
                case PmxRigidShape.Capsule:
                    radius = spec.Size.X;
                    halfHeight = spec.Size.Y * 0.5f;
                    break;
                default: // Box
                    var s = spec.Size;
                    if (s.Y >= s.X && s.Y >= s.Z) { halfHeight = s.Y; radius = MathF.Min(s.X, s.Z); }
                    else if (s.X >= s.Z) { halfHeight = s.X; radius = MathF.Min(s.Y, s.Z); }
                    else { halfHeight = s.Z; radius = MathF.Min(s.X, s.Y); }
                    break;
            }

            bodies[i] = new Body
            {
                BoneIndex = spec.BoneIndex,
                Dynamic = dynamic,
                AlignBonePosition = spec.Mode == PmxPhysicsMode.PhysicsWithBonePosition,
                Group = (ushort)(1 << (spec.Group & 0x0F)),
                Mask = spec.CollisionMask,
                InvMass = dynamic ? 1f / MathF.Max(spec.Mass, 1e-4f) : 0f,
                // Full 1.0 allowed: hair tips are authored damping=1 (velocity dies every step) and
                // clamping below it leaves them visibly floppier than MMD renders them.
                LinearDamping = Math.Clamp(spec.LinearDamping, 0f, 1f),
                AngularDamping = Math.Clamp(spec.AngularDamping, 0f, 1f),
                Radius = MathF.Max(radius, 0.01f),
                HalfHeight = MathF.Max(halfHeight, 0f),
                BoneToBody = bindBody * boneBindInv,   // row-vector: body = (body·bone⁻¹)·bone
                BodyToBone = boneBind * bindBodyInv,
                BindRotation = bindRotation,
                Position = spec.Position,
                Rotation = bindRotation,
                PrevPosition = spec.Position,
                PrevRotation = bindRotation,
            };
        }

        var joints = new List<Joint>(model.Joints.Count);
        var hasDriver = new bool[bodies.Length];
        foreach (var joint in model.Joints)
        {
            if ((uint)joint.RigidBodyA >= (uint)bodies.Length || (uint)joint.RigidBodyB >= (uint)bodies.Length)
                continue;
            // Anchor expressed in each body's bind-local frame (model space at bind).
            var anchorLocalA = ToLocal(bodies[joint.RigidBodyA], joint.Position);
            var anchorLocalB = ToLocal(bodies[joint.RigidBodyB], joint.Position);
            // Angular spring → a per-second pull toward the bind pose (heuristic: the strongest authored
            // axis; the bangs' (5,0,5) springs are what keep them from swinging like free hair).
            var damping = bodies[joint.RigidBodyB].AngularDamping;
            var restore = ShapeRestoreRate * damping * damping * damping * damping;
            var spring = MathF.Max(
                MathF.Max(joint.AngularSpring.X, MathF.Max(joint.AngularSpring.Y, joint.AngularSpring.Z)),
                restore);
            // The FIRST joint per target body (file order) is its structural driver — MMD rigs author the
            // chain joints before the lattice links (skirt ring, strand interconnects).
            var drives = !hasDriver[joint.RigidBodyB];
            hasDriver[joint.RigidBodyB] = true;
            joints.Add(new Joint(joint.RigidBodyA, joint.RigidBodyB, anchorLocalA, anchorLocalB,
                joint.AngularLowerLimit, joint.AngularUpperLimit, MathF.Max(spring, 0f), drives));
        }

        return new MmdPhysics(model, bodies, [.. joints]);

        static Vector3 ToLocal(in Body body, Vector3 modelPoint) =>
            Vector3.Transform(modelPoint - body.Position, Quaternion.Inverse(body.BindRotation));
    }

    /// <summary>MMD rigid-body bind orientation (euler radians, the Y·X·Z order MMD tools emit).</summary>
    private static Quaternion BindRotationOf(PmxRigidBody spec) =>
        Quaternion.CreateFromRotationMatrix(
            Matrix4x4.CreateRotationZ(spec.RotationRadians.Z) *
            Matrix4x4.CreateRotationX(spec.RotationRadians.X) *
            Matrix4x4.CreateRotationY(spec.RotationRadians.Y));

    /// <summary>Resets every body onto its (animated) kinematic pose and clears velocities — a seek/jump
    /// re-basing. <paramref name="world"/> is the animator's post-IK world matrices.</summary>
    public void Reset(Matrix4x4[] world)
    {
        for (var i = 0; i < _bodies.Length; i++)
        {
            ref var body = ref _bodies[i];
            var target = TargetFromBone(body, world);
            body.Position = target.Translation;
            body.Rotation = RotationOf(target);
            body.PrevPosition = body.Position;
            body.PrevRotation = body.Rotation;
            body.Velocity = Vector3.Zero;
        }

        _pendingSeconds = 0f;
        _primed = true;
    }

    /// <summary>
    /// Advances the simulation by <paramref name="deltaSeconds"/> (fixed substeps internally) and writes
    /// dynamic bodies back into <paramref name="world"/>. Call after FK/IK, before skinning.
    /// </summary>
    public void Step(Matrix4x4[] world, float deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!_primed || deltaSeconds < 0f || deltaSeconds > 0.5f)
        {
            Reset(world);
            deltaSeconds = SubstepSeconds; // warm one substep so the chain starts settling
        }

        _pendingSeconds = MathF.Min(_pendingSeconds + deltaSeconds, MaxSubstepsPerFrame * SubstepSeconds);
        while (_pendingSeconds >= SubstepSeconds)
        {
            _pendingSeconds -= SubstepSeconds;
            Substep(world, SubstepSeconds);
        }

        WriteBack(world);
    }

    private void Substep(Matrix4x4[] world, float h)
    {
        // Kinematic bodies snap to their bones; dynamic bodies predict under gravity + damping.
        for (var i = 0; i < _bodies.Length; i++)
        {
            ref var body = ref _bodies[i];
            body.PrevPosition = body.Position;
            body.PrevRotation = body.Rotation;
            if (!body.Dynamic)
            {
                var target = TargetFromBone(body, world);
                body.Position = target.Translation;
                body.Rotation = RotationOf(target);
                continue;
            }

            body.Velocity *= MathF.Pow(1f - body.LinearDamping, h); // Bullet-style per-second damping
            body.Velocity += new Vector3(0f, Gravity * h, 0f);
            body.Position += body.Velocity * h;
        }

        var maxCorrection = MaxContactRecoveryPerSecond * h / SolverIterations;
        for (var iteration = 0; iteration < SolverIterations; iteration++)
        {
            SolveJoints(h / SolverIterations, maxCorrection);
            SolveCollisions(maxCorrection);
        }

        // PBD velocity update.
        var invH = 1f / h;
        for (var i = 0; i < _bodies.Length; i++)
        {
            ref var body = ref _bodies[i];
            if (!body.Dynamic)
                continue;
            body.Velocity = (body.Position - body.PrevPosition) * invH;
        }
    }

    private void SolveJoints(float springDt, float maxLinkPush)
    {
        foreach (var joint in _joints)
        {
            ref var a = ref _bodies[joint.A];
            ref var b = ref _bodies[joint.B];
            var invSum = a.InvMass + b.InvMass;
            if (invSum <= 0f)
                continue;

            var pivot = a.Position + Vector3.Transform(joint.AnchorLocalA, a.Rotation);

            if (!joint.Drives)
            {
                // Lattice link (a body's second+ joint — skirt ring etc.): pull the two anchor points
                // together mass-weighted with relaxation, capped like contact recovery. No rotation
                // edits, no snap — the structural joint owns the body's frame. The cap matters: after a
                // violent move flips part of the ring, UNCAPPED links make the flipped state collectively
                // stable (each plate holds its neighbor up, overpowering the shape-restore spring).
                var anchorB = b.Position + Vector3.Transform(joint.AnchorLocalB, b.Rotation);
                var linkError = (pivot - anchorB) * SoftLinkRelaxation;
                var errorLength = linkError.Length();
                if (errorLength > maxLinkPush)
                    linkError *= maxLinkPush / errorLength;
                b.Position += linkError * (b.InvMass / invSum);
                a.Position -= linkError * (a.InvMass / invSum);
                continue;
            }

            var deltaA = Quaternion.Normalize(a.Rotation * Quaternion.Inverse(a.BindRotation));

            if (b.Dynamic)
            {
                // 1) Swing: "follow" is where a RIGID joint would orient B given A's current motion; the
                //    minimal model-space rotation mapping the followed arm onto the ACTUAL (gravity/
                //    inertia displaced) arm is the joint's swing — twist stays unsimulated, so the
                //    relative rotation below is pure swing in A's frame, exactly what the limits clamp.
                var follow = Quaternion.Normalize(deltaA * b.BindRotation);
                var bindArm = Vector3.Transform(-joint.AnchorLocalB, follow);
                var arm = b.Position - pivot;
                var swung = bindArm.LengthSquared() > 1e-8f && arm.LengthSquared() > 1e-10f
                    ? Quaternion.Normalize(FromTo(Vector3.Normalize(bindArm), Vector3.Normalize(arm)) * follow)
                    : follow;
                b.Rotation = RotateTowards(b.Rotation, swung, MaxSwingRadiansPerSecond * springDt);

                // 2) Spring toward the bind pose (PMX angular spring — the bangs' shape-keeper), then the
                //    HARD per-axis limit clamp: near-locked joints (YYB tails, ±0.1°) collapse the swing
                //    entirely and the link tracks its parent rigidly — the authored stiffness.
                var relative = Quaternion.Normalize(
                    Quaternion.Inverse(deltaA) * Quaternion.Normalize(b.Rotation * Quaternion.Inverse(b.BindRotation)));
                if (joint.SpringRate > 0f)
                    relative = Quaternion.Normalize(Quaternion.Slerp(
                        relative, Quaternion.Identity, 1f - MathF.Exp(-joint.SpringRate * springDt)));
                var clamped = MmdAnimator.ClampEulerXyz(relative, joint.AngularLower, joint.AngularUpper);
                b.Rotation = Quaternion.Normalize(deltaA * clamped * b.BindRotation);
            }

            // 3) Linear DOF: MMD joints lock it (linear limits 0) — B's centre sits where the clamped
            //    frame puts it. Mass-weighted so dynamic A/B chains share the correction.
            var targetPosition = pivot - Vector3.Transform(joint.AnchorLocalB, b.Rotation);
            var error = targetPosition - b.Position;
            b.Position += error * (b.InvMass / invSum);
            a.Position -= error * (a.InvMass / invSum);
        }
    }

    /// <summary>Moves <paramref name="from"/> toward <paramref name="to"/> by at most
    /// <paramref name="maxRadians"/> of rotation.</summary>
    private static Quaternion RotateTowards(Quaternion from, Quaternion to, float maxRadians)
    {
        var angle = 2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(from, to)), 0f, 1f));
        return angle <= maxRadians
            ? to
            : Quaternion.Normalize(Quaternion.Slerp(from, to, maxRadians / angle));
    }

    /// <summary>The shortest rotation mapping unit vector <paramref name="from"/> onto <paramref name="to"/>.</summary>
    private static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot > 0.99999f)
            return Quaternion.Identity;
        if (dot < -0.99999f)
        {
            // Opposite: rotate π about any perpendicular axis.
            var perpendicular = Vector3.Cross(from, Vector3.UnitX);
            if (perpendicular.LengthSquared() < 1e-6f)
                perpendicular = Vector3.Cross(from, Vector3.UnitZ);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(perpendicular), MathF.PI);
        }

        var axis = Vector3.Normalize(Vector3.Cross(from, to));
        return Quaternion.CreateFromAxisAngle(axis, MathF.Acos(dot));
    }

    private void SolveCollisions(float maxPush)
    {
        for (var i = 0; i < _bodies.Length; i++)
        {
            ref var a = ref _bodies[i];
            for (var j = i + 1; j < _bodies.Length; j++)
            {
                ref var b = ref _bodies[j];
                var invSum = a.InvMass + b.InvMass;
                if (invSum <= 0f)
                    continue; // two kinematic bodies never resolve
                if ((a.Mask & b.Group) == 0 || (b.Mask & a.Group) == 0)
                    continue;

                // Closest points between the two capsule segments (spheres = zero-length segments).
                var (pa, pb) = ClosestSegmentPoints(
                    SegmentOf(a), SegmentOf(b));
                var delta = pb - pa;
                var distance = delta.Length();
                var minDistance = a.Radius + b.Radius;
                if (distance >= minDistance || distance < 1e-6f)
                    continue;

                var normal = delta / distance;
                var push = normal * (MathF.Min(minDistance - distance, maxPush) / invSum);
                a.Position -= push * a.InvMass;
                b.Position += push * b.InvMass;
            }
        }
    }

    private static (Vector3 Start, Vector3 End) SegmentOf(in Body body)
    {
        if (body.HalfHeight <= 0f)
            return (body.Position, body.Position);
        var axis = Vector3.Transform(new Vector3(0f, body.HalfHeight, 0f), body.Rotation);
        return (body.Position - axis, body.Position + axis);
    }

    private static (Vector3 A, Vector3 B) ClosestSegmentPoints((Vector3 Start, Vector3 End) s1, (Vector3 Start, Vector3 End) s2)
    {
        var d1 = s1.End - s1.Start;
        var d2 = s2.End - s2.Start;
        var r = s1.Start - s2.Start;
        float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
        float s, t;
        if (a <= 1e-9f && e <= 1e-9f) { s = 0f; t = 0f; }
        else if (a <= 1e-9f) { s = 0f; t = Math.Clamp(f / e, 0f, 1f); }
        else
        {
            var c = Vector3.Dot(d1, r);
            if (e <= 1e-9f) { t = 0f; s = Math.Clamp(-c / a, 0f, 1f); }
            else
            {
                var bDot = Vector3.Dot(d1, d2);
                var denom = a * e - bDot * bDot;
                s = denom > 1e-9f ? Math.Clamp((bDot * f - c * e) / denom, 0f, 1f) : 0f;
                t = Math.Clamp((bDot * s + f) / e, 0f, 1f);
                s = Math.Clamp((bDot * t - c) / a, 0f, 1f);
            }
        }

        return (s1.Start + d1 * s, s2.Start + d2 * t);
    }

    private static Matrix4x4 TargetFromBone(in Body body, Matrix4x4[] world)
    {
        var boneWorld = (uint)body.BoneIndex < (uint)world.Length ? world[body.BoneIndex] : Matrix4x4.Identity;
        return body.BoneToBody * boneWorld;
    }

    private static Quaternion RotationOf(in Matrix4x4 matrix)
    {
        var m = matrix with { Translation = Vector3.Zero };
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    private void WriteBack(Matrix4x4[] world)
    {
        for (var i = 0; i < _bodies.Length; i++)
        {
            ref var body = ref _bodies[i];
            if (!body.Dynamic || (uint)body.BoneIndex >= (uint)world.Length)
                continue;

            var bodyWorld = Matrix4x4.CreateFromQuaternion(body.Rotation) with { Translation = body.Position };
            var boneWorld = body.BodyToBone * bodyWorld;
            if (body.AlignBonePosition)
                boneWorld = boneWorld with { Translation = world[body.BoneIndex].Translation };
            if (IsFinite(boneWorld))
                world[body.BoneIndex] = boneWorld;
        }
    }

    private static bool IsFinite(in Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M22) && float.IsFinite(m.M33)
        && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43);
}
