using System.Text;
using Xunit;

namespace S.Media.Source.MMD.Tests;

/// <summary>
/// Offline coverage on PURPOSE-MADE byte-level fixtures (the review forbids treating the bundled
/// model/motion assets as redistributable test fixtures): a tiny 2-bone / 1-triangle PMX and a
/// matching VMD are synthesized in-memory, then parsed, animated, and rendered.
/// </summary>
public sealed class MMDModuleTests
{
    [Fact]
    public void BulletResolverCandidates_SystemNamesPrecedeStagedApplicationShim()
    {
        var candidates = MMDBulletLibraryResolver.GetCandidates().ToArray();
        var systemIndex = Array.FindIndex(candidates, candidate => !Path.IsPathRooted(candidate));
        var appIndex = Array.FindIndex(candidates, candidate =>
            candidate.StartsWith(AppContext.BaseDirectory, StringComparison.Ordinal));

        Assert.True(systemIndex >= 0);
        Assert.True(appIndex > systemIndex);
    }

    // ---- synthetic fixtures ------------------------------------------------------------------------

    /// <summary>Tiny PMX 2.0: 3 vertices (BDEF1 on bone 1), 1 triangle, 1 red material, bones root+child.</summary>
    private static byte[] TinyPMX()
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

    /// <summary>Tiny VMD: bone "b1" translates 0→(5,0,0) over 30 frames, plus one key for
    /// every scene-level VMD track.</summary>
    private static byte[] TinyVMD()
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
        void BoneFrame(uint frame, float x, bool physicsEnabled)
        {
            Fixed("b1", 15);
            w.Write(frame);
            w.Write(x); w.Write(0f); w.Write(0f);              // translation
            w.Write(0f); w.Write(0f); w.Write(0f); w.Write(1f); // identity quaternion
            var interpolation = Enumerable.Repeat((byte)64, 64).ToArray();
            interpolation[2] = physicsEnabled ? (byte)0 : (byte)0x63;
            interpolation[3] = physicsEnabled ? (byte)0 : (byte)0x0f;
            w.Write(interpolation);                            // linear-ish interpolation + physics toggle
        }

        BoneFrame(0, 0f, physicsEnabled: true);
        BoneFrame(30, 5f, physicsEnabled: false);

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

        w.Write(1u);          // light frames
        w.Write(12u);
        w.Write(0.8f); w.Write(0.7f); w.Write(0.6f);
        w.Write(-0.2f); w.Write(-1f); w.Write(0.4f);

        w.Write(1u);          // self-shadow frames
        w.Write(13u);
        w.Write((byte)1);
        w.Write((10000f - 42f) / 100000f); // packed VMD distance

