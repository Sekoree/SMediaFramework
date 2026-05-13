using System.Diagnostics.CodeAnalysis;

namespace S.Media.Core.Video;

/// <summary>
/// One decoded/received video frame. Carries one or more byte planes plus the
/// presentation time the producer attached to them.
/// </summary>
/// <remarks>
/// Unlike <see cref="Audio.AudioFrame"/>, video frames are large enough that
/// per-frame allocation isn't viable (1080p BGRA32 ≈ 8 MB). Producers pass an
/// optional <c>release</c> callback to free the underlying buffer — refcount
/// decrement (NDI), <c>av_frame_unref</c> (FFmpeg), <c>ArrayPool.Return</c>,
/// etc. The frame is one-shot disposable; calling <see cref="Dispose"/> twice
/// is safe and only the first call invokes the release.
/// </remarks>
public sealed class VideoFrame : IDisposable
{
    private Action? _release;
    private readonly ReadOnlyMemory<byte>[] _planes;
    private readonly int[] _strides;
    private readonly VideoDmabufNv12Backing? _dmabufNv12;
    private readonly VideoDmabufP010Backing? _dmabufP010;
    private readonly VideoDmabufP016Backing? _dmabufP016;
    private readonly VideoWin32Nv12Backing? _win32Nv12;

    public TimeSpan PresentationTime { get; }
    public VideoFormat Format { get; }

    /// <summary>Optional colour transfer detected from metadata (codec / frame side data).</summary>
    public VideoTransferHint ColorTransferHint { get; }

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

    public VideoFrame(
        TimeSpan presentationTime,
        VideoFormat format,
        ReadOnlyMemory<byte>[] planes,
        int[] strides,
        VideoTransferHint colorTransferHint = default,
        Action? release = null,
        VideoDmabufNv12Backing? dmaBufNv12 = null,
        VideoDmabufP010Backing? dmaBufP010 = null,
        VideoDmabufP016Backing? dmaBufP016 = null,
        VideoWin32Nv12Backing? win32Nv12 = null)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(strides);
        PresentationTime = presentationTime;
        Format = format;
        ColorTransferHint = colorTransferHint;
        _release = release;
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
    public static VideoFrame CreateNv12Dmabuf(
        TimeSpan presentationTime,
        VideoFormat format,
        VideoDmabufNv12Backing dmaBufNv12Backing,
        VideoTransferHint colorTransferHint = default,
        Action? additionalRelease = null)
    {
        ArgumentNullException.ThrowIfNull(dmaBufNv12Backing);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(CreateNv12DmabufOnlyLinux);
        Action? release = additionalRelease == null
            ? dmaBufNv12Backing.Dispose
            : () =>
            {
                dmaBufNv12Backing.Dispose();
                additionalRelease();
            };

        ReadOnlyMemory<byte>[] planes = [default, default];
        var strides = new[] { dmaBufNv12Backing.YPlanePitchBytes, dmaBufNv12Backing.UvPlanePitchBytes };
        return new VideoFrame(presentationTime, format, planes, strides, colorTransferHint, release, dmaBufNv12Backing);
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
        return CreateNv12Dmabuf(
            source.PresentationTime,
            source.Format,
            source.DmabufNv12,
            colorTransferHint ?? source.ColorTransferHint,
            additionalRelease: null);
    }

    /// <summary>Builds a P010 <see cref="VideoFrame"/> wrapping Linux DRM PRIME dma-bufs (zero-copy).</summary>
    public static VideoFrame CreateP010Dmabuf(
        TimeSpan presentationTime,
        VideoFormat format,
        VideoDmabufP010Backing dmaBufP010Backing,
        VideoTransferHint colorTransferHint = default,
        Action? additionalRelease = null)
    {
        ArgumentNullException.ThrowIfNull(dmaBufP010Backing);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(CreateP010DmabufOnlyLinux);
        Action? release = additionalRelease == null
            ? dmaBufP010Backing.Dispose
            : () =>
            {
                dmaBufP010Backing.Dispose();
                additionalRelease();
            };

        ReadOnlyMemory<byte>[] planes = [default, default];
        var strides = new[] { dmaBufP010Backing.YPlanePitchBytes, dmaBufP010Backing.UvPlanePitchBytes };
        return new VideoFrame(presentationTime, format, planes, strides, colorTransferHint, release,
            dmaBufNv12: null, dmaBufP010: dmaBufP010Backing, dmaBufP016: null);
    }

