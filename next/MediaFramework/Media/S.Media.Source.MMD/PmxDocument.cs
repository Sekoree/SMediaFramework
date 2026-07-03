using System.Text;

namespace S.Media.Source.MMD;

public enum PmxDeformType : byte
{
    Bdef1 = 0,
    Bdef2 = 1,
    Bdef4 = 2,
    Sdef = 3,
    Qdef = 4,
}

/// <summary>One PMX vertex with its skinning influences and optional SDEF/additional-UV data.</summary>
public readonly record struct PmxVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 Uv,
    int Bone0, int Bone1, int Bone2, int Bone3,
    float Weight0, float Weight1, float Weight2, float Weight3)
{
    public PmxDeformType DeformType { get; init; }
    public Vector3 SdefC { get; init; }
    public Vector3 SdefR0 { get; init; }
    public Vector3 SdefR1 { get; init; }
    public float EdgeScale { get; init; } = 1f;
    public IReadOnlyList<Vector4> AdditionalUvs { get; init; } = [];
}

/// <summary>How a material's sphere (environment) texture combines with the base color (MMD .sph/.spa).</summary>
public enum PmxSphereMode : byte
{
    None = 0,
    Multiply = 1,
    Add = 2,
    SubTexture = 3,
}

public sealed record PmxMaterial(
    string Name,
    Vector4 Diffuse,
    Vector3 Specular,
    float SpecularPower,
    Vector3 Ambient,
    bool DoubleSided,
    int TextureIndex,
    int FaceVertexCount)
{
    /// <summary>Draw the inverted-hull outline for this material (PMX flag 0x10).</summary>
    public bool HasEdge { get; init; }

    public bool HasGroundShadow { get; init; }

    public bool CastsSelfShadow { get; init; }

    public bool ReceivesSelfShadow { get; init; }

    /// <summary>Outline color (RGBA) for the edge pass.</summary>
    public Vector4 EdgeColor { get; init; } = new(0, 0, 0, 1);

    /// <summary>Outline thickness in MMD edge units (scaled by the renderer).</summary>
    public float EdgeSize { get; init; }

    /// <summary>Sphere-map texture index into <see cref="PmxDocument.Textures"/>, or −1.</summary>
    public int SphereTextureIndex { get; init; } = -1;

    public PmxSphereMode SphereMode { get; init; }

    /// <summary>Per-material toon texture index into <see cref="PmxDocument.Textures"/> (−1 when the
    /// material uses a SHARED toon — see <see cref="SharedToonIndex"/> — or none).</summary>
    public int ToonTextureIndex { get; init; } = -1;

    /// <summary>Shared toon slot 0–9 (the classic toon01–toon10 ramps), or −1 when a per-material
    /// texture (<see cref="ToonTextureIndex"/>) or none applies.</summary>
    public int SharedToonIndex { get; init; } = -1;
}

/// <summary>IK link inside a bone's IK chain (angle limits in radians, MMD conventions).</summary>
public sealed record PmxIkLink(int BoneIndex, bool HasLimit, Vector3 LimitMin, Vector3 LimitMax);

public sealed record PmxBone(
    string Name,
    string NameEnglish,
    Vector3 Position,
    int ParentIndex,
    bool AppendRotation,
    bool AppendTranslation,
    int AppendParentIndex,
    float AppendRatio,
    bool IsIk,
    int IkTargetIndex,
    int IkLoopCount,
    float IkLimitRadians,
    IReadOnlyList<PmxIkLink> IkLinks)
{
    /// <summary>PMX deform layer — MMD evaluates bones sorted by (layer, index). The D-bone rigs that
    /// carry the leg skin weights sit on a later layer than the IK bones whose result they inherit.</summary>
    public int DeformLayer { get; init; }

    /// <summary>Bone evaluates AFTER the physics step (PMX flag 0x1000).</summary>
    public bool TransformAfterPhysics { get; init; }

    /// <summary>Fixed rotation axis (PMX flag 0x0400 — the 腕捩/手捩 twist bones): sampled rotation is
    /// projected onto this axis. Null when the bone rotates freely.</summary>
    public Vector3? FixedAxis { get; init; }

    /// <summary>Append reads the donor's LOCAL (world-deformation) state instead of its animated local
    /// pose (PMX flag 0x0080) — rare, but authored on some accessory rigs.</summary>
    public bool LocalAppend { get; init; }
}

