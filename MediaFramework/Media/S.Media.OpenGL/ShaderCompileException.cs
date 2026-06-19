using Silk.NET.OpenGL;

namespace S.Media.OpenGL;

internal sealed class ShaderCompileException : InvalidOperationException
{
    public ShaderCompileException(ShaderType shaderType, string driverLog, string source, string dumpPath)
        : base($"{shaderType} compile failed: {driverLog}\n(source dumped to {dumpPath})")
    {
        ShaderType = shaderType;
        DriverLog = driverLog;
        ShaderSource = source;
        DumpPath = dumpPath;
    }

    public ShaderType ShaderType { get; }
    public string DriverLog { get; }
    public string ShaderSource { get; }
    public string DumpPath { get; }
}
