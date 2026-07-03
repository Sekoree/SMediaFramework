using System.Numerics;
using S.Media.Source.MMD;
using Xunit;

namespace S.Media.Source.MMD.Tests;

/// <summary>
/// CCD IK solver tests (Gate-6 stage 6 — the dance-feet artifact source) on a purpose-built 2-link leg:
/// hip (0,2,0) → knee (0,1,0) → ankle (0,0,0), plus an IK goal bone the motion animates. The chain
/// geometry keeps expectations hand-computable; the real-asset render test covers full-model behavior.
/// </summary>
public sealed class MmdIkTests
{
    private const int Hip = 0;
    private const int Knee = 1;
    private const int Ankle = 2;
    private const int IkGoal = 3;

    private static PmxBone Bone(
        string name, Vector3 position, int parent,
        bool isIk = false, int ikTarget = -1, int ikLoop = 0, float ikLimit = 2f,
        IReadOnlyList<PmxIkLink>? links = null,
        int appendParent = -1, float appendRatio = 0f, int layer = 0) =>
        new(name, name, position, parent,
            AppendRotation: appendParent >= 0, AppendTranslation: false, appendParent, appendRatio,
            isIk, ikTarget, ikLoop, ikLimit, links ?? [])
        {
            DeformLayer = layer,
        };

    /// <summary>A vertex rigidly bound to one bone (to satisfy Evaluate's buffer contract).</summary>
    private static PmxVertex Vertex(Vector3 position, int bone) =>
        new(position, Vector3.UnitY, Vector2.Zero, bone, -1, -1, -1, 1f, 0f, 0f, 0f);

    private static PmxDocument Leg(IReadOnlyList<PmxIkLink> links, int ikLoop = 60) => new()
    {
        Version = 2.0f,
        ModelName = "ik-leg",
        ModelNameEnglish = "ik-leg",
        Vertices = [Vertex(new Vector3(0, 0, 0), Ankle)],
        Indices = [],
        Textures = [],
        Materials = [],
        Bones =
        [
            Bone("hip", new Vector3(0, 2, 0), parent: -1),
            Bone("knee", new Vector3(0, 1, 0), parent: Hip),
            Bone("ankle", new Vector3(0, 0, 0), parent: Knee),
            Bone("ik", new Vector3(0, 0, 0), parent: -1,
                isIk: true, ikTarget: Ankle, ikLoop: ikLoop, ikLimit: 2f, links: links),
        ],
        Morphs = [],
    };

    /// <summary>Motion that moves ONLY the IK goal bone to <paramref name="goal"/> (single keyframe —
    /// no interpolation in play). <paramref name="ikEnabled"/> adds a frame-0 IK on/off key.</summary>
    private static VmdDocument GoalAt(Vector3 goal, bool? ikEnabled = null) => new()
    {
        ModelName = "ik-leg",
        BoneTracks = new Dictionary<string, IReadOnlyList<VmdBoneFrame>>(StringComparer.Ordinal)
        {
            ["ik"] = [new VmdBoneFrame(0, goal, Quaternion.Identity,
                20, 20, 107, 107, 20, 20, 107, 107, 20, 20, 107, 107, 20, 20, 107, 107)],
        },
        MorphTracks = new Dictionary<string, IReadOnlyList<VmdMorphFrame>>(StringComparer.Ordinal),
        CameraTrack = [],
        IkEnableTracks = ikEnabled is { } enabled
            ? new Dictionary<string, IReadOnlyList<VmdIkFrame>>(StringComparer.Ordinal)
            {
                ["ik"] = [new VmdIkFrame(0, enabled)],
            }
            : new Dictionary<string, IReadOnlyList<VmdIkFrame>>(StringComparer.Ordinal),
    };

    private static readonly PmxIkLink KneeFree = new(Knee, HasLimit: false, Vector3.Zero, Vector3.Zero);
    private static readonly PmxIkLink HipFree = new(Hip, HasLimit: false, Vector3.Zero, Vector3.Zero);

