using System.Diagnostics;
using Xunit;

namespace S.Media.Source.MMD.Tests;

/// <summary>
/// Gated tests against the LOCAL reference assets (Reference/MMDTest — YYB Miku + Rolling Girl). Their
/// licenses forbid redistribution, so these skip anywhere the assets aren't present (CI) and exist to
/// prove the parsers/animator handle a real production model+motion, not just the tiny fixtures.
/// </summary>
public sealed class MmdRealAssetTests
{
    // Resolved from the repo checkout (walk up to the repo root, then the non-redistributable local
    // asset folder) so the test runs on any dev box that has the assets, not one hardcoded home.
    private static readonly string AssetRoot = FindAssetRoot();

    private static string FindAssetRoot()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "Reference", "MMDTest")))
                return Path.Combine(dir, "Reference", "MMDTest");
        return "/nonexistent/Reference/MMDTest";
    }

    private static string? FindPmx()
    {
        var root = Path.Combine(AssetRoot, "Model_YYB-Miku-ver1.02");
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.pmx", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;
    }

    private static string? FindVmd()
    {
        var root = Path.Combine(AssetRoot, "Motion_Rolling Girl ");
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.vmd", SearchOption.TopDirectoryOnly).FirstOrDefault()
            : null;
    }

    private sealed class LocalAssetFactAttribute : FactAttribute
    {
        public LocalAssetFactAttribute()
        {
            if (FindPmx() is null || FindVmd() is null)
                Skip = "local MMD reference assets not present (non-redistributable — dev box only)";
        }
    }

    [LocalAssetFact]
    public void RealModelAndMotion_ParseAnimateAndRender()
    {
        var model = PmxDocument.Load(FindPmx()!);
        Assert.True(model.Vertices.Count > 1000, $"real model should be dense (got {model.Vertices.Count})");
        Assert.True(model.Bones.Count > 50, $"real model should have a full skeleton (got {model.Bones.Count})");
        Assert.True(model.Materials.Count > 1);
        Assert.Equal(model.Indices.Count, model.Materials.Sum(m => m.FaceVertexCount));

        var motion = VmdDocument.Load(FindVmd()!);
        Assert.True(motion.BoneTracks.Count > 10, $"real motion should drive many bones (got {motion.BoneTracks.Count})");
        Assert.True(motion.Duration > TimeSpan.FromSeconds(30), $"Rolling Girl should be minutes long (got {motion.Duration})");

        var animator = new MmdAnimator(model, motion);
        var rest = new Vector3[model.Vertices.Count];
        var posed = new Vector3[model.Vertices.Count];
        animator.Evaluate(TimeSpan.Zero, rest);
        animator.Evaluate(TimeSpan.FromSeconds(20), posed);
        var moved = 0;
        for (var i = 0; i < rest.Length; i++)
            if (Vector3.DistanceSquared(rest[i], posed[i]) > 0.01f)
                moved++;
        Assert.True(moved > rest.Length / 10, $"20s into the dance most vertices should have moved (moved={moved}/{rest.Length})");

        // Stage-5 physics over the full 136-body/125-joint chain: simulate ~2s of the dance and require
        // (a) every vertex stays finite/bounded (no explosion) and (b) physics actually diverges from the
        // rigid FK pose (the hair moved on its own).
        Assert.True(model.RigidBodies.Count > 50, $"YYB should carry a full physics rig (got {model.RigidBodies.Count})");
        var physics = MmdPhysics.TryCreate(model);
        Assert.NotNull(physics);
        var withPhysics = new Vector3[model.Vertices.Count];
        var physicsAnimator = new MmdAnimator(model, motion);
        for (var f = 0; f <= 60; f++)
            physicsAnimator.Evaluate(TimeSpan.FromSeconds(18) + TimeSpan.FromSeconds(f / 30.0), withPhysics,
                normals: null, physics, physicsDeltaSeconds: f == 0 ? -1f : 1f / 30f);
        animator.Evaluate(TimeSpan.FromSeconds(20), posed); // rigid reference at the same instant
        var diverged = 0;
        for (var i = 0; i < withPhysics.Length; i++)
        {
            Assert.True(float.IsFinite(withPhysics[i].X) && float.IsFinite(withPhysics[i].Y) && float.IsFinite(withPhysics[i].Z),
                $"vertex {i} exploded under physics");
            Assert.True(withPhysics[i].Length() < 500f, $"vertex {i} flew away ({withPhysics[i]})");
            if (Vector3.DistanceSquared(withPhysics[i], posed[i]) > 0.05f)
                diverged++;
        }
        Assert.True(diverged > 100, $"physics changed almost nothing ({diverged} vertices diverged from FK)");

        // Render one frame through the video source; the model must land on screen with the default framing.
        var sw = Stopwatch.StartNew();
        var uri = MmdSourceUri.Build(new MmdSourceRequest(
            FindPmx()!, FindVmd()!, null, 320, 180,
            CameraDistance: null, CameraTarget: null, CameraRotationDegrees: null, CameraFovDegrees: null));
        using var source = (MmdVideoSource)new MmdDecoderProvider().OpenVideo(uri, options: null);
        source.Seek(TimeSpan.FromSeconds(20));
        Assert.True(source.TryReadNextFrame(out var frame));
        using (frame)
        {
            var plane = frame.Planes[0].Span;
            var lit = 0;
            for (var i = 0; i < plane.Length; i += 4)
                if (plane[i] > 8 || plane[i + 1] > 8 || plane[i + 2] > 8)
                    lit++;
            Assert.True(lit > 500, $"the posed model should be visible (lit={lit})");
        }

        // Not a gate, just visibility: a preview-resolution frame should render in interactive time.
        Assert.True(sw.ElapsedMilliseconds < 30_000, $"render took {sw.ElapsedMilliseconds} ms");
    }
}