/// <summary>Rigid-body collision shape (PMX byte values).</summary>
public enum PmxRigidShape : byte
{
    Sphere = 0,
    Box = 1,
    Capsule = 2,
}

/// <summary>How a rigid body couples to its bone (PMX byte values).</summary>
public enum PmxPhysicsMode : byte
{
    /// <summary>Kinematic: the body follows the animated bone (collider only).</summary>
    FollowBone = 0,

    /// <summary>Dynamic: physics drives the bone (hair/skirt links).</summary>
    Physics = 1,

    /// <summary>Dynamic rotation, but the bone keeps its animated position (root links).</summary>
    PhysicsWithBonePosition = 2,
}

/// <summary>One PMX rigid body (stage-5 physics). Position/rotation are the body's BIND placement in
/// model space; <see cref="Group"/> is the 0–15 collision group and <see cref="CollisionMask"/> the
/// bitmask of groups it collides WITH.</summary>
public sealed record PmxRigidBody(
    string Name,
    int BoneIndex,
    byte Group,
    ushort CollisionMask,
    PmxRigidShape Shape,
    Vector3 Size,
    Vector3 Position,
    Vector3 RotationRadians,
    float Mass,
    float LinearDamping,
    float AngularDamping,
    float Restitution,
    float Friction,
    PmxPhysicsMode Mode);

/// <summary>One PMX joint (spring six-DOF, type 0 — the only kind MMD emits) connecting two rigid
/// bodies with per-axis linear/angular limits and spring constants.</summary>
public sealed record PmxJoint(
    string Name,
    byte Type,
    int RigidBodyA,
    int RigidBodyB,
    Vector3 Position,
    Vector3 RotationRadians,
    Vector3 LinearLowerLimit,
    Vector3 LinearUpperLimit,
    Vector3 AngularLowerLimit,
    Vector3 AngularUpperLimit,
    Vector3 LinearSpring,
    Vector3 AngularSpring);

/// <summary>One vertex-morph offset.</summary>
public readonly record struct PmxVertexMorphOffset(int VertexIndex, Vector3 Offset);

/// <summary>One bone-morph offset: at weight w the bone gains w·<paramref name="Translation"/> and
/// slerp(identity, <paramref name="Rotation"/>, w) on top of its animated pose.</summary>
public readonly record struct PmxBoneMorphOffset(int BoneIndex, Vector3 Translation, Quaternion Rotation);

/// <summary>One group-morph member: the group's weight fans out as weight·<paramref name="Ratio"/>
/// onto the referenced morph.</summary>
public readonly record struct PmxGroupMorphOffset(int MorphIndex, float Ratio);

public readonly record struct PmxUvMorphOffset(int VertexIndex, Vector4 Offset);

public enum PmxMaterialMorphOperation : byte
{
    Multiply = 0,
    Add = 1,
}

public readonly record struct PmxMaterialMorphOffset(
    int MaterialIndex,
    PmxMaterialMorphOperation Operation,
    Vector4 Diffuse,
    Vector3 Specular,
    float SpecularPower,
    Vector3 Ambient,
    Vector4 EdgeColor,
    float EdgeSize,
    Vector4 TextureColor,
    Vector4 SphereTextureColor,
    Vector4 ToonTextureColor);

/// <summary>Per-frame material values after PMX material morphs. Texture colors keep separate
/// multiplicative/additive channels because MMD applies them to sampled texels differently.</summary>
public readonly record struct MmdMaterialState(
    Vector4 Diffuse,
    Vector3 Specular,
    float SpecularPower,
    Vector3 Ambient,
    Vector4 EdgeColor,
    float EdgeSize,
    Vector4 TextureMultiply,
    Vector4 TextureAdd,
    Vector4 SphereMultiply,
    Vector4 SphereAdd,
    Vector4 ToonMultiply,
    Vector4 ToonAdd)
{
    public static MmdMaterialState From(PmxMaterial material) => new(
        material.Diffuse, material.Specular, material.SpecularPower, material.Ambient,
        material.EdgeColor, material.EdgeSize,
        Vector4.One, Vector4.Zero, Vector4.One, Vector4.Zero, Vector4.One, Vector4.Zero);
}

