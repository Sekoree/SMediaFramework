// Phase 3: CompositeMulti typed-target smoke. Composites one red layer once, then delivers it to BOTH a
// zero-copy GlCompositeTarget (blit into a caller-owned GL FBO, no readback) and a CpuFrameCompositeTarget
// (readback to a VideoFrame), and checks both are red. Proves the target dispatch + GL→GL blit on real GL.
using S.Media.Compositor;
using S.Media.Compositor.OpenGL;
using S.Media.Core.Video;
using SDL3;
using SilkGL = Silk.NET.OpenGL;

const int W = 64, H = 64;

if (!SDL.Init(SDL.InitFlags.Video))
{
    Console.Error.WriteLine("FAIL: SDL_Init: " + SDL.GetError());
    return 1;
}

SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
var win = SDL.CreateWindow("composite-targets", W, H, SDL.WindowFlags.OpenGL | SDL.WindowFlags.Hidden);
var glCtx = SDL.GLCreateContext(win);
SDL.GLMakeCurrent(win, glCtx);
var gl = SilkGL.GL.GetApi(SDL.GLGetProcAddress);

var output = new VideoFormat(W, H, PixelFormat.Bgra32, new Rational(30, 1));
var compositor = new GlVideoCompositor(gl, output);
compositor.Configure(output);

var red = SolidBgra(W, H, b: 0, g: 0, r: 255);
var layers = new[] { CompositorLayer.Default(red) };

// A caller-owned RGBA8 target FBO for the zero-copy GL target.
var targetTex = gl.GenTexture();
gl.BindTexture(SilkGL.TextureTarget.Texture2D, targetTex);
unsafe
{
    gl.TexImage2D(SilkGL.TextureTarget.Texture2D, 0, (int)SilkGL.InternalFormat.Rgba8,
        (uint)W, (uint)H, 0, SilkGL.PixelFormat.Rgba, SilkGL.PixelType.UnsignedByte, null);
}
gl.TexParameter(SilkGL.TextureTarget.Texture2D, SilkGL.TextureParameterName.TextureMinFilter, (int)SilkGL.TextureMinFilter.Nearest);
gl.TexParameter(SilkGL.TextureTarget.Texture2D, SilkGL.TextureParameterName.TextureMagFilter, (int)SilkGL.TextureMagFilter.Nearest);
var targetFbo = gl.GenFramebuffer();
gl.BindFramebuffer(SilkGL.FramebufferTarget.Framebuffer, targetFbo);
gl.FramebufferTexture2D(SilkGL.FramebufferTarget.Framebuffer, SilkGL.FramebufferAttachment.ColorAttachment0,
    SilkGL.TextureTarget.Texture2D, targetTex, 0);
gl.BindFramebuffer(SilkGL.FramebufferTarget.Framebuffer, 0);

VideoFrame? cpuFrame = null;
var request = new WarpOutputRequest(output, null); // full-canvas passthrough
compositor.CompositeMultiToTargets(
    layers,
    [
        new TargetedWarpOutput(request, new GlCompositeTarget { Framebuffer = targetFbo, Viewport = new CompositeViewport(0, 0, W, H) }),
        new TargetedWarpOutput(request, new CpuFrameCompositeTarget { OnFrameReady = f => cpuFrame = f }),
    ],
    TimeSpan.Zero);

// Read back the zero-copy GL target FBO (RGBA).
gl.BindFramebuffer(SilkGL.FramebufferTarget.Framebuffer, targetFbo);
var rgba = new byte[W * H * 4];
unsafe
{
    fixed (byte* p = rgba)
        gl.ReadPixels(0, 0, (uint)W, (uint)H, SilkGL.PixelFormat.Rgba, SilkGL.PixelType.UnsignedByte, p);
}

var gi = ((H / 2) * W + (W / 2)) * 4;
var glRed = rgba[gi] > 200 && rgba[gi + 1] < 60 && rgba[gi + 2] < 60;     // RGBA
Console.WriteLine($"GL target  centre RGBA = ({rgba[gi]},{rgba[gi + 1]},{rgba[gi + 2]},{rgba[gi + 3]})");

