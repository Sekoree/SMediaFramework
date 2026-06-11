using System.Diagnostics;
using S.Media.Core.Video;
using S.Media.Effects;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using S.Media.OpenGL;
using S.Media.SDL3;
using SDL3;
using SilkGL = Silk.NET.OpenGL.GL;
using SkiaSharp;

// CompositorSmoke — composite the first decoded frame of a foreground video over the first decoded
// frame of a background video and write the result as a PNG. Verifies that the GL compositor accepts
// the layers' native pixel formats (e.g. yuva444p12le over yuv422p10le) without falling back to BGRA32.
//
// Mapping mode (--pattern): renders a synthetic quadrant pattern through an output-mapping spec
// (affine sections and/or mesh warp) on the real GL stack and writes a PNG — the pixel-correctness
// and measurement harness for Doc/HaPlay-Output-Mapping-Plan.md (§8). No media files needed.

if (args.Contains("--pattern"))
    return MappingSmoke.Run(args);

if (!TryParseArgs(args, out var bgPath, out var fgPath, out var outPath, out var openGlMajor, out var openGlMinor,
        out var seekBgSeconds, out var seekFgSeconds))
{
    WriteUsage();
    return 2;
}

if (!File.Exists(bgPath))
{
    Console.Error.WriteLine($"background not found: {bgPath}");
    return 3;
}
if (!File.Exists(fgPath))
{
    Console.Error.WriteLine($"foreground not found: {fgPath}");
    return 3;
}

FFmpegRuntime.EnsureInitialized();
SDL3Runtime.Acquire();

// 1. Open both containers and pull the first decoded video frame from each.
var videoOpts = new VideoDecoderOpenOptions { TryHardwareAcceleration = false };
using var bgDec = MediaContainerDecoder.Open(bgPath, videoOpts);
using var fgDec = MediaContainerDecoder.Open(fgPath, videoOpts);

if (!bgDec.HasVideo)
{
    Console.Error.WriteLine($"background '{bgPath}' has no video stream.");
    return 4;
}
if (!fgDec.HasVideo)
{
    Console.Error.WriteLine($"foreground '{fgPath}' has no video stream.");
    return 4;
}

Console.WriteLine($"background : {bgDec.Video.Format.Width}x{bgDec.Video.Format.Height} {bgDec.Video.Format.PixelFormat}");
Console.WriteLine($"foreground : {fgDec.Video.Format.Width}x{fgDec.Video.Format.Height} {fgDec.Video.Format.PixelFormat}");

if (seekBgSeconds > 0)
{
    bgDec.SeekPresentation(TimeSpan.FromSeconds(seekBgSeconds));
    Console.WriteLine($"bg seek    : {seekBgSeconds}s");
}
if (seekFgSeconds > 0)
{
    fgDec.SeekPresentation(TimeSpan.FromSeconds(seekFgSeconds));
    Console.WriteLine($"fg seek    : {seekFgSeconds}s");
}

using var bgFrame = TryReadFirstVideoFrame(bgDec.Video, "background")
                    ?? throw new InvalidOperationException("background: no video frame produced");
using var fgFrame = TryReadFirstVideoFrame(fgDec.Video, "foreground")
                    ?? throw new InvalidOperationException("foreground: no video frame produced");

// 2. Set up an SDL window + GL context. Off-screen render via a hidden window keeps the smoke
//    quiet for CI but visible enough that you can inspect the on-screen blit before it exits.
var w = bgFrame.Format.Width;
var h = bgFrame.Format.Height;
SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, openGlMajor);
SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, openGlMinor);
SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
SDL.GLSetAttribute(SDL.GLAttr.DoubleBuffer, 1);
SDL.GLSetAttribute(SDL.GLAttr.RedSize, 8);
SDL.GLSetAttribute(SDL.GLAttr.GreenSize, 8);
SDL.GLSetAttribute(SDL.GLAttr.BlueSize, 8);
SDL.GLSetAttribute(SDL.GLAttr.AlphaSize, 0);
SDL.GLSetAttribute(SDL.GLAttr.DepthSize, 0);

var window = SDL.CreateWindow("CompositorSmoke", w, h, SDL.WindowFlags.OpenGL | SDL.WindowFlags.Hidden);
if (window == 0) throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL.GetError()}");
var glContext = SDL.GLCreateContext(window);
if (glContext == 0) throw new InvalidOperationException($"SDL_GL_CreateContext failed: {SDL.GetError()}");
if (!SDL.GLMakeCurrent(window, glContext))
    throw new InvalidOperationException($"SDL_GL_MakeCurrent failed: {SDL.GetError()}");

