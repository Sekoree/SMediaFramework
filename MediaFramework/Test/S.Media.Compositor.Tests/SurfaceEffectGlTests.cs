using S.Media.Compositor.Effects;
using S.Media.Core.Video;
using S.Media.Present.SDL3;
using Silk.NET.OpenGL;
using Xunit;
using PixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.Compositor.Tests;

/// <summary>
/// Real-GL coverage for the surface-layer effect path: a surface with a chroma-key/color chain is
/// rendered into the intermediate texture and composited through the effect-variant layer shader.
/// Two guarantees pinned here: (1) a no-op effect chain is pixel-identical to the direct surface
/// render (the intermediate hop must not flip/shift/re-tint anything), and (2) a green-screen key
/// actually keys the surface's green output. Skips when the host has no GL.
/// </summary>
public sealed class SurfaceEffectGlTests
{
    private const int W = 32;
    private const int H = 16;
    private static readonly VideoFormat Canvas = new(W, H, PixelFormat.Bgra32, new Rational(30, 1));

    /// <summary>Paints the window-bottom half red and window-top half blue (full canvas, opaque) -
    /// an orientation-revealing pattern: any V-flip in the intermediate hop swaps the halves.</summary>
    private sealed class TwoToneSurface : IVideoCompositorLayerSurface
    {
        public void ConfigureGl(GL gl, VideoFormat canvas) { }

        public void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity)
        {
            gl.Enable(EnableCap.ScissorTest);
            gl.Scissor(0, 0, W, H / 2);
            gl.ClearColor(1f, 0f, 0f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            gl.Scissor(0, H / 2, W, H - (H / 2));
            gl.ClearColor(0f, 0f, 1f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            gl.Disable(EnableCap.ScissorTest);
        }

        public void Dispose() { }
    }

    private sealed class SolidGreenSurface : IVideoCompositorLayerSurface
    {
        public void ConfigureGl(GL gl, VideoFormat canvas) { }

        public void Render(GL gl, uint targetFbo, TimeSpan masterTime, LayerTransform2D transform, float opacity)
        {
            gl.ClearColor(0f, 1f, 0f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
        }

        public void Dispose() { }
    }

    [SkippableFact]
    public void NoOpEffectChain_IsPixelIdenticalToDirectSurfaceRender()
    {
        Skip.IfNot(SDL3GLVideoCompositor.TryProbe(out var glError), $"no GL on this host: {glError}");

        var surface = new TwoToneSurface();
        var direct = new CompositorSurfaceLayer(surface, LayerTransform2D.Identity, 1f);
        var noOp = direct with
        {
            Effects = [BrightnessContrastVideoEffect.Create(new BrightnessContrastSettings(0f, 1f))],
        };

        var directPixels = CompositeToPixels(direct);
        var effectPixels = CompositeToPixels(noOp);
        Assert.Equal(directPixels, effectPixels);

        // Sanity: the pattern itself is two-tone (guards against comparing two blank frames).
        var top = PixelAt(directPixels, W / 2, 1);
        var bottom = PixelAt(directPixels, W / 2, H - 2);
        Assert.NotEqual(top, bottom);
    }

    [SkippableFact]
    public void ChromaKeyOnSurfaceLayer_KeysOutTheSurfaceOutput()
    {
        Skip.IfNot(SDL3GLVideoCompositor.TryProbe(out var glError), $"no GL on this host: {glError}");

        var surface = new SolidGreenSurface();
        var plain = new CompositorSurfaceLayer(surface, LayerTransform2D.Identity, 1f);
        var keyed = plain with { Effects = [ChromaKeyVideoEffect.Create(ChromaKeySettings.GreenScreen)] };

        var plainPixels = CompositeToPixels(plain);
        var (pb, pg, pr, pa) = PixelAt(plainPixels, W / 2, H / 2);
        Assert.True(pg > 200 && pr < 30 && pb < 30 && pa > 250, $"expected opaque green, got b={pb} g={pg} r={pr} a={pa}");

        var keyedPixels = CompositeToPixels(keyed);
        var (_, _, _, ka) = PixelAt(keyedPixels, W / 2, H / 2);
        Assert.True(ka < 10, $"green-screen key should make the surface transparent, got alpha {ka}");
    }

    /// <summary>Composites the layer a few times (the readback is pipelined, so the first frames can
    /// lag) and returns the final frame's BGRA pixels as a flat copy.</summary>
    private static byte[] CompositeToPixels(CompositorSurfaceLayer layer)
    {
        Assert.True(SDL3GLVideoCompositor.TryCreate(Canvas, out var compositor, out var error), error);
        try
        {
            var host = Assert.IsAssignableFrom<IVideoCompositorSurfaceHost>(compositor);
            for (var i = 0; i < 3; i++)
                host.CompositeWithSurfaces([], [layer], TimeSpan.FromMilliseconds(i * 33)).Dispose();
            using var frame = host.CompositeWithSurfaces([], [layer], TimeSpan.FromMilliseconds(99));
            var stride = frame.Strides[0];
            var plane = frame.Planes[0].Span;
            var pixels = new byte[W * H * 4];
            for (var y = 0; y < H; y++)
                plane.Slice(y * stride, W * 4).CopyTo(pixels.AsSpan(y * W * 4));
            return pixels;
        }
        finally
        {
            compositor?.Dispose();
        }
    }

    private static (byte B, byte G, byte R, byte A) PixelAt(byte[] pixels, int x, int y)
    {
        var i = (y * W + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }
}
