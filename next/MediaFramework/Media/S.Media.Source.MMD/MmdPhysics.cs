namespace S.Media.Source.MMD;

/// <summary>
/// MMD physics: hair/skirt/accessory secondary motion from the PMX rigid bodies + spring joints,
/// simulated with a sequential-impulse rigid-body solver (a compact Bullet analogue) so the file's
/// Bullet-tuned parameters mean what their authors intended. Bodies carry real mass, inertia tensors
/// and velocities; every PMX joint becomes one uniform 6-DOF constraint with per-axis linear/angular
/// limits (Bullet's Euler-XYZ convention): warm-started bias-free velocity impulses resolve the
/// dynamic response, and a post-integration position pass (split impulse) resolves the drift — the
/// compliance of impulse-resolved limits is exactly the softness MMD hair has in the reference
/// runtimes. Kinematic (<see cref="PmxPhysicsMode.FollowBone"/>) bodies follow the animated bones with
/// real velocities so they drag the dynamic chains; dynamic bodies write their transforms back to
/// their bones between IK and skinning.
///
/// <para>Deliberate approximation: BOX shapes collide as capsules along their longest axis with the
/// smallest half-extent as radius (their inertia is exact, only the contact shape is approximated) —
/// skirt plates are thin boxes riding just off the leg capsules and the thin radius keeps rest
/// contacts clear.</para>
///
/// <para>STATEFUL by nature (unlike the pure-function-of-time animator): the owner steps it
/// monotonically once per rendered frame. Backward seeks or large jumps reset the chain to the
/// animated pose — the same behavior MMD itself has on seek.</para>
/// </summary>
public sealed class MmdPhysics
{
    private const float Gravity = -98f;          // model units/s² — MMD's Bullet world (9.8 × 10)
    private const float SubstepSeconds = 1f / 60f;
    private const int MaxSubstepsPerFrame = 4;
    // Split-impulse architecture (Box2D / Bullet's split impulse): the velocity rows are BIAS-FREE
    // (drive relative velocity to zero; safe to warm start — no position term ever enters a velocity),
    // and position errors are corrected AFTER integration by a nonlinear Gauss-Seidel pass that moves
    // poses directly without adding energy. This is what keeps the file's Bullet-tuned near-locked
    // chains rigid under load without the ERP limit-cycle wobble.
    private const int VelocityIterations = 16;
    private const int PositionIterations = 10;
    private const float MaxLinearCorrection = 0.2f;   // per NGS iteration (Box2D's caps)
    private const float MaxAngularCorrection = 0.15f;
    private const float ContactPositionFactor = 0.2f; // gentle depenetration fraction per iteration
    private const float ContactSlop = 0.005f;         // penetration allowance before correction kicks in
    private const float RestitutionThreshold = 1f;    // approach speed below which contacts don't bounce

    /// <summary>Angular limits smaller than this collapse to 0 — an always-active equality row instead
    /// of a limit the body coasts through and bounces off every substep (babylon-mmd's
    /// angularLimitClampThreshold, 5°: "small angular limits do not work well"). The YYB tails lock
    /// inner joints to ±0.1° — visually rigid either way.</summary>
    private const float AngularLockThreshold = 5f * MathF.PI / 180f;

    private struct Body
    {
        public int BoneIndex;
        public bool Dynamic;
        public bool AlignBonePosition;   // PhysicsWithBonePosition: rotation from physics, position from bone
        public ushort Group;             // bit (1 << group)
        public ushort Mask;
        public float InvMass;
        public Vector3 InvInertiaLocal;  // inverse diagonal inertia in the body frame (zero when kinematic)
        public float Friction;
        public float Restitution;
        public float LinearDamping;
        public float AngularDamping;
        public float Radius;             // collision radius (sphere/capsule; box approximated)
        public float HalfHeight;         // capsule half-length along local Y (0 ⇒ sphere)
        public Matrix4x4 BoneToBody;     // bind: bodyWorld = BoneToBody · boneWorld (row-vector)
        public Matrix4x4 BodyToBone;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;
    }

    private struct Joint
    {
        public int A;
        public int B;
        public Vector3 PivotA;           // joint anchor in each body's local frame
        public Vector3 PivotB;
        public Quaternion FrameA;        // joint frame rotation in each body's local frame
        public Quaternion FrameB;
        public Vector3 LinearLower, LinearUpper;
        public Vector3 AngularLower, AngularUpper;
        public Vector3 LinearSpring, AngularSpring;
        public bool AngularAllLocked;    // all three axes locked at 0 — the MMD chain-segment default
    }

    /// <summary>One velocity-constraint row (a joint axis), built once per substep and iterated.</summary>
    private struct SolverRow
    {
        public int A;
        public int B;
        public bool Angular;
        public Vector3 Axis;             // world-space solve direction
        public Vector3 ArmA, ArmB;       // anchor arms (linear rows)
        public float InvEffectiveMass;
        public float Bias;               // target-velocity term (zero for joints; kept for symmetry)
        public float MinImpulse, MaxImpulse;
        public float Impulse;            // accumulated (warm-started from the previous substep)
        public int AccumulatorIndex;     // slot in _jointAccumulators persisted across substeps
    }