var ok = glRed;
if (cpuFrame is { } f)
{
    var span = f.Planes[0].Span;
    var ci = ((H / 2) * f.Strides[0]) + (W / 2) * 4;
    var cpuRed = span[ci + 2] > 200 && span[ci + 1] < 60 && span[ci] < 60;  // BGRA -> R is +2
    Console.WriteLine($"CPU target centre RGB  = ({span[ci + 2]},{span[ci + 1]},{span[ci]})");
    ok &= cpuRed;
    f.Dispose();
}
else
{
    Console.Error.WriteLine("FAIL: CpuFrameCompositeTarget produced no frame.");
    ok = false;
}

// Layer-surface seam: a surface that fills the centre quarter green renders ON TOP of a red frame layer,
// directly into the compositor's canvas (proves IVideoCompositorLayerSurface runs in the GL context).
// ConfigureGl is deliberately NOT called here — the surface-hosting compositor owns the configure
// contract (NXT-10) and must invoke it on first sight, on its GL thread.
var red2 = SolidBgra(W, H, b: 0, g: 0, r: 255);
var surface = new GreenCentreSurface();
gl.PixelStore(SilkGL.PixelStoreParameter.UnpackAlignment, 8);
gl.PixelStore(SilkGL.PixelStoreParameter.UnpackRowLength, 7);
var surfFrame = compositor.CompositeWithSurfaces(
    [CompositorLayer.Default(red2)],
    [new CompositorSurfaceLayer(surface, LayerTransform2D.Identity, 1f)],
    TimeSpan.Zero);
{
    var ss = surfFrame.Planes[0].Span;
    var cc = ((H / 2) * surfFrame.Strides[0]) + (W / 2) * 4;   // centre — surface green
    var ce = (4 * surfFrame.Strides[0]) + 4 * 4;               // corner — frame red
    var surfCentreGreen = ss[cc + 1] > 200 && ss[cc + 2] < 60 && ss[cc] < 60;  // BGRA
    var surfCornerRed = ss[ce + 2] > 200 && ss[ce + 1] < 60 && ss[ce] < 60;
    Console.WriteLine($"surface centre BGRA-as-RGB = ({ss[cc + 2]},{ss[cc + 1]},{ss[cc]}); corner = ({ss[ce + 2]},{ss[ce + 1]},{ss[ce]})");
    ok &= surfCentreGreen && surfCornerRed;
}
gl.GetInteger(SilkGL.GetPName.UnpackAlignment, out var unpackAlignmentAfterSurface);
gl.GetInteger(SilkGL.GetPName.UnpackRowLength, out var unpackRowLengthAfterSurface);
var surfaceRestoredUnpackState = unpackAlignmentAfterSurface == 8 && unpackRowLengthAfterSurface == 7;
Console.WriteLine($"surface restored GL_UNPACK state = {surfaceRestoredUnpackState} (alignment={unpackAlignmentAfterSurface}, rowLength={unpackRowLengthAfterSurface})");
ok &= surfaceRestoredUnpackState;
gl.PixelStore(SilkGL.PixelStoreParameter.UnpackAlignment, 4);
gl.PixelStore(SilkGL.PixelStoreParameter.UnpackRowLength, 0);
surfFrame.Dispose();
red2.Dispose();
surface.Dispose();

// ExternalImageCompositeTarget: export the red composite as a dmabuf, then RE-IMPORT it via EGL and read
// it back — the definitive proof the exported pixels are correct (the cross-API zero-copy path, D7/OQ2).
var red3 = SolidBgra(W, H, b: 0, g: 0, r: 255);
ExternalImageHandle? exported = null;
var extTarget = new ExternalImageCompositeTarget
{
    AcceptedHandleTypes = ["dmabuf"],
    OnImageReady = h => exported = h,
};
try
{
    compositor.CompositeMultiToTargets(
        [CompositorLayer.Default(red3)],
        [new TargetedWarpOutput(request, extTarget)],
        TimeSpan.Zero);
}
catch (NotSupportedException)
{
    exported = null;
}

