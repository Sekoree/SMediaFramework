namespace S.Media.Core.Video;

/// <summary>
/// Per-router plugin overrides for video fan-out behavior.
/// </summary>
/// <remarks>
/// When a value is <see langword="null"/>, <see cref="S.Media.Core.Diagnostics.MediaFrameworkPlugins"/>
/// remains the fallback so existing process-wide setup keeps working.
/// </remarks>
public sealed record VideoRouterOptions(
    Func<IVideoCpuFrameConverter>? VideoCpuFrameConverterFactory = null,
    Func<PixelFormat, PixelFormat, int, int, bool>? VideoCpuFrameCanConvertProbe = null)
{
    public static VideoRouterOptions Default { get; } = new();
}