try
{
    var gl = SilkGL.GetApi(SDL.GLGetProcAddress);

    // 3. Composite via VideoCompositor API: background (Source) then foreground (SourceOver), cover-fit.
    var output = new VideoFormat(w, h, PixelFormat.Bgra32, bgFrame.Format.FrameRate);
    using var bgSrc = StaticFrameSource.FromFrame(bgFrame);
    using var fgSrc = StaticFrameSource.FromFrame(fgFrame);
    using var program = VideoCompositor.Create(
        output,
        VideoCompositorBackend.Gl,
        new VideoCompositorOptions { Gl = gl });
    program.AddLayer(bgSrc, new LayerConfig(LayerPosition.Cover, 1f, 1f, 0f, BlendMode.Source));
    program.AddLayer(fgSrc, new LayerConfig(LayerPosition.Cover, 1f, 1f, 0f, BlendMode.SourceOver));

    var t0 = Stopwatch.GetTimestamp();
    if (!program.TryReadNextFrame(out var resultFrame))
        throw new InvalidOperationException("VideoCompositor produced no frame.");
    var elapsedMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    Console.WriteLine($"composite  : {w}x{h} BGRA32  ({elapsedMs:F2} ms incl. readback)");

    try
    {
        // 4. Save the BGRA32 plane as a PNG via SkiaSharp.
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        var span = resultFrame.Planes[0].Span;
        unsafe
        {
            fixed (byte* p = span)
                bmp.InstallPixels(info, (nint)p, w * 4);
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        using var fs = File.Create(outPath);
        data.SaveTo(fs);
        Console.WriteLine($"wrote      : {outPath} ({data.Size:N0} bytes)");
    }
    finally
    {
        resultFrame.Dispose();
    }
}
finally
{
    SDL.GLDestroyContext(glContext);
    SDL.DestroyWindow(window);
}

return 0;

static VideoFrame? TryReadFirstVideoFrame(IVideoSource src, string label, int maxAttempts = 256)
{
    for (var i = 0; i < maxAttempts; i++)
    {
        if (src.TryReadNextFrame(out var frame))
            return frame;
        if (src.IsExhausted)
        {
            Console.Error.WriteLine($"{label}: source exhausted before producing a video frame.");
            return null;
        }
        Thread.Sleep(2);
    }
    Console.Error.WriteLine($"{label}: no frame after {maxAttempts} TryReadNextFrame attempts.");
    return null;
}

static bool TryParseArgs(string[] args, out string bg, out string fg, out string outPath, out int glMajor, out int glMinor,
    out double seekBg, out double seekFg)
{
    bg = string.Empty;
    fg = string.Empty;
    outPath = "compositor-smoke.png";
    glMajor = 3;
    glMinor = 3;
    seekBg = 0;
    seekFg = 0;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--background" or "-b":
                if (++i >= args.Length) return false;
                bg = args[i];
                break;
            case "--foreground" or "-f":
                if (++i >= args.Length) return false;
                fg = args[i];
                break;
            case "--out" or "-o":
                if (++i >= args.Length) return false;
                outPath = args[i];
                break;
            case "--seek":
                if (++i >= args.Length) return false;
                if (!double.TryParse(args[i], System.Globalization.CultureInfo.InvariantCulture, out var s)) return false;
                seekBg = s;
                seekFg = s;
                break;
            case "--seek-bg":
                if (++i >= args.Length) return false;
                if (!double.TryParse(args[i], System.Globalization.CultureInfo.InvariantCulture, out seekBg)) return false;
                break;
            case "--seek-fg":
                if (++i >= args.Length) return false;
                if (!double.TryParse(args[i], System.Globalization.CultureInfo.InvariantCulture, out seekFg)) return false;
                break;
            case "--gl":
                if (++i >= args.Length) return false;
                var parts = args[i].Split('.');
                if (parts.Length != 2 || !int.TryParse(parts[0], out glMajor) || !int.TryParse(parts[1], out glMinor))
                    return false;
                break;
            default:
                Console.Error.WriteLine($"unknown arg: {args[i]}");
                return false;
        }
    }

    return !string.IsNullOrWhiteSpace(bg) && !string.IsNullOrWhiteSpace(fg);
}

