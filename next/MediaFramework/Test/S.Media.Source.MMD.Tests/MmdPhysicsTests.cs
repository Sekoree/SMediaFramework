using System.Numerics;
using S.Media.Source.MMD;
using Xunit;

namespace S.Media.Source.MMD.Tests;

/// <summary>
/// Stage-5 physics on a purpose-built rig: a kinematic anchor at the shoulder with a dynamic sphere on a
/// HORIZONTAL arm — under gravity the arm must swing DOWN through its joint (and stay bounded), which is
/// exactly the hair/skirt behavior the solver exists for. The real-asset test drives the full 136-body
/// YYB chain over real motion.
/// </summary>
public sealed class MmdPhysicsTests
{
    private const int RootBone = 0;
    private const int TipBone = 1;

    private static PmxBone Bone(string name, Vector3 position, int parent) =>
        new(name, name, position, parent,
            AppendRotation: false, AppendTranslation: false, AppendParentIndex: -1, AppendRatio: 0f,
            IsIk: false, IkTargetIndex: -1, IkLoopCount: 0, IkLimitRadians: 0f, IkLinks: []);

    /// <summary>Kinematic anchor at (0,10,0); dynamic sphere on the tip bone at (1,10,0); a wide-limit
    /// joint at the anchor connects them — a 1-unit horizontal pendulum.</summary>
    private static PmxDocument PendulumRig() => new()
    {
        Version = 2.0f,
        ModelName = "pendulum",
        ModelNameEnglish = "pendulum",
        Vertices = [],
        Indices = [],
        Textures = [],
        Materials = [],
        Bones =
        [
            Bone("root", new Vector3(0, 10, 0), parent: -1),
            Bone("tip", new Vector3(1, 10, 0), parent: RootBone),
        ],
        Morphs = [],
        RigidBodies =
        [
            new PmxRigidBody("anchor", RootBone, Group: 0, CollisionMask: 0,
                PmxRigidShape.Sphere, new Vector3(0.1f, 0, 0), new Vector3(0, 10, 0), Vector3.Zero,
                Mass: 1f, LinearDamping: 0.5f, AngularDamping: 0.5f, Restitution: 0f, Friction: 0.5f,
                PmxPhysicsMode.FollowBone),
            new PmxRigidBody("swing", TipBone, Group: 1, CollisionMask: 0,
                PmxRigidShape.Sphere, new Vector3(0.1f, 0, 0), new Vector3(1, 10, 0), Vector3.Zero,
                Mass: 1f, LinearDamping: 0.5f, AngularDamping: 0.5f, Restitution: 0f, Friction: 0.5f,
                PmxPhysicsMode.Physics),
        ],
        Joints =
        [
            new PmxJoint("hinge", Type: 0, RigidBodyA: 0, RigidBodyB: 1,
                new Vector3(0, 10, 0), Vector3.Zero,
                Vector3.Zero, Vector3.Zero,
                new Vector3(-MathF.PI, -MathF.PI, -MathF.PI), new Vector3(MathF.PI, MathF.PI, MathF.PI),
                Vector3.Zero, Vector3.Zero),
        ],
    };

    private static Matrix4x4[] BindWorlds(PmxDocument model) =>
        [.. model.Bones.Select(b => Matrix4x4.CreateTranslation(b.Position))];

    [Fact]
    public void HorizontalPendulum_SwingsDown_AndStaysBounded()
    {
        var model = PendulumRig();
        var physics = MmdPhysics.TryCreate(model);
        Assert.NotNull(physics);

        var world = BindWorlds(model);
        physics.Reset(world);
        for (var i = 0; i < 120; i++) // 2 simulated seconds
            physics.Step(world, 1f / 60f);

        var tip = world[TipBone].Translation;
        Assert.True(tip.Y < 9.7f, $"tip did not swing down under gravity (y={tip.Y:F2}, started at 10)");
        Assert.True(Vector3.Distance(tip, new Vector3(0, 10, 0)) < 1.5f,
            $"tip left the joint's reach — the chain exploded (tip={tip})");
        Assert.True(float.IsFinite(tip.X) && float.IsFinite(tip.Y) && float.IsFinite(tip.Z));
    }