    private struct Contact
    {
        public int A;
        public int B;
        public Vector3 Normal;           // A → B
        public Vector3 ArmA;             // contact point relative to each body's centre
        public Vector3 ArmB;
        public float Bias;               // position correction + restitution target velocity
        public float Friction;
        public float NormalImpulse;
        public Vector3 Tangent1, Tangent2;
        public float TangentImpulse1, TangentImpulse2;
    }

    private readonly PmxDocument _model;
    private readonly Body[] _bodies;
    private readonly Joint[] _joints;
    private readonly bool[] _drivenBones;          // bones whose world transform WriteBack overwrites
    private readonly HashSet<long> _jointedPairs;  // constrained pairs never collide (Bullet's disableCollisionsBetweenLinkedBodies)
    private readonly List<Contact> _contacts = [];
    private readonly List<SolverRow> _rows = [];
    private readonly float[] _jointAccumulators;   // 6 per joint — Bullet-style warm starting: last
                                                   // substep's impulses re-applied up front so ERP only
                                                   // corrects drift, not the standing gravity load
    private readonly Dictionary<long, (float Normal, float Tangent1, float Tangent2)> _contactCache = [];
    private float _pendingSeconds;
    private bool _primed;

    private MmdPhysics(PmxDocument model, Body[] bodies, Joint[] joints, HashSet<long> jointedPairs)
    {
        _model = model;
        _bodies = bodies;
        _joints = joints;
        _jointAccumulators = new float[joints.Length * 6];
        _jointedPairs = jointedPairs;
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
            var bindRotation = BindRotationOf(spec.RotationRadians);
            var bindBody = Matrix4x4.CreateFromQuaternion(bindRotation) with { Translation = spec.Position };
            var boneBind = spec.BoneIndex >= 0 && spec.BoneIndex < model.Bones.Count
                ? Matrix4x4.CreateTranslation(model.Bones[spec.BoneIndex].Position)
                : Matrix4x4.Identity;
            Matrix4x4.Invert(boneBind, out var boneBindInv);
            Matrix4x4.Invert(bindBody, out var bindBodyInv);
            var dynamic = spec.Mode != PmxPhysicsMode.FollowBone && spec.Mass > 0f;
            var mass = MathF.Max(spec.Mass, 1e-4f);

            // Collision primitive: sphere/capsule as authored; a box becomes a capsule along its longest
            // axis with the SMALLEST half-extent as radius (thin radius keeps skirt rest contacts clear).
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
                InvMass = dynamic ? 1f / mass : 0f,
                InvInertiaLocal = dynamic ? InverseInertiaOf(spec, mass) : Vector3.Zero,
                Friction = MathF.Max(spec.Friction, 0f),
                Restitution = Math.Clamp(spec.Restitution, 0f, 1f),
                LinearDamping = Math.Clamp(spec.LinearDamping, 0f, 1f),
                AngularDamping = Math.Clamp(spec.AngularDamping, 0f, 1f),
                Radius = MathF.Max(radius, 0.01f),
                HalfHeight = MathF.Max(halfHeight, 0f),
                BoneToBody = bindBody * boneBindInv,   // row-vector: body = (body·bone⁻¹)·bone
                BodyToBone = boneBind * bindBodyInv,
                Position = spec.Position,
                Rotation = bindRotation,
            };
        }

        var joints = new List<Joint>(model.Joints.Count);
        var jointedPairs = new HashSet<long>();
        foreach (var joint in model.Joints)
        {
            if ((uint)joint.RigidBodyA >= (uint)bodies.Length || (uint)joint.RigidBodyB >= (uint)bodies.Length)
                continue;

            ref var a = ref bodies[joint.RigidBodyA];
            ref var b = ref bodies[joint.RigidBodyB];
            var jointRotation = BindRotationOf(joint.RotationRadians);
            var angularLower = ClampSmallAngles(joint.AngularLowerLimit);
            var angularUpper = ClampSmallAngles(joint.AngularUpperLimit);
            joints.Add(new Joint
            {
                A = joint.RigidBodyA,
                B = joint.RigidBodyB,
                PivotA = Vector3.Transform(joint.Position - a.Position, Quaternion.Conjugate(a.Rotation)),
                PivotB = Vector3.Transform(joint.Position - b.Position, Quaternion.Conjugate(b.Rotation)),
                FrameA = Quaternion.Normalize(Quaternion.Inverse(a.Rotation) * jointRotation),
                FrameB = Quaternion.Normalize(Quaternion.Inverse(b.Rotation) * jointRotation),
                LinearLower = joint.LinearLowerLimit,
                LinearUpper = joint.LinearUpperLimit,
                AngularLower = angularLower,
                AngularUpper = angularUpper,
                LinearSpring = joint.LinearSpring,
                AngularSpring = joint.AngularSpring,
                AngularAllLocked = angularLower == Vector3.Zero && angularUpper == Vector3.Zero,
            });
            jointedPairs.Add(PairKey(joint.RigidBodyA, joint.RigidBodyB));

            // MMD quirk (babylon-mmd, ref: katwat): a PhysicsWithBonePosition body whose bone's PARENT
            // carries the joint's dynamic A body must act as plain Physics — the bone-position snap
            // would fight the parent link it hangs from.
            if (!IsFollowBone(model, joint.RigidBodyA) && b.AlignBonePosition
                && BoneParentOf(model, b.BoneIndex) is { } parentOfB
                && parentOfB == a.BoneIndex)
                b.AlignBonePosition = false;
            else if (!IsFollowBone(model, joint.RigidBodyB) && a.AlignBonePosition
                && BoneParentOf(model, a.BoneIndex) is { } parentOfA
                && parentOfA == b.BoneIndex)
                a.AlignBonePosition = false;
        }

        return new MmdPhysics(model, bodies, [.. joints], jointedPairs);

        static Vector3 ClampSmallAngles(Vector3 limits) => new(
            MathF.Abs(limits.X) < AngularLockThreshold ? 0f : limits.X,
            MathF.Abs(limits.Y) < AngularLockThreshold ? 0f : limits.Y,
            MathF.Abs(limits.Z) < AngularLockThreshold ? 0f : limits.Z);

        static bool IsFollowBone(PmxDocument model, int bodyIndex) =>
            model.RigidBodies[bodyIndex].Mode == PmxPhysicsMode.FollowBone;

        static int? BoneParentOf(PmxDocument model, int boneIndex) =>
            (uint)boneIndex < (uint)model.Bones.Count && model.Bones[boneIndex].ParentIndex >= 0
                ? model.Bones[boneIndex].ParentIndex
                : null;
    }

    private static long PairKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary>Inverse diagonal inertia in the body frame — Bullet's shape formulas (a capsule uses the
    /// enclosing-box approximation Bullet itself uses).</summary>
    private static Vector3 InverseInertiaOf(PmxRigidBody spec, float mass)
    {
        Vector3 half;
        switch (spec.Shape)
        {
            case PmxRigidShape.Sphere:
                var i = 0.4f * mass * spec.Size.X * spec.Size.X;
                return i > 1e-9f ? new Vector3(1f / i) : Vector3.Zero;
            case PmxRigidShape.Capsule:
                half = new Vector3(spec.Size.X, spec.Size.X + spec.Size.Y * 0.5f, spec.Size.X);
                break;
            default: // Box — PMX sizes are half-extents already
                half = spec.Size;
                break;
        }

        half = Vector3.Max(half, new Vector3(0.01f));
        var factor = mass / 3f; // solid box: I = m/12 · (l² + l²) with l = 2·half ⇒ m/3 · (h² + h²)
        var inertia = new Vector3(
            factor * (half.Y * half.Y + half.Z * half.Z),
            factor * (half.X * half.X + half.Z * half.Z),
            factor * (half.X * half.X + half.Y * half.Y));
        return new Vector3(
            inertia.X > 1e-9f ? 1f / inertia.X : 0f,
            inertia.Y > 1e-9f ? 1f / inertia.Y : 0f,
            inertia.Z > 1e-9f ? 1f / inertia.Z : 0f);
    }

    /// <summary>MMD rigid-body/joint bind orientation (euler radians, the Y·X·Z order MMD tools emit).</summary>
    private static Quaternion BindRotationOf(Vector3 euler) =>
        Quaternion.CreateFromRotationMatrix(
            Matrix4x4.CreateRotationZ(euler.Z) *
            Matrix4x4.CreateRotationX(euler.X) *
            Matrix4x4.CreateRotationY(euler.Y));

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
            body.LinearVelocity = Vector3.Zero;
            body.AngularVelocity = Vector3.Zero;
        }

        Array.Clear(_jointAccumulators);
        _contactCache.Clear();
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
        // Kinematic bodies snap to their bones carrying REAL velocities (they drag chains through
        // contacts and joints); dynamic bodies integrate gravity + Bullet-style per-second damping.
        var invH = 1f / h;
        for (var i = 0; i < _bodies.Length; i++)
        {
            ref var body = ref _bodies[i];
            if (!body.Dynamic)
            {
                var target = TargetFromBone(body, world);
                var targetRotation = RotationOf(target);
                body.LinearVelocity = (target.Translation - body.Position) * invH;
                body.AngularVelocity = ToScaledAxis(
                    Quaternion.Normalize(targetRotation * Quaternion.Inverse(body.Rotation))) * invH;
                body.Position = target.Translation;
                body.Rotation = targetRotation;
                continue;
            }

            body.LinearVelocity += new Vector3(0f, Gravity * h, 0f);
            body.LinearVelocity *= MathF.Pow(1f - body.LinearDamping, h);
            body.AngularVelocity *= MathF.Pow(1f - body.AngularDamping, h);
        }

        DetectContacts();
        BuildJointRows();

        for (var iteration = 0; iteration < VelocityIterations; iteration++)
        {
            for (var r = 0; r < _rows.Count; r++)
            {
                var row = _rows[r];
                SolveRow(ref row);
                _rows[r] = row;
            }

            for (var c = 0; c < _contacts.Count; c++)
            {
                var contact = _contacts[c];
                SolveContact(ref contact);
                _contacts[c] = contact;
            }
        }

        // Persist impulses for next substep's warm start.
        foreach (var row in _rows)
            _jointAccumulators[row.AccumulatorIndex] = row.Impulse;
        _contactCache.Clear();
        foreach (var contact in _contacts)
            _contactCache[PairKey(contact.A, contact.B)] =
                (contact.NormalImpulse, contact.TangentImpulse1, contact.TangentImpulse2);

        // Integrate dynamic bodies (semi-implicit Euler; quaternion derivative for the rotation).
        for (var i = 0; i < _bodies.Length; i++)
        {
            ref var body = ref _bodies[i];
            if (!body.Dynamic)
                continue;

            // Runaway guard — a mangled file can still request absurd states; cap speeds, not behavior.
            var speed = body.LinearVelocity.Length();
            if (speed > 500f)
                body.LinearVelocity *= 500f / speed;
            var spin = body.AngularVelocity.Length();
            if (spin > 200f)
                body.AngularVelocity *= 200f / spin;

            body.Position += body.LinearVelocity * h;
            var omega = new Quaternion(body.AngularVelocity, 0f);
            body.Rotation = Quaternion.Normalize(
                body.Rotation + Quaternion.Multiply(omega * body.Rotation, 0.5f * h));
        }

        SolvePositions();
    }

    /// <summary>Builds the substep's constraint rows from every joint: springs applied as pre-solve
    /// impulses, then one BIAS-FREE row per authored linear/angular axis (equality when locked,
    /// one-sided when a range is violated), each warm-started with the impulse it carried last substep.
    /// Position errors are NOT corrected here — the NGS pass owns those.</summary>
    private void BuildJointRows()
    {
        _rows.Clear();
        Span<Vector3> axes = stackalloc Vector3[3];
        for (var j = 0; j < _joints.Length; j++)
        {
            ref var joint = ref _joints[j];
            ref var a = ref _bodies[joint.A];
            ref var b = ref _bodies[joint.B];
            if (a.InvMass + b.InvMass <= 0f)
                continue;

            var rA = Vector3.Transform(joint.PivotA, a.Rotation);
            var rB = Vector3.Transform(joint.PivotB, b.Rotation);
            var rotationA = Quaternion.Normalize(a.Rotation * joint.FrameA);
            var rotationB = Quaternion.Normalize(b.Rotation * joint.FrameB);
            var diff = Vector3.Transform(b.Position + rB - (a.Position + rA), Quaternion.Conjugate(rotationA));
            var euler = EulerXyzOf(Quaternion.Normalize(Quaternion.Inverse(rotationA) * rotationB));
            AngularAxes(rotationA, rotationB, axes);

            ApplyJointSprings(ref a, ref b, joint, rotationA, axes, euler, diff, rA, rB, SubstepSeconds);

            // Linear rows along the A joint frame's axes (Bullet's convention). MMD authors lock most
            // axes (lower == upper == 0) — always-active equality; genuine ranges only when violated.
            for (var axis = 0; axis < 3; axis++)
            {
                var accumulatorIndex = j * 6 + axis;
                var lower = Component(joint.LinearLower, axis);
                var upper = Component(joint.LinearUpper, axis);
                if (lower > upper)
                    continue; // Bullet: inverted range = free axis

                var displacement = Component(diff, axis);
                if (!TryLimitError(displacement, lower, upper, out var error, out var equality))
                {
                    _jointAccumulators[accumulatorIndex] = 0f; // inactive — a stale impulse is a phantom motor
                    continue;
                }

                var n = Vector3.Transform(UnitOf(axis), rotationA);
                var effective = EffectiveMassLinear(a, b, rA, rB, n);
                if (effective <= 1e-9f)
                    continue;

                AddRow(new SolverRow
                {
                    A = joint.A,
                    B = joint.B,
                    Angular = false,
                    Axis = n,
                    ArmA = rA,
                    ArmB = rB,
                    InvEffectiveMass = 1f / effective,
                    // Below-lower violations may only push the value up (λ ≥ 0), above-upper only down.
                    MinImpulse = equality || error > 0f ? float.NegativeInfinity : 0f,
                    MaxImpulse = equality || error < 0f ? float.PositiveInfinity : 0f,
                    AccumulatorIndex = accumulatorIndex,
                });
            }

            // Angular rows about Bullet's 6-DOF solve directions (B-frame X, mutual cross, A-frame Z).
            for (var axis = 0; axis < 3; axis++)
            {
                var accumulatorIndex = j * 6 + 3 + axis;
                var lower = Component(joint.AngularLower, axis);
                var upper = Component(joint.AngularUpper, axis);
                if (lower > upper)
                    continue;

                var angle = Component(euler, axis);
                if (!TryLimitError(angle, lower, upper, out var error, out var equality))
                {
                    _jointAccumulators[accumulatorIndex] = 0f;
                    continue;
                }

                var n = axes[axis];
                var effective = Vector3.Dot(InvInertiaWorld(a, n) + InvInertiaWorld(b, n), n);
                if (effective <= 1e-9f)
                    continue;

                AddRow(new SolverRow
                {
                    A = joint.A,
                    B = joint.B,
                    Angular = true,
                    Axis = n,
                    InvEffectiveMass = 1f / effective,
                    MinImpulse = equality || error > 0f ? float.NegativeInfinity : 0f,
                    MaxImpulse = equality || error < 0f ? float.PositiveInfinity : 0f,
                    AccumulatorIndex = accumulatorIndex,
                });
            }
        }

    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static Vector3 UnitOf(int axis) => axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;

    /// <summary>Violation → signed error; equality (locked) axes are always active with the full
    /// displacement.</summary>
    private static bool TryLimitError(float value, float lower, float upper, out float error, out bool equality)
    {
        equality = upper - lower < 1e-6f;
        if (equality)
        {
            error = value - lower;
            return true;
        }

        if (value < lower) { error = value - lower; return true; }
        if (value > upper) { error = value - upper; return true; }
        error = 0f;
        return false;
    }

    /// <summary>Nonlinear Gauss-Seidel position pass (Box2D's split-impulse analogue): after
    /// integration, joint and contact position errors are corrected directly on the poses — velocities
    /// stay untouched, so no energy is injected and locked chains neither droop under gravity nor
    /// limit-cycle around their locks.</summary>
    private void SolvePositions()
    {
        Span<Vector3> axes = stackalloc Vector3[3];
        for (var iteration = 0; iteration < PositionIterations; iteration++)
        {
            var worst = 0f;

            for (var j = 0; j < _joints.Length; j++)
            {
                ref var joint = ref _joints[j];
                ref var a = ref _bodies[joint.A];
                ref var b = ref _bodies[joint.B];
                if (a.InvMass + b.InvMass <= 0f)
                    continue;

                // Angular first — each body's share of the correction rotates it ABOUT ITS OWN JOINT
                // ANCHOR (orientation and position together), so the anchors stay put and the linear
                // fix below never has to undo the rotation (rotating about the centre instead makes the
                // two fixes fight through the inertia coupling and convergence crawls).
                var rotationA = Quaternion.Normalize(a.Rotation * joint.FrameA);
                var rotationB = Quaternion.Normalize(b.Rotation * joint.FrameB);
                if (joint.AngularAllLocked)
                {
                    // The MMD chain-segment default (all axes locked at 0): correct the FULL relative
                    // rotation exactly — no per-axis Euler decomposition, whose alternating capped
                    // corrections thrash long tail chains once relative rotations grow. A root→tip
                    // sweep then rigidifies a locked chain like FK chaining.
                    var mismatch = ToScaledAxis(Quaternion.Normalize(rotationA * Quaternion.Inverse(rotationB)));
                    var magnitude = mismatch.Length();
                    if (magnitude > 1e-6f)
                    {
                        var n = mismatch / magnitude;
                        var inertiaA = Vector3.Dot(InvInertiaWorld(a, n), n);
                        var inertiaB = Vector3.Dot(InvInertiaWorld(b, n), n);
                        var effective = inertiaA + inertiaB;
                        if (effective > 1e-9f)
                        {
                            worst = MathF.Max(worst, magnitude);
                            var correction = MathF.Min(magnitude, MaxAngularCorrection);
                            if (inertiaB > 0f)
                                RotateAboutAnchor(ref b, n * (correction * inertiaB / effective),
                                    b.Position + Vector3.Transform(joint.PivotB, b.Rotation));
                            if (inertiaA > 0f)
                                RotateAboutAnchor(ref a, n * (-correction * inertiaA / effective),
                                    a.Position + Vector3.Transform(joint.PivotA, a.Rotation));
                        }
                    }
                }
                else
                {
                    var euler = EulerXyzOf(Quaternion.Normalize(Quaternion.Inverse(rotationA) * rotationB));
                    AngularAxes(rotationA, rotationB, axes);
                    for (var axis = 0; axis < 3; axis++)
                    {
                        var lower = Component(joint.AngularLower, axis);
                        var upper = Component(joint.AngularUpper, axis);
                        if (lower > upper)
                            continue;
                        if (!TryLimitError(Component(euler, axis), lower, upper, out var error, out _))
                            continue;

                        var n = axes[axis];
                        var inertiaA = Vector3.Dot(InvInertiaWorld(a, n), n);
                        var inertiaB = Vector3.Dot(InvInertiaWorld(b, n), n);
                        var effective = inertiaA + inertiaB;
                        if (effective <= 1e-9f)
                            continue;

                        worst = MathF.Max(worst, MathF.Abs(error));
                        var correction = Math.Clamp(-error, -MaxAngularCorrection, MaxAngularCorrection);
                        if (inertiaB > 0f)
                            RotateAboutAnchor(ref b, n * (correction * inertiaB / effective),
                                b.Position + Vector3.Transform(joint.PivotB, b.Rotation));
                        if (inertiaA > 0f)
                            RotateAboutAnchor(ref a, n * (-correction * inertiaA / effective),
                                a.Position + Vector3.Transform(joint.PivotA, a.Rotation));
                    }
                }

                // Linear, with arms recomputed after the rotations above. TRANSLATE-ONLY on purpose:
                // a rotational component here would break the angular locks just restored (and the
                // angular fix would re-break this anchor) — the ping-pong diverges on light chains.
                var rA = Vector3.Transform(joint.PivotA, a.Rotation);
                var rB = Vector3.Transform(joint.PivotB, b.Rotation);
                rotationA = Quaternion.Normalize(a.Rotation * joint.FrameA);
                var diff = Vector3.Transform(b.Position + rB - (a.Position + rA), Quaternion.Conjugate(rotationA));
                var invMassSum = a.InvMass + b.InvMass;
                for (var axis = 0; axis < 3; axis++)
                {
                    var lower = Component(joint.LinearLower, axis);
                    var upper = Component(joint.LinearUpper, axis);
                    if (lower > upper)
                        continue;
                    if (!TryLimitError(Component(diff, axis), lower, upper, out var error, out _))
                        continue;

                    worst = MathF.Max(worst, MathF.Abs(error));
                    var n = Vector3.Transform(UnitOf(axis), rotationA);
                    var correction = Math.Clamp(-error, -MaxLinearCorrection, MaxLinearCorrection);
                    var move = n * (correction / invMassSum);
                    a.Position -= move * a.InvMass;
                    b.Position += move * b.InvMass;
                }
            }

            // Contact depenetration — geometry recomputed live, gentle fraction per iteration.
            foreach (var contact in _contacts)
            {
                ref var a = ref _bodies[contact.A];
                ref var b = ref _bodies[contact.B];
                var (pa, pb) = ClosestSegmentPoints(SegmentOf(a), SegmentOf(b));
                var delta = pb - pa;
                var distance = delta.Length();
                var minDistance = a.Radius + b.Radius;
                if (distance >= minDistance - ContactSlop || distance < 1e-6f)
                    continue;

                var n = delta / distance;
                var invMassSum = a.InvMass + b.InvMass;
                if (invMassSum <= 0f)
                    continue;

                var depth = minDistance - distance;
                worst = MathF.Max(worst, depth);
                var correction = MathF.Min(ContactPositionFactor * (depth - ContactSlop), MaxLinearCorrection);
                if (correction <= 0f)
                    continue;

                // Translate-only, like the joint fix above (split-impulse depenetration).
                var move = n * (correction / invMassSum);
                a.Position -= move * a.InvMass;
                b.Position += move * b.InvMass;
            }

            if (worst < 1e-4f)
                break;
        }

        static void RotateAboutAnchor(ref Body body, Vector3 theta, Vector3 anchor)
        {
            var angle = theta.Length();
            if (angle < 1e-9f)
                return;
            var delta = Quaternion.CreateFromAxisAngle(theta / angle, angle);
            body.Rotation = Quaternion.Normalize(delta * body.Rotation);
            body.Position = anchor + Vector3.Transform(body.Position - anchor, delta);
        }
    }

    /// <summary>Adds a row warm-started with (a bounds-clamped copy of) its previous-substep impulse.</summary>
    private void AddRow(SolverRow row)
    {
        var warm = Math.Clamp(_jointAccumulators[row.AccumulatorIndex], row.MinImpulse, row.MaxImpulse);
        if (warm != 0f)
        {
            ref var a = ref _bodies[row.A];
            ref var b = ref _bodies[row.B];
            if (row.Angular)
                ApplyAngularImpulse(ref a, ref b, row.Axis * warm);
            else
                ApplyLinearImpulse(ref a, ref b, row.ArmA, row.ArmB, row.Axis * warm);
        }

        row.Impulse = warm;
        _rows.Add(row);
    }

    private void SolveRow(ref SolverRow row)
    {
        ref var a = ref _bodies[row.A];
        ref var b = ref _bodies[row.B];

        float velocity;
        if (row.Angular)
        {
            velocity = Vector3.Dot(b.AngularVelocity - a.AngularVelocity, row.Axis);
        }
        else
        {
            velocity = Vector3.Dot(
                b.LinearVelocity + Vector3.Cross(b.AngularVelocity, row.ArmB)
                - a.LinearVelocity - Vector3.Cross(a.AngularVelocity, row.ArmA), row.Axis);
        }

        var impulse = -(velocity + row.Bias) * row.InvEffectiveMass;
        var accumulated = Math.Clamp(row.Impulse + impulse, row.MinImpulse, row.MaxImpulse);
        impulse = accumulated - row.Impulse;
        row.Impulse = accumulated;

        if (row.Angular)
            ApplyAngularImpulse(ref a, ref b, row.Axis * impulse);
        else
            ApplyLinearImpulse(ref a, ref b, row.ArmA, row.ArmB, row.Axis * impulse);
    }

    /// <summary>PMX angular/linear springs — Bullet's 6-DOF spring drives the joint toward its bind pose
    /// with force k·delta. Applied as one pre-solve impulse per substep, capped at the impulse that
    /// would zero the delta in one step (the hair rigs author k up to 500 on 50 g bodies).</summary>
    private void ApplyJointSprings(
        ref Body a, ref Body b, in Joint joint,
        Quaternion rotationA, ReadOnlySpan<Vector3> axes, Vector3 euler, Vector3 diff,
        Vector3 rA, Vector3 rB, float h)
    {
        if (joint.AngularSpring != Vector3.Zero)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                var k = axis == 0 ? joint.AngularSpring.X : axis == 1 ? joint.AngularSpring.Y : joint.AngularSpring.Z;
                var angle = axis == 0 ? euler.X : axis == 1 ? euler.Y : euler.Z;
                if (k <= 0f || MathF.Abs(angle) < 1e-6f)
                    continue;

                var n = axes[axis];
                var effective = Vector3.Dot(InvInertiaWorld(a, n) + InvInertiaWorld(b, n), n);
                if (effective <= 1e-9f)
                    continue;

                var impulse = -k * angle * h;
                var critical = MathF.Abs(angle) / (effective * h);
                impulse = Math.Clamp(impulse, -critical, critical);
                ApplyAngularImpulse(ref a, ref b, n * impulse);
            }
        }

        if (joint.LinearSpring != Vector3.Zero)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                var k = axis == 0 ? joint.LinearSpring.X : axis == 1 ? joint.LinearSpring.Y : joint.LinearSpring.Z;
                var displacement = axis == 0 ? diff.X : axis == 1 ? diff.Y : diff.Z;
                if (k <= 0f || MathF.Abs(displacement) < 1e-6f)
                    continue;

                var n = Vector3.Transform(axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ, rotationA);
                var effective = EffectiveMassLinear(a, b, rA, rB, n);
                if (effective <= 1e-9f)
                    continue;

                var impulse = -k * displacement * h;
                var critical = MathF.Abs(displacement) / (effective * h);
                impulse = Math.Clamp(impulse, -critical, critical);
                ApplyLinearImpulse(ref a, ref b, rA, rB, n * impulse);
            }
        }
    }

    /// <summary>Bullet btGeneric6DofConstraint solve axes: X about the B frame's X, Z about the A
    /// frame's Z, Y about their mutual perpendicular.</summary>
    private static void AngularAxes(Quaternion rotationA, Quaternion rotationB, Span<Vector3> axes)
    {
        var axis0 = Vector3.Transform(Vector3.UnitX, rotationB); // B frame X
        var axis2 = Vector3.Transform(Vector3.UnitZ, rotationA); // A frame Z
        var axis1 = Vector3.Cross(axis2, axis0);
        axes[0] = SafeNormalize(Vector3.Cross(axis1, axis2), axis0);
        axes[1] = SafeNormalize(axis1, Vector3.UnitY);
        axes[2] = SafeNormalize(Vector3.Cross(axis0, axis1), axis2);

        static Vector3 SafeNormalize(Vector3 v, Vector3 fallback) =>
            v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : fallback;
    }

    /// <summary>Bullet's matrixToEulerXYZ of the relative joint rotation, mapped to System.Numerics
    /// row-vector matrices (btMatrix column [r][c] ≡ our M(c+1)(r+1)).</summary>
    internal static Vector3 EulerXyzOf(Quaternion relative)
    {
        var m = Matrix4x4.CreateFromQuaternion(relative);
        var sinY = Math.Clamp(m.M31, -1f, 1f);
        if (MathF.Abs(sinY) < 0.99999f)
            return new Vector3(
                MathF.Atan2(-m.M32, m.M33),
                MathF.Asin(sinY),
                MathF.Atan2(-m.M21, m.M11));

        return new Vector3(
            sinY > 0 ? MathF.Atan2(m.M12, m.M22) : -MathF.Atan2(m.M12, m.M22),
            sinY > 0 ? MathF.PI / 2f : -MathF.PI / 2f,
            0f);
    }

    private void DetectContacts()
    {
        _contacts.Clear();
        for (var i = 0; i < _bodies.Length; i++)
        {
            ref var a = ref _bodies[i];
            for (var j = i + 1; j < _bodies.Length; j++)
            {
                ref var b = ref _bodies[j];
                if (a.InvMass + b.InvMass <= 0f)
                    continue; // two kinematic bodies never resolve
                if ((a.Mask & b.Group) == 0 || (b.Mask & a.Group) == 0)
                    continue;
                if (_jointedPairs.Contains(PairKey(i, j)))
                    continue; // Bullet: constrained pairs don't collide

                var (pa, pb) = ClosestSegmentPoints(SegmentOf(a), SegmentOf(b));
                var delta = pb - pa;
                var distance = delta.Length();
                var minDistance = a.Radius + b.Radius;
                if (distance >= minDistance || distance < 1e-6f)
                    continue;

                var normal = delta / distance;
                var point = (pa + pb) * 0.5f;
                var rA = point - a.Position;
                var rB = point - b.Position;

                // Restitution keys off the PRE-solve approach speed (Bullet's restitution threshold).
                var approach = Vector3.Dot(
                    b.LinearVelocity + Vector3.Cross(b.AngularVelocity, rB)
                    - a.LinearVelocity - Vector3.Cross(a.AngularVelocity, rA), normal);
                var restitution = a.Restitution * b.Restitution;
                // Velocity-level target is the bounce only — penetration is fixed by the position pass.
                var bias = approach < -RestitutionThreshold ? -restitution * approach : 0f;

                var tangent1 = Vector3.Cross(normal, MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
                tangent1 = Vector3.Normalize(tangent1);

                var contact = new Contact
                {
                    A = i,
                    B = j,
                    Normal = normal,
                    ArmA = rA,
                    ArmB = rB,
                    Bias = bias,
                    Friction = a.Friction * b.Friction,
                    Tangent1 = tangent1,
                    Tangent2 = Vector3.Cross(normal, tangent1),
                };

                // Warm start from the pair's previous-substep impulses (resting contacts stop sinking).
                if (_contactCache.TryGetValue(PairKey(i, j), out var cached))
                {
                    contact.NormalImpulse = MathF.Max(cached.Normal, 0f);
                    var maxFriction = contact.Friction * contact.NormalImpulse;
                    contact.TangentImpulse1 = Math.Clamp(cached.Tangent1, -maxFriction, maxFriction);
                    contact.TangentImpulse2 = Math.Clamp(cached.Tangent2, -maxFriction, maxFriction);
                    var warm = normal * contact.NormalImpulse
                               + contact.Tangent1 * contact.TangentImpulse1
                               + contact.Tangent2 * contact.TangentImpulse2;
                    ApplyLinearImpulse(ref a, ref b, rA, rB, warm);
                }

                _contacts.Add(contact);
            }
        }
    }

    private void SolveContact(ref Contact contact)
    {
        ref var a = ref _bodies[contact.A];
        ref var b = ref _bodies[contact.B];

        // Normal: accumulated impulse never pulls (λ ≥ 0).
        var n = contact.Normal;
        var effective = EffectiveMassLinear(a, b, contact.ArmA, contact.ArmB, n);
        if (effective <= 1e-9f)
            return;
        var velocity = Vector3.Dot(
            b.LinearVelocity + Vector3.Cross(b.AngularVelocity, contact.ArmB)
            - a.LinearVelocity - Vector3.Cross(a.AngularVelocity, contact.ArmA), n);
        var impulse = (contact.Bias - velocity) / effective;
        var target = MathF.Max(contact.NormalImpulse + impulse, 0f);
        impulse = target - contact.NormalImpulse;
        contact.NormalImpulse = target;
        ApplyLinearImpulse(ref a, ref b, contact.ArmA, contact.ArmB, n * impulse);

        if (contact.Friction <= 0f)
            return;

        // Friction: two tangent axes, box-clamped to μ · accumulated normal impulse (Bullet style).
        var maxFriction = contact.Friction * contact.NormalImpulse;
        SolveFrictionAxis(ref a, ref b, ref contact, contact.Tangent1, ref contact.TangentImpulse1, maxFriction);
        SolveFrictionAxis(ref a, ref b, ref contact, contact.Tangent2, ref contact.TangentImpulse2, maxFriction);

        void SolveFrictionAxis(ref Body a, ref Body b, ref Contact c, Vector3 t, ref float accumulated, float limit)
        {
            var k = EffectiveMassLinear(a, b, c.ArmA, c.ArmB, t);
            if (k <= 1e-9f)
                return;
            var v = Vector3.Dot(
                b.LinearVelocity + Vector3.Cross(b.AngularVelocity, c.ArmB)
                - a.LinearVelocity - Vector3.Cross(a.AngularVelocity, c.ArmA), t);
            var lambda = -v / k;
            var clamped = Math.Clamp(accumulated + lambda, -limit, limit);
            lambda = clamped - accumulated;
            accumulated = clamped;
            ApplyLinearImpulse(ref a, ref b, c.ArmA, c.ArmB, t * lambda);
        }
    }

    private static float EffectiveMassLinear(in Body a, in Body b, Vector3 rA, Vector3 rB, Vector3 n)
    {
        var raxn = Vector3.Cross(rA, n);
        var rbxn = Vector3.Cross(rB, n);
        return a.InvMass + b.InvMass
               + Vector3.Dot(Vector3.Cross(InvInertiaWorld(a, raxn), rA), n)
               + Vector3.Dot(Vector3.Cross(InvInertiaWorld(b, rbxn), rB), n);
    }

    private static void ApplyLinearImpulse(ref Body a, ref Body b, Vector3 rA, Vector3 rB, Vector3 impulse)
    {
        a.LinearVelocity -= impulse * a.InvMass;
        a.AngularVelocity -= InvInertiaWorld(a, Vector3.Cross(rA, impulse));
        b.LinearVelocity += impulse * b.InvMass;
        b.AngularVelocity += InvInertiaWorld(b, Vector3.Cross(rB, impulse));
    }

    private static void ApplyAngularImpulse(ref Body a, ref Body b, Vector3 impulse)
    {
        a.AngularVelocity -= InvInertiaWorld(a, impulse);
        b.AngularVelocity += InvInertiaWorld(b, impulse);
    }

    /// <summary>I⁻¹·v in world space: rotate into the body frame, scale by the diagonal inverse
    /// inertia, rotate back. Zero for kinematic bodies (their inertia inverse is zero).</summary>
    private static Vector3 InvInertiaWorld(in Body body, Vector3 v) =>
        Vector3.Transform(
            body.InvInertiaLocal * Vector3.Transform(v, Quaternion.Conjugate(body.Rotation)),
            body.Rotation);

    /// <summary>Shortest-arc scaled axis (axis · angle) of a rotation — kinematic angular velocity.</summary>
    private static Vector3 ToScaledAxis(Quaternion q)
    {
        if (q.W < 0f)
            q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        var w = Math.Clamp(q.W, -1f, 1f);
        var sin = MathF.Sqrt(MathF.Max(1f - w * w, 0f));
        var v = new Vector3(q.X, q.Y, q.Z);
        return sin < 1e-6f
            ? v * 2f // small-angle: q ≈ (axis·θ/2, 1)
            : v / sin * (2f * MathF.Acos(w));
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

            // PhysicsWithBonePosition bodies re-seed their POSITION from the (animated) bone each frame
            // — three.js MMDPhysics' _setPositionFromBone; rotation stays simulated, drift can't build.
            if (body.AlignBonePosition)
                body.Position = TargetFromBone(body, world).Translation;
        }
    }

    private static bool IsFinite(in Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M22) && float.IsFinite(m.M33)
        && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43);
}