    /// <summary>Builds a P016 <see cref="VideoFrame"/> wrapping Linux DRM PRIME dma-bufs (zero-copy).</summary>
    public static VideoFrame CreateP016Dmabuf(
        TimeSpan presentationTime,
        VideoFormat format,
        VideoDmabufP016Backing dmaBufP016Backing,
        VideoTransferHint colorTransferHint = default,
        Action? additionalRelease = null)
    {
        ArgumentNullException.ThrowIfNull(dmaBufP016Backing);
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(CreateP016DmabufOnlyLinux);
        Action? release = additionalRelease == null
            ? dmaBufP016Backing.Dispose
            : () =>
            {
                dmaBufP016Backing.Dispose();
                additionalRelease();
            };

        ReadOnlyMemory<byte>[] planes = [default, default];
        var strides = new[] { dmaBufP016Backing.YPlanePitchBytes, dmaBufP016Backing.UvPlanePitchBytes };
        return new VideoFrame(presentationTime, format, planes, strides, colorTransferHint, release,
            dmaBufNv12: null, dmaBufP010: null, dmaBufP016: dmaBufP016Backing);
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
        return CreateP016Dmabuf(
            source.PresentationTime,
            source.Format,
            source.DmabufP016,
            colorTransferHint ?? source.ColorTransferHint,
            additionalRelease: null);
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
        return CreateP010Dmabuf(
            source.PresentationTime,
            source.Format,
            source.DmabufP010,
            colorTransferHint ?? source.ColorTransferHint,
            additionalRelease: null);
    }

    internal const string CreateNv12DmabufOnlyLinux = "CreateNv12Dmabuf is supported on Linux only.";
    internal const string CreateP010DmabufOnlyLinux = "CreateP010Dmabuf is supported on Linux only.";
    internal const string CreateP016DmabufOnlyLinux = "CreateP016Dmabuf is supported on Linux only.";

    /// <summary>Builds an NV12 <see cref="VideoFrame"/> wrapping Windows DXGI/D3D11 NT shared handles (decoder export).</summary>
    public static VideoFrame CreateNv12Win32Shared(
        TimeSpan presentationTime,
        VideoFormat format,
        VideoWin32Nv12Backing win32Nv12Backing,
        VideoTransferHint colorTransferHint = default,
        Action? additionalRelease = null)
    {
        ArgumentNullException.ThrowIfNull(win32Nv12Backing);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(CreateNv12Win32SharedOnlyWindows);

        Action? release = additionalRelease == null
            ? win32Nv12Backing.Dispose
            : () =>
            {
                win32Nv12Backing.Dispose();
                additionalRelease();
            };

        ReadOnlyMemory<byte>[] planes = [default, default];
        var strides = new[] { win32Nv12Backing.YPlanePitchBytes, win32Nv12Backing.UvPlanePitchBytes };
        return new VideoFrame(presentationTime, format, planes, strides, colorTransferHint, release, win32Nv12: win32Nv12Backing);
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
        return CreateNv12Win32Shared(
            source.PresentationTime,
            source.Format,
            source.Win32Nv12,
            colorTransferHint ?? source.ColorTransferHint,
            additionalRelease: null);
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
                    hint,
                    shared);
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
        VideoTransferHint colorTransferHint = default,
        Action? release = null)
        : this(presentationTime, format, [plane], [stride], colorTransferHint, release) { }
    public void Dispose()
    {
        var r = Interlocked.Exchange(ref _release, null);
        r?.Invoke();
    }
}
