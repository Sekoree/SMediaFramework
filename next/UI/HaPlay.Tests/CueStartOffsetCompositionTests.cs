using S.Media.Routing;
using S.Media.Core.Video;
using S.Media.Compositor;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Integration regression for the start-offset black-screen bug. A cue with a start offset is
/// seeked to <c>ClipWindow.Start</c>, so its frames carry source-timeline PTS (e.g. 82 s in).
/// <c>CuePlaybackEngine</c> wraps the composition layer slot in
/// <see cref="RetimingVideoOutput"/> (offset <c>−ClipWindow.Start</c>) so those frames are rebased
/// to cue-relative PTS before they reach the master-aligned slot. These tests drive frames through
/// the same <see cref="VideoCompositorSource"/> slot path cue playback uses (the
/// <c>MediaPlayer.VideoRouter → slot.Output → compositor</c> boundary) and prove the wrapper is
/// what makes the clip visible at cue-relative t=0.
/// </summary>
public sealed class CueStartOffsetCompositionTests
{
    private static readonly VideoFormat Bgra4x4 = new(4, 4, PixelFormat.Bgra32, new Rational(30, 1));
    private static readonly TimeSpan StartOffset = TimeSpan.FromSeconds(80);

    [Fact]
    public void StartOffsetCue_RebasedSourceFrame_EntersCompositionAtCueRelativeTime()
    {
        using var compositor = new CpuVideoCompositor(Bgra4x4);
        using var mixer = new VideoCompositorSource(Bgra4x4, compositor, disposeCompositorOnDispose: false);
        var slot = mixer.AddSlot();
        slot.KeepPolicy = SlotKeepPolicy.MasterAligned;

        // Same wrapping CuePlaybackEngine.WireVideoPlacements applies for a start-offset cue.
        using var layerOutput = new RetimingVideoOutput(slot.Output, -StartOffset);
        layerOutput.Configure(Bgra4x4);

        // The decoder, seeked to the clip start, emits a frame at source PTS 82 s.
        layerOutput.Submit(MakeFrame(b: 0, g: 0, r: 255, a: 255, pts: StartOffset + TimeSpan.FromSeconds(2)));

        // The composition master is at cue-relative t = 2 s. The rebased frame (82 s − 80 s = 2 s)
        // now lands inside the master window and composites.
        Assert.True(mixer.TryReadNextFrame(TimeSpan.FromSeconds(2), out var composite));
        try
        {
            var span = composite.Planes[0].Span;
            Assert.Equal(0, span[0]);    // B
            Assert.Equal(0, span[1]);    // G
            Assert.Equal(255, span[2]);  // R — the clip frame is visible
            Assert.Equal(255, span[3]);  // A
        }
        finally { composite.Dispose(); }
    }

    [Fact]
    public void WithoutRebase_SourcePtsFrame_IsWithheldAsTooFuture()
    {
        // Control: the exact black-screen symptom the wrapper fixes. Feed the same source-PTS frame
        // straight into the master-aligned slot (no RetimingVideoOutput). At cue-relative master
        // t = 2 s a frame at 82 s is far beyond the one-canvas-period future window, so the slot
        // withholds it and the composition stays transparent.
        using var compositor = new CpuVideoCompositor(Bgra4x4);
        using var mixer = new VideoCompositorSource(Bgra4x4, compositor, disposeCompositorOnDispose: false);
        var slot = mixer.AddSlot();
        slot.KeepPolicy = SlotKeepPolicy.MasterAligned;
        slot.Output.Configure(Bgra4x4);

        slot.Output.Submit(MakeFrame(b: 0, g: 0, r: 255, a: 255, pts: StartOffset + TimeSpan.FromSeconds(2)));

        Assert.True(mixer.TryReadNextFrame(TimeSpan.FromSeconds(2), out var composite));
        try
        {
            var span = composite.Planes[0].Span;
            for (var i = 0; i < span.Length; i++)
                Assert.Equal(0, span[i]); // nothing composited — black screen without rebasing
        }
        finally { composite.Dispose(); }
    }

    private static VideoFrame MakeFrame(byte b, byte g, byte r, byte a, TimeSpan pts)
    {
        var buf = new byte[Bgra4x4.Width * Bgra4x4.Height * 4];
        for (var i = 0; i < Bgra4x4.Width * Bgra4x4.Height; i++)
        {
            buf[i * 4 + 0] = b;
            buf[i * 4 + 1] = g;
            buf[i * 4 + 2] = r;
            buf[i * 4 + 3] = a;
        }
        return new VideoFrame(pts, Bgra4x4, buf, Bgra4x4.Width * 4, release: null);
    }
}
