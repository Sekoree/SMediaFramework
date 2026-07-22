// ProjectMGlSmoke - the projectM visualizer gate on real GL. Runs the EXACT app path: an SDL 3.3-core
// context, GlVideoCompositor.CompositeWithSurfaces hosting ProjectMVisualSource's layer surface
// (ConfigureGl → PCM feed → Render), asserts the canvas actually lights up, and dumps a frame as BMP
// so the render is eyeballable. This is the tool that would have caught the GLES-build segfault.
//
//   MFP_PROJECTM_LIB=<libdir> dotnet run --project MediaFramework/Tools/ProjectMGlSmoke -- \
//       [--presets <dir>] [--frames N] [--out frame.bmp]
using ProjectMLib;
using S.Media.Compositor;
using S.Media.Compositor.OpenGL;
using S.Media.Core.Video;
using S.Media.Visualizer.ProjectM;
using SDL3;
using SilkGL = Silk.NET.OpenGL;

string? presetDir = null;
string? outPath = null;
var frames = 90;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--presets") presetDir = args[i + 1];
    if (args[i] == "--out") outPath = args[i + 1];
    if (args[i] == "--frames") _ = int.TryParse(args[i + 1], out frames);
}

if (!ProjectMRuntime.IsAvailable)
{
    Console.Error.WriteLine($"FAIL: projectM unavailable: {ProjectMRuntime.UnavailableReason}");
    return 1;
}

Console.WriteLine($"projectM {ProjectMRuntime.Version} ({ProjectMRuntime.LoadedLibraryPath ?? "path unknown"})");

if (!SDL.Init(SDL.InitFlags.Video))
{
    Console.Error.WriteLine("FAIL: SDL_Init: " + SDL.GetError());
    return 1;
}

SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
var win = SDL.CreateWindow("projectm-gl-smoke", 64, 64, SDL.WindowFlags.OpenGL | SDL.WindowFlags.Hidden);
var glCtx = SDL.GLCreateContext(win);
SDL.GLMakeCurrent(win, glCtx);
var gl = SilkGL.GL.GetApi(SDL.GLGetProcAddress);

const int W = 640, H = 360, Fps = 30, SampleRate = 48_000;
var canvas = new VideoFormat(W, H, PixelFormat.Bgra32, new Rational(Fps, 1));
using var compositor = new GlVideoCompositor(gl, canvas);
compositor.Configure(canvas);

// Deliberately render at a DIFFERENT resolution than the canvas (1280x720 into a 640x360 canvas):
// exercises the decoupled internal-render-resolution path - projectM runs at RenderWidth/Height and
// the surface blits scaled into the canvas.
using var source = new ProjectMVisualSource(1280, 720, new Rational(Fps, 1), new ProjectMOptions
{
    PresetDirectory = presetDir,
    PresetDurationSeconds = 2, // rotate fast so a multi-second run covers several presets
    RenderWidth = 1280,
    RenderHeight = 720,
    Fps = 60,
});
var surface = source.CreateLayerSurface();

// Feed + render like live playback: one audio chunk and one composite per frame instant.
var pcm = new float[SampleRate / Fps * 2];
VideoFrame? lastFrame = null;
long litPixels = 0;
for (var f = 0; f < frames; f++)
{
    var t = TimeSpan.FromTicks(TimeSpan.TicksPerSecond * f / Fps);
    for (var s = 0; s < pcm.Length / 2; s++)
    {
        // A loud beat-ish tone so beat-reactive presets have something to chew on.
        var x = (f * (pcm.Length / 2) + s) / (double)SampleRate;
        var v = (float)(0.6 * Math.Sin(2 * Math.PI * 440 * x) * (0.5 + 0.5 * Math.Sin(2 * Math.PI * 2 * x)));
        pcm[s * 2] = v;
        pcm[s * 2 + 1] = v;
    }

    source.Submit(pcm);

    // A real frame layer under the surface, deliberately with an ODD width: the layer upload sets
    // GL_UNPACK_ROW_LENGTH, and a host that doesn't reset pixel-store before the surface pass feeds
    // projectM's texture preloads a stale row stride → driver reads out of bounds (the 2026-07-11
    // in-app crash: cover-art layer + VIZ). This smoke MUST include a layer to cover that path.
    const int layerW = 300, layerH = 200;
    var layerPixels = new byte[layerW * 4 * layerH];
    for (var i = 0; i < layerPixels.Length; i += 4)
    {
        layerPixels[i + 2] = 64; // dim red backdrop
        layerPixels[i + 3] = 255;
    }

    using var layerFrame = new VideoFrame(
        t, new VideoFormat(layerW, layerH, PixelFormat.Bgra32, new Rational(Fps, 1)), [layerPixels], [layerW * 4]);

    lastFrame?.Dispose();
    lastFrame = compositor.CompositeWithSurfaces(
        [new CompositorLayer(layerFrame, LayerTransform2D.Identity, 1f, BlendMode.SourceOver)],
        [new CompositorSurfaceLayer(surface, LayerTransform2D.Identity, 1f)], t);

    if (f == frames - 1)
    {
        var px = lastFrame.Planes[0].Span;
        var stride = lastFrame.Strides[0];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var o = y * stride + x * 4;
            if (px[o] > 12 || px[o + 1] > 12 || px[o + 2] > 12)
                litPixels++;
        }
    }
}

if (outPath is not null && lastFrame is not null)
{
    SaveBmp(outPath, lastFrame);
    Console.WriteLine($"wrote {outPath}");
}

var litPercent = 100.0 * litPixels / (W * H);
Console.WriteLine($"rendered {frames} frames; final frame lit pixels: {litPixels:N0} ({litPercent:0.0}%)");
lastFrame?.Dispose();
surface.Dispose();

// Minimal test presets legitimately draw just a thin waveform (~0.3-0.5% of pixels); pure black
// means the output copy never reached our FBO (the pre-patch symptom).
if (litPixels < 200)
{
    Console.Error.WriteLine("FAIL: final frame is (near-)black - projectM did not render");
    return 1;
}

Console.WriteLine("PASS");
return 0;

static void SaveBmp(string path, VideoFrame frame)
{
    // Minimal BGRA→BMP (bottom-up, 32bpp) - eyeball output without any imaging package.
    var w = frame.Format.Width;
    var h = frame.Format.Height;
    var stride = frame.Strides[0];
    var src = frame.Planes[0].Span;
    using var fs = File.Create(path);
    using var bw = new BinaryWriter(fs);
    var imageSize = w * h * 4;
    bw.Write((ushort)0x4D42);
    bw.Write(54 + imageSize);
    bw.Write(0);
    bw.Write(54);
    bw.Write(40);
    bw.Write(w);
    bw.Write(h);
    bw.Write((ushort)1);
    bw.Write((ushort)32);
    bw.Write(0);
    bw.Write(imageSize);
    bw.Write(2835);
    bw.Write(2835);
    bw.Write(0);
    bw.Write(0);
    for (var y = h - 1; y >= 0; y--)
        bw.Write(src.Slice(y * stride, w * 4));
}