public sealed record PmxMorph(string Name, int Panel, IReadOnlyList<PmxVertexMorphOffset> VertexOffsets)
{
    public byte Type { get; init; } = VertexOffsets.Count > 0 ? (byte)1 : (byte)0;

    /// <summary>Bone offsets (morph type 2); empty for other morph kinds.</summary>
    public IReadOnlyList<PmxBoneMorphOffset> BoneOffsets { get; init; } = [];

    /// <summary>Group members (morph type 0); empty for other morph kinds.</summary>
    public IReadOnlyList<PmxGroupMorphOffset> GroupOffsets { get; init; } = [];

    /// <summary>Base UV (type 3) or additional UV1–4 (types 4–7) offsets.</summary>
    public IReadOnlyList<PmxUvMorphOffset> UvOffsets { get; init; } = [];

    public IReadOnlyList<PmxMaterialMorphOffset> MaterialOffsets { get; init; } = [];
}

/// <summary>
/// Parsed PMX 2.0/2.1 model. Faithfully parses the sections this renderer evaluates (vertices +
/// skinning, faces, materials, bones including IK, vertex/group/bone/UV/material morphs, rigid bodies
/// and joints). Cosmetic display frames and MMD-unsupported PMX 2.1 impulse payloads are structurally
/// skipped; PMX 2.1 soft bodies remain outside MMD playback parity. All reads are bounds-checked — a malformed count/index throws
/// <see cref="PmxFormatException"/> instead of corrupting memory.
/// </summary>
public sealed class PmxDocument
{
    public required float Version { get; init; }
    public required string ModelName { get; init; }
    public required string ModelNameEnglish { get; init; }
    public required IReadOnlyList<PmxVertex> Vertices { get; init; }
    public required IReadOnlyList<int> Indices { get; init; }
    public required IReadOnlyList<string> Textures { get; init; }
    public required IReadOnlyList<PmxMaterial> Materials { get; init; }
    public required IReadOnlyList<PmxBone> Bones { get; init; }
    public required IReadOnlyList<PmxMorph> Morphs { get; init; }

    /// <summary>Rigid bodies (stage-5 physics); empty when the file carries none.</summary>
    public IReadOnlyList<PmxRigidBody> RigidBodies { get; init; } = [];

    /// <summary>Spring six-DOF joints between rigid bodies; empty when the file carries none.</summary>
    public IReadOnlyList<PmxJoint> Joints { get; init; } = [];

