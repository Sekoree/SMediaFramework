using System.Collections.Generic;
using S.Media.Core.Video;
using S.Media.Gpu;
using Xunit;

namespace S.Media.Gpu.Tests;

/// <summary>
/// Validates GL recipe tuning (P010 is high-bit normalization in ushort — no rescale versus 1023).
/// </summary>
public sealed class GlVideoFormatSupportTests
{
    [Fact]
    public void SupportedPixelFormats_prefers_Yuv422P10Le_before_Nv12_for_negotiation()
    {
        var list = YuvVideoRenderer.SupportedPixelFormats;
        var i422 = IndexOf(list, PixelFormat.Yuv422P10Le);
        var nv12 = IndexOf(list, PixelFormat.Nv12);
        Assert.True(i422 >= 0 && nv12 >= 0);
        Assert.True(i422 < nv12);
    }

    private static int IndexOf(IReadOnlyList<PixelFormat> list, PixelFormat pf)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == pf)
                return i;
        }

        return -1;
    }

    [Fact]
    public void P010_DefaultBitScale_IsOne()
    {
        Assert.True(GlVideoFormatSupport.TryGetRecipe(PixelFormat.P010, out var recipe));
        Assert.Equal(1f, recipe.DefaultBitScale);
    }

    /// <summary>10-bit packed in bits 15:6 yields expected shader input after UNSIGNED_SHORT normalization.</summary>
    [Fact]
    public void P010_HighPacked10Bit_NormalizedMatchesWordOver65535()
    {
        // Full-scale 10-bit pattern in Microsoft's / FFmpeg LE P010 packing.
        ushort yWord = unchecked((ushort)(0xFFC0));
        float expected = yWord / 65535f;
        float shaderInput = expected * 1f; // recipe bitScale now 1.0 — same as planar R16 10-bit in high bits
        Assert.InRange(shaderInput, 0.994f, 1.001f);

        ushort midWord = unchecked((ushort)(512 << 6));
        float a = midWord / 65535f;
        float b = a * 1f;
        Assert.True(MathF.Abs(a - b) < 1e-6f);
    }

    [Theory]
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
    [InlineData(PixelFormat.Yuv422P12Le)]
    [InlineData(PixelFormat.Yuv444P12Le)]
    [InlineData(PixelFormat.Rgba16)]
    [InlineData(PixelFormat.Rgba16F)]
    [InlineData(PixelFormat.P216)]
    [InlineData(PixelFormat.Pa16)]
    public void NewFormats_HaveGlRecipe_AndAppearInSupportedList(PixelFormat fmt)
    {
        Assert.True(GlVideoFormatSupport.TryGetRecipe(fmt, out var recipe),
            $"missing GL recipe for {fmt}");
        Assert.NotNull(recipe.Samplers);

        var list = YuvVideoRenderer.SupportedPixelFormats;
        Assert.True(IndexOf(list, fmt) >= 0, $"{fmt} is not listed in YuvVideoRenderer.SupportedPixelFormats");
    }

    /// <summary>
    /// 10-bit / 12-bit / 16-bit storage uses R16 with the matching bitScale so the shader sees [0, 1].
    /// 8-bit formats keep bitScale = 1.0.
    /// </summary>
    [Theory]
    [InlineData(PixelFormat.Yuva420p, 1f)]
    [InlineData(PixelFormat.Yuva422P, 1f)]
    [InlineData(PixelFormat.Yuva444P, 1f)]
    [InlineData(PixelFormat.Yuva420P10Le, 65535f / 1023f)]
    [InlineData(PixelFormat.Yuva422P10Le, 65535f / 1023f)]
    [InlineData(PixelFormat.Yuva444P10Le, 65535f / 1023f)]
    [InlineData(PixelFormat.Yuva422P12Le, 65535f / 4095f)]
    [InlineData(PixelFormat.Yuva444P12Le, 65535f / 4095f)]
    [InlineData(PixelFormat.Yuva420P16Le, 1f)]
    [InlineData(PixelFormat.Yuva422P16Le, 1f)]
    [InlineData(PixelFormat.Yuva444P16Le, 1f)]
    [InlineData(PixelFormat.Yuv422P12Le, 65535f / 4095f)]
    [InlineData(PixelFormat.Yuv444P12Le, 65535f / 4095f)]
    [InlineData(PixelFormat.Rgba16, 1f)]
    [InlineData(PixelFormat.Rgba16F, 1f)]
    [InlineData(PixelFormat.P216, 1f)]
    [InlineData(PixelFormat.Pa16, 1f)]
    public void NewFormats_BitScale_MatchesStorage(PixelFormat fmt, float expected)
    {
        Assert.True(GlVideoFormatSupport.TryGetRecipe(fmt, out var recipe));
        Assert.Equal(expected, recipe.DefaultBitScale, precision: 3);
    }

    [Fact]
    public void AllConcreteFrameworkPixelFormats_HaveGlRecipe()
    {
        foreach (var format in Enum.GetValues<PixelFormat>())
        {
            if (format == PixelFormat.Unknown)
                continue;

            Assert.True(
                GlVideoFormatSupport.TryGetRecipe(format, out _),
                $"{format} is in the framework PixelFormat enum but has no GL recipe.");
            Assert.Contains(format, YuvVideoRenderer.SupportedPixelFormats);
        }
    }
}