static void WriteUsage()
{
    Console.WriteLine("""
CompositorSmoke — composite a foreground video frame over a background frame.

USAGE:
    dotnet run --project MediaFramework/Tools/CompositorSmoke -- \
        --background <bg.mov> --foreground <fg.mov> [--out out.png] [--gl 3.3] \
        [--seek <seconds>] [--seek-bg <seconds>] [--seek-fg <seconds>]

The first decoded video frame from each input is fed to VideoCompositor (GL backend) as layers:
  - Background: Source blend, cover-fit.
  - Foreground: SourceOver blend, cover-fit to the background canvas.

Native pixel formats are passed through directly — no upstream BGRA32 conversion.
Inspect the reported `background:` / `foreground:` lines to confirm yuva444p12le /
yuv422p10le (or whatever) actually reaches the compositor. Output is BGRA32 PNG.

MAPPING MODE:
    dotnet run --project MediaFramework/Tools/CompositorSmoke -- \
        --pattern 640x360 [--mapping spec.json | --mapping-json '<json>'] \
        [--out out.png] [--probe x,y]...

Renders a synthetic quadrant pattern (TL red, TR green, BL blue, BR yellow, white border)
through a ClipOutputMappingSpec on the real GL warp pass (affine + mesh sections) and writes
a PNG. --probe prints `probe (x,y) = #RRGGBB` from the result for scripted assertions.
""");
}

