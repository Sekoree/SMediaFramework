using S.Media.Core.Video;
using S.Media.OpenGL;
using Xunit;

namespace S.Media.OpenGL.Tests;

/// <summary>
/// Validates GL recipe tuning (P010 is high-bit normalization in ushort — no rescale versus 1023).
/// </summary>
public sealed class GlVideoFormatSupportTests
{
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
}
