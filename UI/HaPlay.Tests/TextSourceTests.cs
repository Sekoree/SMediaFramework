using HaPlay.Models;
using HaPlay.Playback;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using Xunit;

namespace HaPlay.Tests;

/// <summary>NXT-06: the <c>text:</c> registry provider that lets the ShowSession cue path play a text cue. Covers
/// the URI round-trip, the render + duration-bounding, and the mapper emitting a <c>text:</c> binding. The rendered
/// text's visual correctness is verified on hardware; these pin the headless open/bind path.</summary>
public class TextSourceTests
{
    [Fact]
    public void TextSourceUri_RoundTrips_RenderSpecAndDuration()
    {
        var item = new TextPlaylistItem
        {
            Text = "Lower third", FontFamily = "Inter", FontSizePx = 72, Bold = true,
            ColorArgb = 0xFFAABBCC, CanvasWidth = 1280, CanvasHeight = 720,
        };
        var uri = TextSourceUri.Encode(item, durationMs: 1500);

        Assert.True(TextSourceUri.IsTextUri(uri));
        var spec = TextSourceUri.Decode(uri);
        Assert.NotNull(spec);
        Assert.Equal("Lower third", spec!.Text);
        Assert.True(spec.Bold);
        Assert.Equal(0xFFAABBCCu, spec.ColorArgb);
        Assert.Equal(1280, spec.CanvasWidth);
        Assert.Equal(1500, spec.DurationMs);

        // Round-trips back to a TextPlaylistItem the renderer consumes.
        var back = spec.ToItem();
        Assert.Equal("Lower third", back.Text);
        Assert.True(back.Bold);
    }

    [Fact]
    public void TextDecoderProvider_Renders_ReportsDuration_AndNeverSelfExhausts()
    {
        var uri = TextSourceUri.Encode(new TextPlaylistItem { Text = "Hi", CanvasWidth = 64, CanvasHeight = 48 }, durationMs: 200);
        var provider = new TextDecoderProvider();
        Assert.Equal(1.0, provider.Probe(uri, MediaKind.Video));
        Assert.Equal(0.0, provider.Probe(uri, MediaKind.Audio)); // video-only

        using var src = (TextHeldVideoSource)provider.OpenVideo(uri, null);
        Assert.Equal(TimeSpan.FromMilliseconds(200), src.Duration); // reported → ShowSession's monitor ends at duration

        Assert.True(src.TryReadNextFrame(out var frame)); // renders the text frame at the canvas size
        Assert.Equal(64, frame.Format.Width);
        Assert.Equal(48, frame.Format.Height);
        frame.Dispose();

        // UNBOUNDED: reads never exhaust it — the time-based end-monitor stops the cue, not read count. A resize /
        // live-edit that re-primes the pipeline (a burst of reads) must NOT end the cue, which was the mid-playback
        // stop the read-count bound caused.
        for (var i = 0; i < 1000; i++)
            if (src.TryReadNextFrame(out var f))
                f.Dispose();
        Assert.False(src.IsExhausted);
    }

    [Fact]
    public void TextDecoderProvider_ZeroDuration_HoldsUnbounded()
    {
        using var src = (TextHeldVideoSource)new TextDecoderProvider()
            .OpenVideo(TextSourceUri.Encode(new TextPlaylistItem { Text = "Hold", CanvasWidth = 32, CanvasHeight = 32 }, durationMs: 0), null);
        Assert.Equal(TimeSpan.Zero, src.Duration); // unbounded → holds until stopped
        for (var i = 0; i < 100; i++)
        {
            Assert.True(src.TryReadNextFrame(out var f));
            f.Dispose();
        }
        Assert.False(src.IsExhausted);
    }

    [Fact]
    public void TextHeldVideoSource_ReplaceFrame_SwapsContentLive()
    {
        // NXT-06 live text update: a playing text cue's frame can be swapped in place (IReplaceableFrameSource)
        // when its text/style is edited, without re-firing. A size change proves the new frame took effect.
        using var src = (TextHeldVideoSource)new TextDecoderProvider()
            .OpenVideo(TextSourceUri.Encode(new TextPlaylistItem { Text = "A", CanvasWidth = 64, CanvasHeight = 48 }, 0), null);
        Assert.IsAssignableFrom<IReplaceableFrameSource>(src);

        Assert.True(src.TryReadNextFrame(out var f1));
        Assert.Equal(64, f1.Format.Width);
        f1.Dispose();

        // Swap to a differently-sized rendered frame; the next read must reflect it (the live edit path).
        var replacement = TextFrameRenderer.Render(
            new TextPlaylistItem { Text = "B", CanvasWidth = 100, CanvasHeight = 40 }, new Rational(30, 1));
        Assert.NotNull(replacement);
        ((IReplaceableFrameSource)src).ReplaceFrame(replacement!);

        Assert.True(src.TryReadNextFrame(out var f2));
        Assert.Equal(100, f2.Format.Width); // reads now reflect the swapped-in frame
        Assert.Equal(40, f2.Format.Height);
        f2.Dispose();
    }

    [Fact]
    public void TextFrameRenderer_RendersAtTheCanvasSize()
    {
        // The frame is the full CanvasWidth×CanvasHeight (fixed size); the text sits at its FontSizePx within it.
        var frame = TextFrameRenderer.Render(
            new TextPlaylistItem { Text = "Hi", CanvasWidth = 320, CanvasHeight = 180, FontSizePx = 96 },
            new Rational(30, 1));

        Assert.NotNull(frame);
        Assert.Equal(320, frame!.Format.Width);
        Assert.Equal(180, frame.Format.Height);
        frame.Dispose();
    }

    [Fact]
    public void TextFrameRenderer_MeasureNormalizedBounds_IsAnInsetSubRect()
    {
        // Placement outline: the text extent as fractions of the canvas — inset (default center/middle alignment).
        var bounds = TextFrameRenderer.MeasureNormalizedBounds(
            new TextPlaylistItem { Text = "Hi", CanvasWidth = 1920, CanvasHeight = 1080, FontSizePx = 96 });

        Assert.NotNull(bounds);
        var (x, y, w, h) = bounds!.Value;
        Assert.True(w is > 0 and < 1 && h is > 0 and < 1);
        Assert.True(x > 0 && x + w < 1 && y > 0 && y + h < 1);
    }

    [Fact]
    public void Mapper_TextCue_BindsToATextUri_WithDuration()
    {
        var cueList = new CueList
        {
            Name = "Show",
            Nodes = { new MediaCueNode { Label = "Title", Source = new TextPlaylistItem { Text = "Welcome" }, DurationMs = 3000 } },
        };

        var doc = HaPlayShowMapper.ToShowDocument(cueList);

        var clip = Assert.Single(doc.Clips);
        Assert.True(TextSourceUri.IsTextUri(clip.MediaPath));
        var spec = TextSourceUri.Decode(clip.MediaPath);
        Assert.Equal("Welcome", spec!.Text);
        Assert.Equal(3000, spec.DurationMs);
    }
}
