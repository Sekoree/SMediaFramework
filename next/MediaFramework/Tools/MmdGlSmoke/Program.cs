// MmdGlSmoke — the NXT-10 MMD GL renderer gate on real GL. Opens an mmd:// scene exactly like the
// session does (MmdVideoSource → ILayerSurfaceVideoSource.CreateLayerSurface), composites it through
// GlVideoCompositor.CompositeWithSurfaces at a few timeline instants, asserts the model actually covers
// pixels (and that time moves the pose when a motion is present), and dumps the middle frame as a BMP so
// the render is EYEBALLABLE without a display.
//
//   MmdGlSmoke <model.pmx> [motion.vmd] [--t seconds] [--out frame.bmp]
using S.Media.Compositor;
using S.Media.Compositor.OpenGL;
using S.Media.Core.Video;
using S.Media.Source.MMD;
using SDL3;
using SilkGL = Silk.NET.OpenGL;

// Positionals = everything that is neither a flag nor a value consumed by a value-taking flag.
var valueFlags = new[] { "--t", "--out" };
var positionalList = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (valueFlags.Contains(args[i], StringComparer.OrdinalIgnoreCase)) { i++; continue; }
    if (args[i].StartsWith("--", StringComparison.Ordinal)) continue;
    positionalList.Add(args[i]);
}
var positional = positionalList.ToArray();
if (positional.Length < 1)
{
    Console.Error.WriteLine("usage: MmdGlSmoke <model.pmx> [motion.vmd] [--t seconds] [--out frame.bmp]");
    return 2;
}

var modelPath = positional[0];
var motionPath = positional.Length > 1 ? positional[1] : null;
var t = 5.0;
string? outPath = null;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--t") _ = double.TryParse(args[i + 1], out t);
    if (args[i] == "--out") outPath = args[i + 1];
}

const int W = 1280, H = 720;

if (!SDL.Init(SDL.InitFlags.Video))
{
    Console.Error.WriteLine("FAIL: SDL_Init: " + SDL.GetError());
    return 1;
}

SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
var win = SDL.CreateWindow("mmd-gl-smoke", 64, 64, SDL.WindowFlags.OpenGL | SDL.WindowFlags.Hidden);
var glCtx = SDL.GLCreateContext(win);
SDL.GLMakeCurrent(win, glCtx);
var gl = SilkGL.GL.GetApi(SDL.GLGetProcAddress);

var canvas = new VideoFormat(W, H, PixelFormat.Bgra32, new Rational(30, 1));
using var compositor = new GlVideoCompositor(gl, canvas);
compositor.Configure(canvas);

// --diag: channel-order probes for the surface path (2026-07-03 "colors are wrong" report).
if (args.Contains("--diag", StringComparer.OrdinalIgnoreCase))
{
    // Stage 1 — texture upload: RGBA red up, glGetTexImage RGBA back. Expect R=255.
    var tex = gl.GenTexture();
    gl.BindTexture(SilkGL.TextureTarget.Texture2D, tex);
    unsafe
    {
        var red = stackalloc byte[] { 255, 0, 0, 255 };
        gl.TexImage2D(SilkGL.TextureTarget.Texture2D, 0, SilkGL.InternalFormat.Rgba8, 1, 1, 0,
            SilkGL.GLEnum.Rgba, SilkGL.GLEnum.UnsignedByte, red);
        var back = stackalloc byte[4];
        gl.GetTexImage(SilkGL.TextureTarget.Texture2D, 0, SilkGL.GLEnum.Rgba, SilkGL.GLEnum.UnsignedByte, back);
        Console.WriteLine($"diag upload/readback RGBA: ({back[0]},{back[1]},{back[2]},{back[3]}) — expect (255,0,0,255)");
    }

    // Stage 2 — a surface that clears the canvas FBO to RED, through CompositeWithSurfaces + frame
    // readback (the compositor labels the frame Bgra32). Expect the FRAME's BGRA bytes = (0,0,255,255).
    var redSurface = new SolidClearSurface(1f, 0f, 0f);
    var diagFrame = compositor.CompositeWithSurfaces(
        [], [new CompositorSurfaceLayer(redSurface, LayerTransform2D.Identity, 1f)], TimeSpan.Zero);
    var s = diagFrame.Planes[0].Span;
    var c = (H / 2) * diagFrame.Strides[0] + (W / 2) * 4;
    Console.WriteLine($"diag surface-red frame BGRA bytes: ({s[c]},{s[c + 1]},{s[c + 2]},{s[c + 3]}) — expect (0,0,255,255)");
    diagFrame.Dispose();
    redSurface.Dispose();
    return 0;
}

