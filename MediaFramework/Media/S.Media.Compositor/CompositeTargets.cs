namespace S.Media.Compositor;

/// <summary>A pixel rectangle in a destination GL framebuffer (origin bottom-left, GL convention).</summary>
public readonly record struct CompositeViewport(int X, int Y, int Width, int Height);

/// <summary>
/// Where one <c>CompositeMulti</c> output goes (Doc 04 §4). The compositor composites the layers once into
/// its retained canvas, runs each output's warp pass, then routes the warped result to its target — three
/// domains: same-context GL (zero-copy), CPU readback, or an exported external image (zero-copy across an
/// API/context boundary). One of <see cref="GlCompositeTarget"/> / <see cref="CpuFrameCompositeTarget"/> /
/// <see cref="ExternalImageCompositeTarget"/>.
/// </summary>
public interface ICompositeOutputTarget;

/// <summary>
/// Zero-copy, <strong>same context/thread</strong>: blit the warped output straight into a GL framebuffer
/// the caller owns on the compositor's context (an SDL3 window's FBO, a local GL surface). No readback.
/// </summary>
public sealed class GlCompositeTarget : ICompositeOutputTarget
{
    /// <summary>Destination GL framebuffer name (0 = the context's default/window framebuffer).</summary>
    public required uint Framebuffer { get; init; }

    /// <summary>Destination rectangle within <see cref="Framebuffer"/>.</summary>
    public required CompositeViewport Viewport { get; init; }
}

/// <summary>
/// CPU readback: one readback into a <see cref="VideoFrame"/> handed to <see cref="OnFrameReady"/>. The path
/// for NDI (its SDK send is CPU <c>p_data</c> — OQ3), file dump, CPU encoders, and the fallback when no
/// external-image type fits. The callback owns the frame and must dispose it.
/// </summary>
public sealed class CpuFrameCompositeTarget : ICompositeOutputTarget
{
    public required Action<VideoFrame> OnFrameReady { get; init; }
}

/// <summary>
/// Zero-copy across a context/API boundary (Avalonia's render context, a D3D11/Vulkan consumer, a cross-API
/// plugin): export the warped result as an external image (dmabuf fd on Linux / D3D11 DXGI shared handle on
/// Windows) plus a negotiated sync primitive (OQ2). The consumer imports it. Same handle currency as the D8
/// plugin frame ABI (D7). NDI is <strong>not</strong> an external-image target (OQ3) — use the CPU target.
/// </summary>
public sealed class ExternalImageCompositeTarget : ICompositeOutputTarget
{
    /// <summary>Handle kinds the consumer can import, best-first (e.g. <c>"dmabuf"</c>, <c>"d3d11-nt-handle"</c>).
    /// The compositor exports the first kind it can produce on this platform/context.</summary>
    public required IReadOnlyList<string> AcceptedHandleTypes { get; init; }

    /// <summary>Called on the compositor thread with the exported handle + negotiated sync. The handle is
    /// valid until the consumer signals done via <see cref="ExternalImageHandle.Release"/>.</summary>
    public required Action<ExternalImageHandle> OnImageReady { get; init; }
}

/// <summary>The negotiated cross-boundary synchronization primitive for an exported image (OQ2).</summary>
public enum ExternalImageSyncKind
{
    /// <summary>No explicit sync — the producer finished (e.g. a glFinish/fence already waited).</summary>
    None = 0,
    /// <summary>A GL/EGL/Vulkan sync-fd fence the consumer waits on before reading (Linux).</summary>
    SyncFdFence = 1,
    /// <summary>A D3D11 keyed mutex on the shared resource (Windows).</summary>
    KeyedMutex = 2,
    /// <summary>A binary/timeline semaphore handle (Vulkan/GL).</summary>
    Semaphore = 3,
}

/// <summary>
/// An exported external image plus its negotiated sync (Doc 04 §4 / OQ2). The producer fills the handle
/// fields for the exported <see cref="HandleType"/>; the consumer imports the image, waits on the sync,
/// reads, then calls <see cref="Release"/>. A single struct so dmabuf and D3D11 share one currency.
/// </summary>
public sealed class ExternalImageHandle
{
    public required string HandleType { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>DRM FourCC of the exported image (Linux dmabuf), or 0 when not applicable.</summary>
    public uint DrmFourcc { get; init; }
    /// <summary>DRM format modifier (Linux dmabuf), or 0.</summary>
    public ulong DrmModifier { get; init; }
    /// <summary>dmabuf file descriptor(s) (Linux), one per plane; empty otherwise.</summary>
    public IReadOnlyList<int> DmabufFds { get; init; } = Array.Empty<int>();
    /// <summary>Per-plane byte offset for <see cref="DmabufFds"/>.</summary>
    public IReadOnlyList<int> Offsets { get; init; } = Array.Empty<int>();
    /// <summary>Per-plane stride for <see cref="DmabufFds"/>.</summary>
    public IReadOnlyList<int> Strides { get; init; } = Array.Empty<int>();
    /// <summary>D3D11 DXGI NT shared handle (Windows), or 0.</summary>
    public nint D3D11SharedHandle { get; init; }

    /// <summary>The negotiated sync primitive the consumer must honor before reading.</summary>
    public ExternalImageSyncKind SyncKind { get; init; }
    /// <summary>The sync object handle (sync-fd, semaphore fd, or keyed-mutex key), per <see cref="SyncKind"/>.</summary>
    public nint SyncHandle { get; init; }

    /// <summary>
    /// Consumer calls this once it has finished importing/reading so the producer can recycle the backing and
    /// close any fds it still owns. Implementations must tolerate calls from a consumer/render thread while the
    /// producer compositor is still alive; producer-owned GL cleanup may be deferred to the compositor thread.
    /// </summary>
    public required Action Release { get; init; }
}

/// <summary>A <see cref="WarpOutputRequest"/> paired with where its warped result is delivered.</summary>
public readonly record struct TargetedWarpOutput(WarpOutputRequest Request, ICompositeOutputTarget Target);
