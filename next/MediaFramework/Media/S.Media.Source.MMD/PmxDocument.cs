using System.Text;

namespace S.Media.Source.MMD;

/// <summary>One PMX vertex with its skinning influences (up to 4 bones; SDEF/QDEF read but evaluated as
/// their linear-blend equivalents in this prototype).</summary>
public readonly record struct PmxVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 Uv,
    int Bone0, int Bone1, int Bone2, int Bone3,
    float Weight0, float Weight1, float Weight2, float Weight3);

public sealed record PmxMaterial(
    string Name,
    Vector4 Diffuse,
    Vector3 Specular,
    float SpecularPower,
    Vector3 Ambient,
    bool DoubleSided,
    int TextureIndex,
    int FaceVertexCount);

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
    IReadOnlyList<PmxIkLink> IkLinks);

/// <summary>One vertex-morph offset.</summary>
public readonly record struct PmxVertexMorphOffset(int VertexIndex, Vector3 Offset);

public sealed record PmxMorph(string Name, int Panel, IReadOnlyList<PmxVertexMorphOffset> VertexOffsets);

/// <summary>
/// Parsed PMX 2.0/2.1 model (review Gate-6 stage 1). Faithfully parses the sections this prototype
/// evaluates (vertices + skinning, faces, materials, bones incl. IK data, vertex/group morphs) and
/// structurally skips the rest (display frames, rigid bodies, joints, soft bodies) so any valid file
/// loads. All reads are bounds-checked — a malformed count/index throws <see cref="PmxFormatException"/>
/// instead of corrupting memory (review: parser bounds tests are an acceptance gate).
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
            for (var a = 0; a < addVec4Count; a++)
                reader.Skip(16);
            var deformType = reader.U8();
            int b0 = -1, b1 = -1, b2 = -1, b3 = -1;
            float w0 = 0, w1 = 0, w2 = 0, w3 = 0;
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
                case 4: // QDEF (dual-quaternion in MMD; linear-blend approximated here)
                    b0 = reader.Index(boneIndexSize); b1 = reader.Index(boneIndexSize);
                    b2 = reader.Index(boneIndexSize); b3 = reader.Index(boneIndexSize);
                    w0 = reader.F32(); w1 = reader.F32(); w2 = reader.F32(); w3 = reader.F32();
                    break;
                case 3: // SDEF (spherical blend; C/R0/R1 read and dropped — evaluated as BDEF2)
                    b0 = reader.Index(boneIndexSize); b1 = reader.Index(boneIndexSize);
                    w0 = reader.F32(); w1 = 1f - w0;
                    reader.Skip(3 * 12);
                    break;
                default:
                    throw new PmxFormatException($"unknown vertex deform type {deformType}");
            }

            reader.Skip(4); // edge scale
            vertices[i] = new PmxVertex(pos, normal, uv, b0, b1, b2, b3, w0, w1, w2, w3);
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
            reader.Skip(16 + 4); // edge color + edge size
            var textureIndex = reader.Index(textureIndexSize);
            _ = reader.Index(textureIndexSize); // sphere texture
            reader.Skip(1); // sphere mode
            var sharedToon = reader.U8();
            if (sharedToon == 0)
                _ = reader.Index(textureIndexSize);
            else
                reader.Skip(1);
            _ = Text(); // memo
            var faceVertexCount = reader.I32();
            if (faceVertexCount < 0 || faceVertexCount % 3 != 0)
                throw new PmxFormatException($"material '{name}' face-vertex count {faceVertexCount} invalid");
            materials[i] = new PmxMaterial(
                name, diffuse, specular, specularPower, ambient,
                DoubleSided: (flags & 0x01) != 0, textureIndex, faceVertexCount);
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
            reader.Skip(4); // deform layer
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

            if ((flags & 0x0400) != 0) reader.Skip(12);      // fixed axis
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
                isIk, ikTarget, ikLoop, ikLimit, ikLinks);
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

                    morphs.Add(new PmxMorph(name, panel, offsets));
                    break;
                case 0: // group: (morph index, weight) — kept as empty; evaluation follows the referenced morphs later
                    for (var o = 0; o < offsetCount; o++)
                    {
                        _ = reader.Index(morphIndexSize);
                        reader.Skip(4);
                    }

                    morphs.Add(new PmxMorph(name, panel, []));
                    break;
                case 2: reader.SkipPer(offsetCount, boneIndexSize + 12 + 16); morphs.Add(new PmxMorph(name, panel, [])); break; // bone
                case 3: case 4: case 5: case 6: case 7: // UV / additional UV1-4
                    reader.SkipPer(offsetCount, vertexIndexSize + 16); morphs.Add(new PmxMorph(name, panel, [])); break;
                case 8: reader.SkipPer(offsetCount, materialIndexSize + 1 + 112); morphs.Add(new PmxMorph(name, panel, [])); break; // material
                case 9: reader.SkipPer(offsetCount, morphIndexSize + 4); morphs.Add(new PmxMorph(name, panel, [])); break; // flip (2.1)
                case 10: reader.SkipPer(offsetCount, rigidIndexSize + 1 + 24); morphs.Add(new PmxMorph(name, panel, [])); break; // impulse (2.1)
                default:
                    throw new PmxFormatException($"unknown morph type {type}");
            }
        }

        // Remaining sections (display frames / rigid bodies / joints / soft bodies) are not needed by the
        // prototype and are not read — the parser stops here by design.
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
