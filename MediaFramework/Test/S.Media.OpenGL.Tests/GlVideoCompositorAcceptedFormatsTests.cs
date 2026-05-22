using System.Linq;
using S.Media.Core.Video;
using S.Media.Effects.OpenGL;
using S.Media.OpenGL;
using Xunit;

namespace S.Media.OpenGL.Tests;

/// <summary>
/// Static-surface checks that don't need a live GL context — they ensure the compositor's
/// accepted-layer list matches the renderer's supported-format set, so the user's
/// <c>yuva444p12le</c> over <c>yuv422p10le</c> pipeline doesn't fall back to BGRA32 conversion.
/// </summary>
public sealed class GlVideoCompositorAcceptedFormatsTests
{
    [Theory]
    [InlineData(PixelFormat.Bgra32)]
    [InlineData(PixelFormat.Yuv422P10Le)]
    [InlineData(PixelFormat.Yuv422P12Le)]
    [InlineData(PixelFormat.Yuv444P10Le)]
    [InlineData(PixelFormat.Yuv444P12Le)]
    [InlineData(PixelFormat.Yuv420P10Le)]
    [InlineData(PixelFormat.Yuv420P12Le)]
    [InlineData(PixelFormat.Yuva420p)]
    [InlineData(PixelFormat.Yuva422P)]
    [InlineData(PixelFormat.Yuva444P)]
    [InlineData(PixelFormat.Yuva420P10Le)]
    [InlineData(PixelFormat.Yuva422P10Le)]
    [InlineData(PixelFormat.Yuva444P10Le)]
    [InlineData(PixelFormat.Yuva422P12Le)]
    [InlineData(PixelFormat.Yuva444P12Le)]
    [InlineData(PixelFormat.Yuva420P16Le)]
    [InlineData(PixelFormat.Yuva422P16Le)]
    [InlineData(PixelFormat.Yuva444P16Le)]
    [InlineData(PixelFormat.Nv12)]
    [InlineData(PixelFormat.I420)]
    public void GlCompositorAccepted_Includes_KeyProfessionalFormats(PixelFormat fmt)
    {
        // We construct a stub compositor type purely to read its static AcceptedLayerPixelFormats
        // surface — no GL context is needed because the accepted set is a static initialiser.
        var accepted = (System.Collections.Generic.IReadOnlyList<PixelFormat>)
            typeof(GlVideoCompositor)
                .GetField("AcceptedFormatsArr",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(null)!;

        Assert.Contains(fmt, accepted);
    }

    [Fact]
    public void AcceptedSet_Mirrors_YuvVideoRendererSupportedSet()
    {
        var accepted = (System.Collections.Generic.IReadOnlyList<PixelFormat>)
            typeof(GlVideoCompositor)
                .GetField("AcceptedFormatsArr",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(null)!;
        Assert.Equal(
            YuvVideoRenderer.SupportedPixelFormats.OrderBy(static x => (int)x),
            accepted.OrderBy(static x => (int)x));
    }
}
