using S.Media.Core.Video;
using S.Media.Effects;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class VideoCompositorSourceTests
{
    private static readonly VideoFormat Bgra32_4x4 = new(4, 4, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public void TwoSlots_RoundTripsThroughCompositor()
    {
        using var compositor = new CpuVideoCompositor(Bgra32_4x4);
        using var output = new VideoCompositorSource(Bgra32_4x4, compositor, disposeCompositorOnDispose: false);
        var slotA = output.AddSlot("A");
        var slotB = output.AddSlot("B");

        Assert.Equal(2, output.Slots.Count);

        slotA.Output.Configure(Bgra32_4x4);
        slotB.Output.Configure(Bgra32_4x4);
        slotA.Output.Submit(MakeFrame(0, 0, 255, 255)); // red
        slotB.Opacity = 0.5f;
        slotB.Output.Submit(MakeFrame(255, 0, 0, 255)); // blue at half opacity

        Assert.True(output.TryReadNextFrame(out var composite));
        try
        {
            var span = composite.Planes[0].Span;
            // SourceOver (slotB is default): blue * 0.5 over red.
            // src.rgb*opacity = (127,0,0). (1 - 0.5)=0.5. dst*0.5 = (0,0,127.5).
            // sum: (127, 0, 127).
            Assert.InRange(span[0], 126, 130);
            Assert.Equal(0, span[1]);
            Assert.InRange(span[2], 126, 130);
            Assert.True(composite.PresentationTime >= TimeSpan.Zero);
        }
        finally { composite.Dispose(); }
        Assert.Equal(1, output.CompositesEmitted);
    }

    [Fact]
    public void LatestWins_DropsOldFrameAndCountsOverflow()
    {
        using var compositor = new CpuVideoCompositor(Bgra32_4x4);
        using var output = new VideoCompositorSource(Bgra32_4x4, compositor, disposeCompositorOnDispose: false);
        var slot = output.AddSlot();
        slot.Output.Configure(Bgra32_4x4);

        // Submit twice without reading — second submit replaces first.
        slot.Output.Submit(MakeFrame(255, 0, 0, 255)); // blue
        slot.Output.Submit(MakeFrame(0, 0, 255, 255)); // red (should be the one composited)
        Assert.Equal(1, slot.OverflowFrames);

        Assert.True(output.TryReadNextFrame(out var composite));
        try
        {
            var span = composite.Planes[0].Span;
            Assert.Equal(0, span[0]);
            Assert.Equal(0, span[1]);
            Assert.Equal(255, span[2]);
        }
        finally { composite.Dispose(); }
    }

    [Fact]
    public void Slot_HoldsLastFrameAcrossCompositeReads()
    {
        using var compositor = new CpuVideoCompositor(Bgra32_4x4);
        using var output = new VideoCompositorSource(Bgra32_4x4, compositor, disposeCompositorOnDispose: false);
        var slot = output.AddSlot();
        slot.Output.Configure(Bgra32_4x4);
        slot.Output.Submit(MakeFrame(0, 0, 255, 255)); // red

        Assert.True(output.TryReadNextFrame(out var first));
        first.Dispose();

        Assert.True(output.TryReadNextFrame(out var second));
        try
        {
            var span = second.Planes[0].Span;
            Assert.Equal(0, span[0]);
            Assert.Equal(0, span[1]);
            Assert.Equal(255, span[2]);
            Assert.Equal(255, span[3]);
        }
        finally { second.Dispose(); }
    }

    [Fact]
    public void SortSlots_ReordersBackToFrontComposition()
    {
        using var compositor = new CpuVideoCompositor(Bgra32_4x4);
        using var output = new VideoCompositorSource(Bgra32_4x4, compositor, disposeCompositorOnDispose: false);
        var slotA = output.AddSlot("A");
        var slotB = output.AddSlot("B");
        slotA.Output.Configure(Bgra32_4x4);
        slotB.Output.Configure(Bgra32_4x4);
        slotA.Output.Submit(MakeFrame(0, 0, 255, 255)); // red
        slotB.Output.Submit(MakeFrame(255, 0, 0, 255)); // blue

        output.SortSlots(static (a, b) => string.CompareOrdinal(b.Id, a.Id));

        Assert.True(output.TryReadNextFrame(out var composite));
        try
        {
            var span = composite.Planes[0].Span;
            Assert.Equal(0, span[0]);
            Assert.Equal(0, span[1]);
            Assert.Equal(255, span[2]);
        }
        finally { composite.Dispose(); }
    }

    [Fact]
    public void EmptySlot_ContributesTransparency()
    {
        using var compositor = new CpuVideoCompositor(Bgra32_4x4);
        using var output = new VideoCompositorSource(Bgra32_4x4, compositor, disposeCompositorOnDispose: false);
        output.AddSlot();
        output.AddSlot();
        // No submits — every slot is empty.
        Assert.True(output.TryReadNextFrame(out var composite));
        try
        {
            var span = composite.Planes[0].Span;
            for (var i = 0; i < span.Length; i++)
                Assert.Equal(0, span[i]);
        }
        finally { composite.Dispose(); }
    }

    [Fact]
    public void PtsAdvancesPerRead()
    {
        using var compositor = new CpuVideoCompositor(Bgra32_4x4);
        using var output = new VideoCompositorSource(Bgra32_4x4, compositor, disposeCompositorOnDispose: false);
        output.AddSlot();

        Assert.True(output.TryReadNextFrame(out var f1));
        var p1 = f1.PresentationTime;
        f1.Dispose();
        Assert.True(output.TryReadNextFrame(out var f2));
        var p2 = f2.PresentationTime;
        f2.Dispose();
        Assert.True(p2 > p1, $"second PTS {p2} should exceed first {p1}");
    }

    [Fact]
    public void Slot_RejectsUnacceptedPixelFormat()
    {
        using var compositor = new CpuVideoCompositor(Bgra32_4x4);
        using var output = new VideoCompositorSource(Bgra32_4x4, compositor, disposeCompositorOnDispose: false);
        var slot = output.AddSlot();
        var nv12Format = new VideoFormat(4, 4, PixelFormat.Nv12, new Rational(30, 1));
        Assert.Throws<ArgumentException>(() => slot.Output.Configure(nv12Format));
    }

    [Fact]
    public void Dispose_DisposesHeldFramesAndCompositor()
    {
        var compositor = new TrackingCompositor();
        var output = new VideoCompositorSource(compositor.OutputFormat, compositor, disposeCompositorOnDispose: true);
        var slot = output.AddSlot();
        slot.Output.Configure(compositor.OutputFormat);
        var disposed = false;
        var frame = new VideoFrame(TimeSpan.Zero, compositor.OutputFormat,
            new byte[4 * 4 * 4], 4 * 4, release: DisposableRelease.Wrap(() => disposed = true));
        slot.Output.Submit(frame);

        output.Dispose();
        Assert.True(disposed, "held frame should have been disposed");
        Assert.True(compositor.IsDisposed);
    }

    [Fact]
    public void MasterAligned_PicksFrameClosestToMasterPts()
    {
        using var compositor = new CpuVideoCompositor(Bgra32_4x4);
        using var output = new VideoCompositorSource(Bgra32_4x4, compositor, disposeCompositorOnDispose: false);
        var slot = output.AddSlot();
        slot.KeepPolicy = SlotKeepPolicy.MasterAligned;
        slot.Output.Configure(Bgra32_4x4);

        slot.Output.Submit(MakeFrame(0, 0, 255, 255, TimeSpan.Zero)); // red @ 0

        Assert.True(output.TryReadNextFrame(TimeSpan.FromMilliseconds(5), out var atZero));
        try
        {
            Assert.Equal(255, atZero.Planes[0].Span[2]);
        }
        finally { atZero.Dispose(); }

        slot.Output.Submit(MakeFrame(255, 0, 0, 255, TimeSpan.FromMilliseconds(33))); // blue @ 33ms

        Assert.True(output.TryReadNextFrame(TimeSpan.FromMilliseconds(34), out var atMid));
        try
        {
            Assert.Equal(255, atMid.Planes[0].Span[0]);
        }
        finally { atMid.Dispose(); }
    }

    [Fact]
    public void VideoCompositorAuto_UsesRegisteredBackend()
    {
        TrackingCompositor? used = null;
        using var registration = VideoCompositor.RegisterAutoBackend((VideoFormat output, out IVideoCompositor? compositor, out string? error) =>
        {
            used = new TrackingCompositor(output);
            compositor = used;
            error = null;
            return true;
        });

        using var videoCompositor = VideoCompositor.Create(Bgra32_4x4, VideoCompositorBackend.Auto);

        Assert.NotNull(used);
        Assert.True(videoCompositor.TryReadNextFrame(out var frame));
        frame.Dispose();
        Assert.Equal(1, used.CompositeCalls);
    }

    [Fact]
    public void SteadyStateCompositeRead_AllocationDoesNotScaleWithSlotCount()
    {
        // P2-5: TryReadNextFrame reuses scratch (slot snapshot + layer list) and no longer allocates a
        // per-slot lease object, so a pure composite read (held frames, no new submits) allocates a
        // slot-count-INDEPENDENT amount — just the compositor's fixed output frame. TrackingCompositor
        // allocates a constant buffer per Composite regardless of layer count, so any growth in
        // per-read allocation between 1 and 8 slots would be the per-slot leases / per-call lists
        // regressing back in.
        static long PerReadBytes(int slotCount)
        {
            var compositor = new TrackingCompositor(Bgra32_4x4);
            using var output = new VideoCompositorSource(Bgra32_4x4, compositor, disposeCompositorOnDispose: true);
            for (var i = 0; i < slotCount; i++)
            {
                var slot = output.AddSlot();
                slot.Output.Configure(Bgra32_4x4);
                slot.Output.Submit(MakeFrame(0, 0, 255, 255));
            }
            // Warm up: JIT, first-frame promotion into each slot, scratch-list capacity growth.
            for (var i = 0; i < 300; i++) { output.TryReadNextFrame(out var f); f.Dispose(); }

            const int iters = 1000;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iters; i++) { output.TryReadNextFrame(out var f); f.Dispose(); }
            var after = GC.GetAllocatedBytesForCurrentThread();
            return (after - before) / iters;
        }

        var perRead1 = PerReadBytes(1);
        var perRead8 = PerReadBytes(8);

        // Old path allocated ~7 extra lease objects + a larger Slot[] snapshot + List backing per read
        // for the 8-slot case; the new path reuses all of it. Allow slack for measurement noise.
        Assert.True(perRead8 - perRead1 < 200,
            $"per-read allocation scales with slot count (P2-5 regression): 1-slot={perRead1}B, 8-slot={perRead8}B");
    }

    [Fact]
    public async Task VideoCompositor_AddRemoveLayerWhileAdvanceToIsReadingSource_DoesNotCorruptLayerList()
    {
        using var blockedSource = new BlockingVideoSource(Bgra32_4x4);
        using var addedSource = StaticFrameSource.FromFrame(MakeFrame(0, 255, 0, 255));
        using var program = VideoCompositor.Create(Bgra32_4x4, VideoCompositorBackend.Cpu);
        program.Clock = new FixedPlaybackClock(TimeSpan.Zero);
        var blockedLayer = program.AddLayer(blockedSource, LayerConfig.Background);

        var readTask = Task.Run(() =>
        {
            var ok = program.TryReadNextFrame(out var frame);
            frame?.Dispose();
            return ok;
        });

        Assert.True(blockedSource.Entered.Wait(TimeSpan.FromSeconds(2)), "read should be blocked inside layer source");

        var addedLayer = program.AddLayer(addedSource, LayerConfig.Background);
        var removeTask = Task.Run(() => program.RemoveLayer(blockedLayer));

        blockedSource.Release();

        Assert.True(await CompleteWithin(readTask, TimeSpan.FromSeconds(2)), "read should complete after source release");
        Assert.True(await readTask);
        Assert.True(await CompleteWithin(removeTask, TimeSpan.FromSeconds(2)), "remove should complete after source release");
        Assert.True(await removeTask);
        Assert.Equal([addedLayer], program.Layers);
    }

    [Fact]
    public async Task DisposeWhileTryReadNextFrameIsInCompositor_WaitsAndDisposesHeldFrame()
    {
        var compositor = new BlockingCompositor(Bgra32_4x4);
        using var output = new VideoCompositorSource(Bgra32_4x4, compositor, disposeCompositorOnDispose: true);
        var slot = output.AddSlot();
        slot.Output.Configure(Bgra32_4x4);
        var heldReleased = 0;
        slot.Output.Submit(MakeFrame(0, 0, 255, 255, release: DisposableRelease.Wrap(() => Interlocked.Increment(ref heldReleased))));

        var readTask = Task.Run(() =>
        {
            var ok = output.TryReadNextFrame(out var frame);
            frame?.Dispose();
            return ok;
        });

        Assert.True(compositor.Entered.Wait(TimeSpan.FromSeconds(2)), "read should be blocked inside compositor");

        var disposeTask = Task.Run(output.Dispose);
        await Task.Delay(50);
        Assert.False(disposeTask.IsCompleted, "Dispose should wait for the active read to release slot frames");

        compositor.Release();

        Assert.True(await CompleteWithin(readTask, TimeSpan.FromSeconds(2)), "read should complete after compositor release");
        Assert.True(await readTask);
        Assert.True(await CompleteWithin(disposeTask, TimeSpan.FromSeconds(2)), "dispose should finish after read exits");
        await disposeTask;
        Assert.Equal(1, Volatile.Read(ref heldReleased));
        Assert.True(compositor.IsDisposed);
    }

    private static async Task<bool> CompleteWithin(Task task, TimeSpan timeout)
        => await Task.WhenAny(task, Task.Delay(timeout)) == task;

    private static VideoFrame MakeFrame(byte b, byte g, byte r, byte a, TimeSpan pts = default, IDisposable? release = null)
    {
        var buf = new byte[4 * 4 * 4];
        for (var i = 0; i < 4 * 4; i++)
        {
            buf[i * 4 + 0] = b;
            buf[i * 4 + 1] = g;
            buf[i * 4 + 2] = r;
            buf[i * 4 + 3] = a;
        }
        return new VideoFrame(pts, Bgra32_4x4, buf, 4 * 4, release: release);
    }

    private sealed class TrackingCompositor : IVideoCompositor
    {
        public TrackingCompositor()
            : this(new VideoFormat(4, 4, PixelFormat.Bgra32, new Rational(30, 1)))
        {
        }

        public TrackingCompositor(VideoFormat outputFormat)
        {
            OutputFormat = outputFormat;
        }

        public bool IsDisposed { get; private set; }
        public int CompositeCalls { get; private set; }
        public VideoFormat OutputFormat { get; }
        public IReadOnlyList<PixelFormat> AcceptedLayerPixelFormats { get; } = new[] { PixelFormat.Bgra32 };
        public void Configure(VideoFormat output) { }
        public VideoFrame Composite(IReadOnlyList<CompositorLayer> layers, TimeSpan pts)
        {
            CompositeCalls++;
            return new VideoFrame(pts, OutputFormat, new byte[OutputFormat.Width * OutputFormat.Height * 4],
                OutputFormat.Width * 4, release: null);
        }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class BlockingCompositor(VideoFormat outputFormat) : IVideoCompositor
    {
        private readonly ManualResetEventSlim _release = new(false);
        public ManualResetEventSlim Entered { get; } = new(false);
        public bool IsDisposed { get; private set; }
        public VideoFormat OutputFormat { get; } = outputFormat;
        public IReadOnlyList<PixelFormat> AcceptedLayerPixelFormats { get; } = new[] { PixelFormat.Bgra32 };
        public void Configure(VideoFormat output) { }

        public VideoFrame Composite(IReadOnlyList<CompositorLayer> layers, TimeSpan pts)
        {
            Entered.Set();
            _release.Wait();
            return new VideoFrame(pts, OutputFormat, new byte[OutputFormat.Width * OutputFormat.Height * 4],
                OutputFormat.Width * 4, release: null);
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            Release();
            IsDisposed = true;
        }
    }

    private sealed class FixedPlaybackClock(TimeSpan elapsed) : S.Media.Core.Clock.IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart { get; } = elapsed;
        public bool IsAdvancing => true;
    }

    private sealed class BlockingVideoSource(VideoFormat format) : IVideoSource, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _disposed;
        public ManualResetEventSlim Entered { get; } = new(false);
        public VideoFormat Format { get; } = format;
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = new[] { PixelFormat.Bgra32 };
        public bool IsExhausted => Volatile.Read(ref _disposed) != 0;
        public void SelectOutputFormat(PixelFormat format) { }

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            Entered.Set();
            _release.Wait();
            if (IsExhausted)
            {
                frame = null!;
                return false;
            }

            frame = MakeFrame(0, 0, 255, 255);
            return true;
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            Release();
        }
    }
}
