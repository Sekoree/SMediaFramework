using System.Diagnostics.CodeAnalysis;

namespace S.Media.Core.Video;

/// <summary>
/// One decoded/received video frame. Carries one or more byte planes plus the
/// presentation time the producer attached to them.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="Audio.AudioFrame"/>, video frames are large enough that
/// per-frame allocation isn't viable (1080p BGRA32 ≈ 8 MB). Producers pass an
/// optional <c>release</c> callback (or an <see cref="IDisposable"/> via
/// <c>disposableRelease</c>) to free the underlying buffer — refcount
/// decrement (NDI), <c>av_frame_unref</c> (FFmpeg), <c>ArrayPool.Return</c>,
/// etc. The frame is one-shot disposable; calling <see cref="Dispose"/> twice
/// is safe and only the first call invokes the release.
/// </para>
/// <para>
/// Optional metadata (transfer hint, color space / range, field order, SMPTE
/// timecode) is bundled into <see cref="Metadata"/>; individual properties
/// (<see cref="ColorTransferHint"/>, <see cref="ColorSpace"/>, etc.) forward
/// to the bundle for convenient access.
/// </para>
/// </remarks>
public sealed class VideoFrame : IDisposable
{
    private Action? _release;
    private IDisposable? _disposableRelease;
    private readonly ReadOnlyMemory<byte>[] _planes;
    private readonly int[] _strides;
    private readonly VideoDmabufNv12Backing? _dmabufNv12;
    private readonly VideoDmabufP010Backing? _dmabufP010;
    private readonly VideoDmabufP016Backing? _dmabufP016;
    private readonly VideoWin32Nv12Backing? _win32Nv12;

    public TimeSpan PresentationTime { get; }
    public VideoFormat Format { get; }

    /// <summary>Per-frame metadata bundle — see <see cref="VideoFrameMetadata"/>.</summary>
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

    /// <summary>
    /// When set, <see cref="Planes"/> are empty stubs; upload via DMA-BUF / EGL instead of CPU memory.
    /// </summary>
    public VideoDmabufNv12Backing? DmabufNv12 => _dmabufNv12;

    /// <summary>
    /// When set, <see cref="Planes"/> are empty stubs; upload P010 via DMA-BUF / EGL instead of CPU memory.
    /// </summary>
    public VideoDmabufP010Backing? DmabufP010 => _dmabufP010;

    /// <summary>
    /// When set, <see cref="Planes"/> are empty stubs; upload P016 via DMA-BUF / EGL instead of CPU memory.
    /// </summary>
    public VideoDmabufP016Backing? DmabufP016 => _dmabufP016;

    /// <summary>
    /// When set, <see cref="Planes"/> are empty stubs; upload via D3D11 shared texture / GL interop on Windows.
    /// </summary>
    public VideoWin32Nv12Backing? Win32Nv12 => _win32Nv12;

    /// <summary>
    /// Plane bytes, in pixel-format order. <see cref="PixelFormat.I420"/> is
    /// Y, U, V; <see cref="PixelFormat.Nv12"/> is Y, UV; etc. Exposed as a
    /// concrete array so hot-path consumers can index without interface
    /// dispatch — do not mutate.
    /// </summary>
    public ReadOnlyMemory<byte>[] Planes => _planes;

    /// <summary>
    /// Stride (pitch) in bytes for each plane — may exceed the visible row
    /// width for alignment padding. Exposed as a concrete array (and a
    /// <see cref="ReadOnlySpan{Int32}"/> via <see cref="StrideSpan"/>) — do
    /// not mutate.
    /// </summary>
    public int[] Strides => _strides;

    /// <summary>Stride view as a <see cref="ReadOnlySpan{Int32}"/> for hot loops.</summary>
    public ReadOnlySpan<int> StrideSpan => _strides;

    /// <summary>Number of byte planes. Same as <c>Planes.Length</c>.</summary>
    public int PlaneCount => _planes.Length;

