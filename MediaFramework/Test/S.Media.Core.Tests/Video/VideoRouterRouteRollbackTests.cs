using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

/// <summary>
/// R1/R2 regression guards (HotPath review 2026-06-10): a failed branch negotiation in
/// <see cref="VideoRouter.TryAddRoute"/> must return false and roll the half-added route back
/// (previously it threw and every later submit on the input failed), and an empty
/// <see cref="IVideoOutput.AcceptedPixelFormats"/> branch must receive the negotiated format
/// pass-through instead of failing fan-out. Plus the H1 submit-plan cache counter guard.
/// </summary>
public sealed class VideoRouterRouteRollbackTests
{
    private const int W = 64, H = 64;
    private static readonly VideoFormat Nv12 = new(W, H, PixelFormat.Nv12, new Rational(60, 1));

    private static VideoRouter NoConvertRouter() => new(null, new VideoRouterOptions(
        VideoCpuFrameConverterFactory: () => throw new InvalidOperationException("no converter in this test"),
        VideoCpuFrameCanConvertProbe: (_, _, _, _) => false));

    [Fact]
    public void TryAddRoute_BranchNegotiationFails_ReturnsFalseRollsBackAndKeepsPresenting()
    {
        using var router = NoConvertRouter();
        var primary = new CountingOutput([PixelFormat.Nv12]);
        var badBranch = new CountingOutput([PixelFormat.Bgra32]); // unconvertible (probe says no)
        var primId = router.AddOutput(primary, "primary", synchronous: true);
        var badId = router.AddOutput(badBranch, "bad", synchronous: true);
        var input = router.AddInput(primId, "in");
        input.Output.Configure(Nv12);

        input.Output.Submit(MakeNv12Frame());
        Assert.Equal(1, primary.SubmitCount);

        // The Try* contract: no throw, false + message, graph rolled back.
        Assert.False(router.TryAddRoute(input.Id, badId, out var error));
        Assert.NotNull(error);
        Assert.Contains("rolled back", error);

        // The input keeps presenting on its previous (valid) configuration.
        input.Output.Submit(MakeNv12Frame());
        Assert.Equal(2, primary.SubmitCount);
        Assert.Equal(0, badBranch.SubmitCount);

        // The rejected output is not left half-routed: it is free to be claimed by a valid route
        // (rollback released ownership), and a later valid branch add still works.
        var goodBranch = new CountingOutput([PixelFormat.Nv12]);
        var goodId = router.AddOutput(goodBranch, "good", synchronous: true);
        Assert.True(router.TryAddRoute(input.Id, goodId, out _));
        input.Output.Submit(MakeNv12Frame());
        Assert.Equal(3, primary.SubmitCount);
        Assert.Equal(1, goodBranch.SubmitCount);
    }

    [Fact]
    public void TryAddRoute_FailedThenRetrySameOutput_StillReturnsFalseWithoutCorruption()
    {
        using var router = NoConvertRouter();
        var primary = new CountingOutput([PixelFormat.Nv12]);
        var badBranch = new CountingOutput([PixelFormat.Bgra32]);
        var primId = router.AddOutput(primary, "primary", synchronous: true);
        var badId = router.AddOutput(badBranch, "bad", synchronous: true);
        var input = router.AddInput(primId, "in");
        input.Output.Configure(Nv12);

        for (var attempt = 0; attempt < 3; attempt++)
            Assert.False(router.TryAddRoute(input.Id, badId, out _));

        input.Output.Submit(MakeNv12Frame());
        Assert.Equal(1, primary.SubmitCount);
    }

    [Fact]
    public void TryAddRoute_EmptyAcceptedBranch_ReceivesNegotiatedFormatPassThrough()
    {
        using var router = NoConvertRouter(); // proves no converter is needed for the permissive branch
        var primary = new CountingOutput([PixelFormat.Nv12]);
        var permissive = new CountingOutput([]); // DiscardingVideoOutput-style "accepts anything"
        var primId = router.AddOutput(primary, "primary", synchronous: true);
        var permId = router.AddOutput(permissive, "permissive", synchronous: true);
        var input = router.AddInput(primId, "in");
        Assert.True(router.TryAddRoute(input.Id, permId, out var error));
        Assert.Null(error);
        input.Output.Configure(Nv12);

        input.Output.Submit(MakeNv12Frame());

        Assert.Equal(1, primary.SubmitCount);
        Assert.Equal(1, permissive.SubmitCount);
        Assert.Equal(PixelFormat.Nv12, permissive.LastFormat);
        Assert.Equal(PixelFormat.Nv12, permissive.Format.PixelFormat);
    }

    [Fact]
    public void SubmitPlan_IsCachedAcrossSubmits_AndRebuiltOnRouteChange()
    {
        using var router = NoConvertRouter();
        var primary = new CountingOutput([PixelFormat.Nv12]);
        var branch = new CountingOutput([PixelFormat.Nv12]);
        var primId = router.AddOutput(primary, "primary", synchronous: true);
        var branchId = router.AddOutput(branch, "branch", synchronous: true);
        var input = router.AddInput(primId, "in");
        Assert.True(router.TryAddRoute(input.Id, branchId, out _));
        input.Output.Configure(Nv12);

        input.Output.Submit(MakeNv12Frame());
        var rebuildsAfterFirst = router.SubmitPlanRebuilds;
        Assert.True(rebuildsAfterFirst >= 1);

        for (var i = 0; i < 25; i++)
            input.Output.Submit(MakeNv12Frame());
        Assert.Equal(rebuildsAfterFirst, router.SubmitPlanRebuilds); // steady state: zero rebuilds

        Assert.True(router.TryRemoveRoute(input.Id, branchId, out _));
        Assert.True(router.TryAddRoute(input.Id, branchId, out _));
        input.Output.Submit(MakeNv12Frame());
        Assert.True(router.SubmitPlanRebuilds > rebuildsAfterFirst);

        Assert.Equal(27, primary.SubmitCount);
    }

    private static VideoFrame MakeNv12Frame() =>
        new(TimeSpan.Zero, Nv12, [new byte[W * H], new byte[W * (H / 2)]], [W, W]);

    private sealed class CountingOutput(PixelFormat[] accepted) : IVideoOutput
    {
        private int _submits;
        private volatile object _lastFormat = PixelFormat.Unknown;
        public int SubmitCount => Volatile.Read(ref _submits);
        public PixelFormat LastFormat => (PixelFormat)_lastFormat;
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = accepted;
        public VideoFormat Format { get; private set; }
        public void Configure(VideoFormat format) => Format = format;
        public void Submit(VideoFrame frame)
        {
            _lastFormat = frame.Format.PixelFormat;
            Interlocked.Increment(ref _submits);
            frame.Dispose();
        }
    }
}
