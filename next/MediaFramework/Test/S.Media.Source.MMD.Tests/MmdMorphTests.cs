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

    private static VmdDocument MorphMotion(params (string Name, float Weight)[] morphs) => new()
    {
        ModelName = "morphs",
        BoneTracks = new Dictionary<string, IReadOnlyList<VmdBoneFrame>>(StringComparer.Ordinal),
        MorphTracks = morphs.ToDictionary(
            m => m.Name,
            m => (IReadOnlyList<VmdMorphFrame>)[new VmdMorphFrame(0, m.Weight)],
            StringComparer.Ordinal),
        CameraTrack = [],
    };

    private static PmxDocument Model(
        PmxVertex vertex,
        IReadOnlyList<PmxMorph> morphs,
        IReadOnlyList<PmxMaterial>? materials = null) => new()
    {
        Version = 2.1f,
        ModelName = "morph-test",
        ModelNameEnglish = "morph-test",
        Vertices = [vertex],
        Indices = [],
        Textures = [],
        Materials = materials ?? [],
        Bones = [Bone("left", Vector3.Zero), Bone("right", Vector3.Zero)],
        Morphs = morphs,
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

    [Theory]
    [InlineData(PmxDeformType.Sdef)]
    [InlineData(PmxDeformType.Qdef)]
    public void SphericalSkinning_DoesNotCollapseOpposingBoneRotations(PmxDeformType deformType)
    {
        var vertex = new PmxVertex(
            Vector3.UnitX, Vector3.UnitY, Vector2.Zero,
            0, 1, -1, -1, 0.5f, 0.5f, 0f, 0f)
        {
            DeformType = deformType,
            SdefC = Vector3.Zero,
            SdefR0 = Vector3.Zero,
            SdefR1 = Vector3.Zero,
        };
        var rotations = new PmxMorph("opposed", 0, [])
        {
            Type = 2,
            BoneOffsets =
            [
                new PmxBoneMorphOffset(0, Vector3.Zero,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3)),
                new PmxBoneMorphOffset(1, Vector3.Zero,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 3)),
            ],
        };
        var animator = new MmdAnimator(Model(vertex, [rotations]), MorphMotion("opposed", 1f));
        var positions = new Vector3[1];
        animator.Evaluate(TimeSpan.Zero, positions);

        Assert.True(Vector3.Distance(positions[0], Vector3.UnitX) < 1e-4f,
            $"{deformType} collapsed the opposed rotations to {positions[0]}");
    }

    [Fact]
    public void UvAndMaterialMorphs_UpdateRuntimeRenderState()
    {
        var vertex = Vertex(Vector3.Zero, bone: 0) with
        {
            AdditionalUvs = [new Vector4(0.1f, 0.2f, 0.3f, 0.4f)],
        };
        var uv = new PmxMorph("uv", 0, [])
        {
            Type = 3,
            UvOffsets = [new PmxUvMorphOffset(0, new Vector4(0.4f, 0.2f, 0, 0))],
        };
        var additionalUv = new PmxMorph("uv1", 0, [])
        {
            Type = 4,
            UvOffsets = [new PmxUvMorphOffset(0, new Vector4(0.2f, 0.4f, 0.6f, 0.8f))],
        };
        var materialMorph = new PmxMorph("material", 0, [])
        {
            Type = 8,
            MaterialOffsets =
            [
                new PmxMaterialMorphOffset(
                    0, PmxMaterialMorphOperation.Multiply,
                    new Vector4(2, 1, 1, 1), new Vector3(0.5f), 2,
                    Vector3.One, Vector4.One, 2,
                    new Vector4(0.5f), Vector4.One, Vector4.One),
            ],
        };
        var material = new PmxMaterial(
            "m", new Vector4(0.5f, 0.4f, 0.3f, 1), Vector3.One, 4,
            Vector3.One, true, -1, 0)
        {
            EdgeSize = 1f,
        };
        var animator = new MmdAnimator(
            Model(vertex, [uv, additionalUv, materialMorph], [material]),
            MorphMotion(("uv", 0.5f), ("uv1", 0.5f), ("material", 0.5f)));

        animator.Evaluate(TimeSpan.Zero, new Vector3[1]);

        Assert.Equal(new Vector2(0.2f, 0.1f), animator.CurrentUvs[0]);
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 0.8f), animator.CurrentAdditionalUv1[0]);
        var state = Assert.Single(animator.MaterialStates);
        Assert.Equal(0.75f, state.Diffuse.X, precision: 4);
        Assert.Equal(6f, state.SpecularPower, precision: 4);
        Assert.Equal(1.5f, state.EdgeSize, precision: 4);
        Assert.Equal(new Vector4(0.75f), state.TextureMultiply);
    }
}
