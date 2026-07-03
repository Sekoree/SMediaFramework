using System.Text;
using Xunit;

namespace S.Media.Source.MMD.Tests;

/// <summary>
/// Offline coverage on PURPOSE-MADE byte-level fixtures (the review forbids treating the bundled
/// model/motion assets as redistributable test fixtures): a tiny 2-bone / 1-triangle PMX and a
/// matching VMD are synthesized in-memory, then parsed, animated, and rendered.
/// </summary>
public sealed class MmdModuleTests
{
    // ---- synthetic fixtures ------------------------------------------------------------------------

    /// <summary>Tiny PMX 2.0: 3 vertices (BDEF1 on bone 1), 1 triangle, 1 red material, bones root+child.</summary>
    private static byte[] TinyPmx()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("PMX "u8);
        w.Write(2.0f);
        w.Write((byte)8);
        w.Write((byte)0);  // utf16-le text
        w.Write((byte)0);  // additional vec4s
        w.Write((byte)1);  // vertex index size
        w.Write((byte)1);  // texture index size
        w.Write((byte)1);  // material index size
        w.Write((byte)1);  // bone index size
        w.Write((byte)1);  // morph index size
        w.Write((byte)1);  // rigid index size
        void Text(string s)
        {
            var bytes = Encoding.Unicode.GetBytes(s);
            w.Write(bytes.Length);
            w.Write(bytes);
        }

        Text("tiny");        // model name
        Text("tiny");        // english
        Text("");            // comment
        Text("");            // comment english

        w.Write(3);          // vertices
        void Vertex(float x, float y, float z)
        {
            w.Write(x); w.Write(y); w.Write(z);       // position
            w.Write(0f); w.Write(0f); w.Write(-1f);   // normal
            w.Write(0f); w.Write(0f);                 // uv
            w.Write((byte)0);                         // BDEF1
            w.Write((byte)1);                         // bone index 1 (child)
            w.Write(1f);                              // edge scale
        }

        Vertex(-1, 0, 0);
        Vertex(1, 0, 0);
        Vertex(0, 2, 0);

        w.Write(3);                                   // face-vertex count
        w.Write((byte)0); w.Write((byte)1); w.Write((byte)2);

        w.Write(0);                                   // textures