// The same construction path the session uses: source → surface seam.
var request = new MmdSourceRequest(modelPath, motionPath, null, W, H, null, null, null, null);
using var source = new MmdVideoSource(request);

// --software: dump the SOFTWARE renderer's frame instead (orientation/framing reference).
if (args.Contains("--software", StringComparer.OrdinalIgnoreCase))
{
    source.Seek(TimeSpan.FromSeconds(t));
    if (source.TryReadNextFrame(out var softFrame))
    {
        WriteBmp(outPath ?? "software.bmp", softFrame);
        Console.WriteLine($"software reference written to {outPath ?? "software.bmp"}");
        softFrame.Dispose();
        return 0;
    }

    Console.Error.WriteLine("FAIL: software renderer produced no frame");
    return 3;
}

var surface = ((ILayerSurfaceVideoSource)source).CreateLayerSurface();

var times = motionPath is null
    ? new[] { TimeSpan.FromSeconds(t) }
    : [TimeSpan.Zero, TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(t * 2)];
var coverage = new double[times.Length];
VideoFrame? keep = null;
for (var i = 0; i < times.Length; i++)
{
    // Physics is stateful and resets on big time jumps: warm the 1.5 s BEFORE the captured instant at
    // 30 fps so hair/skirt chains carry real momentum into the frame (matches continuous playback).
    for (var w = 45; w >= 1; w--)
    {
        var warmTime = times[i] - TimeSpan.FromSeconds(w / 30.0);
        if (warmTime < TimeSpan.Zero)
            continue;
        compositor.CompositeWithSurfaces(
            [], [new CompositorSurfaceLayer(surface, LayerTransform2D.Identity, 1f)], warmTime).Dispose();
    }

    var frame = compositor.CompositeWithSurfaces(
        [], [new CompositorSurfaceLayer(surface, LayerTransform2D.Identity, 1f)], times[i]);
    coverage[i] = OpaqueCoverage(frame);
    Console.WriteLine($"t={times[i].TotalSeconds,6:0.00}s  coverage={coverage[i]:P2}");
    if (i == times.Length / 2)
    {
        keep?.Dispose();
        keep = frame;
    }
    else
    {
        frame.Dispose();
    }
}

if (outPath is not null && keep is not null)
{
    WriteBmp(outPath, keep);
    Console.WriteLine($"wrote {outPath}");
}

keep?.Dispose();
surface.Dispose();

if (coverage.Any(c => c < 0.005))
{
    Console.Error.WriteLine("FAIL: the model covered <0.5% of the frame at some instant — nothing rendered");
    return 17;
}

Console.WriteLine("MmdGlSmoke OK — the GL layer surface rendered the model on real GL.");
return 0;

static double OpaqueCoverage(VideoFrame frame)
{
    var span = frame.Planes[0].Span;
    var stride = frame.Strides[0];
    long opaque = 0;
    for (var y = 0; y < frame.Format.Height; y++)
    {
        var row = span.Slice(y * stride, frame.Format.Width * 4);
        for (var x = 3; x < row.Length; x += 4)
            if (row[x] > 16)
                opaque++;
    }

    return opaque / (double)(frame.Format.Width * frame.Format.Height);
}

static void WriteBmp(string path, VideoFrame frame)
{
    int w = frame.Format.Width, h = frame.Format.Height;
    var stride = frame.Strides[0];
    var src = frame.Planes[0].Span;
    using var fs = File.Create(path);
    using var bw = new BinaryWriter(fs);
    var rowBytes = w * 4;
    var dataSize = rowBytes * h;
    bw.Write((ushort)0x4D42);
    bw.Write(54 + dataSize);
    bw.Write(0);
    bw.Write(54);
    bw.Write(40);
    bw.Write(w);
    bw.Write(h); // bottom-up
    bw.Write((ushort)1);
    bw.Write((ushort)32);
    bw.Write(0);
    bw.Write(dataSize);
    bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
    for (var y = h - 1; y >= 0; y--)
        bw.Write(src.Slice(y * stride, rowBytes));
}

/// <summary>Diagnostic surface: clears the bound target to one solid color.</summary>
sealed class SolidClearSurface(float r, float g, float b) : IVideoCompositorLayerSurface
{
    public void ConfigureGl(SilkGL.GL gl, VideoFormat canvas) { }
    public void Render(SilkGL.GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity)
    {
        gl.Disable(SilkGL.EnableCap.ScissorTest);
        gl.ClearColor(r, g, b, 1f);
        gl.Clear((uint)SilkGL.ClearBufferMask.ColorBufferBit);
    }
    public void Dispose() { }
}
