using S.Media.Core;
using S.Media.Core.Video;
using S.Media.Effects;
using Xunit;

namespace S.Media.Core.Tests.Video;

/// <summary>
/// M1 regression: once a <see cref="LayerHandle"/> is closed (its layer removed / the compositor
/// disposed), <see cref="LayerHandle.AdvanceTo"/> and <see cref="LayerHandle.PullOneAndSubmit"/> must not
/// pull any more source frames. A composite iterating a pre-removal layer snapshot used to re-populate the
/// look-ahead after <c>Close</c> had already cleared it, leaking those native-backed frames.
/// </summary>
public sealed class LayerHandleClosedGuardTests
{
    private static readonly VideoFormat Bgra32_2x2 = new(2, 2, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public void AdvanceTo_AfterClose_DoesNotPullOrLeakFrames()
    {
        var src = new CountingFrameSource(Bgra32_2x2);
        using var program = VideoCompositor.Create(Bgra32_2x2, VideoCompositorBackend.Cpu);
        var handle = program.AddLayer(src, LayerConfig.Background);

        // Prime the layer with a master-clock advance so it has buffered look-ahead to lose on close.
        handle.AdvanceTo(TimeSpan.FromMilliseconds(70), Bgra32_2x2);
        Assert.True(src.FramesPulled > 0);

        handle.Close();
        var pulledAtClose = src.FramesPulled;

        // The race the guard closes: a composite advancing a removed layer must not re-pull the source.
        handle.AdvanceTo(TimeSpan.FromMilliseconds(500), Bgra32_2x2);
        handle.PullOneAndSubmit(TimeSpan.FromMilliseconds(600), Bgra32_2x2);

        Assert.Equal(pulledAtClose, src.FramesPulled);

        // No leak: every frame the source produced is freed (look-ahead on Close, the cover frame when the
        // slot is disposed). Without the guard the re-pulled frames would have no owner left to dispose them.
        program.Dispose();
        Assert.Equal(src.FramesPulled, src.FramesDisposed);
    }

    [Fact]
    public void RemoveLayer_DisposesAllPulledFrames()
    {
        var src = new CountingFrameSource(Bgra32_2x2);
        using var program = VideoCompositor.Create(Bgra32_2x2, VideoCompositorBackend.Cpu);
        var handle = program.AddLayer(src, LayerConfig.Background);

        program.Clock = new FixedClock(TimeSpan.FromMilliseconds(70));
        Assert.True(program.TryReadNextFrame(out var f));
        f.Dispose();

        Assert.True(program.RemoveLayer(handle));
        Assert.Equal(src.FramesPulled, src.FramesDisposed);
    }

    private sealed class CountingFrameSource(VideoFormat fmt) : IVideoSource
    {
        private int _next;
        private int _disposed;
        public VideoFormat Format { get; } = fmt;
        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = new[] { PixelFormat.Bgra32 };
        public bool IsExhausted => false;
        public int FramesPulled => Volatile.Read(ref _next);
        public int FramesDisposed => Volatile.Read(ref _disposed);
        public void SelectOutputFormat(PixelFormat format) { }

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            var idx = _next++;
            var buf = new byte[Format.Width * Format.Height * 4];
            for (var i = 0; i < Format.Width * Format.Height; i++)
                buf[i * 4 + 3] = 255; // opaque

            frame = new VideoFrame(
                TimeSpan.FromSeconds(idx / 30.0),
                Format,
                [buf],
                [Format.Width * 4],
                release: DisposableRelease.Wrap(() => Interlocked.Increment(ref _disposed)));
            return true;
        }
    }

    private sealed class FixedClock(TimeSpan elapsed) : S.Media.Core.Clock.IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart { get; } = elapsed;
        public bool IsAdvancing => true;
    }
}