        w.Write(1);                                   // materials
        Text("red"); Text("red");
        w.Write(1f); w.Write(0f); w.Write(0f); w.Write(1f);  // diffuse RGBA
        w.Write(0f); w.Write(0f); w.Write(0f);               // specular
        w.Write(1f);                                          // specular power
        w.Write(0.2f); w.Write(0f); w.Write(0f);              // ambient
        w.Write((byte)0x01);                                  // double-sided
        w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f);   // edge color
        w.Write(1f);                                          // edge size
        w.Write((byte)0xFF);                                  // texture index (-1)
        w.Write((byte)0xFF);                                  // sphere index (-1)
        w.Write((byte)0);                                     // sphere mode
        w.Write((byte)1);                                     // shared toon flag
        w.Write((byte)0);                                     // shared toon index
        Text("");                                             // memo
        w.Write(3);                                           // face-vertex count

        w.Write(2);                                   // bones
        void Bone(string name, float y, sbyte parent)
        {
            Text(name); Text(name);
            w.Write(0f); w.Write(y); w.Write(0f);
            w.Write((byte)unchecked((byte)parent));   // parent index (size 1)
            w.Write(0);                               // deform layer
            w.Write((ushort)0x0002);                  // flags: rotatable only (tail = offset)
            w.Write(0f); w.Write(0f); w.Write(0f);    // tail offset
        }

        Bone("root", 0, -1);
        Bone("b1", 0, 0);

        w.Write(0);                                   // morphs
        return ms.ToArray();
    }

    /// <summary>Tiny VMD: bone "b1" translates 0→(5,0,0) over 30 frames; one camera key.</summary>
    private static byte[] TinyVmd()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Fixed(string s, int width)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            w.Write(bytes);
            for (var i = bytes.Length; i < width; i++)
                w.Write((byte)0);
        }

        Fixed("Vocaloid Motion Data 0002", 30);
        Fixed("tiny", 20);

        w.Write(2u); // bone frames
        void BoneFrame(uint frame, float x)
        {
            Fixed("b1", 15);
            w.Write(frame);
            w.Write(x); w.Write(0f); w.Write(0f);              // translation
            w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); // identity quaternion
            for (var i = 0; i < 64; i++)
                w.Write((byte)((i % 4 == 0) ? 64 : 64));       // linear-ish interpolation block
        }

        BoneFrame(0, 0f);
        BoneFrame(30, 5f);

        w.Write(0u); // morph frames

        w.Write(1u); // camera frames
        w.Write(0u);          // frame
        w.Write(-20f);        // distance
        w.Write(0f); w.Write(1f); w.Write(0f); // target
        w.Write(0f); w.Write(0f); w.Write(0f); // rotation
        for (var i = 0; i < 24; i++)
            w.Write((byte)64);
        w.Write(30u);         // fov
        w.Write((byte)0);     // perspective
        return ms.ToArray();
    }

    // ---- parsing -----------------------------------------------------------------------------------

    [Fact]
    public void Pmx_TinyFixture_ParsesEveryEvaluatedSection()
    {
        var model = PmxDocument.Load(new MemoryStream(TinyPmx()));
        Assert.Equal("tiny", model.ModelName);
        Assert.Equal(3, model.Vertices.Count);
        Assert.Equal(3, model.Indices.Count);
        var material = Assert.Single(model.Materials);
        Assert.Equal(new Vector4(1, 0, 0, 1), material.Diffuse);
        Assert.Equal(2, model.Bones.Count);
        Assert.Equal(0, model.Bones[1].ParentIndex);
        Assert.Equal(1, model.Vertices[0].Bone0);
    }

    [Fact]
    public void Pmx_Truncated_ThrowsFormatError_NeverCrashes()
    {
        var bytes = TinyPmx();
        for (var cut = 4; cut < bytes.Length; cut += 13)
        {
            var slice = bytes.AsSpan(0, cut).ToArray();
            Assert.ThrowsAny<Exception>(() => PmxDocument.Load(new MemoryStream(slice)));
        }
    }

    [Fact]
    public void Vmd_TinyFixture_ParsesBoneAndCameraTracks()
    {
        var motion = VmdDocument.Load(new MemoryStream(TinyVmd()));
        Assert.Equal("tiny", motion.ModelName);
        var track = motion.BoneTracks["b1"];
        Assert.Equal(2, track.Count);
        Assert.Equal(30u, motion.LastFrame);
        Assert.Equal(TimeSpan.FromSeconds(1), motion.Duration);
        var camera = Assert.Single(motion.CameraTrack);
        Assert.Equal(-20f, camera.Distance);
        Assert.True(camera.Perspective);
    }

    // ---- animation ---------------------------------------------------------------------------------

    [Fact]
    public void Animator_SamplesBoneTranslation_AcrossTheTimeline()
    {
        var model = PmxDocument.Load(new MemoryStream(TinyPmx()));
        var motion = VmdDocument.Load(new MemoryStream(TinyVmd()));
        var animator = new MmdAnimator(model, motion);
        var positions = new Vector3[model.Vertices.Count];

        animator.Evaluate(TimeSpan.Zero, positions);
        Assert.Equal(-1f, positions[0].X, precision: 3);

        animator.Evaluate(TimeSpan.FromSeconds(1), positions); // b1 fully translated +5 on X
        Assert.Equal(4f, positions[0].X, precision: 2);
        Assert.Equal(6f, positions[1].X, precision: 2);

        // Deterministic seek-back: the same time yields the same pose (pure function of time).
        animator.Evaluate(TimeSpan.Zero, positions);
        Assert.Equal(-1f, positions[0].X, precision: 3);
    }

    [Fact]
    public void Bezier_DefaultCurve_IsIdentityLike_AndEndpointsExact()
    {
        Assert.Equal(0f, MmdAnimator.Bezier(20, 20, 107, 107, 0f), precision: 3);
        Assert.Equal(1f, MmdAnimator.Bezier(20, 20, 107, 107, 1f), precision: 3);
        var mid = MmdAnimator.Bezier(20, 20, 107, 107, 0.5f);
        Assert.InRange(mid, 0.4f, 0.6f);
        // A strong ease-in curve stays below linear early on.
        Assert.True(MmdAnimator.Bezier(100, 10, 120, 30, 0.3f) < 0.3f);
    }

    // ---- rendering + source -----------------------------------------------------------------------

    [Fact]
    public void VideoSource_RendersAnimatedFrames_SeeksAndReportsDuration()
    {
        var dir = Directory.CreateTempSubdirectory("mmd-fixture-").FullName;
        try
        {
            var pmx = Path.Combine(dir, "tiny.pmx");
            var vmd = Path.Combine(dir, "tiny.vmd");
            File.WriteAllBytes(pmx, TinyPmx());
            File.WriteAllBytes(vmd, TinyVmd());

            // Explicit camera: the DEFAULT framing targets humanoid-model height (y≈12) and would miss
            // this 2-unit fixture triangle entirely.
            var uri = MmdSourceUri.Build(new MmdSourceRequest(
                pmx, vmd, null, 96, 64,
                CameraDistance: -8f, CameraTarget: new Vector3(0, 1, 0),
                CameraRotationDegrees: Vector3.Zero, CameraFovDegrees: 30f));
            Assert.True(MmdSourceUri.TryParse(uri, out var parsed));
            Assert.Equal(pmx, parsed!.ModelPath);

            var provider = new MmdDecoderProvider();
            Assert.Equal(1.0, provider.Probe(uri, MediaKind.Video));
            Assert.Equal(0.0, provider.Probe("/tmp/file.mp4", MediaKind.Video));

            using var source = (MmdVideoSource)provider.OpenVideo(uri, options: null);
            Assert.Equal(TimeSpan.FromSeconds(1), source.Duration);

            Assert.True(source.TryReadNextFrame(out var frame));
            using (frame)
            {
                Assert.Equal(96, frame.Format.Width);
                var plane = frame.Planes[0].Span;
                var lit = 0;
                for (var i = 0; i < plane.Length; i += 4)
                    if (plane[i + 2] > 16) // red channel — the material is red
                        lit++;
                Assert.True(lit > 20, $"expected the red triangle on screen, lit={lit}");
            }

            // Seek to the end: exhausted; seek back: frames again (pure function of time).
            source.Seek(TimeSpan.FromSeconds(5));
            Assert.False(source.TryReadNextFrame(out _));
            source.Seek(TimeSpan.FromMilliseconds(100));
            Assert.True(source.TryReadNextFrame(out var again));
            again.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void VideoSource_ManualCameraOverride_ChangesTheFraming()
    {
        var dir = Directory.CreateTempSubdirectory("mmd-cam-").FullName;
        try
        {
            var pmx = Path.Combine(dir, "tiny.pmx");
            File.WriteAllBytes(pmx, TinyPmx());

            byte[] RenderWith(float distance)
            {
                var uri = MmdSourceUri.Build(new MmdSourceRequest(
                    pmx, null, null, 96, 64,
                    CameraDistance: distance, CameraTarget: new Vector3(0, 1, 0),
                    CameraRotationDegrees: Vector3.Zero, CameraFovDegrees: 30));
                using var source = (MmdVideoSource)new MmdDecoderProvider().OpenVideo(uri, options: null);
                Assert.True(source.TryReadNextFrame(out var frame));
                using (frame)
                    return frame.Planes[0].ToArray();
            }

            int Lit(byte[] plane)
            {
                var n = 0;
                for (var i = 0; i < plane.Length; i += 4)
                    if (plane[i + 2] > 16)
                        n++;
                return n;
            }

            var near = Lit(RenderWith(-6f));
            var far = Lit(RenderWith(-60f));
            Assert.True(near > far * 2, $"camera distance must change framing (near={near}, far={far})");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void ResolveTexturePath_FallsBackToCaseInsensitiveSegments()
    {
        // MMD models are authored on Windows: `spa\\A1.bmp` in the PMX happily matches `spa/a1.bmp` on
        // disk there — on Linux the exact path misses and the material used to silently render white
        // (the 2026-07-03 "black and white model" report).
        var root = Directory.CreateTempSubdirectory("mmd-tex-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Tex"));
            File.WriteAllBytes(Path.Combine(root, "Tex", "Body01.PNG"), [1, 2, 3]);

            var exact = MmdGlLayerSurface.ResolveTexturePath(root, Path.Combine("Tex", "Body01.PNG"));
            Assert.NotNull(exact);

            var folded = MmdGlLayerSurface.ResolveTexturePath(root, Path.Combine("tex", "body01.png"));
            Assert.Equal(exact, folded);

            Assert.Null(MmdGlLayerSurface.ResolveTexturePath(root, Path.Combine("tex", "missing.png")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
