// Phase 3 CompositorSmoke - proves the GL compositor (Gpu + Compositor + the SDL3 bridge) composites on
// real hardware GL with NO decoder/FFmpeg/Skia in the graph. Builds two synthetic BGRA layers
// (red background + a centered green foreground) entirely from StaticFrameSource, composites them on the
// SDL3 GL backend, reads the frame back, and checks the pixels: corner stays red, centre turns green.
using S.Media.Compositor;
using S.Media.Core.Video;
using S.Media.Present.SDL3;

const int W = 256, H = 256;
var output = new VideoFormat(W, H, PixelFormat.Bgra32, new Rational(30, 1));

VideoCompositor compositor;
try
{
    compositor = VideoCompositor.Create(
        output,
        VideoCompositorBackend.Gl,
        new VideoCompositorOptions { AutoBackends = [SDL3GLVideoCompositor.TryCreate] });
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: could not create GL compositor backend: {ex.Message}");
    return 1;
}

using (compositor)
{
    // Red background (full canvas) + green foreground at half size, centred.
    compositor.AddLayer(SolidBgra(W, H, b: 0, g: 0, r: 255), LayerConfig.Background);
    compositor.AddLayer(SolidBgra(W / 2, H / 2, b: 0, g: 255, r: 0), LayerConfig.CenteredHalf);

    if (!compositor.TryReadNextFrame(out var frame))
    {
        Console.Error.WriteLine("FAIL: GL compositor produced no frame.");
        return 1;
    }

    using (frame)
    {
        if (frame.Format.Width != W || frame.Format.Height != H)
        {
            Console.Error.WriteLine($"FAIL: output {frame.Format.Width}x{frame.Format.Height}, expected {W}x{H}.");
            return 1;
        }

        var (cr, cg, cb) = SamplePixel(frame, W / 2, H / 2);   // centre - should be green
        var (er, eg, eb) = SamplePixel(frame, 4, 4);           // corner - should be red
        Console.WriteLine($"centre BGRA-as-RGB = ({cr},{cg},{cb}); corner = ({er},{eg},{eb})");

        var centreGreen = cg > 180 && cr < 80 && cb < 80;
        var cornerRed = er > 180 && eg < 80 && eb < 80;
        if (!centreGreen || !cornerRed)
        {
            Console.Error.WriteLine(
                $"FAIL: composite wrong (centreGreen={centreGreen}, cornerRed={cornerRed}).");
            return 1;
        }
    }
}

Console.WriteLine("CompositorSmoke OK - GL composite + readback correct, FFmpeg absent.");
return 0;

static StaticFrameSource SolidBgra(int w, int h, byte b, byte g, byte r)
{
    var stride = w * 4;
    var buf = new byte[stride * h];
    for (var i = 0; i < buf.Length; i += 4)
    {
        buf[i] = b;
        buf[i + 1] = g;
        buf[i + 2] = r;
        buf[i + 3] = 255;
    }

    return new StaticFrameSource(new VideoFormat(w, h, PixelFormat.Bgra32, new Rational(30, 1)), [buf], [stride]);
}

static (byte R, byte G, byte B) SamplePixel(VideoFrame frame, int x, int y)
{
    var span = frame.Planes[0].Span;
    var i = (y * frame.Strides[0]) + (x * 4);
    return (span[i + 2], span[i + 1], span[i]); // BGRA -> R,G,B
}
