using System.Diagnostics.CodeAnalysis;
using S.Media.Core;

namespace S.Media.Core.Video;

/// <summary>
/// One decoded/received video frame. Carries one or more byte planes plus the
/// presentation time the producer attached to them.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="Audio.AudioFrame"/>, video frames are large enough that
/// per-frame allocation isn't viable (1080p BGRA32 ≈ 8 MB). Producers pass an
/// optional <see cref="IDisposable"/> <c>release</c> to free the underlying buffer - refcount
/// decrement (NDI), <c>av_frame_unref</c> (FFmpeg), <c>ArrayPool.Return</c>,
/// etc. The frame is one-shot disposable; calling <see cref="Dispose"/> twice
/// is safe and only the first call invokes the release.
/// </para>
/// <para>
/// Optional metadata (transfer hint, color space / range, field order, SMPTE
/// timecode, alpha mode) is bundled into <see cref="Metadata"/>; individual
/// properties (<see cref="ColorTransferHint"/>, <see cref="ColorSpace"/>, etc.)
/// forward to the bundle for convenient access.
/// </para>
/// </remarks>
public sealed partial class VideoFrame : IDisposable
{
    private IDisposable? _release;
    private readonly ReadOnlyMemory<byte>[] _planes;
    private readonly int[] _strides;
    private readonly VideoFrameHardwareBacking? _hardwareBacking;

    public TimeSpan PresentationTime { get; }
    public VideoFormat Format { get; }

    /// <summary>Per-frame metadata bundle - see <see cref="VideoFrameMetadata"/>.</summary>
    public VideoFrameMetadata Metadata { get; }

    /// <inheritdoc cref="VideoFrameMetadata.ColorTransferHint" />
    public VideoTransferHint ColorTransferHint => Metadata.ColorTransferHint;

    /// <inheritdoc cref="VideoFrameMetadata.ColorSpace" />
    public VideoColorSpace ColorSpace => Metadata.ColorSpace;

    /// <inheritdoc cref="VideoFrameMetadata.ColorRange" />
    public VideoColorRange ColorRange => Metadata.ColorRange;

    /// <inheritdoc cref="VideoFrameMetadata.FieldOrder" />
    public VideoFieldOrder FieldOrder => Metadata.FieldOrder;

    /// <inheritdoc cref="VideoFrameMetadata.Timecode" />
    public VideoTimecode? Timecode => Metadata.Timecode;

    /// <inheritdoc cref="VideoFrameMetadata.AlphaMode" />
    public VideoAlphaMode AlphaMode => Metadata.AlphaMode;

    /// <summary>Hardware backing when present; <c>null</c> for CPU-only frames.</summary>
    public VideoFrameHardwareBacking? HardwareBacking => _hardwareBacking;

    public DmabufNv12Backing? DmabufNv12 => _hardwareBacking as DmabufNv12Backing;
    public DmabufP010Backing? DmabufP010 => _hardwareBacking as DmabufP010Backing;
    public DmabufP016Backing? DmabufP016 => _hardwareBacking as DmabufP016Backing;
    public Win32SharedNv12Backing? Win32Nv12 => _hardwareBacking as Win32SharedNv12Backing;

    /// <summary>
    /// Plane bytes, in pixel-format order. <see cref="PixelFormat.I420"/> is
    /// Y, U, V; <see cref="PixelFormat.Nv12"/> is Y, UV; etc. Exposed as a
    /// concrete array so hot-path consumers can index without interface
    /// dispatch - do not mutate.
    /// </summary>
    public ReadOnlyMemory<byte>[] Planes => _planes;

    /// <summary>
    /// Stride (pitch) in bytes for each plane - may exceed the visible row
    /// width for alignment padding. Exposed as a concrete array (and a
    /// <see cref="ReadOnlySpan{Int32}"/> via <see cref="StrideSpan"/>) - do
    /// not mutate.
    /// </summary>
    public int[] Strides => _strides;

    /// <summary>Stride view as a <see cref="ReadOnlySpan{Int32}"/> for hot loops.</summary>
    public ReadOnlySpan<int> StrideSpan => _strides;

    /// <summary>Number of byte planes. Same as <c>Planes.Length</c>.</summary>
    public int PlaneCount => _planes.Length;