    /// <summary>The canonical MMD knee: a pure X hinge that only bends one way.</summary>
    private static readonly PmxIkLink KneeHinge = new(
        Knee, HasLimit: true, new Vector3(-MathF.PI, 0, 0), Vector3.Zero);

    private static Vector3 AnkleAfterSolve(PmxDocument model, Vector3 goal)
    {
        var animator = new MmdAnimator(model, GoalAt(goal));
        var positions = new Vector3[model.Vertices.Count];
        animator.Evaluate(TimeSpan.Zero, positions);
        return animator.TryGetBoneWorldPosition("ankle")!.Value;
    }

    [Fact]
    public void CcdChain_ReachesAReachableGoal()
    {
        // Goal well inside the 2-unit reach, off both axes so hip AND knee must rotate.
        var goal = new Vector3(0.6f, 0.9f, 0.4f);
        var ankle = AnkleAfterSolve(Leg([KneeFree, HipFree]), goal);
        Assert.True(Vector3.Distance(ankle, goal) < 0.05f,
            $"ankle {ankle} did not reach goal {goal} (dist={Vector3.Distance(ankle, goal):F3})");
    }

    [Fact]
    public void CcdChain_UnreachableGoal_StretchesTowardIt()
    {
        // Beyond the 2-unit reach: the chain must extend toward the goal direction from the hip,
        // landing near full extension (|ankle-hip| ≈ 2) along the goal ray.
        var goal = new Vector3(3f, -3f, 0f);
        var ankle = AnkleAfterSolve(Leg([KneeFree, HipFree]), goal);
        var hip = new Vector3(0, 2, 0);
        var toGoal = Vector3.Normalize(goal - hip);
        var toAnkle = Vector3.Normalize(ankle - hip);
        Assert.True(Vector3.Dot(toGoal, toAnkle) > 0.99f,
            $"chain did not point at the unreachable goal (dot={Vector3.Dot(toGoal, toAnkle):F3})");
        Assert.True(Vector3.Distance(ankle, hip) > 1.8f,
            $"chain did not extend toward the goal (reach={Vector3.Distance(ankle, hip):F3})");
    }

    [Fact]
    public void KneeHingeLimit_KeepsTheBendOnItsAxis()
    {
        // Hip fixed (knee is the only link). An X-hinged knee moves the ankle strictly in the YZ plane —
        // a goal displaced in X must NOT pull the ankle off X=0 (the limit clamps the solve).
        var goal = new Vector3(0.8f, 1.0f, 0f);
        var ankle = AnkleAfterSolve(Leg([KneeHinge]), goal);
        Assert.True(MathF.Abs(ankle.X) < 1e-3f,
            $"X-hinged knee left the YZ plane (ankle.X={ankle.X:F4})");
    }

    [Fact]
    public void KneeHingeLimit_StillBendsWithinTheAllowedAxis()
    {
        // A goal on the hinge's ALLOWED side (X ∈ [-π, 0] folds the ankle toward +Z), at exactly the
        // ankle's 1-unit radius from the knee so it is reachable by the hinge alone: the knee must
        // actually bend there, not just sit clamped. Real motions always put the goal on this side.
        var goal = new Vector3(0f, 1f, 0f) + new Vector3(0f, 0.6f, 0.8f); // knee + unit direction
        var ankle = AnkleAfterSolve(Leg([KneeHinge]), goal);
        Assert.True(Vector3.Distance(ankle, goal) < 0.08f,
            $"hinged knee did not bend to the in-plane goal (ankle={ankle}, dist={Vector3.Distance(ankle, goal):F3})");
    }

    [Fact]
    public void Evaluate_IsDeterministicAcrossSeeks()
    {
        // IK state must reset per evaluation: t=0 → t≠0 → t=0 lands on the identical pose.
        var model = Leg([KneeFree, HipFree]);
        var animator = new MmdAnimator(model, GoalAt(new Vector3(0.6f, 0.9f, 0.4f)));
        var positions = new Vector3[model.Vertices.Count];

        animator.Evaluate(TimeSpan.Zero, positions);
        var first = animator.TryGetBoneWorldPosition("ankle")!.Value;
        animator.Evaluate(TimeSpan.FromSeconds(3), positions);
        animator.Evaluate(TimeSpan.Zero, positions);
        var again = animator.TryGetBoneWorldPosition("ankle")!.Value;

        Assert.True(Vector3.Distance(first, again) < 1e-5f, $"seek-back diverged: {first} vs {again}");
    }

