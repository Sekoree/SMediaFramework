namespace S.Media.Routing;

/// <summary>
/// Per-router video fan-out converter factories, wired from the media registry.
/// </summary>
/// <remarks>
/// Set <see cref="VideoCpuFrameConverterFactory"/> from <c>IMediaRegistry.CreateCpuConverter</c>. When it
/// is <see langword="null"/> the router has no CPU conversion path and throws if a branch needs one.
/// </remarks>
public sealed record VideoRouterOptions(
    Func<IVideoCpuFrameConverter>? VideoCpuFrameConverterFactory = null,
    Func<PixelFormat, PixelFormat, int, int, bool>? VideoCpuFrameCanConvertProbe = null)
{
    public static VideoRouterOptions Default { get; } = new();
}
