using System.Runtime.CompilerServices;

namespace S.Media.Source.MMD;

/// <summary>
/// MMD physics: hair/skirt/accessory secondary motion, run on <b>real Bullet</b> (3.25) through the
/// <see cref="MMDBulletNative"/> C ABI, exactly as MikuMikuDance and babylon-mmd do. The PMX rigid bodies
/// become Bullet rigid bodies (sphere/box/capsule with the file's mass, inertia, damping, friction,
/// restitution and 16-bit collision group/mask), and every PMX joint becomes a
/// <c>btGeneric6DofSpringConstraint</c> with the authored per-axis limits and spring stiffnesses — so the
/// file's Bullet-tuned parameters mean precisely what their authors intended. Kinematic
/// (<see cref="PMXPhysicsMode.FollowBone"/>) bodies follow the animated bones and drag the dynamic chains;
/// dynamic bodies write their transforms back to their bones between IK and skinning.
///
/// <para>STATEFUL by nature (unlike the pure-function-of-time animator): the owner steps it monotonically
/// once per rendered frame. Backward seeks or large jumps reset the chain to the animated pose and run a
/// one-frame kinematic warm-up — the same behavior MMD itself has on seek.</para>
///
/// <para>Owns an unmanaged Bullet world; <see cref="Dispose"/> frees it (a finalizer backstops leaks).</para>
/// </summary>
public sealed class MMDPhysics : IDisposable
{
    private const float Gravity = -98f;          // MMD's Bullet world: 9.8 × 10 model units/s²
    private const int MaxSubSteps = 5;           // babylon-mmd's MMD runtime cadence
    private const float FixedTimeStep = 1f / 60f;

    private struct Body
    {
        public int NativeHandle;         // index into the native world (>=0), or -1 if not created
        public int BoneIndex;
        public bool AuthoredDynamic;     // Physics or PhysicsWithBonePosition
        public bool AlignBonePosition;   // PhysicsWithBonePosition: rotation from physics, position from bone
        public Matrix4x4 BoneToBody;     // bind: bodyWorld = BoneToBody · boneWorld (row-vector)
        public Matrix4x4 BodyToBone;
    }

    private readonly nint _world;
    private readonly Body[] _bodies;
    private readonly bool[] _drivenBones;
    private readonly bool[] _bodyEnabled;        // per-body VMD physics-enable toggle (default true)
    private bool _primed;
    private bool _warmup;                         // one kinematic-follow frame after a reset (babylon makeKinematicOnce)
    private bool _disposed;

    /// <summary>Retained for API/back-compat: the old custom solver fired this when its anti-stuck
    /// supervisor snapped a body back onto its bone. Real Bullet has no such supervisor, so this never
    /// fires now — kept only so existing callers/tests compile.</summary>
    public static Action<int>? DebugStuckReset;

    private MMDPhysics(nint world, Body[] bodies, bool[] drivenBones)
    {
        _world = world;
        _bodies = bodies;
        _drivenBones = drivenBones;
        _bodyEnabled = new bool[bodies.Length];
        Array.Fill(_bodyEnabled, true);
    }

    /// <summary>True when the simulation overwrites this bone's world transform — the animator re-chains
    /// the OTHER bones under their (possibly physics-moved) parents after <see cref="Step"/>.</summary>
    public bool DrivesBone(int boneIndex) =>
        (uint)boneIndex < (uint)_drivenBones.Length && _drivenBones[boneIndex];

    /// <summary>Builds the simulation, or null when the model carries no usable dynamic bodies.</summary>
    public static MMDPhysics? TryCreate(PMXDocument model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.RigidBodies.Count == 0 || model.RigidBodies.All(b => b.Mode == PMXPhysicsMode.FollowBone))
            return null;

        var world = MMDBulletNative.WorldCreate(0f, Gravity, 0f);
        if (world == nint.Zero)
            return null;

        var bodies = new Body[model.RigidBodies.Count];
        var drivenBones = new bool[model.Bones.Count];

