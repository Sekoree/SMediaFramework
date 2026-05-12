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
        VideoWin32Nv12Backing? win32Nv12 = null)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(strides);
        PresentationTime = presentationTime;
        Format = format;
        ColorTransferHint = colorTransferHint;
        _release = release;
        _dmabufNv12 = dmaBufNv12;
        _win32Nv12 = win32Nv12;

        if (dmaBufNv12 is not null && win32Nv12 is not null)
            throw new ArgumentException("A frame cannot combine DMA-BUF and Win32 NV12 backings.", nameof(win32Nv12));

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

    internal const string CreateNv12DmabufOnlyLinux = "CreateNv12Dmabuf is supported on Linux only.";

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