    public static PmxDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static PmxDocument Load(Stream stream)
    {
        var reader = new Reader(stream);
        var magic = reader.Bytes(4);
        if (magic[0] != (byte)'P' || magic[1] != (byte)'M' || magic[2] != (byte)'X' || magic[3] != (byte)' ')
            throw new PmxFormatException("not a PMX file (bad signature)");
        var version = reader.F32();
        if (version is < 2.0f or > 2.1f)
            throw new PmxFormatException($"unsupported PMX version {version}");

        var globalsCount = reader.U8();
        if (globalsCount < 8)
            throw new PmxFormatException($"PMX globals count {globalsCount} < 8");
        var globals = reader.Bytes(globalsCount);
        var encoding = globals[0] switch
        {
            0 => Encoding.Unicode,
            1 => Encoding.UTF8,
            _ => throw new PmxFormatException($"unknown text encoding {globals[0]}"),
        };
        int addVec4Count = globals[1];
        int vertexIndexSize = globals[2], textureIndexSize = globals[3], materialIndexSize = globals[4];
        int boneIndexSize = globals[5], morphIndexSize = globals[6], rigidIndexSize = globals[7];

        string Text() => reader.Text(encoding);
        var modelName = Text();
        var modelNameEn = Text();
        _ = Text(); // comment
        _ = Text(); // comment english

        // Vertices
        var vertexCount = reader.Count("vertex");
        var vertices = new PmxVertex[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var pos = reader.Vec3();
            var normal = reader.Vec3();
            var uv = new Vector2(reader.F32(), reader.F32());
            var additionalUvs = new Vector4[addVec4Count];
            for (var a = 0; a < addVec4Count; a++)
                additionalUvs[a] = reader.Vec4();
            var deformType = reader.U8();
            int b0 = -1, b1 = -1, b2 = -1, b3 = -1;
            float w0 = 0, w1 = 0, w2 = 0, w3 = 0;
            Vector3 sdefC = default, sdefR0 = default, sdefR1 = default;
            switch (deformType)
            {
                case 0: // BDEF1
                    b0 = reader.Index(boneIndexSize); w0 = 1f;
                    break;
                case 1: // BDEF2
                    b0 = reader.Index(boneIndexSize); b1 = reader.Index(boneIndexSize);
                    w0 = reader.F32(); w1 = 1f - w0;
                    break;
                case 2: // BDEF4
                case 4: // QDEF is a PMX 2.1 extension (not produced by MMD itself)
                    b0 = reader.Index(boneIndexSize); b1 = reader.Index(boneIndexSize);
                    b2 = reader.Index(boneIndexSize); b3 = reader.Index(boneIndexSize);
                    w0 = reader.F32(); w1 = reader.F32(); w2 = reader.F32(); w3 = reader.F32();
                    break;
                case 3: // SDEF (spherical blend)
                    b0 = reader.Index(boneIndexSize); b1 = reader.Index(boneIndexSize);
                    w0 = reader.F32(); w1 = 1f - w0;
                    sdefC = reader.Vec3();
                    sdefR0 = reader.Vec3();
                    sdefR1 = reader.Vec3();
                    break;
                default:
                    throw new PmxFormatException($"unknown vertex deform type {deformType}");
            }

            var edgeScale = reader.F32();
            vertices[i] = new PmxVertex(pos, normal, uv, b0, b1, b2, b3, w0, w1, w2, w3)
            {
                DeformType = (PmxDeformType)deformType,
                SdefC = sdefC,
                SdefR0 = sdefR0,
                SdefR1 = sdefR1,
                EdgeScale = edgeScale,
                AdditionalUvs = additionalUvs,
            };
        }

        // Faces (stored as face-vertex indices; count is divisible by 3)
        var indexCount = reader.Count("face-vertex");
        if (indexCount % 3 != 0)
            throw new PmxFormatException($"face-vertex count {indexCount} not divisible by 3");
        var indices = new int[indexCount];
        for (var i = 0; i < indexCount; i++)
        {
            var v = reader.UnsignedIndex(vertexIndexSize);
            if (v < 0 || v >= vertexCount)
                throw new PmxFormatException($"face vertex index {v} out of range (vertices: {vertexCount})");
            indices[i] = v;
        }

        // Textures
        var textureCount = reader.Count("texture");
        var textures = new string[textureCount];
        for (var i = 0; i < textureCount; i++)
            textures[i] = Text();

        // Materials
        var materialCount = reader.Count("material");
        var materials = new PmxMaterial[materialCount];
        for (var i = 0; i < materialCount; i++)
        {
            var name = Text();
            _ = Text(); // english name
            var diffuse = reader.Vec4();
            var specular = reader.Vec3();
            var specularPower = reader.F32();
            var ambient = reader.Vec3();
            var flags = reader.U8();
            var edgeColor = reader.Vec4();
            var edgeSize = reader.F32();
            var textureIndex = reader.Index(textureIndexSize);
            var sphereTexture = reader.Index(textureIndexSize);
            var sphereMode = reader.U8();
            var sharedToon = reader.U8();
            int toonTexture = -1, sharedToonIndex = -1;
            if (sharedToon == 0)
                toonTexture = reader.Index(textureIndexSize);
            else
                sharedToonIndex = reader.U8();
            _ = Text(); // memo
            var faceVertexCount = reader.I32();
            if (faceVertexCount < 0 || faceVertexCount % 3 != 0)
                throw new PmxFormatException($"material '{name}' face-vertex count {faceVertexCount} invalid");
            materials[i] = new PmxMaterial(
                name, diffuse, specular, specularPower, ambient,
                DoubleSided: (flags & 0x01) != 0, textureIndex, faceVertexCount)
            {
                HasEdge = (flags & 0x10) != 0,
                HasGroundShadow = (flags & 0x02) != 0,
                CastsSelfShadow = (flags & 0x04) != 0,
                ReceivesSelfShadow = (flags & 0x08) != 0,
                EdgeColor = edgeColor,
                EdgeSize = edgeSize,
                SphereTextureIndex = sphereTexture,
                SphereMode = sphereMode <= 3 ? (PmxSphereMode)sphereMode : PmxSphereMode.None,
                ToonTextureIndex = toonTexture,
                SharedToonIndex = sharedToonIndex,
            };
        }

        // Bones
        var boneCount = reader.Count("bone");
        var bones = new PmxBone[boneCount];
        for (var i = 0; i < boneCount; i++)
        {
            var name = Text();
            var nameEn = Text();
            var position = reader.Vec3();
            var parent = reader.Index(boneIndexSize);
            var deformLayer = reader.I32();
            var flags = reader.U16();

            if ((flags & 0x0001) != 0) // tail = bone index
                _ = reader.Index(boneIndexSize);
            else
                reader.Skip(12); // tail = offset vector

            var appendRotation = (flags & 0x0100) != 0;
            var appendTranslation = (flags & 0x0200) != 0;
            var appendParent = -1;
            var appendRatio = 0f;
            if (appendRotation || appendTranslation)
            {
                appendParent = reader.Index(boneIndexSize);
                appendRatio = reader.F32();
            }

            Vector3? fixedAxis = null;
            if ((flags & 0x0400) != 0) fixedAxis = reader.Vec3();
            if ((flags & 0x0800) != 0) reader.Skip(24);      // local axes
            if ((flags & 0x2000) != 0) reader.Skip(4);       // external parent key

            var isIk = (flags & 0x0020) != 0;
            var ikTarget = -1;
            var ikLoop = 0;
            var ikLimit = 0f;
            IReadOnlyList<PmxIkLink> ikLinks = [];
            if (isIk)
            {
                ikTarget = reader.Index(boneIndexSize);
                ikLoop = reader.I32();
                ikLimit = reader.F32();
                var linkCount = reader.Count("ik-link");
                var links = new PmxIkLink[linkCount];
                for (var l = 0; l < linkCount; l++)
                {
                    var linkBone = reader.Index(boneIndexSize);
                    var hasLimit = reader.U8() != 0;
                    Vector3 min = default, max = default;
                    if (hasLimit)
                    {
                        min = reader.Vec3();
                        max = reader.Vec3();
                    }

                    links[l] = new PmxIkLink(linkBone, hasLimit, min, max);
                }

                ikLinks = links;
            }

            bones[i] = new PmxBone(
                name, nameEn, position, parent,
                appendRotation, appendTranslation, appendParent, appendRatio,
                isIk, ikTarget, ikLoop, ikLimit, ikLinks)
            {
                DeformLayer = deformLayer,
                TransformAfterPhysics = (flags & 0x1000) != 0,
                FixedAxis = fixedAxis,
                LocalAppend = (flags & 0x0080) != 0,
            };
        }

        // Morphs — vertex (and group→vertex) offsets are evaluated; other types are structurally skipped.
        var morphCount = reader.Count("morph");
        var morphs = new List<PmxMorph>(morphCount);
        for (var i = 0; i < morphCount; i++)
        {
            var name = Text();
            _ = Text();
            var panel = reader.U8();
            var type = reader.U8();
            var offsetCount = reader.Count("morph-offset");
            switch (type)
            {
                case 1: // vertex morph
                    var offsets = new PmxVertexMorphOffset[offsetCount];
                    for (var o = 0; o < offsetCount; o++)
                    {
                        var v = reader.UnsignedIndex(vertexIndexSize);
                        if (v < 0 || v >= vertexCount)
                            throw new PmxFormatException($"morph '{name}' vertex index {v} out of range");
                        offsets[o] = new PmxVertexMorphOffset(v, reader.Vec3());
                    }

                    morphs.Add(new PmxMorph(name, panel, offsets) { Type = type });
                    break;
                case 0: // group: the group's sampled weight fans out ratio-scaled onto its members
                    var members = new PmxGroupMorphOffset[offsetCount];
                    for (var o = 0; o < offsetCount; o++)
                        members[o] = new PmxGroupMorphOffset(reader.Index(morphIndexSize), reader.F32());
                    morphs.Add(new PmxMorph(name, panel, []) { Type = type, GroupOffsets = members });
                    break;
                case 2: // bone: per-bone translation + rotation offsets blended by the sampled weight
                    var boneOffsets = new PmxBoneMorphOffset[offsetCount];
                    for (var o = 0; o < offsetCount; o++)
                    {
                        var morphBone = reader.Index(boneIndexSize);
                        var morphTranslation = reader.Vec3();
                        var q = reader.Vec4();
                        boneOffsets[o] = new PmxBoneMorphOffset(
                            morphBone, morphTranslation, new Quaternion(q.X, q.Y, q.Z, q.W));
                    }

                    morphs.Add(new PmxMorph(name, panel, []) { Type = type, BoneOffsets = boneOffsets });
                    break;
                case 3: case 4: case 5: case 6: case 7: // UV / additional UV1-4
                    var uvOffsets = new PmxUvMorphOffset[offsetCount];
                    for (var o = 0; o < offsetCount; o++)
                    {
                        var vertex = reader.UnsignedIndex(vertexIndexSize);
                        if (vertex < 0 || vertex >= vertexCount)
                            throw new PmxFormatException($"morph '{name}' UV vertex index {vertex} out of range");
                        uvOffsets[o] = new PmxUvMorphOffset(vertex, reader.Vec4());
                    }
                    morphs.Add(new PmxMorph(name, panel, []) { Type = type, UvOffsets = uvOffsets });
                    break;
                case 8: // material
                    var materialOffsets = new PmxMaterialMorphOffset[offsetCount];
                    for (var o = 0; o < offsetCount; o++)
                    {
                        var material = reader.Index(materialIndexSize);
                        if (material < -1 || material >= materialCount)
                            throw new PmxFormatException($"morph '{name}' material index {material} out of range");
                        var operation = reader.U8();
                        if (operation > 1)
                            throw new PmxFormatException($"morph '{name}' material operation {operation} invalid");
                        materialOffsets[o] = new PmxMaterialMorphOffset(
                            material,
                            operation == 0 ? PmxMaterialMorphOperation.Multiply : PmxMaterialMorphOperation.Add,
                            reader.Vec4(), reader.Vec3(), reader.F32(), reader.Vec3(),
                            reader.Vec4(), reader.F32(), reader.Vec4(), reader.Vec4(), reader.Vec4());
                    }
                    morphs.Add(new PmxMorph(name, panel, []) { Type = type, MaterialOffsets = materialOffsets });
                    break;
                case 9:
                    var flips = new PmxGroupMorphOffset[offsetCount];
                    for (var o = 0; o < offsetCount; o++)
                        flips[o] = new PmxGroupMorphOffset(reader.Index(morphIndexSize), reader.F32());
                    morphs.Add(new PmxMorph(name, panel, []) { Type = type, GroupOffsets = flips });
                    break;
                case 10:
                    reader.SkipPer(offsetCount, rigidIndexSize + 1 + 24);
                    morphs.Add(new PmxMorph(name, panel, []) { Type = type });
                    break; // impulse morph is not supported by MMD itself
                default:
                    throw new PmxFormatException($"unknown morph type {type}");
            }
        }

        // Display frames (cosmetic — skipped), then rigid bodies + joints (stage-5 physics). A file that
        // ends after the morphs (our tiny test fixtures) simply carries no physics. Soft bodies (2.1,
        // rare) remain unread.
        var rigidBodies = Array.Empty<PmxRigidBody>();
        var joints = Array.Empty<PmxJoint>();
        if (reader.HasMore)
        {
            var frameCount = reader.Count("display frame");
            for (var i = 0; i < frameCount; i++)
            {
                _ = Text();
                _ = Text();
                reader.Skip(1); // special-frame flag
                var elements = reader.Count("display element");
                for (var e = 0; e < elements; e++)
                {
                    var kind = reader.U8();
                    _ = reader.Index(kind == 0 ? boneIndexSize : morphIndexSize);
                }
            }
        }

        if (reader.HasMore)
        {
            var rigidCount = reader.Count("rigid body");
            var bodies = new PmxRigidBody[rigidCount];
            for (var i = 0; i < rigidCount; i++)
            {
                var name = Text();
                _ = Text();
                var bone = reader.Index(boneIndexSize);
                var group = reader.U8();
                var mask = reader.U16();
                var shape = reader.U8();
                var size = reader.Vec3();
                var position = reader.Vec3();
                var rotationRad = reader.Vec3();
                var mass = reader.F32();
                var linearDamping = reader.F32();
                var angularDamping = reader.F32();
                var restitution = reader.F32();
                var friction = reader.F32();
                var mode = reader.U8();
                bodies[i] = new PmxRigidBody(
                    name, bone, group, mask,
                    shape <= 2 ? (PmxRigidShape)shape : PmxRigidShape.Sphere,
                    size, position, rotationRad,
                    mass, linearDamping, angularDamping, restitution, friction,
                    mode <= 2 ? (PmxPhysicsMode)mode : PmxPhysicsMode.FollowBone);
            }

            rigidBodies = bodies;
        }

        if (reader.HasMore)
        {
            var jointCount = reader.Count("joint");
            var parsed = new PmxJoint[jointCount];
            for (var i = 0; i < jointCount; i++)
            {
                var name = Text();
                _ = Text();
                var type = reader.U8();
                var a = reader.Index(rigidIndexSize);
                var b = reader.Index(rigidIndexSize);
                parsed[i] = new PmxJoint(
                    name, type, a, b,
                    reader.Vec3(), reader.Vec3(),
                    reader.Vec3(), reader.Vec3(),
                    reader.Vec3(), reader.Vec3(),
                    reader.Vec3(), reader.Vec3());
            }

            joints = parsed;
        }

        return new PmxDocument
        {
            Version = version,
            ModelName = modelName,
            ModelNameEnglish = modelNameEn,
            Vertices = vertices,
            Indices = indices,
            Textures = textures,
            Materials = materials,
            Bones = bones,
            Morphs = morphs,
            RigidBodies = rigidBodies,
            Joints = joints,
        };
    }