    /// <param name="release">Optional <see cref="Action"/> invoked on <see cref="Dispose"/>. Suitable for closures that need to capture local state (e.g. pooled buffers).</param>
    /// <param name="disposableRelease">
    /// Optional <see cref="IDisposable"/> invoked on <see cref="Dispose"/>. Prefer this over <paramref name="release"/>
    /// when the cleanup target is already an <see cref="IDisposable"/> (e.g. a refcounted hardware backing) — avoids
    /// the per-frame method-group → delegate allocation that <paramref name="release"/> incurs. Both may be set; both
    /// fire on dispose.
    /// </param>
    /// <param name="metadata">Optional per-frame metadata (transfer hint / color space / range / field order / SMPTE timecode).</param>
    public VideoFrame(
        TimeSpan presentationTime,
        VideoFormat format,
        ReadOnlyMemory<byte>[] planes,
        int[] strides,
        Action? release = null,
        VideoDmabufNv12Backing? dmaBufNv12 = null,
        VideoDmabufP010Backing? dmaBufP010 = null,
        VideoDmabufP016Backing? dmaBufP016 = null,
        VideoWin32Nv12Backing? win32Nv12 = null,
        VideoFrameMetadata metadata = default,
        IDisposable? disposableRelease = null)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(strides);
        PresentationTime = presentationTime;
        Format = format;
        Metadata = metadata;
        _release = release;
        _disposableRelease = disposableRelease;
        _dmabufNv12 = dmaBufNv12;
        _dmabufP010 = dmaBufP010;
        _dmabufP016 = dmaBufP016;
        _win32Nv12 = win32Nv12;

        if (dmaBufNv12 is not null && dmaBufP010 is not null)
            throw new ArgumentException("A frame cannot combine NV12 and P010 DMA-BUF backings.", nameof(dmaBufP010));
        if (dmaBufNv12 is not null && dmaBufP016 is not null)
            throw new ArgumentException("A frame cannot combine NV12 and P016 DMA-BUF backings.", nameof(dmaBufP016));
        if (dmaBufNv12 is not null && win32Nv12 is not null)
            throw new ArgumentException("A frame cannot combine DMA-BUF and Win32 NV12 backings.", nameof(win32Nv12));
        if (dmaBufP010 is not null && dmaBufP016 is not null)
            throw new ArgumentException("A frame cannot combine P010 and P016 DMA-BUF backings.", nameof(dmaBufP016));
        if (dmaBufP010 is not null && win32Nv12 is not null)
            throw new ArgumentException("A frame cannot combine DMA-BUF P010 and Win32 NV12 backings.", nameof(win32Nv12));
        if (dmaBufP016 is not null && win32Nv12 is not null)
            throw new ArgumentException("A frame cannot combine DMA-BUF P016 and Win32 NV12 backings.", nameof(win32Nv12));