    [Fact]
    public void SkinnedVertex_FollowsTheSolvedChain()
    {
        // The ankle-bound vertex must land where the solved ankle bone landed (skin pass ran post-IK).
        var model = Leg([KneeFree, HipFree]);
        var goal = new Vector3(0.6f, 0.9f, 0.4f);
        var animator = new MmdAnimator(model, GoalAt(goal));
        var positions = new Vector3[model.Vertices.Count];
        animator.Evaluate(TimeSpan.Zero, positions);
        var ankleBone = animator.TryGetBoneWorldPosition("ankle")!.Value;
        Assert.True(Vector3.Distance(positions[0], ankleBone) < 1e-4f,
            $"skinned vertex {positions[0]} detached from the solved ankle {ankleBone}");
    }

    /// <summary>The YYB-style D-bone rig: a parallel chain on a LATER deform layer whose bones inherit
    /// rotation (ratio 1) from the IK-solved chain and carry the skin weights. The append fold must see
    /// the donors' IK rotations — the "knees don't bend" regression.</summary>
    [Fact]
    public void AppendBones_InheritIkRotation_DChainTracksTheSolvedChain()
    {
        var model = new PmxDocument
        {
            Version = 2.0f,
            ModelName = "ik-leg-d",
            ModelNameEnglish = "ik-leg-d",
            Vertices = [Vertex(new Vector3(0, 0, 0), bone: 6)], // skinned to ankleD, like real rigs
            Indices = [],
            Textures = [],
            Materials = [],
            Bones =
            [
                Bone("hip", new Vector3(0, 2, 0), parent: -1),
                Bone("knee", new Vector3(0, 1, 0), parent: Hip),
                Bone("ankle", new Vector3(0, 0, 0), parent: Knee),
                Bone("ik", new Vector3(0, 0, 0), parent: -1,
                    isIk: true, ikTarget: Ankle, ikLoop: 60, ikLimit: 2f, links: [KneeFree, HipFree]),
                Bone("hipD", new Vector3(0, 2, 0), parent: -1, appendParent: Hip, appendRatio: 1f, layer: 1),
                Bone("kneeD", new Vector3(0, 1, 0), parent: 4, appendParent: Knee, appendRatio: 1f, layer: 1),
                Bone("ankleD", new Vector3(0, 0, 0), parent: 5, appendParent: Ankle, appendRatio: 1f, layer: 1),
            ],
            Morphs = [],
        };

        var goal = new Vector3(0.6f, 0.9f, 0.4f);
        var animator = new MmdAnimator(model, GoalAt(goal));
        var positions = new Vector3[model.Vertices.Count];
        animator.Evaluate(TimeSpan.Zero, positions);

        var ankle = animator.TryGetBoneWorldPosition("ankle")!.Value;
        var ankleD = animator.TryGetBoneWorldPosition("ankleD")!.Value;
        Assert.True(Vector3.Distance(ankle, ankleD) < 1e-3f,
            $"D-chain detached from the IK-solved chain: ankle={ankle} ankleD={ankleD}");
        Assert.True(Vector3.Distance(positions[0], ankleD) < 1e-4f,
            $"skinned vertex {positions[0]} detached from ankleD {ankleD}");
    }

