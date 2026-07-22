using System.Numerics;
using S.Media.Core.Video;

namespace S.Media.Compositor;


/// <summary>
/// One output requested from a single composited canvas. <see cref="Sections"/> = null means
/// full-canvas passthrough scaled to <see cref="OutputFormat"/>; an empty section list means
/// a mapped output with no enabled sections, so the result is transparent black.
/// </summary>
public readonly record struct WarpOutputRequest(
    VideoFormat OutputFormat,
    IReadOnlyList<WarpSection>? Sections);


/// <summary>
/// Optional compositor capability: after compositing the layers, render the warp sections from the
/// composited canvas into a (possibly differently sized) output - entirely on the GPU, with a
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
    /// Thread-safe snapshot swap - callable while another thread composites.
    /// </summary>
    void SetWarpPass(VideoFormat warpOutput, IReadOnlyList<WarpSection>? sections);

    /// <summary>
    /// Composite <paramref name="layersBackToFront"/> once into the internal canvas, then emit one
    /// CPU-readable frame for each requested output by running each output's warp pass against the
    /// retained canvas texture. Implementations must return frames in request order.
    /// </summary>
    IReadOnlyList<VideoFrame> CompositeMulti(
        IReadOnlyList<CompositorLayer> layersBackToFront,
        IReadOnlyList<WarpOutputRequest> outputs,
        TimeSpan presentationTime);

    /// <summary>
    /// Like <see cref="CompositeMulti"/> but routes each output to a typed <see cref="ICompositeOutputTarget"/>
    /// (Doc 04 §4): composite the layers once, warp each output, then deliver it zero-copy into a GL
    /// framebuffer (<see cref="GlCompositeTarget"/>), as a CPU readback frame (<see cref="CpuFrameCompositeTarget"/>),
    /// or as an exported external image (<see cref="ExternalImageCompositeTarget"/>). Results go to the
    /// per-target callbacks/FBOs in request order; nothing is returned. Runs on the compositor's GL thread.
    /// </summary>
    void CompositeMultiToTargets(
        IReadOnlyList<CompositorLayer> layersBackToFront,
        IReadOnlyList<TargetedWarpOutput> targets,
        TimeSpan presentationTime);
}
