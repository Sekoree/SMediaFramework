// FrameDump: decode frame N of a clip with SOFTWARE decode, render it through the real YuvVideoRenderer into
// an RGBA8 FBO, glReadPixels it, and write raw RGBA bytes. Lets us see exactly what the GL upload/shader path
// produces for any pixel format (compare vs an ffmpeg reference PNG) without a visible window.
//   FrameDump <input> <frameIndex> <out.raw>
using SDL3;
using Silk.NET.OpenGL;
using S.Media.Core.Video;
using S.Media.Effects;
using S.Media.Effects.OpenGL;
using GlPixelFormat = Silk.NET.OpenGL.PixelFormat;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using S.Media.OpenGL;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: FrameDump <input> <frameIndex> <out.raw>");
    return 2;
}

var input = args[0];
var frameIndex = int.Parse(args[1]);
var outRaw = args[2];

if (!SDL.Init(SDL.InitFlags.Video)) { Console.Error.WriteLine("SDL_Init: " + SDL.GetError()); return 1; }
SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
SDL.GLSetAttribute(SDL.GLAttr.DoubleBuffer, 1);
var win = SDL.CreateWindow("framedump", 64, 64, SDL.WindowFlags.OpenGL | SDL.WindowFlags.Hidden);
var ctx = SDL.GLCreateContext(win);
SDL.GLMakeCurrent(win, ctx);
var gl = GL.GetApi(name => SDL.GLGetProcAddress(name));

FFmpegRuntime.EnsureInitialized();
using var dec = VideoFileDecoder.Open(input, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });

VideoFrame? frame = null;
for (var i = 0; i <= frameIndex; i++)
{
    frame?.Dispose();
    if (!dec.TryReadNextFrame(out frame))
    {
        Console.Error.WriteLine($"ran out of frames at {i} (wanted {frameIndex})");
        return 1;
    }
}

var fmt = dec.Format;
var w = fmt.Width;
var h = fmt.Height;
var useCompositor = args.Length > 3 && args[3] == "compositor";
Console.Error.WriteLine($"format={fmt.PixelFormat} {w}x{h} strides=[{string.Join(",", frame!.Strides)}] mode={(useCompositor ? "compositor" : "direct")}");

if (useCompositor)
{
    // Route the decoded frame through the SAME GlVideoCompositor HaPlay uses for its media-player composition,
    // as a single full-canvas layer. Composite() returns a baked BGRA32 CPU frame we can dump directly.
    var compositor = new GlVideoCompositor(gl, new VideoFormat(w, h, S.Media.Core.Video.PixelFormat.Bgra32, fmt.FrameRate));
    var baked = compositor.Composite([new CompositorLayer(frame!, LayerTransform2D.Identity, 1f, BlendMode.Source)], TimeSpan.Zero);
    var span = baked.Planes[0].Span;
    var stride = baked.Strides[0];
    var outBuf = new byte[(long)w * h * 4];
    for (var y = 0; y < h; y++)
        span.Slice(y * stride, w * 4).CopyTo(outBuf.AsSpan(y * w * 4));
    File.WriteAllBytes(outRaw, outBuf);   // BGRA, top-down (no vflip needed)
    Console.Out.WriteLine($"{w}x{h}");
    baked.Dispose();
    compositor.Dispose();   // before GL context teardown
    frame!.Dispose();
    SDL.GLDestroyContext(ctx);
    SDL.DestroyWindow(win);
    SDL.Quit();
    return 0;
}

// RGBA8 FBO at the frame size.
var fbo = gl.GenFramebuffer();
gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
var tex = gl.GenTexture();
gl.BindTexture(TextureTarget.Texture2D, tex);
unsafe { gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, (uint)w, (uint)h, 0, GlPixelFormat.Rgba, PixelType.UnsignedByte, null); }
gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
{
    Console.Error.WriteLine("FBO incomplete");
    return 1;
}

gl.Viewport(0, 0, (uint)w, (uint)h);
gl.ClearColor(0f, 0f, 0f, 1f);
gl.Clear((uint)ClearBufferMask.ColorBufferBit);

var renderer = new YuvVideoRenderer(gl, fmt);
// Match the real outputs: apply the HDR transfer hint per frame (default FollowFrameHints).
GlVideoOutputHdr.ApplyTransferHint(renderer, frame!, GlVideoOutputHdrPreference.FollowFrameHints);
Console.Error.WriteLine(
    $"colorTransferHint={frame!.ColorTransferHint} -> HdrTransfer={renderer.HdrTransfer} " +
    $"colorSpace={frame.ColorSpace} colorRange={frame.ColorRange}");
renderer.Upload(frame);
renderer.Render(w, h);
gl.Finish();

var buf = new byte[(long)w * h * 4];
unsafe { fixed (byte* p = buf) gl.ReadPixels(0, 0, (uint)w, (uint)h, GlPixelFormat.Rgba, PixelType.UnsignedByte, p); }
File.WriteAllBytes(outRaw, buf);
Console.Out.WriteLine($"{w}x{h}");   // stdout: dimensions for the caller's ffmpeg raw->png step

renderer.Dispose();   // must precede GL context teardown (deletes GL textures)
frame.Dispose();
SDL.GLDestroyContext(ctx);
SDL.DestroyWindow(win);
SDL.Quit();
return 0;
