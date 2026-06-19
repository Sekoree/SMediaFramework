namespace S.Media.OpenGL;

internal static class GlslShaderSource
{
    internal static string ConvertDesktop330ToEs300(string source)
    {
        const string esHeader = "#version 300 es\nprecision highp float;\nprecision highp int;\n";
        if (!source.StartsWith("#version", StringComparison.Ordinal))
            return esHeader + source;

        var nl = source.IndexOf('\n');
        return nl < 0 ? esHeader : esHeader + source[(nl + 1)..];
    }

    internal static bool LooksLikeDesktopSourceOnEsCompiler(string driverLog) =>
        driverLog.Contains("invalid version directive", StringComparison.OrdinalIgnoreCase)
        || driverLog.Contains("GLSL ES", StringComparison.OrdinalIgnoreCase)
        || driverLog.Contains("'core'", StringComparison.OrdinalIgnoreCase);
}
