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

    public TimeSpan PresentationTime { get; }
    public VideoFormat Format { get; }

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
        Action? release = null)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(strides);
        if (planes.Length == 0)
            throw new ArgumentException("at least one plane required", nameof(planes));
        if (planes.Length != strides.Length)
            throw new ArgumentException("planes and strides must have the same length", nameof(strides));
        for (var i = 0; i < strides.Length; i++)
        {
            if (strides[i] <= 0)
                throw new ArgumentOutOfRangeException(nameof(strides), strides[i],
                    $"stride[{i}] must be positive");
        }

        PresentationTime = presentationTime;
        Format = format;
        _planes = planes;
        _strides = strides;
        _release = release;
    }

    /// <summary>Convenience overload for single-plane (packed) formats like <see cref="PixelFormat.Bgra32"/>.</summary>
    public VideoFrame(
        TimeSpan presentationTime,
        VideoFormat format,
        ReadOnlyMemory<byte> plane,
        int stride,
        Action? release = null)
        : this(presentationTime, format, [plane], [stride], release) { }

    public void Dispose()
    {
        var r = Interlocked.Exchange(ref _release, null);
        r?.Invoke();
    }
}
