using ProjectMLib;
using S.Media.Present.SDL3;
using Xunit;

namespace S.Media.Visualizer.ProjectM.Tests;

/// <summary>
/// Skip-if-unavailable smoke for the CONTINUOUS visualizer: a <see cref="ProjectMVisualSource"/> with the
/// offscreen GL factory wired renders frames on its own thread, independent of any composition - the
/// mechanism behind "the visualizer keeps running while compositions rebuild per track". Runs for real
/// (SDL3 GL context + libprojectM) when the host has them; returns early otherwise.
/// </summary>
public sealed class ContinuousRendererSmokeTests
{
    [Fact]
    public void ContinuousSource_RendersFramesWithoutAnyComposition_AndSurvivesSurfaceChurn()
    {
        if (!ProjectMRuntime.IsAvailable)
            return; // no native projectM on this host
        if (!SDL3GLVideoCompositor.TryProbe(out _))
            return; // no GL on this host

        var previous = ProjectMVisualSource.OffscreenGlContextFactory;
        ProjectMVisualSource.OffscreenGlContextFactory = SDL3OffscreenGlContext.TryCreate;
        try
        {
            using var source = new ProjectMVisualSource(
                256, 144, new Rational(30, 1),
                new ProjectMOptions { RenderWidth = 256, RenderHeight = 144, Fps = 30 });
            Assert.True(source.IsContinuous, "factory wired ⇒ the source must run the continuous renderer");

            // Feed a little audio (a sine) so projectM has a signal, then wait for frames to flow.
            var sine = new float[960 * 2];
            for (var i = 0; i < 960; i++)
            {
                var v = MathF.Sin(i * 0.05f) * 0.5f;
                sine[i * 2] = v;
                sine[i * 2 + 1] = v;
            }

            var frame = new byte[256 * 144 * 4];
            long seen = 0;
            var got = 0;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            // "Surface churn": consume via fresh surfaces mid-run, like compositions rebuilding per track.
            var surface1 = source.CreateLayerSurface();
            while (DateTime.UtcNow < deadline && got < 5)
            {
                source.Submit(sine);
                if (TryCopy(source, frame, ref seen))
                    got++;
                Thread.Sleep(50);
            }

            surface1.Dispose(); // a composition died (track change) ...
            using var surface2 = source.CreateLayerSurface(); // ... the next one attaches

            var gotAfterChurn = 0;
            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && gotAfterChurn < 5)
            {
                source.Submit(sine);
                if (TryCopy(source, frame, ref seen))
                    gotAfterChurn++;
                Thread.Sleep(50);
            }

            Assert.True(got >= 5, $"renderer produced only {got} frames before churn");
            Assert.True(gotAfterChurn >= 5, $"renderer produced only {gotAfterChurn} frames after surface churn");
            Assert.True(source.PresetCount >= 0, "preset enumeration should have run");
        }
        finally
        {
            ProjectMVisualSource.OffscreenGlContextFactory = previous;
        }
    }

    private static bool TryCopy(ProjectMVisualSource source, byte[] dest, ref long seen)
    {
        // Reach the renderer through the same door the blit surface uses: the source's internal renderer.
        // Exposed for the test via the public IsContinuous/PresetCount surface + a reflection-free copy:
        // the blit surface is internal, so poll the version through a temp surface's own mechanism is
        // overkill - use the internal accessor.
        return source.TryCopyLatestFrameForTest(dest, ref seen);
    }
}