/// <summary>
/// Mapping mode: a synthetic pattern composited on the real GL stack, warped by a
/// <see cref="S.Media.Playback.ClipOutputMappingSpec"/> — the missing-pixels test for the
/// integrated warp pass (affine sections and Phase 4 mesh warp) without needing media files.
/// </summary>
internal static class MappingSmoke
{
    public static int Run(string[] args)
    {
        var width = 640;
        var height = 360;
        string? mappingPath = null;
        string? mappingJson = null;
        var outPath = "mapping-smoke.png";
        var probes = new List<(int X, int Y)>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pattern":
                    if (++i >= args.Length) return Usage();
                    var dims = args[i].Split('x', 'X');
                    if (dims.Length != 2 || !int.TryParse(dims[0], out width) || !int.TryParse(dims[1], out height))
                        return Usage();
                    break;
                case "--mapping":
                    if (++i >= args.Length) return Usage();
                    mappingPath = args[i];
                    break;
                case "--mapping-json":
                    if (++i >= args.Length) return Usage();
                    mappingJson = args[i];
                    break;
                case "--out" or "-o":
                    if (++i >= args.Length) return Usage();
                    outPath = args[i];
                    break;
                case "--probe":
                    if (++i >= args.Length) return Usage();
                    var xy = args[i].Split(',');
                    if (xy.Length != 2 || !int.TryParse(xy[0], out var px) || !int.TryParse(xy[1], out var py))
                        return Usage();
                    probes.Add((px, py));
                    break;
                default:
                    Console.Error.WriteLine($"unknown arg: {args[i]}");
                    return Usage();
            }
        }

        S.Media.Playback.ClipOutputMappingSpec? spec = null;
        if (mappingPath is not null)
            mappingJson = File.ReadAllText(mappingPath);
        if (mappingJson is not null)
        {
            spec = System.Text.Json.JsonSerializer.Deserialize<S.Media.Playback.ClipOutputMappingSpec>(
                mappingJson,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("mapping JSON deserialized to null.");
        }

        SDL3Runtime.Acquire();
        SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
        SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
        var window = SDL.CreateWindow("MappingSmoke", width, height, SDL.WindowFlags.OpenGL | SDL.WindowFlags.Hidden);
        if (window == 0) throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL.GetError()}");
        var glContext = SDL.GLCreateContext(window);
        if (glContext == 0) throw new InvalidOperationException($"SDL_GL_CreateContext failed: {SDL.GetError()}");
        if (!SDL.GLMakeCurrent(window, glContext))
            throw new InvalidOperationException($"SDL_GL_MakeCurrent failed: {SDL.GetError()}");

        try
        {
            var gl = SilkGL.GetApi(SDL.GLGetProcAddress);
            var canvasFormat = new VideoFormat(width, height, PixelFormat.Bgra32, new Rational(60, 1));
            using var compositor = new S.Media.Effects.OpenGL.GlVideoCompositor(gl, canvasFormat);

            var resultW = width;
            var resultH = height;
            if (spec is not null)
            {
                var outputFormat = S.Media.Playback.OutputMappingResolver.ResolveOutputFormat(spec, canvasFormat);
                var resolved = S.Media.Playback.OutputMappingResolver.Resolve(spec, width, height);
                var sections = new WarpSection[resolved.Count];
                for (var i = 0; i < resolved.Count; i++)
                    sections[i] = new WarpSection(resolved[i].SourceCrop, resolved[i].Transform, resolved[i].Opacity, resolved[i].Mesh);
                compositor.SetWarpPass(outputFormat, sections);
                resultW = outputFormat.Width;
                resultH = outputFormat.Height;
                Console.WriteLine($"mapping    : {resolved.Count} section(s), {resolved.Count(s => s.Mesh is not null)} with mesh → {resultW}x{resultH}");
            }

            using var pattern = RenderPattern(width, height, canvasFormat);
            var layers = new[] { new CompositorLayer(pattern, LayerTransform2D.Identity, 1f, BlendMode.Source) };

            var t0 = Stopwatch.GetTimestamp();
            var frame = compositor.Composite(layers, TimeSpan.Zero);
            var elapsedMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            Console.WriteLine($"composite  : {width}x{height} canvas → {resultW}x{resultH} output ({elapsedMs:F2} ms incl. readback)");

            try
            {
                var span = frame.Planes[0].Span;
                var stride = frame.Strides[0];
                foreach (var (px, py) in probes)
                {
                    if (px < 0 || px >= resultW || py < 0 || py >= resultH)
                    {
                        Console.WriteLine($"probe ({px},{py}) = out of bounds");
                        continue;
                    }
                    var o = py * stride + px * 4;
                    Console.WriteLine($"probe ({px},{py}) = #{span[o + 2]:X2}{span[o + 1]:X2}{span[o]:X2}");
                }

                var info = new SKImageInfo(resultW, resultH, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var bmp = new SKBitmap(info);
                unsafe
                {
                    fixed (byte* p = span)
                        bmp.InstallPixels(info, (nint)p, stride);
                }
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 90);
                using var fs = File.Create(outPath);
                data.SaveTo(fs);
                Console.WriteLine($"wrote      : {outPath} ({data.Size:N0} bytes)");
            }
            finally
            {
                frame.Dispose();
            }

            return 0;
        }
        finally
        {
            SDL.GLDestroyContext(glContext);
            SDL.DestroyWindow(window);
        }
    }

    /// <summary>Quadrants TL red / TR green / BL blue / BR yellow with a 2px white border —
    /// unambiguous under slicing, swapping, and warping.</summary>
    private static VideoFrame RenderPattern(int width, int height, VideoFormat format)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = y * stride + x * 4;
                byte b, g, r;
                if (x < 2 || y < 2 || x >= width - 2 || y >= height - 2)
                    (b, g, r) = ((byte)255, (byte)255, (byte)255);
                else if (x < width / 2 && y < height / 2) (b, g, r) = ((byte)0, (byte)0, (byte)255);
                else if (x >= width / 2 && y < height / 2) (b, g, r) = ((byte)0, (byte)255, (byte)0);
                else if (x < width / 2) (b, g, r) = ((byte)255, (byte)0, (byte)0);
                else (b, g, r) = ((byte)0, (byte)255, (byte)255);
                pixels[o] = b;
                pixels[o + 1] = g;
                pixels[o + 2] = r;
                pixels[o + 3] = 255;
            }
        }

        return new VideoFrame(TimeSpan.Zero, format, pixels, stride);
    }

    private static int Usage()
    {
        Console.WriteLine("""
USAGE (mapping mode):
    dotnet run --project MediaFramework/Tools/CompositorSmoke -- \
        --pattern 640x360 [--mapping spec.json | --mapping-json '<json>'] \
        [--out out.png] [--probe x,y]...

The mapping JSON is a ClipOutputMappingSpec, e.g.:
    { "Sections": [ { "Id": "s1", "Enabled": true,
        "SrcX": 0, "SrcY": 0, "SrcWidth": 1, "SrcHeight": 1,
        "DestX": 0, "DestY": 0, "DestWidth": 640, "DestHeight": 360,
        "MeshColumns": 2, "MeshRows": 2,
        "MeshPoints": [ {"X":0,"Y":0}, {"X":1,"Y":0}, {"X":0,"Y":1}, {"X":0.8,"Y":0.8} ] } ] }
""");
        return 2;
    }
}
