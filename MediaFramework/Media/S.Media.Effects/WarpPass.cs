using S.Media.Core.Video;

namespace S.Media.Effects;

/// <summary>One warp section: a crop of the composited canvas plus its affine placement (canvas
/// pixels → warp-output pixels) and opacity. See <see cref="IWarpPassVideoCompositor"/>.</summary>
public readonly record struct WarpSection(RectNormalized SourceCrop, LayerTransform2D Transform, float Opacity);

/// <summary>
/// Optional compositor capability: after compositing the layers, render the warp sections from the
/// composited canvas into a (possibly differently sized) output — entirely on the GPU, with a
/// single readback at the end. This is the integrated fast path for output mapping
/// (Doc/HaPlay-Output-Mapping-Plan.md Phase 2); chaining two compositors costs an extra readback +
/// re-upload per frame instead.
/// </summary>
public interface IWarpPassVideoCompositor : IVideoCompositor
{
    /// <summary>
    /// Configures the warp pass. With non-null <paramref name="sections"/>, subsequent
    /// <see cref="IVideoCompositor.Composite"/> calls return frames of <paramref name="warpOutput"/>
    /// size containing the warped sections; null disables the pass (raw canvas again).
    /// Thread-safe snapshot swap — callable while another thread composites.
    /// </summary>
    void SetWarpPass(VideoFormat warpOutput, IReadOnlyList<WarpSection>? sections);
}
