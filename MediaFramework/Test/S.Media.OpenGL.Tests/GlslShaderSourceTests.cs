using S.Media.OpenGL;
using Xunit;

namespace S.Media.OpenGL.Tests;

public sealed class GlslShaderSourceTests
{
    [Fact]
    public void ConvertDesktop330ToEs300_ReplacesVersionAndAddsPrecisionHeader()
    {
        const string desktop = """
                              #version 330 core
                              layout(location = 0) in vec2 a_pos;
                              out vec2 v_uv;
                              void main() { v_uv = a_pos; }
                              """;

        var es = GlslShaderSource.ConvertDesktop330ToEs300(desktop);

        Assert.StartsWith("#version 300 es\nprecision highp float;\nprecision highp int;\n", es, StringComparison.Ordinal);
        Assert.DoesNotContain("#version 330 core", es, StringComparison.Ordinal);
        Assert.Contains("layout(location = 0) in vec2 a_pos;", es, StringComparison.Ordinal);
        Assert.Contains("out vec2 v_uv;", es, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ERROR: 0:1: 'core' : invalid version directive")]
    [InlineData("out : storage qualifier supported in GLSL ES 3.00 and above only")]
    public void LooksLikeDesktopSourceOnEsCompiler_DetectsProfileMismatch(string log)
    {
        Assert.True(GlslShaderSource.LooksLikeDesktopSourceOnEsCompiler(log));
    }
}