    /// <summary>VMD show/IK keys can switch a solver off — the chain must then hold its FK pose even
    /// though the goal bone is animated elsewhere.</summary>
    [Fact]
    public void IkToggle_DisabledSolver_LeavesTheChainAtItsFkPose()
    {
        var model = Leg([KneeFree, HipFree]);
        var goal = new Vector3(0.6f, 0.9f, 0.4f);

        var animator = new MmdAnimator(model, GoalAt(goal, ikEnabled: false));
        var positions = new Vector3[model.Vertices.Count];
        animator.Evaluate(TimeSpan.Zero, positions);
        var ankle = animator.TryGetBoneWorldPosition("ankle")!.Value;
        Assert.True(Vector3.Distance(ankle, Vector3.Zero) < 1e-4f,
            $"disabled IK still moved the chain (ankle={ankle}, expected bind (0,0,0))");

        // Sanity: the same motion with the toggle ON reaches the goal.
        animator = new MmdAnimator(model, GoalAt(goal, ikEnabled: true));
        animator.Evaluate(TimeSpan.Zero, positions);
        ankle = animator.TryGetBoneWorldPosition("ankle")!.Value;
        Assert.True(Vector3.Distance(ankle, goal) < 0.05f,
            $"enabled IK did not reach the goal (ankle={ankle})");
    }

    [Fact]
    public void LimitAngle_ReflectsPastLimits_OnlyDuringTheAxisPhase()
    {
        // MMD's knee bootstrap: past-limit angles bounce to their in-range mirror image during the
        // first half of the iterations, and clamp plainly afterwards.
        Assert.Equal(-0.3f, MmdAnimator.LimitAngle(0.3f, -MathF.PI, 0f, useAxis: true), 5);
        Assert.Equal(0f, MmdAnimator.LimitAngle(0.3f, -MathF.PI, 0f, useAxis: false), 5);
        // Below the minimum: reflected up into the range (2·min − angle).
        Assert.Equal(4f - 2f * MathF.PI, MmdAnimator.LimitAngle(-4f, -MathF.PI, 0f, useAxis: true), 5);
        // Mirror image itself out of range → plain clamp even in the axis phase.
        Assert.Equal(0.2f, MmdAnimator.LimitAngle(0.5f, -0.05f, 0.2f, useAxis: true), 5);
        // In range: untouched.
        Assert.Equal(-1f, MmdAnimator.LimitAngle(-1f, -MathF.PI, 0f, useAxis: true), 5);
    }

    [Fact]
    public void ProjectToAxis_KeepsTheFullAngleAboutTheFixedAxis()
    {
        // A twist bone's sampled rotation is rebuilt about its authored axis with the SAME angle,
        // signed by the rotation axis' alignment (babylon-mmd runtime axis-limit semantics).
        var aligned = MmdAnimator.ProjectToAxis(
            Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1f, 0.2f, 0f)), 0.5f), Vector3.UnitX);
        AssertSameRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f), aligned);

        var opposed = MmdAnimator.ProjectToAxis(
            Quaternion.CreateFromAxisAngle(-Vector3.UnitX, 0.4f), Vector3.UnitX);
        AssertSameRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.4f), opposed);

        AssertSameRotation(Quaternion.Identity, MmdAnimator.ProjectToAxis(Quaternion.Identity, Vector3.UnitX));
    }

    [Fact]
    public void ClampEulerXyz_RoundTripsWithinLimits_AndClampsOutside()
    {
        // Inside wide limits: the rotation is unchanged (up to quaternion sign).
        var q = Quaternion.CreateFromYawPitchRoll(0.3f, -0.4f, 0.2f);
        var wide = MmdAnimator.ClampEulerXyz(q, new Vector3(-3f, -3f, -3f), new Vector3(3f, 3f, 3f));
        AssertSameRotation(q, wide);

        // Y/Z clamped to zero: the result must rotate the Y axis only within the XY... i.e. act as a pure
        // X rotation — it maps unit-Z onto the YZ plane and keeps unit-X fixed.
        var clamped = MmdAnimator.ClampEulerXyz(q, new Vector3(-MathF.PI, 0, 0), Vector3.Zero);
        var xAxis = Vector3.Transform(Vector3.UnitX, clamped);
        Assert.True(Vector3.Distance(xAxis, Vector3.UnitX) < 1e-4f,
            $"X-only clamp moved the X axis: {xAxis}");
    }

    private static void AssertSameRotation(Quaternion expected, Quaternion actual)
    {
        var dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual)));
        Assert.True(dot > 0.9999f, $"rotations differ (|dot|={dot:F5}): {expected} vs {actual}");
    }
}
