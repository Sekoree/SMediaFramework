// AbiGlSmoke - verifies the GL layer-surface adapter end-to-end on a real (SDL) GL context: the plugin's layer
// surface, created from a config blob + registered into an ICompositorRegistry, renders into a canvas FBO; readback
// confirms the config-driven clear colour. Headless: run under xvfb.
using System.Diagnostics;
using S.Abi;
using S.Media.Compositor;
using S.Media.Core.Video;
using SDL3;
using SilkGL = Silk.NET.OpenGL;

const int W = 4, H = 4;

var root = FindNextRoot(AppContext.BaseDirectory);
var pluginC = Path.Combine(root, "MediaFramework", "Tools", "AbiSmoke", "test_plugin.c");
var includeDir = Path.Combine(root, "MediaFramework", "Interop", "S.Abi", "include");
var so = Path.Combine(Path.GetTempPath(), "mfp_test_plugin_gl.so");

Console.WriteLine($"compiling {Path.GetFileName(pluginC)} -> {so}");
if (!CompilePlugin(pluginC, includeDir, so))
    return 1;

if (!SDL.Init(SDL.InitFlags.Video))
{
    Console.Error.WriteLine("FAIL: SDL_Init: " + SDL.GetError());
    return 1;
}
SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
var win = SDL.CreateWindow("abi-gl", W, H, SDL.WindowFlags.OpenGL | SDL.WindowFlags.Hidden);
var glCtx = SDL.GLCreateContext(win);
SDL.GLMakeCurrent(win, glCtx);
var gl = SilkGL.GL.GetApi(SDL.GLGetProcAddress);

// Load the plugin and register its layer surface into a config-aware compositor registry.
using var plugin = AbiPluginHost.Load(so);
var registry = CompositorRegistryBuilder.Build(b => AbiPluginHost.RegisterInto(plugin, compositor: b));
Console.WriteLine($"layer-surface kinds: [{string.Join(", ", registry.LayerSurfaceKinds)}]");
if (!registry.TryCreateLayerSurface("testlayer", "40", out var surface))
{
    Console.Error.WriteLine("FAIL: the compositor registry did not create the plugin layer surface.");
    return 2;
}

// A caller-owned RGBA8 FBO for the layer to render into.
var tex = gl.GenTexture();
gl.BindTexture(SilkGL.TextureTarget.Texture2D, tex);
unsafe
{
    gl.TexImage2D(SilkGL.TextureTarget.Texture2D, 0, (int)SilkGL.InternalFormat.Rgba8,
        (uint)W, (uint)H, 0, SilkGL.PixelFormat.Rgba, SilkGL.PixelType.UnsignedByte, null);
}
var fbo = gl.GenFramebuffer();
gl.BindFramebuffer(SilkGL.FramebufferTarget.Framebuffer, fbo);
gl.FramebufferTexture2D(SilkGL.FramebufferTarget.Framebuffer, SilkGL.FramebufferAttachment.ColorAttachment0,
    SilkGL.TextureTarget.Texture2D, tex, 0);
gl.Viewport(0, 0, (uint)W, (uint)H);

// Configure + render the plugin layer surface (config "40" => clear red = 40/255).
var canvas = new VideoFormat(W, H, PixelFormat.Rgba32, new Rational(30, 1));
surface.ConfigureGl(gl, canvas);
surface.Render(gl, fbo, TimeSpan.FromSeconds(1), LayerTransform2D.Identity, 1.0f);
gl.Finish();

// Read back the FBO and verify the config-driven colour.
gl.BindFramebuffer(SilkGL.FramebufferTarget.Framebuffer, fbo);
var rgba = new byte[W * H * 4];
unsafe
{
    fixed (byte* p = rgba)
        gl.ReadPixels(0, 0, (uint)W, (uint)H, SilkGL.PixelFormat.Rgba, SilkGL.PixelType.UnsignedByte, p);
}
Console.WriteLine($"layer-rendered FBO px0=({rgba[0]},{rgba[1]},{rgba[2]},{rgba[3]})");

surface.Dispose();
gl.DeleteFramebuffer(fbo);
gl.DeleteTexture(tex);
SDL.Quit();

if (rgba[0] != 40 || rgba[1] != 0 || rgba[2] != 0 || rgba[3] != 255)
{
    Console.Error.WriteLine("FAIL: the layer surface did not render the config-driven colour (expected R=40).");
    return 3;
}

Console.WriteLine("AbiGlSmoke OK - a native plugin GL layer surface, created from a config blob + registered into the compositor registry, rendered into the canvas FBO on a real GL context (config drove the colour).");
return 0;

static bool CompilePlugin(string cFile, string includeDir, string outSo)
{
    var psi = new ProcessStartInfo("gcc", $"-shared -fPIC -I\"{includeDir}\" \"{cFile}\" -o \"{outSo}\"")
    {
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var p = Process.Start(psi);
    if (p is null)
    {
        Console.Error.WriteLine("could not start gcc");
        return false;
    }

    var err = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0)
    {
        Console.Error.WriteLine($"gcc failed ({p.ExitCode}):\n{err}");
        return false;
    }

    return true;
}

static string FindNextRoot(string start)
{
    var configured = Environment.GetEnvironmentVariable("MFPLAYER_NEXT_ROOT");
    if (!string.IsNullOrWhiteSpace(configured)
        && File.Exists(Path.Combine(configured, "MFPlayer.sln")))
        return Path.GetFullPath(configured);

    var d = new DirectoryInfo(start);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "MFPlayer.sln")))
        d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("MFPlayer.sln not found above " + start);
}