        unsafe
        {
            var m = stackalloc float[16];
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

                var authoredDynamic = spec.Mode != PMXPhysicsMode.FollowBone;
                var motionType = authoredDynamic ? MMDBulletNative.MotionDynamic : MMDBulletNative.MotionKinematic;

                var (shapeType, sx, sy, sz, zeroVolume) = ShapeOf(spec);
                var group = (ushort)(1 << (spec.Group & 0x0F));
                var mask = spec.CollisionMask;
                var noContactResponse = mask == 0 || zeroVolume;

                WriteMatrix(bindBody, m);
                var handle = MMDBulletNative.WorldAddBody(
                    world, shapeType, sx, sy, sz, m, motionType,
                    MathF.Max(spec.Mass, 0f), spec.LinearDamping, spec.AngularDamping, spec.Friction, spec.Restitution,
                    group, mask,
                    additionalDamping: 1, noContactResponse: noContactResponse ? 1 : 0, disableDeactivation: 1);

                bodies[i] = new Body
                {
                    NativeHandle = handle,
                    BoneIndex = spec.BoneIndex,
                    AuthoredDynamic = authoredDynamic,
                    AlignBonePosition = spec.Mode == PMXPhysicsMode.PhysicsWithBonePosition,
                    BoneToBody = bindBody * boneBindInv,   // row-vector: body = (body·bone⁻¹)·bone
                    BodyToBone = boneBind * bindBodyInv,
                };
            }

