using System.Diagnostics;
using S.Media.Core.Video;
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

    // 3. Composite: foreground (SourceOver) over background (Source). Both layers use the full canvas.
    var output = new VideoFormat(w, h, PixelFormat.Bgra32, bgFrame.Format.FrameRate);
    using var compositor = new GlVideoCompositor(gl, output);
    var bgLayer = new CompositorLayer(bgFrame, LayerTransform2D.Identity, 1f, BlendMode.Source);
    // The foreground may be a different size — stretch it to fit the canvas via Scale.
    var fgTransform = (fgFrame.Format.Width == w && fgFrame.Format.Height == h)
        ? LayerTransform2D.Identity
        : LayerTransform2D.Scale((float)w / fgFrame.Format.Width, (float)h / fgFrame.Format.Height);
    var fgLayer = new CompositorLayer(fgFrame, fgTransform, 1f, BlendMode.SourceOver);

    var t0 = Stopwatch.GetTimestamp();
    var resultFrame = compositor.Composite([bgLayer, fgLayer], TimeSpan.Zero);
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

The first decoded video frame from each input is fed to GlVideoCompositor as a layer:
  - Background: Source blend, identity transform.
  - Foreground: SourceOver blend, scaled to the background's canvas size.

Native pixel formats are passed through directly — no upstream BGRA32 conversion.
Inspect the reported `background:` / `foreground:` lines to confirm yuva444p12le /
yuv422p10le (or whatever) actually reaches the compositor. Output is BGRA32 PNG.
""");
}
