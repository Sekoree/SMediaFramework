using System.Text;

namespace LibAssLib.Tests;

public class AssRenderTests
{
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

    private const int W = 640;
    private const int H = 360;

    private static (AssLibrary lib, AssRenderer renderer, AssTrack track) NewPipeline()
    {
        var lib = new AssLibrary();
        var renderer = lib.CreateRenderer();
        renderer.SetFrameSize(W, H);
        renderer.SetStorageSize(W, H);
        renderer.SetFonts("Sans");
        var track = lib.ReadMemory(Encoding.UTF8.GetBytes(MinimalAss));
        return (lib, renderer, track);
    }

    [Fact]
    public void LibraryVersion_IsReported()
    {
        Assert.True(AssLibrary.Version > 0);
    }

    [Fact]
    public unsafe void RenderFrame_DuringCue_ProducesBlendablePixels()
    {
        var (lib, renderer, track) = NewPipeline();
        try
        {
            var head = renderer.RenderFrame(track, 3000, out _); // 3 s: inside the 1–5 s cue
            Assert.True(head != null, "libass produced no layers for an active dialogue");

            var buffer = new byte[W * H * 4];
            var touched = AssImageBlender.Blend(head, buffer, W, H, W * 4);
            Assert.True(touched > 0, "blend touched no pixels");

            var alpha = 0;
            for (var i = 3; i < buffer.Length; i += 4)
                if (buffer[i] != 0)
                    alpha++;
            Assert.True(alpha > 0, "rendered subtitle had no visible alpha");
        }
        finally
        {
            track.Dispose();
            renderer.Dispose();
            lib.Dispose();
        }
    }

    [Fact]
    public unsafe void RenderFrame_OutsideCue_ProducesNoLayers()
    {
        var (lib, renderer, track) = NewPipeline();
        try
        {
            var head = renderer.RenderFrame(track, 10_000, out _); // 10 s: after the cue ends
            Assert.True(head == null);
        }
        finally
        {
            track.Dispose();
            renderer.Dispose();
            lib.Dispose();
        }
    }
}
