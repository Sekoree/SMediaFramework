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
    private const string AssetRoot = "/home/seko/RiderProjects/MFPlayer/Reference/MMDTest";

    private static string? FindPmx() =>
        Directory.Exists(AssetRoot)
            ? Directory.EnumerateFiles(AssetRoot, "*.pmx", SearchOption.AllDirectories).FirstOrDefault()
            : null;

    private static string? FindVmd() =>
        Directory.Exists(AssetRoot)
            ? Directory.EnumerateFiles(AssetRoot, "*.vmd", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains("Model_View_Implementations", StringComparison.Ordinal))
            : null;

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
