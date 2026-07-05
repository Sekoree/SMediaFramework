// MMDGlSmoke — the NXT-10 MMD GL renderer gate on real GL. Opens an mmd:// scene exactly like the
// session does (MMDVideoSource → ILayerSurfaceVideoSource.CreateLayerSurface), composites it through
// GlVideoCompositor.CompositeWithSurfaces at a few timeline instants, asserts the model actually covers
// pixels (and that time moves the pose when a motion is present), and dumps the middle frame as a BMP so
// the render is EYEBALLABLE without a display.
//
//   MMDGlSmoke <model.pmx> [motion.vmd] [--t seconds] [--out frame.bmp]
using System.Numerics;
using S.Media.Compositor;
using S.Media.Compositor.OpenGL;
using S.Media.Core.Video;
using S.Media.Source.MMD;
using SDL3;
using SilkGL = Silk.NET.OpenGL;

// Positionals = everything that is neither a flag nor a value consumed by a value-taking flag.
var valueFlags = new[] { "--t", "--out", "--from", "--to", "--fps", "--w", "--h", "--tx", "--ty", "--tz", "--dist" };
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
    Console.Error.WriteLine("usage: MMDGlSmoke <model.pmx> [motion.vmd] [--t seconds] [--out frame.bmp]");
    return 2;
}

var modelPath = positional[0];
var motionPath = positional.Length > 1 ? positional[1] : null;
var t = 5.0;
float? tx = null, ty = null, tz = null, dist = null;
double seqFrom = -1, seqTo = -1, seqFps = 30;
string? outPath = null;
var outW = 1280;
var outH = 720;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--t") _ = double.TryParse(args[i + 1], out t);
    if (args[i] == "--out") outPath = args[i + 1];
    if (args[i] == "--from") _ = double.TryParse(args[i + 1], out seqFrom);
    if (args[i] == "--to") _ = double.TryParse(args[i + 1], out seqTo);
    if (args[i] == "--fps") _ = double.TryParse(args[i + 1], out seqFps);
    if (args[i] == "--w") _ = int.TryParse(args[i + 1], out outW);
    if (args[i] == "--h") _ = int.TryParse(args[i + 1], out outH);
    if (args[i] == "--tx") tx = float.TryParse(args[i + 1], out var fx) ? fx : tx;
    if (args[i] == "--ty") ty = float.TryParse(args[i + 1], out var fy) ? fy : ty;
    if (args[i] == "--tz") tz = float.TryParse(args[i + 1], out var fz) ? fz : tz;
    if (args[i] == "--dist") dist = float.TryParse(args[i + 1], out var fd) ? fd : dist;
}

int W = outW, H = outH;

// --from/--to sequence mode renders CONTINUOUS frames; the physics must be the deterministic offline
// bake (what playback converges to), not the live warm-up, so wait for the bake before rendering.
if (seqFrom >= 0 && seqTo > seqFrom && motionPath is not null)
{
    var bakeModel = S.Media.Source.MMD.PMXDocument.Load(modelPath);
    var bakeMotion = S.Media.Source.MMD.VMDDocument.Load(motionPath);
    var (ready, pending) = MMDPhysicsBakeCache.LoadOrStart(modelPath, motionPath, bakeModel, bakeMotion);
    if (ready is null)
    {
        Console.Error.WriteLine("baking physics…");
        var baked = pending.GetAwaiter().GetResult();
        Console.Error.WriteLine(baked is not null ? "bake complete" : "bake FAILED (live physics will be used)");
    }
}

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

