// Phase 5 compositor→NDI egress smoke. A GL compositor composites a red layer onto a canvas; the composite
// is delivered to a CpuFrameCompositeTarget (readback to a CPU VideoFrame — OQ3, NDI SDK send is CPU p_data),
// and its OnFrameReady submits each frame to an NDIOutput. A loopback receiver discovers the sender and
// confirms the composited frames arrive over NDI as non-black content — proving the
// CpuFrameCompositeTarget → NDIOutput.Video.Submit egress end-to-end. Needs real GL + libndi.
using System.Diagnostics;
using S.Media.Compositor;
using S.Media.Compositor.OpenGL;
using S.Media.Core.Video;
using S.Media.NDI;
using SDL3;
using SilkGL = Silk.NET.OpenGL;

var seconds = args.Length > 0 && double.TryParse(args[0], out var s) ? s : 8.0;
const int W = 320, H = 240;
const string senderName = "MFPlayer Composite NDI";

if (!SDL.Init(SDL.InitFlags.Video))
{
    Console.Error.WriteLine("FAIL: SDL_Init: " + SDL.GetError());
    return 1;
}

SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
var win = SDL.CreateWindow("composite-to-ndi", W, H, SDL.WindowFlags.OpenGL | SDL.WindowFlags.Hidden);
var glCtx = SDL.GLCreateContext(win);
SDL.GLMakeCurrent(win, glCtx);
var gl = SilkGL.GL.GetApi(SDL.GLGetProcAddress);

var canvas = new VideoFormat(W, H, PixelFormat.Bgra32, new Rational(30, 1));
var compositor = new GlVideoCompositor(gl, canvas);
compositor.Configure(canvas);
var request = new WarpOutputRequest(canvas, null); // full-canvas passthrough

using var ndiOut = new NDIOutput(senderName);
ndiOut.Video.Configure(canvas);

// Receiver thread: discover our composite sender and read frames back, checking they carry composited
// (non-black) content rather than nothing / black.
long received = 0;
var nonBlack = false;
var running = true;
var recvThread = new Thread(() =>
{
    Thread.Sleep(1500); // let the sender announce + push the first frames
    var sources = NDISource.Find(TimeSpan.FromSeconds(4));
    var mine = sources.FirstOrDefault(x => x.Name.Contains(senderName, StringComparison.Ordinal));
    if (mine.Name is null)
        return;

    using var recv = NDISource.Open(mine, new NDISourceOptions { ReceiveVideo = true, ReceiveAudio = false });
    while (Volatile.Read(ref running) && recv.Video.TryReadNextFrame(out var f))
    {
        var span = f.Planes[0].Span;
        var centre = ((H / 2) * f.Strides[0]) + (W / 2) * 4;
        if (centre + 3 < span.Length && (span[centre] > 30 || span[centre + 1] > 30 || span[centre + 2] > 30))
            nonBlack = true;
        f.Dispose();
        Interlocked.Increment(ref received);
    }
}) { IsBackground = true, Name = "composite-ndi-recv" };
recvThread.Start();

Console.WriteLine($"compositing a red {W}x{H} canvas → CpuFrameCompositeTarget → NDIOutput '{senderName}' for {seconds:0}s…");

// Composite loop on the GL thread: composite the layer, read it back to CPU, submit each frame to NDI.
var sw = Stopwatch.StartNew();
long composited = 0;
while (sw.Elapsed.TotalSeconds < seconds)
{
    var red = SolidBgra(W, H, b: 0, g: 0, r: 255);
    compositor.CompositeMultiToTargets(
        [CompositorLayer.Default(red)],
        [new TargetedWarpOutput(request, new CpuFrameCompositeTarget { OnFrameReady = f => { ndiOut.Video.Submit(f); f.Dispose(); } })],
        TimeSpan.Zero);
    red.Dispose();
    composited++;
    Thread.Sleep(33); // ~30 fps
}

Volatile.Write(ref running, false);
recvThread.Join(TimeSpan.FromSeconds(2));

compositor.Dispose();
SDL.GLDestroyContext(glCtx);
SDL.DestroyWindow(win);
SDL.Quit();

Console.WriteLine($"composited {composited} frames → NDI; received {received} back, content-non-black={nonBlack}; sender sees {ndiOut.GetReceiverConnectionCount()} receiver(s).");
if (received < 10 || !nonBlack)
{
    Console.Error.WriteLine("FAIL: composited frames did not arrive over NDI as non-black content.");
    return 1;
}

Console.WriteLine("CompositeToNdiSmoke OK — a composition was sent out as NDI (CpuFrameCompositeTarget → NDIOutput.Video) and received back.");
return 0;

static VideoFrame SolidBgra(int w, int h, byte b, byte g, byte r)
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

    return new VideoFrame(TimeSpan.Zero, new VideoFormat(w, h, PixelFormat.Bgra32, new Rational(30, 1)), [buf], [stride]);
}