        if (dmaBufNv12 != null)
        {
            if (format.PixelFormat != PixelFormat.Nv12)
                throw new ArgumentException("DMA-BUF frames require PixelFormat.Nv12", nameof(format));
            if (planes.Length != 2 || strides.Length != 2)
                throw new ArgumentException("NV12 DMA-BUF requires two planes and strides (often empty stubs).",
                    nameof(planes));

            foreach (var p in planes)
            {
                if (!p.IsEmpty)
                    throw new ArgumentException(
                        "CPU plane memory must be empty for DMA-BUF frames; use stub ReadOnlyMemory<byte>.Empty entries.",
                        nameof(planes));
            }

                if (strides[0] != dmaBufNv12.YPlanePitchBytes || strides[1] != dmaBufNv12.UvPlanePitchBytes)
                    throw new ArgumentException(
                        "strides must mirror VideoDmabufNv12Backing pitches for NV12 dma-buf frames.", nameof(strides));
        }
        else if (dmaBufP010 != null)
        {
            if (format.PixelFormat != PixelFormat.P010)
                throw new ArgumentException("P010 DMA-BUF frames require PixelFormat.P010", nameof(format));
            if (planes.Length != 2 || strides.Length != 2)
                throw new ArgumentException("P010 DMA-BUF requires two planes and strides (often empty stubs).",
                    nameof(planes));

            foreach (var p in planes)
            {
                if (!p.IsEmpty)
                    throw new ArgumentException(
                        "CPU plane memory must be empty for DMA-BUF frames; use stub ReadOnlyMemory<byte>.Empty entries.",
                        nameof(planes));
            }

            if (strides[0] != dmaBufP010.YPlanePitchBytes || strides[1] != dmaBufP010.UvPlanePitchBytes)
                throw new ArgumentException(
                    "strides must mirror VideoDmabufP010Backing pitches for P010 dma-buf frames.", nameof(strides));
        }
        else if (dmaBufP016 != null)
        {
            if (format.PixelFormat != PixelFormat.P016)
                throw new ArgumentException("P016 DMA-BUF frames require PixelFormat.P016", nameof(format));
            if (planes.Length != 2 || strides.Length != 2)
                throw new ArgumentException("P016 DMA-BUF requires two planes and strides (often empty stubs).",
                    nameof(planes));

            foreach (var p in planes)
            {
                if (!p.IsEmpty)
                    throw new ArgumentException(
                        "CPU plane memory must be empty for DMA-BUF frames; use stub ReadOnlyMemory<byte>.Empty entries.",
                        nameof(planes));
            }

            if (strides[0] != dmaBufP016.YPlanePitchBytes || strides[1] != dmaBufP016.UvPlanePitchBytes)
                throw new ArgumentException(
                    "strides must mirror VideoDmabufP016Backing pitches for P016 dma-buf frames.", nameof(strides));
        }
        else if (win32Nv12 != null)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Win32 NV12 frames are Windows-only.");
            if (format.PixelFormat != PixelFormat.Nv12)
                throw new ArgumentException("Win32 shared-handle frames require PixelFormat.Nv12", nameof(format));
            if (planes.Length != 2 || strides.Length != 2)
                throw new ArgumentException("NV12 Win32 requires two planes and strides (often empty stubs).",
                    nameof(planes));

            foreach (var p in planes)
            {
                if (!p.IsEmpty)
                    throw new ArgumentException(
                        "CPU plane memory must be empty for Win32 NV12 frames; use stub ReadOnlyMemory<byte>.Empty entries.",
                        nameof(planes));
            }