// --textures: dump the PMX texture table, per-material texture/sphere/toon assignments and how each
// path resolves on disk (the "eyes have no texture" diagnosis).
if (args.Contains("--textures", StringComparer.OrdinalIgnoreCase))
{
    var doc = S.Media.Source.MMD.PMXDocument.Load(modelPath);
    var dir = Path.GetDirectoryName(Path.GetFullPath(modelPath)) ?? ".";
    Console.WriteLine($"model dir: {dir}");
    for (var i = 0; i < doc.Textures.Count; i++)
    {
        var rel = doc.Textures[i].Replace('\\', Path.DirectorySeparatorChar);
        var resolved = ResolveTextureLikeRenderer(dir, rel);
        var decode = "unresolved";
        if (resolved is not null)
        {
            try
            {
                var img = StbImageSharp.ImageResult.FromMemory(
                    File.ReadAllBytes(resolved), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
                decode = $"decoded {img.Width}x{img.Height}";
            }
            catch (Exception ex)
            {
                decode = $"DECODE FAILED: {ex.Message}";
            }
        }

        Console.WriteLine($"tex[{i}] '{doc.Textures[i]}' -> {(resolved ?? "MISSING")} [{decode}]");
    }

    for (var m = 0; m < doc.Materials.Count; m++)
    {
        var mat = doc.Materials[m];
        Console.WriteLine(
            $"mat[{m}] '{mat.Name}' tex={mat.TextureIndex} sphere={mat.SphereTextureIndex}({mat.SphereMode}) " +
            $"toon={mat.ToonTextureIndex} sharedToon={mat.SharedToonIndex} diffuse={mat.Diffuse} " +
            $"doubleSided={mat.DoubleSided} edge={mat.HasEdge}");
    }

    return 0;
}

// --keys <boneName> [--t seconds]: raw VMD keys around a frame for one bone track.
if (args.Contains("--keys", StringComparer.OrdinalIgnoreCase) && motionPath is not null)
{
    var vmd = S.Media.Source.MMD.VMDDocument.Load(motionPath);
    var frame = (float)(t * 30.0);
    foreach (var name in new[] { "センター", "グルーブ", "右足ＩＫ", "左足ＩＫ", "全ての親", "上半身", "右足", "左足" })
    {
        if (!vmd.BoneTracks.TryGetValue(name, out var track) || track.Count == 0)
        {
            Console.WriteLine($"'{name}': no track");
            continue;
        }

        var around = track.Where(k => Math.Abs(k.Frame - frame) <= 30).Take(5).ToList();
        if (around.Count == 0)
            around = [track[0]];
        foreach (var k in around)
            Console.WriteLine($"'{name}' f={k.Frame} pos={k.Translation} rot={k.Rotation}");
    }

    return 0;
}

// --tracks: which VMD bone tracks bind to PMX bones by name (encoding/name mismatches leave bones
// at bind pose — the "wrong intro stance" diagnosis).
if (args.Contains("--tracks", StringComparer.OrdinalIgnoreCase) && motionPath is not null)
{
    var doc = S.Media.Source.MMD.PMXDocument.Load(modelPath);
    var vmd = S.Media.Source.MMD.VMDDocument.Load(motionPath);
    var boneNames = doc.Bones.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
    Console.WriteLine($"PMX bones: {doc.Bones.Count}, VMD bone tracks: {vmd.BoneTracks.Count}");
    foreach (var (name, track) in vmd.BoneTracks.OrderByDescending(kv => kv.Value.Count))
        Console.WriteLine($"{(boneNames.Contains(name) ? "  ok  " : "UNMATCHED")} '{name}' keys={track.Count}");
    return 0;
}

// The same construction path the session uses: source → surface seam.
var cameraTarget = tx is not null || ty is not null || tz is not null
    ? new Vector3(tx ?? 0f, ty ?? 10f, tz ?? 0f)
    : (Vector3?)null;
var request = new MMDSourceRequest(modelPath, motionPath, null, W, H, dist, cameraTarget, null, null);
using var source = new MMDVideoSource(request);

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

// Sequence mode: continuous frames from --from to --to at --fps, raw BGRA32 to stdout (ffmpeg-ready:
//   ... --from 0 --to 20 --fps 30 | ffmpeg -f rawvideo -pix_fmt bgra -s WxH -r FPS -i - out.mp4
// ). Logs go to stderr so stdout stays a pure pixel stream.
if (seqFrom >= 0 && seqTo > seqFrom)
{
    using var stdout = Console.OpenStandardOutput();
    var total = (int)Math.Round((seqTo - seqFrom) * seqFps);
    for (var f = 0; f < total; f++)
    {
        var time = TimeSpan.FromSeconds(seqFrom + f / seqFps);
        var frame = compositor.CompositeWithSurfaces(
            [], [new CompositorSurfaceLayer(surface, LayerTransform2D.Identity, 1f)], time);
        var span = frame.Planes[0].Span;
        var stride = frame.Strides[0];
        for (var y = 0; y < H; y++)
            stdout.Write(span.Slice(y * stride, W * 4));
        frame.Dispose();
        if (f % 60 == 0)
            Console.Error.WriteLine($"seq {f}/{total} t={time.TotalSeconds:0.00}s");
    }

    surface.Dispose();
    Console.Error.WriteLine($"seq done: {total} frames");
    return 0;
}

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

Console.WriteLine("MMDGlSmoke OK — the GL layer surface rendered the model on real GL.");
return 0;

// Mirror of MMDGlLayerSurface.ResolveTexturePath (internal there): exact path, then a case-insensitive
// per-segment walk.
static string? ResolveTextureLikeRenderer(string root, string relative)
{
    var direct = Path.Combine(root, relative);
    if (File.Exists(direct))
        return direct;

    var current = root;
    var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
    for (var i = 0; i < segments.Length; i++)
    {
        var want = segments[i];
        var match = i == segments.Length - 1
            ? Directory.EnumerateFiles(current)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), want, StringComparison.OrdinalIgnoreCase))
            : Directory.EnumerateDirectories(current)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), want, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return null;
        current = match;
    }

    return current;
}

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