    /// <summary>Bounds-checked little-endian reader over the whole file.</summary>
    private sealed class Reader(Stream stream)
    {
        private readonly BinaryReader _r = new(stream, Encoding.UTF8, leaveOpen: true);

        public byte U8() => _r.ReadByte();
        public ushort U16() => _r.ReadUInt16();
        public int I32() => _r.ReadInt32();
        public float F32() => _r.ReadSingle();
        public Vector3 Vec3() => new(F32(), F32(), F32());
        public Vector4 Vec4() => new(F32(), F32(), F32(), F32());
        public byte[] Bytes(int count) => _r.ReadBytes(count) is { } b && b.Length == count
            ? b
            : throw new PmxFormatException("unexpected end of file");

        public void Skip(int count) => Bytes(count);

        /// <summary>Whether any bytes remain — trailing sections (rigid bodies/joints) are optional for
        /// minimal files (test fixtures end after the morphs).</summary>
        public bool HasMore => stream.Position < stream.Length;

        public void SkipPer(int count, int stride)
        {
            for (var i = 0; i < count; i++)
                Skip(stride);
        }

        public int Count(string what)
        {
            var n = I32();
            if (n is < 0 or > 50_000_000)
                throw new PmxFormatException($"implausible {what} count {n}");
            return n;
        }

        public string Text(Encoding encoding)
        {
            var length = I32();
            if (length is < 0 or > 4_000_000)
                throw new PmxFormatException($"implausible text length {length}");
            return length == 0 ? string.Empty : encoding.GetString(Bytes(length));
        }

        /// <summary>Signed sized index (bone/texture/material/morph/rigid): -1 = none.</summary>
        public int Index(int size) => size switch
        {
            1 => unchecked((sbyte)U8()),
            2 => unchecked((short)U16()),
            4 => I32(),
            _ => throw new PmxFormatException($"unsupported index size {size}"),
        };

        /// <summary>Vertex indices are UNSIGNED for sizes 1/2 (unlike every other index kind).</summary>
        public int UnsignedIndex(int size) => size switch
        {
            1 => U8(),
            2 => U16(),
            4 => I32(),
            _ => throw new PmxFormatException($"unsupported index size {size}"),
        };
    }
}

public sealed class PmxFormatException(string message) : Exception(message);