            if (strides[0] != win32Nv12.YPlanePitchBytes || strides[1] != win32Nv12.UvPlanePitchBytes)
                throw new ArgumentException(
                    "strides must mirror VideoWin32Nv12Backing pitches for NV12 Win32 frames.", nameof(strides));
        }
        else
        {
            if (planes.Length == 0)
                throw new ArgumentException("at least one plane required", nameof(planes));
        }

        if (planes.Length != strides.Length)
            throw new ArgumentException("planes and strides must have the same length", nameof(strides));
        for (var i = 0; i < strides.Length; i++)
        {
            if (strides[i] <= 0)
                throw new ArgumentOutOfRangeException(nameof(strides), strides[i],
                    $"stride[{i}] must be positive");
        }

        _planes = planes;
        _strides = strides;
    }

    /// <summary>Builds an NV12 <see cref="VideoFrame"/> wrapping Linux DRM PRIME dma-bufs (zero-copy).</summary>
    /// <remarks>
    /// When <paramref name="additionalRelease"/> is null, the dma-buf backing is wired as
    /// <c>disposableRelease</c> directly — saves one method-group delegate allocation per frame on the
    /// fan-out hot path.
    /// </remarks>
    public static VideoFrame CreateNv12Dmabuf(
        TimeSpan presentationTime,
        VideoFormat format,
        VideoDmabufNv12Backing dmaBufNv12Backing,
        VideoFrameMetadata metadata = default,
        Action? additionalRelease = null)
    {
        ArgumentNullException.ThrowIfNull(dmaBufNv12Backing);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(CreateNv12DmabufOnlyLinux);

        ReadOnlyMemory<byte>[] planes = [default, default];
        var strides = new[] { dmaBufNv12Backing.YPlanePitchBytes, dmaBufNv12Backing.UvPlanePitchBytes };

        if (additionalRelease is null)
        {
            return new VideoFrame(presentationTime, format, planes, strides,
                release: null, dmaBufNv12: dmaBufNv12Backing,
                metadata: metadata,
                disposableRelease: dmaBufNv12Backing);
        }

        Action release = () =>
        {
            dmaBufNv12Backing.Dispose();
            additionalRelease();
        };
        return new VideoFrame(presentationTime, format, planes, strides,
            release: release, dmaBufNv12: dmaBufNv12Backing,
            metadata: metadata);
    }

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

        source.DmabufNv12.AddReference();
        var meta = colorTransferHint is { } hint
            ? source.Metadata with { ColorTransferHint = hint }
            : source.Metadata;
        return CreateNv12Dmabuf(source.PresentationTime, source.Format, source.DmabufNv12, meta);
    }

    /// <summary>Builds a P010 <see cref="VideoFrame"/> wrapping Linux DRM PRIME dma-bufs (zero-copy).</summary>
    public static VideoFrame CreateP010Dmabuf(
        TimeSpan presentationTime,
        VideoFormat format,
        VideoDmabufP010Backing dmaBufP010Backing,
        VideoFrameMetadata metadata = default,
        Action? additionalRelease = null)
    {
        ArgumentNullException.ThrowIfNull(dmaBufP010Backing);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(CreateP010DmabufOnlyLinux);

        ReadOnlyMemory<byte>[] planes = [default, default];
        var strides = new[] { dmaBufP010Backing.YPlanePitchBytes, dmaBufP010Backing.UvPlanePitchBytes };

        if (additionalRelease is null)
        {
            return new VideoFrame(presentationTime, format, planes, strides,
                release: null,
                dmaBufNv12: null, dmaBufP010: dmaBufP010Backing, dmaBufP016: null,
                metadata: metadata,
                disposableRelease: dmaBufP010Backing);
        }

        Action release = () =>
        {
            dmaBufP010Backing.Dispose();
            additionalRelease();
        };
        return new VideoFrame(presentationTime, format, planes, strides,
            release: release,
            dmaBufNv12: null, dmaBufP010: dmaBufP010Backing, dmaBufP016: null,
            metadata: metadata);
    }

    /// <summary>Builds a P016 <see cref="VideoFrame"/> wrapping Linux DRM PRIME dma-bufs (zero-copy).</summary>
    public static VideoFrame CreateP016Dmabuf(
        TimeSpan presentationTime,
        VideoFormat format,
        VideoDmabufP016Backing dmaBufP016Backing,
        VideoFrameMetadata metadata = default,
        Action? additionalRelease = null)
    {
        ArgumentNullException.ThrowIfNull(dmaBufP016Backing);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(CreateP016DmabufOnlyLinux);

        ReadOnlyMemory<byte>[] planes = [default, default];
        var strides = new[] { dmaBufP016Backing.YPlanePitchBytes, dmaBufP016Backing.UvPlanePitchBytes };

        if (additionalRelease is null)
        {
            return new VideoFrame(presentationTime, format, planes, strides,
                release: null,
                dmaBufNv12: null, dmaBufP010: null, dmaBufP016: dmaBufP016Backing,
                metadata: metadata,
                disposableRelease: dmaBufP016Backing);
        }

        Action release = () =>
        {
            dmaBufP016Backing.Dispose();
            additionalRelease();
        };
        return new VideoFrame(presentationTime, format, planes, strides,
            release: release,
            dmaBufNv12: null, dmaBufP010: null, dmaBufP016: dmaBufP016Backing,
            metadata: metadata);
    }

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

        source.DmabufP016.AddReference();
        var meta = colorTransferHint is { } hint
            ? source.Metadata with { ColorTransferHint = hint }
            : source.Metadata;
        return CreateP016Dmabuf(source.PresentationTime, source.Format, source.DmabufP016, meta);
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

        source.DmabufP010.AddReference();
        var meta = colorTransferHint is { } hint
            ? source.Metadata with { ColorTransferHint = hint }
            : source.Metadata;
        return CreateP010Dmabuf(source.PresentationTime, source.Format, source.DmabufP010, meta);
    }

    internal const string CreateNv12DmabufOnlyLinux = "CreateNv12Dmabuf is supported on Linux only.";
    internal const string CreateP010DmabufOnlyLinux = "CreateP010Dmabuf is supported on Linux only.";
    internal const string CreateP016DmabufOnlyLinux = "CreateP016Dmabuf is supported on Linux only.";

    /// <summary>Builds an NV12 <see cref="VideoFrame"/> wrapping Windows DXGI/D3D11 NT shared handles (decoder export).</summary>
    public static VideoFrame CreateNv12Win32Shared(
        TimeSpan presentationTime,
        VideoFormat format,
        VideoWin32Nv12Backing win32Nv12Backing,
        VideoFrameMetadata metadata = default,
        Action? additionalRelease = null)
    {
        ArgumentNullException.ThrowIfNull(win32Nv12Backing);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(CreateNv12Win32SharedOnlyWindows);

        ReadOnlyMemory<byte>[] planes = [default, default];
        var strides = new[] { win32Nv12Backing.YPlanePitchBytes, win32Nv12Backing.UvPlanePitchBytes };

        if (additionalRelease is null)
        {
            return new VideoFrame(presentationTime, format, planes, strides,
                release: null, win32Nv12: win32Nv12Backing,
                metadata: metadata,
                disposableRelease: win32Nv12Backing);
        }

        Action release = () =>
        {
            win32Nv12Backing.Dispose();
            additionalRelease();
        };
        return new VideoFrame(presentationTime, format, planes, strides,
            release: release, win32Nv12: win32Nv12Backing,
            metadata: metadata);
    }

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

        source.Win32Nv12.AddReference();
        var meta = colorTransferHint is { } hint
            ? source.Metadata with { ColorTransferHint = hint }
            : source.Metadata;
        return CreateNv12Win32Shared(source.PresentationTime, source.Format, source.Win32Nv12, meta);
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
        views = null;
        if (viewCount < 2 || !IsNv12CpuFanOutEligible(source))
            return false;

        var inner = Interlocked.Exchange(ref source._release, null);
        if (inner is null)
            return false;

        var viewsLocal = new VideoFrame[viewCount];
        var remaining = new[] { viewCount };
        Action shared = () =>
        {
            if (Interlocked.Decrement(ref remaining[0]) == 0)
                inner();
        };

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
                    release: shared,
                    metadata: viewMeta);
            }

            views = viewsLocal;
            return true;
        }
        catch
        {
            if (created == 0)
            {
                if (Interlocked.CompareExchange(ref source._release, inner, null) is not null)
                    inner();
            }
            else
            {
                Interlocked.Add(ref remaining[0], created - viewCount);
                for (var j = 0; j < created; j++)
                    viewsLocal[j].Dispose();
            }

            return false;
        }
    }

    private static bool IsNv12CpuFanOutEligible(VideoFrame source)
    {
        if (source.Format.PixelFormat != PixelFormat.Nv12)
            return false;
        if (source._dmabufNv12 is not null || source._dmabufP010 is not null || source._dmabufP016 is not null ||
            source._win32Nv12 is not null)
            return false;
        if (source._planes.Length != 2 || source._strides.Length != 2)
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
        Action? release = null,
        VideoFrameMetadata metadata = default,
        IDisposable? disposableRelease = null)
        : this(presentationTime, format, [plane], [stride],
            release: release, metadata: metadata, disposableRelease: disposableRelease) { }

    public void Dispose()
    {
        var a = Interlocked.Exchange(ref _release, null);
        var d = Interlocked.Exchange(ref _disposableRelease, null);
        a?.Invoke();
        d?.Dispose();
    }
}
