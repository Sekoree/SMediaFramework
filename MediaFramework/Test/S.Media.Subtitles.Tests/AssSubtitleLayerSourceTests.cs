using System.Text;

namespace S.Media.Subtitles.Tests;

public class AssSubtitleLayerSourceTests
{
    private const int W = 640;
    private const int H = 360;

    // A minimal but complete ASS document: one white "Hello libass" dialogue showing 1–5 s, authored at 640×360.
    private const string MinimalAss =
        "[Script Info]\n" +
        "ScriptType: v4.00+\n" +
        "PlayResX: 640\n" +
        "PlayResY: 360\n" +
        "\n" +
        "[V4+ Styles]\n" +
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, " +
        "Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, " +
        "MarginL, MarginR, MarginV, Encoding\n" +
        "Style: Default,Sans,48,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1\n" +
        "\n" +
        "[Events]\n" +
        "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n" +
        "Dialogue: 0,0:00:01.00,0:00:05.00,Default,,0,0,0,,Hello libass\n";

    private static byte[] Doc => Encoding.UTF8.GetBytes(MinimalAss);

    private static int CountVisible(VideoFrame frame)
    {
        var span = frame.Planes[0].Span;
        var n = 0;
        for (var i = 3; i < span.Length; i += 4)
            if (span[i] != 0)
                n++;
        return n;
    }

    [LibAssFact]
    public void RendersActiveCueToBgra32Premultiplied_NullOutside()
    {
        using var source = new AssSubtitleLayerSource(Doc, W, H);

        var inCue = source.RenderAt(TimeSpan.FromSeconds(3));
        Assert.NotNull(inCue);
        Assert.Equal(PixelFormat.Bgra32, inCue.Format.PixelFormat);
        Assert.Equal(VideoAlphaMode.Premultiplied, inCue.AlphaMode);
        Assert.True(CountVisible(inCue) > 0, "libass overlay had no visible pixels");

        Assert.Null(source.RenderAt(TimeSpan.FromSeconds(10))); // after the cue ends
    }

    [LibAssFact]
    public void ReusesFrameInstance_WhileContentIsStatic()
    {
        using var source = new AssSubtitleLayerSource(Doc, W, H);

        var a = source.RenderAt(TimeSpan.FromSeconds(2));
        var b = source.RenderAt(TimeSpan.FromSeconds(3)); // same static "Hello libass" cue → libass reports unchanged
        Assert.NotNull(a);
        Assert.Same(a, b);
    }

    // The header (script info + styles + events format line) of MinimalAss, without any dialogue -
    // what an FFmpeg text-subtitle decode hands the streaming ctor via codec private data.
    private static byte[] HeaderOnly =>
        Encoding.UTF8.GetBytes(MinimalAss[..MinimalAss.IndexOf("Dialogue:", StringComparison.Ordinal)]);

    [LibAssFact]
    public void AppendEvents_RendersEventsAddedAfterConstruction()
    {
        // The streaming-decode path for large containers: the source goes live with ZERO events and a
        // background pump appends them as the demux sweep passes - a cue appended mid-flight must render.
        using var source = new AssSubtitleLayerSource(W, H, HeaderOnly, events: []);

        Assert.Null(source.RenderAt(TimeSpan.FromSeconds(2))); // nothing appended yet

        source.AppendEvents([new AssEventChunk(
            Encoding.UTF8.GetBytes("0,0,Default,,0,0,0,,Streamed line"),
            StartMs: 1000, DurationMs: 4000)]);

        var frame = source.RenderAt(TimeSpan.FromSeconds(2));
        Assert.NotNull(frame);
        Assert.True(CountVisible(frame) > 0, "appended event did not render");

        Assert.Null(source.RenderAt(TimeSpan.FromSeconds(10))); // outside the appended cue
    }

    [LibAssFact]
    public void AppendEvents_AfterDispose_IsANoOp()
    {
        // The background pump may outlive the layer (dispose stops it at the next batch boundary) - a
        // late append must not throw or touch freed libass state.
        var source = new AssSubtitleLayerSource(W, H, HeaderOnly, events: []);
        source.Dispose();

        source.AppendEvents([new AssEventChunk(
            Encoding.UTF8.GetBytes("0,0,Default,,0,0,0,,Late line"), 0, 1000)]);
    }
}
