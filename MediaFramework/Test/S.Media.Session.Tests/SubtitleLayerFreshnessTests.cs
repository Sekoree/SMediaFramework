using S.Media.Core.Registry;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Session.Tests;

/// <summary>
/// Regression guard for the subtitle-freeze bug: a subtitle overlay is pump-driven and re-rendered in place,
/// so every frame it submits carries <see cref="VideoFrame.PresentationTime"/> = 0. The composition layer it
/// lands on must therefore be <c>Latest</c>-wins - a <c>MasterAligned</c> slot treats every constant-PTS
/// frame as equidistant from the clock and freezes on the FIRST one, so only the first subtitle ever shows
/// (its later text updates are silently dropped). This drives a real session and asserts the composited
/// canvas actually TRACKS the overlay's changing content.
/// </summary>
public sealed class SubtitleLayerFreshnessTests
{
    // An overlay whose content changes over time but whose frames always have PTS 0 (the real ASS/bitmap
    // subtitle shape). The blue channel encodes the current whole-second bucket so the compositor output is
    // trivially checkable.
    private sealed class TickingOverlay : IVideoOverlaySource
    {
        private readonly byte[] _buffer;
        private readonly VideoFrame _frame;

        public TickingOverlay(int width, int height)
        {
            Width = width;
            Height = height;
            _buffer = new byte[width * height * 4];
            // Re-rendered in place (like the real ASS source): one reused frame, PresentationTime fixed at 0.
            _frame = new VideoFrame(
                TimeSpan.Zero,
                new VideoFormat(width, height, PixelFormat.Bgra32, new Rational(30, 1)),
                _buffer, width * 4,
                new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Premultiplied));
        }

        public int Width { get; }
        public int Height { get; }

        public VideoFrame? RenderAt(TimeSpan masterTime)
        {
            var bucket = (byte)Math.Clamp((int)masterTime.TotalSeconds, 0, 255);
            for (var i = 0; i < _buffer.Length; i += 4)
            {
                _buffer[i] = bucket;   // B - the tick marker
                _buffer[i + 1] = 0;    // G
                _buffer[i + 2] = 0;    // R
                _buffer[i + 3] = 255;  // A - opaque so it defines the canvas
            }
            return _frame;
        }

        public void Dispose() { }
    }

    private sealed class CanvasSampler : IVideoOutput
    {
        private VideoFormat _format;
        public readonly List<byte> BlueSamples = [];
        public VideoFormat Format => _format;
        public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = [];
        public void Configure(VideoFormat format) => _format = format;

        public void Submit(VideoFrame frame)
        {
            var b = frame.Planes[0].Span[0]; // top-left blue = the overlay's current tick marker
            lock (BlueSamples)
                BlueSamples.Add(b);
            frame.Dispose();
        }
    }

    [Fact]
    public async Task SubtitleOverlay_WithConstantPtsFrames_KeepsUpdatingTheComposite()
    {
        var sampler = new CanvasSampler();
        await using var session = new ShowSession(
            UnboundedHeldProvider.Registry(TimeSpan.FromSeconds(30)),
            subtitleFactory: (_, _, w, h) => new TickingOverlay(w, h),
            videoOutputFactory: (compId, name, _, _) => compId == "screen"
                ? [new ClipCompositionOutputLease("out", name, sampler, DisposeOutputOnRuntimeDispose: false)]
                : []);

        session.LoadDocument(new ShowDocument(
            Version: 1,
            Cues: [new CueDefinition("c", 1, "Video")],
            Clips: [new ShowClipBinding("c", "held://x", CompositionId: "screen", LayerIndex: 0, SubtitlePath: "sub.ass")],
            Compositions: [new ShowComposition("screen", "Screen", 4, 4, 30, 1)], Routes: []));

        Assert.Equal(CueExecutionStatus.Fired, await session.GoAsync());

        // Let the playhead cross several whole-second boundaries; poll until the composite has surfaced at
        // least three DISTINCT tick markers (the overlay's content advancing) or time out.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            lock (sampler.BlueSamples)
                if (sampler.BlueSamples.Distinct().Count() >= 3)
                    break;
            await Task.Delay(50);
        }

        int distinct;
        byte first, last;
        lock (sampler.BlueSamples)
        {
            Assert.NotEmpty(sampler.BlueSamples);
            distinct = sampler.BlueSamples.Distinct().Count();
            first = sampler.BlueSamples[0];
            last = sampler.BlueSamples[^1];
        }

        // The freeze bug pins the composite to the first frame → exactly one distinct marker forever.
        Assert.True(distinct >= 3,
            $"composited subtitle froze on the first frame (only {distinct} distinct tick markers; first={first}, last={last})");
        Assert.True(last > first,
            $"subtitle tick marker did not advance in the composite (first={first}, last={last})");
    }
}