    /// <summary>YYB-style tails lock their inner joints to ±0.1°: the link must FOLLOW the parent bone
    /// rigidly (authored stiffness) instead of dangling under gravity like a free pendulum.</summary>
    [Fact]
    public void NearLockedJoint_TracksTheParentRigidly()
    {
        var template = PendulumRig();
        var locked = new Vector3(0.0017f, 0.0017f, 0.0017f); // ±0.1° — the tails' authored lock
        var model = new PmxDocument
        {
            Version = template.Version,
            ModelName = template.ModelName,
            ModelNameEnglish = template.ModelNameEnglish,
            Vertices = template.Vertices,
            Indices = template.Indices,
            Textures = template.Textures,
            Materials = template.Materials,
            Bones = template.Bones,
            Morphs = template.Morphs,
            RigidBodies = template.RigidBodies,
            Joints =
            [
                new PmxJoint("locked", Type: 0, RigidBodyA: 0, RigidBodyB: 1,
                    new Vector3(0, 10, 0), Vector3.Zero,
                    Vector3.Zero, Vector3.Zero,
                    -locked, locked,
                    Vector3.Zero, Vector3.Zero),
            ],
        };
        var physics = MmdPhysics.TryCreate(model)!;

        // Root rotated 45° about Z: a rigid arm carries the tip to root + R·(1,0,0).
        var rotated = Matrix4x4.CreateRotationZ(MathF.PI / 4) with { Translation = new Vector3(0, 10, 0) };
        var world = BindWorlds(model);
        world[RootBone] = rotated;
        physics.Reset(world);
        for (var i = 0; i < 120; i++)
            physics.Step(world, 1f / 60f);

        var expected = new Vector3(0, 10, 0) + Vector3.Transform(new Vector3(1, 0, 0), Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateRotationZ(MathF.PI / 4)));
        var tip = world[TipBone].Translation;
        Assert.True(Vector3.Distance(tip, expected) < 0.1f,
            $"locked joint should carry the tip rigidly with the parent (tip={tip}, expected={expected})");
    }

    [Fact]
    public void BackwardJump_ResetsOntoTheAnimatedPose()
    {
        var model = PendulumRig();
        var physics = MmdPhysics.TryCreate(model)!;
        var world = BindWorlds(model);
        physics.Reset(world);
        for (var i = 0; i < 60; i++)
            physics.Step(world, 1f / 60f); // swings away from bind

        // A negative delta (backward seek) must re-base onto the CURRENT animated pose, not keep momentum.
        var fresh = BindWorlds(model);
        physics.Step(fresh, -1f);
        var tip = fresh[TipBone].Translation;
        Assert.True(Vector3.Distance(tip, new Vector3(1, 10, 0)) < 0.25f,
            $"backward seek did not re-base the chain (tip={tip}, expected near bind (1,10,0))");
    }

    [Fact]
    public void ModelWithoutDynamicBodies_HasNoSimulation()
    {
        var template = PendulumRig();
        var model = new PmxDocument
        {
            Version = template.Version,
            ModelName = template.ModelName,
            ModelNameEnglish = template.ModelNameEnglish,
            Vertices = template.Vertices,
            Indices = template.Indices,
            Textures = template.Textures,
            Materials = template.Materials,
            Bones = template.Bones,
            Morphs = template.Morphs,
            RigidBodies =
            [
                new PmxRigidBody("anchor", RootBone, 0, 0, PmxRigidShape.Sphere,
                    new Vector3(0.1f, 0, 0), new Vector3(0, 10, 0), Vector3.Zero,
                    1f, 0.5f, 0.5f, 0f, 0.5f, PmxPhysicsMode.FollowBone),
            ],
        };
        Assert.Null(MmdPhysics.TryCreate(model));
    }
}
