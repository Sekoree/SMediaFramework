// Minimal GL probe: create an SDL3 GL context exactly like SDL3GLVideoOutput, print GL strings, and
// report whether the WGL_NV_DX_interop entry points (the zero-copy D3D11->GL hand-off) resolve on this
// driver/context. Answers: is the SDL output on native WGL or ANGLE, and is zero-copy possible here?
using SDL3;
using Silk.NET.OpenGL;

if (!SDL.Init(SDL.InitFlags.Video))
{
    Console.Error.WriteLine("SDL_Init failed: " + SDL.GetError());
    return 1;
}

SDL.GLSetAttribute(SDL.GLAttr.ContextMajorVersion, 3);
SDL.GLSetAttribute(SDL.GLAttr.ContextMinorVersion, 3);
SDL.GLSetAttribute(SDL.GLAttr.ContextProfileMask, (int)SDL.GLProfile.Core);
SDL.GLSetAttribute(SDL.GLAttr.DoubleBuffer, 1);

var win = SDL.CreateWindow("glprobe", 320, 240, SDL.WindowFlags.OpenGL | SDL.WindowFlags.Hidden);
if (win == nint.Zero)
{
    Console.Error.WriteLine("SDL_CreateWindow failed: " + SDL.GetError());
    return 1;
}

var ctx = SDL.GLCreateContext(win);
if (ctx == nint.Zero)
{
    Console.Error.WriteLine("SDL_GL_CreateContext failed: " + SDL.GetError());
    return 1;
}

if (!SDL.GLMakeCurrent(win, ctx))
{
    Console.Error.WriteLine("SDL_GL_MakeCurrent failed: " + SDL.GetError());
    return 1;
}

var gl = GL.GetApi(name => SDL.GLGetProcAddress(name));
Console.WriteLine("GL_VENDOR   = " + (gl.GetStringS(StringName.Vendor) ?? "?"));
Console.WriteLine("GL_RENDERER = " + (gl.GetStringS(StringName.Renderer) ?? "?"));
Console.WriteLine("GL_VERSION  = " + (gl.GetStringS(StringName.Version) ?? "?"));
Console.WriteLine("GL_SHADING  = " + (gl.GetStringS(StringName.ShadingLanguageVersion) ?? "?"));

Console.WriteLine();
Console.WriteLine("WGL_NV_DX_interop entry points (zero-copy D3D11<->GL):");
string[] procs =
[
    "wglDXOpenDeviceNV", "wglDXCloseDeviceNV", "wglDXRegisterObjectNV",
    "wglDXUnregisterObjectNV", "wglDXLockObjectsNV", "wglDXUnlockObjectsNV",
];
var allPresent = true;
foreach (var p in procs)
{
    var addr = SDL.GLGetProcAddress(p);
    var ok = addr != nint.Zero;
    allPresent &= ok;
    Console.WriteLine($"  {p,-26} = {(ok ? "OK" : "MISSING")}");
}

Console.WriteLine();
Console.WriteLine(allPresent
    ? ">>> WGL_NV_DX_interop AVAILABLE — zero-copy D3D11->GL is possible on this SDL context."
    : ">>> WGL_NV_DX_interop UNAVAILABLE — must use CPU upload (or an ANGLE/EGL import path).");

// Also probe PBO support indicator + max texture size for the CPU-upload optimisation angle.
Console.WriteLine();
Console.WriteLine("GL_ARB_pixel_buffer_object in extensions: " +
    ((gl.GetStringS(StringName.Extensions) ?? "").Contains("pixel_buffer_object") ? "yes" : "(core in 3.3 regardless)"));

SDL.GLDestroyContext(ctx);
SDL.DestroyWindow(win);
SDL.Quit();
return 0;
