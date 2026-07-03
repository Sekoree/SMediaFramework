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

    /// <summary>Hair chains are authored with the linear DOF locked — no matter how hard the anchor is
    /// whipped around, the segments must never stretch apart (the operator-visible "hair ends stretch"
    /// regression: iterative-solver residual concentrates at the tips of long chains).</summary>
    [Fact]
    public void LockedChain_DoesNotStretch_UnderFastAnchorMotion()
    {
        const int links = 8;
        var bones = new List<PmxBone> { Bone("root", new Vector3(0, 10, 0), parent: -1) };
        var rigids = new List<PmxRigidBody>
        {
            new("anchor", 0, Group: 0, CollisionMask: 0, PmxRigidShape.Sphere,
                new Vector3(0.1f, 0, 0), new Vector3(0, 10, 0), Vector3.Zero,
                1f, 0.5f, 0.5f, 0f, 0.5f, PmxPhysicsMode.FollowBone),
        };
        var joints = new List<PmxJoint>();
        for (var k = 1; k <= links; k++)
        {
            var position = new Vector3(0, 10 - k, 0);
            bones.Add(Bone($"seg{k}", position, parent: k - 1));
            rigids.Add(new PmxRigidBody($"seg{k}", k, Group: 0, CollisionMask: 0, PmxRigidShape.Sphere,
                new Vector3(0.1f, 0, 0), position, Vector3.Zero,
                0.05f, 0.99f, 0.99f, 0f, 0.5f, PmxPhysicsMode.Physics));
            // Locked linear + near-locked angular — the YYB tail authoring.
            joints.Add(new PmxJoint($"j{k}", Type: 0, RigidBodyA: k - 1, RigidBodyB: k,
                new Vector3(0, 10 - k + 0.5f, 0), Vector3.Zero,
                Vector3.Zero, Vector3.Zero,
                new Vector3(-0.0017f), new Vector3(0.0017f),
                Vector3.Zero, Vector3.Zero));
        }

        var model = new PmxDocument
        {
            Version = 2.0f,
            ModelName = "chain",
            ModelNameEnglish = "chain",
            Vertices = [],
            Indices = [],
            Textures = [],
            Materials = [],
            Bones = bones,
            Morphs = [],
            RigidBodies = rigids,
            Joints = joints,
        };
        var physics = MmdPhysics.TryCreate(model)!;
        var world = BindWorlds(model);
        physics.Reset(world);

        // Whip the anchor: teleport the root 1.5 units per frame, alternating direction.
        for (var frame = 0; frame < 90; frame++)
        {
            var x = MathF.Sin(frame * 0.4f) * 6f;
            world[0] = Matrix4x4.CreateTranslation(new Vector3(x, 10, 0));
            physics.Step(world, 1f / 60f);

            for (var k = 1; k <= links; k++)
            {
                var spacing = Vector3.Distance(world[k - 1].Translation, world[k].Translation);
                Assert.True(spacing < 1.05f,
                    $"chain stretched at frame {frame}, segment {k}: spacing {spacing:F3} (bind 1.0)");
            }
        }
    }

    /// <summary>Skirt plates are wide thin BOXES: a capsule sweeping into the flat face must collide
    /// (the old capsule approximation only collided along the thin edge — legs passed through the face
    /// and trapped the plate inside the body).</summary>
    [Fact]
    public void BoxPlate_IsPushedByACapsule_AcrossItsWideFace()
    {
        var model = new PmxDocument
        {
            Version = 2.0f,
            ModelName = "plate",
            ModelNameEnglish = "plate",
            Vertices = [],
            Indices = [],
            Textures = [],
            Materials = [],
            Bones =
            [
                Bone("anchor", new Vector3(0, 12, 0), parent: -1),
                Bone("plate", new Vector3(0, 10, 0), parent: 0),
                Bone("leg", new Vector3(0, 9.2f, 0.3f), parent: -1),
            ],
            Morphs = [],
            RigidBodies =
            [
                new PmxRigidBody("anchor", 0, Group: 0, CollisionMask: 0, PmxRigidShape.Sphere,
                    new Vector3(0.1f, 0, 0), new Vector3(0, 12, 0), Vector3.Zero,
                    1f, 0.5f, 0.5f, 0f, 0.5f, PmxPhysicsMode.FollowBone),
                // Wide thin plate hanging from the anchor on a free swing joint; the capsule overlaps
                // the LOWER HALF of its wide XY face (offset 0.3 in front, radius 0.5) so the contact
                // torque swings the plate about its anchor.
                new PmxRigidBody("plate", 1, Group: 1, CollisionMask: 0xFFFF, PmxRigidShape.Box,
                    new Vector3(1.0f, 1.0f, 0.05f), new Vector3(0, 10, 0), Vector3.Zero,
                    1f, 0.5f, 0.5f, 0f, 0.5f, PmxPhysicsMode.Physics),
                new PmxRigidBody("leg", 2, Group: 2, CollisionMask: 0xFFFF, PmxRigidShape.Capsule,
                    new Vector3(0.5f, 1f, 0), new Vector3(0, 9.2f, 0.3f), Vector3.Zero,
                    1f, 0.5f, 0.5f, 0f, 0.5f, PmxPhysicsMode.FollowBone),
            ],
            Joints =
            [
                new PmxJoint("swing", Type: 0, RigidBodyA: 0, RigidBodyB: 1,
                    new Vector3(0, 12, 0), Vector3.Zero,
                    Vector3.Zero, Vector3.Zero,
                    new Vector3(-MathF.PI, -MathF.PI, -MathF.PI), new Vector3(MathF.PI, MathF.PI, MathF.PI),
                    Vector3.Zero, Vector3.Zero),
            ],
        };
        var physics = MmdPhysics.TryCreate(model)!;
        var world = BindWorlds(model);
        physics.Reset(world);
        for (var i = 0; i < 90; i++)
            physics.Step(world, 1f / 60f);

        // The leg capsule (radius 0.5 at z=0.3) overlaps the plate's face (z extent ±0.05): the plate
        // must be pushed AWAY along −Z. With the old capsule approximation no contact fires at all.
        var plateZ = world[1].Translation.Z;
        Assert.True(plateZ < -0.1f,
            $"plate was not pushed out of the capsule across its wide face (z={plateZ:F3}, expected < -0.1)");
        Assert.True(float.IsFinite(plateZ) && MathF.Abs(plateZ) < 5f, $"plate exploded (z={plateZ:F3})");
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