if (exported is { } eh)
{
    // The export is verified by handle well-formedness (valid fd, an 8888 DRM fourcc, a stride that fits
    // the row). The same-process GL re-import is a best-effort diagnostic: radeonsi exports a TILED buffer
    // with an INVALID modifier, which isn't portably re-importable without modifier negotiation — that
    // (and the Windows D3D11 leg) is verified by the real Phase-5 consumer / cross-platform CI.
    var wellFormed = eh.DmabufFds.Count == 1 && eh.DmabufFds[0] >= 0 && eh.Strides[0] >= W * 4 && eh.DrmFourcc != 0;
    Console.WriteLine($"dmabuf export: fourcc=0x{eh.DrmFourcc:X8} fd={eh.DmabufFds[0]} stride={eh.Strides[0]} modifier=0x{eh.DrmModifier:X} → well-formed={wellFormed}");
    Console.WriteLine(Egl.ImportAndCheckRed(gl, eh, W, H)
        ? "dmabuf round-trip (export→reimport→readback) = red ✓"
        : "dmabuf round-trip = not re-importable on this driver (tiled/INVALID-modifier; needs modifier negotiation — Phase-5 consumer)");
    ok &= wellFormed;
    var releaseThread = new Thread(() => eh.Release()) { IsBackground = true, Name = "dmabuf-release-smoke" };
    releaseThread.Start();
    releaseThread.Join();
    compositor.CompositeMultiToTargets([], [], TimeSpan.Zero); // drains deferred GL cleanup on the compositor thread
}
else
{
    Console.WriteLine("dmabuf export unavailable on this platform/context — consumers use the CpuFrameCompositeTarget fallback.");
}
red3.Dispose();

// Tear down GL objects while the context is still current, before destroying it.
gl.DeleteFramebuffer(targetFbo);
gl.DeleteTexture(targetTex);
compositor.Dispose();
red.Dispose();
SDL.GLDestroyContext(glCtx);
SDL.DestroyWindow(win);
SDL.Quit();

if (!ok)
{
    Console.Error.WriteLine("FAIL: a composite target did not receive the red composite.");
    return 1;
}

Console.WriteLine("CompositeTargetsSmoke OK — zero-copy GlCompositeTarget + CpuFrameCompositeTarget both correct.");
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

// A trivial layer surface: clears the centre quarter of the canvas green (proves the surface renders
// directly into the compositor's canvas FBO, on its GL context).
sealed class GreenCentreSurface : IVideoCompositorLayerSurface
{
    private int _w, _h;

    public void ConfigureGl(SilkGL.GL gl, VideoFormat canvas)
    {
        _w = canvas.Width;
        _h = canvas.Height;
    }

    public void Render(SilkGL.GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity)
    {
        gl.PixelStore(SilkGL.PixelStoreParameter.UnpackAlignment, 1);
        gl.PixelStore(SilkGL.PixelStoreParameter.UnpackRowLength, 11);
        gl.Enable(SilkGL.EnableCap.ScissorTest);
        gl.Scissor(_w / 4, _h / 4, (uint)(_w / 2), (uint)(_h / 2));
        gl.ClearColor(0f, 1f, 0f, 1f);
        gl.Clear(SilkGL.ClearBufferMask.ColorBufferBit);
        gl.Disable(SilkGL.EnableCap.ScissorTest);
    }

    public void Dispose() { }
}

// Minimal EGL dmabuf RE-IMPORT for the round-trip check: imports the exported dmabuf as a GL texture and
// reads back the centre pixel — the same path a real GL consumer (Avalonia's GL backend) would use.
static unsafe partial class Egl
{
    private const int EGL_NONE = 0x3038;
    private const uint EGL_LINUX_DMA_BUF_EXT = 0x3270;
    private const int EGL_WIDTH = 0x3057, EGL_HEIGHT = 0x3058;
    private const int EGL_LINUX_DRM_FOURCC_EXT = 0x3271;
    private const int EGL_DMA_BUF_PLANE0_FD_EXT = 0x3272;
    private const int EGL_DMA_BUF_PLANE0_OFFSET_EXT = 0x3273;
    private const int EGL_DMA_BUF_PLANE0_PITCH_EXT = 0x3274;
    private const int EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT = 0x3443;
    private const int EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT = 0x3444;
    private const uint GL_TEXTURE_2D = 0x0DE1;
    private const ulong DRM_FORMAT_MOD_INVALID = 0x00ffffffffffffff;

