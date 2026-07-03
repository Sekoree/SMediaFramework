using System.Numerics;
using S.Media.Source.MMD;
using Xunit;

namespace S.Media.Source.MMD.Tests;

/// <summary>Bone morphs (PMX type 2) and group morphs (type 0) — the two morph kinds beyond plain
/// vertex morphs that motions actually drive.</summary>
public sealed class MmdMorphTests
{
    private static PmxBone Bone(string name, Vector3 position, int parent = -1) =>
        new(name, name, position, parent,
            AppendRotation: false, AppendTranslation: false, AppendParentIndex: -1, AppendRatio: 0f,
            IsIk: false, IkTargetIndex: -1, IkLoopCount: 0, IkLimitRadians: 0f, IkLinks: []);

    private static PmxVertex Vertex(Vector3 position, int bone) =>
        new(position, Vector3.UnitY, Vector2.Zero, bone, -1, -1, -1, 1f, 0f, 0f, 0f);

    private static VmdDocument MorphMotion(string morphName, float weight) => new()
    {
        ModelName = "morphs",
        BoneTracks = new Dictionary<string, IReadOnlyList<VmdBoneFrame>>(StringComparer.Ordinal),
        MorphTracks = new Dictionary<string, IReadOnlyList<VmdMorphFrame>>(StringComparer.Ordinal)
        {
            [morphName] = [new VmdMorphFrame(0, weight)],
        },
        CameraTrack = [],
    };

    [Fact]
    public void BoneMorph_RotatesAndTranslatesTheBone_ScaledByWeight()
    {
        var model = new PmxDocument
        {
            Version = 2.0f,
            ModelName = "bone-morph",
            ModelNameEnglish = "bone-morph",
            Vertices = [Vertex(new Vector3(1, 0, 0), bone: 0)],
            Indices = [],
            Textures = [],
            Materials = [],
            Bones = [Bone("b", Vector3.Zero)],
            Morphs =
            [
                new PmxMorph("twist", 0, [])
                {
                    BoneOffsets =
                    [
                        new PmxBoneMorphOffset(0, new Vector3(0, 1, 0),
                            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)),
                    ],
                },
            ],
        };

        // Full weight: the vertex at (1,0,0) rotates 90° about Z then rides the (0,1,0) translation.
        var animator = new MmdAnimator(model, MorphMotion("twist", 1f));
        var positions = new Vector3[1];
        animator.Evaluate(TimeSpan.Zero, positions);
        Assert.True(Vector3.Distance(positions[0], new Vector3(0, 2, 0)) < 1e-4f,
            $"full-weight bone morph: expected (0,2,0), got {positions[0]}");

        // Half weight: slerped to 45° and half the translation.
        animator = new MmdAnimator(model, MorphMotion("twist", 0.5f));
        animator.Evaluate(TimeSpan.Zero, positions);
        var expected = new Vector3(MathF.Cos(MathF.PI / 4), MathF.Sin(MathF.PI / 4) + 0.5f, 0);
        Assert.True(Vector3.Distance(positions[0], expected) < 1e-4f,
            $"half-weight bone morph: expected {expected}, got {positions[0]}");
    }

    [Fact]
    public void GroupMorph_FansItsWeightOntoMembers_RatioScaled()
    {
        var model = new PmxDocument
        {
            Version = 2.0f,
            ModelName = "group-morph",
            ModelNameEnglish = "group-morph",
            Vertices = [Vertex(Vector3.Zero, bone: 0)],
            Indices = [],
            Textures = [],
            Materials = [],
            Bones = [Bone("b", Vector3.Zero)],
            Morphs =
            [
                new PmxMorph("member", 0, [new PmxVertexMorphOffset(0, new Vector3(0, 0, 1))]),
                new PmxMorph("group", 0, []) { GroupOffsets = [new PmxGroupMorphOffset(0, 0.5f)] },
            ],
        };

        // The motion drives ONLY the group at 0.8: the member must apply at 0.8 · 0.5 = 0.4.
        var animator = new MmdAnimator(model, MorphMotion("group", 0.8f));
        var positions = new Vector3[1];
        animator.Evaluate(TimeSpan.Zero, positions);
        Assert.True(MathF.Abs(positions[0].Z - 0.4f) < 1e-4f,
            $"group fan-out: expected z=0.4, got {positions[0].Z:F4}");
    }
}