    /// <summary>Canonical constructor - at most one <paramref name="backing"/>.</summary>
    public VideoFrame(
        TimeSpan presentationTime,
        VideoFormat format,
        ReadOnlyMemory<byte>[] planes,
        int[] strides,
        VideoFrameMetadata metadata = default,
        VideoFrameHardwareBacking? backing = null,
        IDisposable? release = null)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(strides);
        PresentationTime = presentationTime;
        Format = format;
        Metadata = metadata;
        _hardwareBacking = backing;
        ValidateHardwareBacking(backing, format, planes, strides);
        _planes = planes;
        _strides = strides;
        _release = release;
    }

    /// <summary>Builds an NV12 <see cref="VideoFrame"/> wrapping Linux DRM PRIME dma-bufs (zero-copy).</summary>
    public static VideoFrame CreateNv12Dmabuf(
        TimeSpan presentationTime,
        VideoFormat format,
        DmabufNv12Backing dmaBufNv12Backing,
        VideoFrameMetadata metadata = default,
        Action? additionalRelease = null) =>
        dmaBufNv12Backing.CreateFrame(presentationTime, format, metadata, additionalRelease);

    /// <summary>
    /// Builds a second <see cref="VideoFrame"/> that shares <paramref name="source"/>'s
    /// <see cref="VideoFrame.DmabufNv12"/> backing (refcounted). Each frame must be disposed independently.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="source"/> is not an NV12 dma-buf frame.</exception>
    public static VideoFrame CreateNv12DmabufSharedReference(VideoFrame source, VideoTransferHint? colorTransferHint = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.DmabufNv12 is null)
            throw new ArgumentException("Source is not an NV12 dma-buf frame.", nameof(source));
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(CreateNv12DmabufOnlyLinux);

        var meta = colorTransferHint is { } hint
            ? source.Metadata with { ColorTransferHint = hint }
            : source.Metadata;
        return source.DmabufNv12.CreateSharedReference(source.PresentationTime, source.Format, meta);
    }

    /// <summary>Builds a P010 <see cref="VideoFrame"/> wrapping Linux DRM PRIME dma-bufs (zero-copy).</summary>
    public static VideoFrame CreateP010Dmabuf(
        TimeSpan presentationTime,
        VideoFormat format,
        DmabufP010Backing dmaBufP010Backing,
        VideoFrameMetadata metadata = default,
        Action? additionalRelease = null) =>
        dmaBufP010Backing.CreateFrame(presentationTime, format, metadata, additionalRelease);

    /// <summary>Builds a P016 <see cref="VideoFrame"/> wrapping Linux DRM PRIME dma-bufs (zero-copy).</summary>
    public static VideoFrame CreateP016Dmabuf(
        TimeSpan presentationTime,
        VideoFormat format,
        DmabufP016Backing dmaBufP016Backing,
        VideoFrameMetadata metadata = default,
        Action? additionalRelease = null) =>
        dmaBufP016Backing.CreateFrame(presentationTime, format, metadata, additionalRelease);

    /// <summary>
    /// Builds a second <see cref="VideoFrame"/> that shares <paramref name="source"/>'s
    /// <see cref="VideoFrame.DmabufP016"/> backing (refcounted). Each frame must be disposed independently.
    /// </summary>
    public static VideoFrame CreateP016DmabufSharedReference(VideoFrame source, VideoTransferHint? colorTransferHint = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.DmabufP016 is null)
            throw new ArgumentException("Source is not a P016 dma-buf frame.", nameof(source));
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(CreateP016DmabufOnlyLinux);

        var meta = colorTransferHint is { } hint
            ? source.Metadata with { ColorTransferHint = hint }
            : source.Metadata;
        return source.DmabufP016.CreateSharedReference(source.PresentationTime, source.Format, meta);
    }

    /// <summary>
    /// Builds a second <see cref="VideoFrame"/> that shares <paramref name="source"/>'s
    /// <see cref="VideoFrame.DmabufP010"/> backing (refcounted). Each frame must be disposed independently.
    /// </summary>
    public static VideoFrame CreateP010DmabufSharedReference(VideoFrame source, VideoTransferHint? colorTransferHint = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.DmabufP010 is null)
            throw new ArgumentException("Source is not a P010 dma-buf frame.", nameof(source));
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(CreateP010DmabufOnlyLinux);

        var meta = colorTransferHint is { } hint
            ? source.Metadata with { ColorTransferHint = hint }
            : source.Metadata;
        return source.DmabufP010.CreateSharedReference(source.PresentationTime, source.Format, meta);
    }

    internal const string CreateNv12DmabufOnlyLinux = "CreateNv12Dmabuf is supported on Linux only.";
    internal const string CreateP010DmabufOnlyLinux = "CreateP010Dmabuf is supported on Linux only.";
    internal const string CreateP016DmabufOnlyLinux = "CreateP016Dmabuf is supported on Linux only.";

    /// <summary>Builds an NV12 <see cref="VideoFrame"/> wrapping Windows DXGI/D3D11 NT shared handles (decoder export).</summary>
    public static VideoFrame CreateNv12Win32Shared(
        TimeSpan presentationTime,
        VideoFormat format,
        Win32SharedNv12Backing win32Nv12Backing,
        VideoFrameMetadata metadata = default,
        Action? additionalRelease = null) =>
        win32Nv12Backing.CreateFrame(presentationTime, format, metadata, additionalRelease);

    /// <summary>
    /// Builds a second <see cref="VideoFrame"/> that shares <paramref name="source"/>'s
    /// <see cref="VideoFrame.Win32Nv12"/> backing (refcounted). Each frame must be disposed independently.
    /// </summary>
    public static VideoFrame CreateNv12Win32SharedReference(VideoFrame source, VideoTransferHint? colorTransferHint = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Win32Nv12 is null)
            throw new ArgumentException("Source is not an NV12 Win32 shared-handle frame.", nameof(source));
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(CreateNv12Win32SharedOnlyWindows);

        var meta = colorTransferHint is { } hint
            ? source.Metadata with { ColorTransferHint = hint }
            : source.Metadata;
        return source.Win32Nv12.CreateSharedReference(source.PresentationTime, source.Format, meta);
    }

    internal const string CreateNv12Win32SharedOnlyWindows = "CreateNv12Win32Shared is supported on Windows only.";

    /// <summary>
    /// Builds <paramref name="viewCount"/> <see cref="VideoFrame"/> instances that share the same CPU NV12
    /// plane memory and invoke the source backing <c>release</c> exactly once after all views are disposed.
    /// On failure returns <c>false</c> and leaves <paramref name="source"/> usable (release restored when nothing was published).
    /// </summary>
    /// <remarks>
    /// Callers must not mutate shared plane bytes while any view is alive. When this returns <c>false</c>,
    /// fall back to a deep copy (for example <c>VideoCpuFrameConverter.DuplicateCpuBacking</c>).
    /// </remarks>
    public static bool TryCreateNv12CpuFanOutViews(
        VideoFrame source,
        int viewCount,
        VideoTransferHint hint,
        [NotNullWhen(true)] out VideoFrame[]? views)
    {
        // NV12-specific shape gate, then the generic CPU fan-out does the actual (rollback-safe)
        // view construction - one implementation of the delicate release-swap/countdown logic.
        if (source.Format.PixelFormat != PixelFormat.Nv12
            || source._planes.Length != 2 || source._strides.Length != 2)
        {
            views = null;
            return false;
        }

        return TryCreateCpuFanOutViews(source, viewCount, hint, out views);
    }

    /// <summary>
    /// Like <see cref="TryCreateNv12CpuFanOutViews"/> but for any CPU-backed pixel format: produces
    /// <paramref name="viewCount"/> independently-disposable frames that share <paramref name="source"/>'s
    /// planes (zero copy) and refcount its backing so the underlying release fires only once every view is
    /// disposed. The router uses this to hand a converting branch a raw view it repacks asynchronously on
    /// its own pump thread instead of converting on the submit thread. Fails (returns false, leaving
    /// <paramref name="source"/> untouched) for hardware (dma-buf / Win32) backings or a frame with no
    /// release to share.
    /// </summary>
    public static bool TryCreateCpuFanOutViews(
        VideoFrame source,
        int viewCount,
        VideoTransferHint hint,
        [NotNullWhen(true)] out VideoFrame[]? views)
    {
        views = null;
        if (viewCount < 2 || !IsCpuFanOutEligible(source))
            return false;

        var inner = Interlocked.Exchange(ref source._release, null);
        if (inner is null)
            return false;

        var viewsLocal = new VideoFrame[viewCount];
        var countdown = DisposableRelease.SharedCountdown(inner, viewCount);
        var viewMeta = source.Metadata with { ColorTransferHint = hint };
        var created = 0;
        try
        {
            for (; created < viewCount; created++)
            {
                viewsLocal[created] = new VideoFrame(
                    source.PresentationTime,
                    source.Format,
                    source._planes,
                    source._strides,
                    viewMeta,
                    release: DisposableRelease.Wrap(countdown.Dispose));
            }

            views = viewsLocal;
            return true;
        }
        catch
        {
            if (created == 0)
            {
                if (Interlocked.CompareExchange(ref source._release, inner, null) is not null)
                    inner.Dispose();
            }
            else
            {
                DisposableRelease.AdjustSharedCountdown(countdown, created - viewCount);
                for (var j = 0; j < created; j++)
                    viewsLocal[j].Dispose();
            }

            return false;
        }
    }

    private static bool IsCpuFanOutEligible(VideoFrame source)
    {
        if (source._hardwareBacking is not null)
            return false;
        if (source._planes.Length == 0)
            return false;
        foreach (var p in source._planes)
        {
            if (p.IsEmpty)
                return false;
        }

        return true;
    }

    /// <summary>Convenience overload for single-plane (packed) formats like <see cref="PixelFormat.Bgra32"/>.</summary>
    public VideoFrame(
        TimeSpan presentationTime,
        VideoFormat format,
        ReadOnlyMemory<byte> plane,
        int stride,
        VideoFrameMetadata metadata = default,
        IDisposable? release = null)
        : this(presentationTime, format, [plane], [stride], metadata, null, release) { }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Dispose();
}