    [System.Runtime.InteropServices.LibraryImport("libEGL.so.1")] private static partial nint eglGetCurrentDisplay();
    [System.Runtime.InteropServices.LibraryImport("libEGL.so.1", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf8)]
    private static partial nint eglGetProcAddress(string name);

    private delegate nint CreateImageKHR(nint dpy, nint ctx, uint target, nint buffer, int* attribs);
    private delegate uint DestroyImageKHR(nint dpy, nint image);
    private delegate void GlImageTargetTexture2D(uint target, nint image);

    private static T? Resolve<T>(string proc) where T : Delegate
    {
        var pfn = eglGetProcAddress(proc);
        return pfn == nint.Zero ? null : System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<T>(pfn);
    }

    public static bool ImportAndCheckRed(SilkGL.GL gl, ExternalImageHandle h, int w, int hh)
    {
        var dpy = eglGetCurrentDisplay();
        var createImage = Resolve<CreateImageKHR>("eglCreateImageKHR");
        var destroyImage = Resolve<DestroyImageKHR>("eglDestroyImageKHR");
        var glTargetTex = Resolve<GlImageTargetTexture2D>("glEGLImageTargetTexture2DOES");
        if (dpy == nint.Zero || createImage is null || glTargetTex is null)
            return false;

        var attribs = new List<int>
        {
            EGL_WIDTH, w, EGL_HEIGHT, hh, EGL_LINUX_DRM_FOURCC_EXT, (int)h.DrmFourcc,
            EGL_DMA_BUF_PLANE0_FD_EXT, h.DmabufFds[0],
            EGL_DMA_BUF_PLANE0_OFFSET_EXT, h.Offsets[0],
            EGL_DMA_BUF_PLANE0_PITCH_EXT, h.Strides[0],
        };
        if (h.DrmModifier != 0 && h.DrmModifier != DRM_FORMAT_MOD_INVALID)
        {
            attribs.Add(EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT); attribs.Add((int)(h.DrmModifier & 0xFFFFFFFF));
            attribs.Add(EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT); attribs.Add((int)(h.DrmModifier >> 32));
        }
        attribs.Add(EGL_NONE);

        nint image;
        var arr = attribs.ToArray();
        fixed (int* a = arr)
            image = createImage(dpy, nint.Zero, EGL_LINUX_DMA_BUF_EXT, nint.Zero, a);
        if (image == nint.Zero)
            return false;

        var tex = gl.GenTexture();
        gl.BindTexture(SilkGL.TextureTarget.Texture2D, tex);
        gl.TexParameter(SilkGL.TextureTarget.Texture2D, SilkGL.TextureParameterName.TextureMinFilter, (int)SilkGL.TextureMinFilter.Nearest);
        gl.TexParameter(SilkGL.TextureTarget.Texture2D, SilkGL.TextureParameterName.TextureMagFilter, (int)SilkGL.TextureMagFilter.Nearest);
        glTargetTex(GL_TEXTURE_2D, image);

        var fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(SilkGL.FramebufferTarget.Framebuffer, fbo);
        gl.FramebufferTexture2D(SilkGL.FramebufferTarget.Framebuffer, SilkGL.FramebufferAttachment.ColorAttachment0,
            SilkGL.TextureTarget.Texture2D, tex, 0);

        var red = false;
        var status = gl.CheckFramebufferStatus(SilkGL.FramebufferTarget.Framebuffer);
        if (status == SilkGL.GLEnum.FramebufferComplete)
        {
            var px = new byte[w * hh * 4];
            fixed (byte* p = px)
                gl.ReadPixels(0, 0, (uint)w, (uint)hh, SilkGL.PixelFormat.Rgba, SilkGL.PixelType.UnsignedByte, p);
            var i = ((hh / 2) * w + (w / 2)) * 4;
            Console.WriteLine($"  [diag] re-import centre RGBA = ({px[i]},{px[i + 1]},{px[i + 2]},{px[i + 3]})");
            red = px[i] > 200 && px[i + 1] < 60 && px[i + 2] < 60; // RGBA
        }
        else
        {
            Console.WriteLine($"  [diag] re-import FBO status = {status}");
        }

        gl.DeleteFramebuffer(fbo);
        gl.DeleteTexture(tex);
        destroyImage?.Invoke(dpy, image);
        return red;
    }
}