        w.Write(1u);          // show/IK frames
        w.Write(14u);
        w.Write((byte)1);     // visible
        w.Write(1u);          // one IK state
        Fixed("leg IK", 20);
        w.Write((byte)0);
        return ms.ToArray();
    }

    // ---- parsing -----------------------------------------------------------------------------------

    [Fact]
    public void PMX_TinyFixture_ParsesEveryEvaluatedSection()
    {
        var model = PMXDocument.Load(new MemoryStream(TinyPMX()));
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
    public void PMX_Truncated_ThrowsFormatError_NeverCrashes()
    {
        var bytes = TinyPMX();
        for (var cut = 4; cut < bytes.Length; cut += 13)
        {
            var slice = bytes.AsSpan(0, cut).ToArray();
            Assert.ThrowsAny<Exception>(() => PMXDocument.Load(new MemoryStream(slice)));
        }
    }

    [Fact]
    public void VMD_TinyFixture_ParsesBoneAndCameraTracks()
    {
        var motion = VMDDocument.Load(new MemoryStream(TinyVMD()));
        Assert.Equal("tiny", motion.ModelName);
        var track = motion.BoneTracks["b1"];
        Assert.Equal(2, track.Count);
        Assert.True(track[0].PhysicsEnabled);
        Assert.False(track[1].PhysicsEnabled);
        Assert.Equal(30u, motion.LastFrame);
        Assert.Equal(TimeSpan.FromSeconds(1), motion.Duration);
        var camera = Assert.Single(motion.CameraTrack);
        Assert.Equal(-20f, camera.Distance);
        Assert.True(camera.Perspective);
        var light = Assert.Single(motion.LightTrack);
        Assert.Equal(12u, light.Frame);
        Assert.Equal(new Vector3(0.8f, 0.7f, 0.6f), light.Color);
        var shadow = Assert.Single(motion.SelfShadowTrack);
        Assert.Equal((byte)1, shadow.Mode);
        Assert.Equal(42f, shadow.Distance);
        Assert.True(Assert.Single(motion.VisibilityTrack).Visible);
        Assert.False(Assert.Single(motion.IkEnableTracks["leg IK"]).Enabled);
    }

    // ---- animation ---------------------------------------------------------------------------------

    [Fact]
    public void Animator_SamplesBoneTranslation_AcrossTheTimeline()
    {
        var model = PMXDocument.Load(new MemoryStream(TinyPMX()));
        var motion = VMDDocument.Load(new MemoryStream(TinyVMD()));
        var animator = new MMDAnimator(model, motion);
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
        Assert.Equal(0f, MMDAnimator.Bezier(20, 20, 107, 107, 0f), precision: 3);
        Assert.Equal(1f, MMDAnimator.Bezier(20, 20, 107, 107, 1f), precision: 3);
        var mid = MMDAnimator.Bezier(20, 20, 107, 107, 0.5f);
        Assert.InRange(mid, 0.4f, 0.6f);
        // A strong ease-in curve stays below linear early on.
        Assert.True(MMDAnimator.Bezier(100, 10, 120, 30, 0.3f) < 0.3f);
    }

    [Fact]
    public void Animator_UsesDestinationKeyBezier_ForBoneAndCameraSegments()
    {
        static VMDBoneFrame BoneKey(uint frame, float x, byte x1, byte y1, byte x2, byte y2) =>
            new(frame, new Vector3(x, 0, 0), Quaternion.Identity,
                x1, y1, x2, y2,
                20, 20, 107, 107, 20, 20, 107, 107, 20, 20, 107, 107);

        var destinationCameraInterpolation = Enumerable.Repeat((byte)64, 24).ToArray();
        destinationCameraInterpolation[0] = 100; // x1,x2,y1,y2 packed per camera channel
        destinationCameraInterpolation[1] = 120;
        destinationCameraInterpolation[2] = 10;
        destinationCameraInterpolation[3] = 30;
        var motion = new VMDDocument
        {
            ModelName = "destination-curve",
            BoneTracks = new Dictionary<string, IReadOnlyList<VMDBoneFrame>>(StringComparer.Ordinal)
            {
                ["b1"] =
                [
                    BoneKey(0, 0, 20, 20, 107, 107),
                    BoneKey(30, 10, 100, 10, 120, 30),
                ],
            },
            MorphTracks = new Dictionary<string, IReadOnlyList<VMDMorphFrame>>(StringComparer.Ordinal),
            CameraTrack =
            [
                new VMDCameraFrame(0, -20, Vector3.Zero, Vector3.Zero, 30, true),
                new VMDCameraFrame(30, -20, new Vector3(10, 0, 0), Vector3.Zero, 30, true)
                {
                    Interpolation = destinationCameraInterpolation,
                },
            ],
        };

        var animator = new MMDAnimator(PMXDocument.Load(new MemoryStream(TinyPMX())), motion);
        var positions = new Vector3[3];
        animator.Evaluate(TimeSpan.FromSeconds(0.3), positions); // frame 9 / t=.3
        Assert.True(positions[0].X < 2f,
            $"destination ease-in was not used for bone X: {positions[0].X:F3}");

        var camera = MMDAnimator.SampleCamera(motion, TimeSpan.FromSeconds(0.3));
        Assert.True(camera.Target.X < 2f,
            $"destination ease-in was not used for camera X: {camera.Target.X:F3}");
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
            File.WriteAllBytes(pmx, TinyPMX());
            File.WriteAllBytes(vmd, TinyVMD());

            // Explicit camera: the DEFAULT framing targets humanoid-model height (y≈12) and would miss
            // this 2-unit fixture triangle entirely.
            var uri = MMDSourceUri.Build(new MMDSourceRequest(
                pmx, vmd, null, 96, 64,
                CameraDistance: -8f, CameraTarget: new Vector3(0, 1, 0),
                CameraRotationDegrees: Vector3.Zero, CameraFovDegrees: 30f));
            Assert.True(MMDSourceUri.TryParse(uri, out var parsed));
            Assert.Equal(pmx, parsed!.ModelPath);

            var provider = new MMDDecoderProvider();
            Assert.Equal(1.0, provider.Probe(uri, MediaKind.Video));
            Assert.Equal(0.0, provider.Probe("/tmp/file.mp4", MediaKind.Video));

            using var source = (MMDVideoSource)provider.OpenVideo(uri, options: null);
            Assert.Equal(TimeSpan.FromSeconds(1), source.Duration);

            Assert.True(source.TryReadNextFrame(out var frame));
            using (frame)
            {
                Assert.Equal(96, frame.Format.Width);
                var plane = frame.Planes[0].Span;
                var lit = 0;
                for (var i = 0; i < plane.Length; i += 4)
                    if (plane[i + 2] > 16) // red channel - the material is red
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
            File.WriteAllBytes(pmx, TinyPMX());

            byte[] RenderWith(float distance)
            {
                var uri = MMDSourceUri.Build(new MMDSourceRequest(
                    pmx, null, null, 96, 64,
                    CameraDistance: distance, CameraTarget: new Vector3(0, 1, 0),
                    CameraRotationDegrees: Vector3.Zero, CameraFovDegrees: 30));
                using var source = (MMDVideoSource)new MMDDecoderProvider().OpenVideo(uri, options: null);
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
        // disk there - on Linux the exact path misses and the material used to silently render white
        // (the 2026-07-03 "black and white model" report).
        var root = Directory.CreateTempSubdirectory("mmd-tex-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Tex"));
            File.WriteAllBytes(Path.Combine(root, "Tex", "Body01.PNG"), [1, 2, 3]);

            var exact = MMDGlLayerSurface.ResolveTexturePath(root, Path.Combine("Tex", "Body01.PNG"));
            Assert.NotNull(exact);

            var folded = MMDGlLayerSurface.ResolveTexturePath(root, Path.Combine("tex", "body01.png"));
            // The regression guard: a miscased path MUST still resolve (on Linux it used to miss → white).
            Assert.NotNull(folded);
            // Compare modulo case. On a case-SENSITIVE FS (Linux CI) the resolver folds the miscased
            // path to the real on-disk casing, so this is an exact match. On a case-INSENSITIVE FS
            // (Windows CI, default macOS) the miscased path opens the file directly, so the resolver
            // returns it verbatim - same file, different casing - which an ordinal Equal would wrongly reject.
            Assert.Equal(exact, folded, StringComparer.OrdinalIgnoreCase);

            Assert.Null(MMDGlLayerSurface.ResolveTexturePath(root, Path.Combine("tex", "missing.png")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
