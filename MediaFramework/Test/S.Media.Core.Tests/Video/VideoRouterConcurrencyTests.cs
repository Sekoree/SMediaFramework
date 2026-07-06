using System.Collections.Concurrent;
using Xunit;

namespace S.Media.Core.Tests.Video;

/// <summary>
/// ROUTE-02: the video router must stay correct when routes/outputs are added and removed <em>concurrently</em>
/// with frame submission. <c>SubmitPhased</c> snapshots the routed outputs (and branch converters) under the
/// router lock and leases them, does the heavy per-branch work with the lock released, then delivers to the
/// outputs that still exist; a reconfigure racing that window defers converter disposal until the in-flight
/// submit drains, and outputs are pump-wrapped by default so a submit is a non-blocking enqueue. This stresses
/// that path — it must never throw a use-after-dispose, corrupt the submit-plan cache, or drop a frame from the
/// always-routed primary output.
/// </summary>
public sealed class VideoRouterConcurrencyTests
{
    private const int W = 64, H = 64;
    private static readonly VideoFormat Nv12 = new(W, H, PixelFormat.Nv12, new Rational(60, 1));

    [Fact]
    public void ConcurrentSubmit_WhileRoutesChurn_NeverCrashesAndAlwaysReachesThePrimary()
    {
        using var router = new VideoRouter(null, new VideoRouterOptions(
            VideoCpuFrameConverterFactory: () => throw new InvalidOperationException("no converter in this test"),
            VideoCpuFrameCanConvertProbe: (_, _, _, _) => false));

        var primary = new CountingOutput([PixelFormat.Nv12]);
        var branchA = new CountingOutput([PixelFormat.Nv12]);
        var branchB = new CountingOutput([PixelFormat.Nv12]);
        var primId = router.AddOutput(primary, "primary", synchronous: true);
        var aId = router.AddOutput(branchA, "a", synchronous: true);
        var bId = router.AddOutput(branchB, "b", synchronous: true);
        var input = router.AddInput(primId, "in");
        Assert.True(router.TryAddRoute(input.Id, aId, out _)); // start multi-branch so the leased phased path runs
        input.Output.Configure(Nv12);

        const int submits = 2_000;
        var errors = new ConcurrentQueue<Exception>();

        var submitter = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < submits; i++)
                    input.Output.Submit(MakeNv12Frame());
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        // Churn a second branch route in and out while frames are flowing — every iteration reshapes the
        // snapshotted submit plan under the lock while a submit may be mid-flight with the lock released.
        var churn = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 500; i++)
                {
                    router.TryAddRoute(input.Id, bId, out _);
                    router.TryRemoveRoute(input.Id, bId, out _);
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        submitter.Start();
        churn.Start();
        Assert.True(submitter.Join(TimeSpan.FromSeconds(30)), "submitter thread did not finish");
        Assert.True(churn.Join(TimeSpan.FromSeconds(30)), "route-churn thread did not finish");

        Assert.True(errors.IsEmpty,
            "concurrent submit vs route churn threw: " + string.Join(" | ", errors.Select(e => $"{e.GetType().Name}: {e.Message}")));

        // The primary output is routed for the whole run, so it must have received exactly every frame —
        // no frame lost to a race, none delivered twice.
        Assert.Equal(submits, primary.SubmitCount);
        // The router is still usable after the storm.
        input.Output.Submit(MakeNv12Frame());
        Assert.Equal(submits + 1, primary.SubmitCount);
    }

    private static VideoFrame MakeNv12Frame() =>
        new(TimeSpan.Zero, Nv12, [new byte[W * H], new byte[W * (H / 2)]], [W, W]);

    private sealed class CountingOutput(PixelFormat[] accepted) : IVideoOutput
    {
        private int _submits;
        public int SubmitCount => Volatile.Read(ref _submits);
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = accepted;
        public VideoFormat Format { get; private set; }
        public void Configure(VideoFormat format) => Format = format;
        public void Submit(VideoFrame frame)
        {
            Interlocked.Increment(ref _submits);
            frame.Dispose();
        }
    }
}