            // Joints → 6-DOF spring constraints. Frames are the joint frame expressed in each body's
            // local space: frame = jointTransform · bodyBind⁻¹ (row-vector), matching babylon-mmd.
            // All per-iteration scratch is hoisted above the loop: C# stackalloc is released only when the
            // method returns, so allocating inside the loop grows the stack by jointCount (CA2014). We reuse
            // these buffers and overwrite [0]/[1]/[2] each iteration.
            var frameA = stackalloc float[16];
            var frameB = stackalloc float[16];
            var linLower = stackalloc float[3];
            var linUpper = stackalloc float[3];
            var angLower = stackalloc float[3];
            var angUpper = stackalloc float[3];
            var springPos = stackalloc float[3];
            var springRot = stackalloc float[3];
            foreach (var joint in model.Joints)
            {
                if ((uint)joint.RigidBodyA >= (uint)bodies.Length || (uint)joint.RigidBodyB >= (uint)bodies.Length)
                    continue;
                ref var a = ref bodies[joint.RigidBodyA];
                ref var b = ref bodies[joint.RigidBodyB];
                if (a.NativeHandle < 0 || b.NativeHandle < 0)
                    continue;

                var specA = model.RigidBodies[joint.RigidBodyA];
                var specB = model.RigidBodies[joint.RigidBodyB];
                var jointTransform = Matrix4x4.CreateFromQuaternion(BindRotationOf(joint.RotationRadians))
                    with { Translation = joint.Position };
                var bindA = Matrix4x4.CreateFromQuaternion(BindRotationOf(specA.RotationRadians))
                    with { Translation = specA.Position };
                var bindB = Matrix4x4.CreateFromQuaternion(BindRotationOf(specB.RotationRadians))
                    with { Translation = specB.Position };
                Matrix4x4.Invert(bindA, out var bindAInv);
                Matrix4x4.Invert(bindB, out var bindBInv);
                WriteMatrix(jointTransform * bindAInv, frameA);
                WriteMatrix(jointTransform * bindBInv, frameB);

                linLower[0] = joint.LinearLowerLimit.X; linLower[1] = joint.LinearLowerLimit.Y; linLower[2] = joint.LinearLowerLimit.Z;
                linUpper[0] = joint.LinearUpperLimit.X; linUpper[1] = joint.LinearUpperLimit.Y; linUpper[2] = joint.LinearUpperLimit.Z;
                angLower[0] = joint.AngularLowerLimit.X; angLower[1] = joint.AngularLowerLimit.Y; angLower[2] = joint.AngularLowerLimit.Z;
                angUpper[0] = joint.AngularUpperLimit.X; angUpper[1] = joint.AngularUpperLimit.Y; angUpper[2] = joint.AngularUpperLimit.Z;
                springPos[0] = joint.LinearSpring.X; springPos[1] = joint.LinearSpring.Y; springPos[2] = joint.LinearSpring.Z;
                springRot[0] = joint.AngularSpring.X; springRot[1] = joint.AngularSpring.Y; springRot[2] = joint.AngularSpring.Z;

                MMDBulletNative.WorldAddSpringConstraint(
                    world, a.NativeHandle, b.NativeHandle, frameA, frameB,
                    linLower, linUpper, angLower, angUpper, springPos, springRot);

                // MMD quirk (katwat, ref: babylon-mmd): a PhysicsWithBonePosition body whose bone's PARENT
                // carries the joint's dynamic A body must act as plain Physics — the bone-position snap
                // would fight the parent link it hangs from.
                if (specA.Mode != PMXPhysicsMode.FollowBone && b.AlignBonePosition
                    && BoneParentOf(model, b.BoneIndex) is { } parentOfB && parentOfB == a.BoneIndex)
                    b.AlignBonePosition = false;
                else if (specB.Mode != PMXPhysicsMode.FollowBone && a.AlignBonePosition
                    && BoneParentOf(model, a.BoneIndex) is { } parentOfA && parentOfA == b.BoneIndex)
                    a.AlignBonePosition = false;
            }
        }

        for (var i = 0; i < bodies.Length; i++)
            if (bodies[i].AuthoredDynamic && (uint)bodies[i].BoneIndex < (uint)drivenBones.Length)
                drivenBones[bodies[i].BoneIndex] = true;

        return new MMDPhysics(world, bodies, drivenBones);

        static int? BoneParentOf(PMXDocument model, int boneIndex) =>
            (uint)boneIndex < (uint)model.Bones.Count && model.Bones[boneIndex].ParentIndex >= 0
                ? model.Bones[boneIndex].ParentIndex
                : null;
    }

    /// <summary>Applies the VMD per-bone physics toggle. Disabled dynamic bodies become kinematic and
    /// follow the sampled bone until a later key enables physics again.</summary>
    public void SetBonePhysicsEnabled(ReadOnlySpan<bool> enabledBones)
    {
        Array.Clear(_drivenBones);
        for (var i = 0; i < _bodies.Length; i++)
        {
            ref readonly var body = ref _bodies[i];
            var enabled = (uint)body.BoneIndex >= (uint)enabledBones.Length || enabledBones[body.BoneIndex];
            _bodyEnabled[i] = enabled;
            if (body.AuthoredDynamic && enabled && (uint)body.BoneIndex < (uint)_drivenBones.Length)
                _drivenBones[body.BoneIndex] = true;
        }
    }

    /// <summary>Resets every body onto its (animated) kinematic pose and clears velocities — a seek/jump
    /// re-basing. <paramref name="world"/> is the animator's post-IK world matrices. The next
    /// <see cref="Step"/> runs a one-frame kinematic warm-up before dynamics take over (MMD's
    /// play-from-here behavior), so a mid-dance FK pose never explodes the chain.</summary>
    public void Reset(Matrix4x4[] world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_disposed)
            return;
        unsafe
        {
            var m = stackalloc float[16];
            for (var i = 0; i < _bodies.Length; i++)
            {
                ref readonly var body = ref _bodies[i];
                if (body.NativeHandle < 0)
                    continue;
                WriteMatrix(TargetFromBone(body, world), m);
                MMDBulletNative.BodyReset(_world, body.NativeHandle, m);
            }
        }

        _warmup = true;
        _primed = true;
    }

    /// <summary>
    /// Advances the simulation by <paramref name="deltaSeconds"/> (fixed 1/60 Bullet substeps internally)
    /// and writes dynamic bodies back into <paramref name="world"/>. Call after FK/IK, before skinning.
    /// A negative/huge delta (seek/jump) resets and re-bases first.
    /// </summary>
    public void Step(Matrix4x4[] world, float deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_disposed)
            return;
        if (!_primed || deltaSeconds < 0f || deltaSeconds > 0.5f)
        {
            Reset(world);
            deltaSeconds = FixedTimeStep; // warm one step so the chain starts settling
        }

        unsafe
        {
            var m = stackalloc float[16];
            // Drive kinematic bodies (FollowBone always; dynamic bodies during warm-up or while their VMD
            // physics toggle is off) onto their bone pose. Bullet derives their velocity from the delta so
            // they drag the dynamic chains through contacts and joints.
            for (var i = 0; i < _bodies.Length; i++)
            {
                ref readonly var body = ref _bodies[i];
                if (body.NativeHandle < 0)
                    continue;

                var kinematic = !body.AuthoredDynamic || _warmup || !_bodyEnabled[i];
                if (body.AuthoredDynamic)
                    MMDBulletNative.BodySetKinematic(_world, body.NativeHandle, kinematic ? 1 : 0);
                if (kinematic)
                {
                    WriteMatrix(TargetFromBone(body, world), m);
                    MMDBulletNative.BodySetKinematicTransform(_world, body.NativeHandle, m);
                }
            }

            MMDBulletNative.WorldStep(_world, deltaSeconds, MaxSubSteps, FixedTimeStep);

            // The warm-up frame is over; dynamic bodies resume free simulation from the exactly-followed
            // pose on the next step (the loop above flips them back with BodySetKinematic).
            _warmup = false;

            // Write dynamic bodies back onto their bones.
            var t = stackalloc float[16];
            for (var i = 0; i < _bodies.Length; i++)
            {
                ref readonly var body = ref _bodies[i];
                if (body.NativeHandle < 0 || !body.AuthoredDynamic || !_bodyEnabled[i]
                    || (uint)body.BoneIndex >= (uint)world.Length)
                    continue;

                MMDBulletNative.BodyGetTransform(_world, body.NativeHandle, t);
                var bodyWorld = ReadMatrix(t);
                var boneWorld = body.BodyToBone * bodyWorld;
                if (body.AlignBonePosition)
                    boneWorld = boneWorld with { Translation = world[body.BoneIndex].Translation };
                if (IsFinite(boneWorld))
                    world[body.BoneIndex] = boneWorld;
            }
        }
    }

    /// <summary>Shape type + Bullet size triple + zero-volume flag (matches babylon-mmd's create_shape).
    /// Sphere: radius = size.X. Box: half-extents = size. Capsule: (radius = size.X, height = size.Y).</summary>
    private static (int ShapeType, float X, float Y, float Z, bool ZeroVolume) ShapeOf(PMXRigidBody spec) =>
        spec.Shape switch
        {
            PMXRigidShape.Sphere => (MMDBulletNative.ShapeSphere, spec.Size.X, 0f, 0f, spec.Size.X == 0f),
            PMXRigidShape.Capsule => (MMDBulletNative.ShapeCapsule, spec.Size.X, spec.Size.Y, 0f,
                spec.Size.X == 0f || spec.Size.Y == 0f),
            _ => (MMDBulletNative.ShapeBox, spec.Size.X, spec.Size.Y, spec.Size.Z,
                spec.Size.X == 0f || spec.Size.Y == 0f || spec.Size.Z == 0f),
        };

    /// <summary>MMD rigid-body/joint bind orientation (euler radians, the Y·X·Z order MMD tools emit).</summary>
    private static Quaternion BindRotationOf(Vector3 euler) =>
        Quaternion.CreateFromRotationMatrix(
            Matrix4x4.CreateRotationZ(euler.Z) *
            Matrix4x4.CreateRotationX(euler.X) *
            Matrix4x4.CreateRotationY(euler.Y));

    private static Matrix4x4 TargetFromBone(in Body body, Matrix4x4[] world)
    {
        var boneWorld = (uint)body.BoneIndex < (uint)world.Length ? world[body.BoneIndex] : Matrix4x4.Identity;
        return body.BoneToBody * boneWorld;
    }

    private static unsafe void WriteMatrix(in Matrix4x4 m, float* dst)
    {
        // System.Numerics stores M11..M44 sequentially — bit-identical to OpenGL column-major, so the
        // native side reads the same transform back with setFromOpenGLMatrix (no transpose).
        dst[0] = m.M11; dst[1] = m.M12; dst[2] = m.M13; dst[3] = m.M14;
        dst[4] = m.M21; dst[5] = m.M22; dst[6] = m.M23; dst[7] = m.M24;
        dst[8] = m.M31; dst[9] = m.M32; dst[10] = m.M33; dst[11] = m.M34;
        dst[12] = m.M41; dst[13] = m.M42; dst[14] = m.M43; dst[15] = m.M44;
    }

    private static unsafe Matrix4x4 ReadMatrix(float* s) => new(
        s[0], s[1], s[2], s[3],
        s[4], s[5], s[6], s[7],
        s[8], s[9], s[10], s[11],
        s[12], s[13], s[14], s[15]);

    private static bool IsFinite(in Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M22) && float.IsFinite(m.M33)
        && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_world != nint.Zero)
            MMDBulletNative.WorldDestroy(_world);
        GC.SuppressFinalize(this);
    }

    ~MMDPhysics()
    {
        if (!_disposed && _world != nint.Zero)
            MMDBulletNative.WorldDestroy(_world);
    }
}
