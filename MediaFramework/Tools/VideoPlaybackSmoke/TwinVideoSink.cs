using S.Media.Core.Video;

namespace VideoPlaybackSmoke;

/// <summary>Forwards negotiated frames to an SDL GPU sink plus a duplicate sink (typically NDI) from one decoded stream.</summary>
internal sealed class TwinCpuVideoSink : IVideoSink, IDisposable
{
    private readonly IVideoSink _primary;
    private readonly IVideoSink _secondary;

    /// <remarks>Preserves <paramref name="primary"/> preference order.</remarks>
    public TwinCpuVideoSink(IVideoSink primary, IVideoSink secondary)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _secondary = secondary ?? throw new ArgumentNullException(nameof(secondary));
        AcceptedPixelFormats = IntersectPreserveOrder(primary.AcceptedPixelFormats, secondary.AcceptedPixelFormats);
        if (AcceptedPixelFormats.Count == 0 && primary.AcceptedPixelFormats.Count != 0 && secondary.AcceptedPixelFormats.Count != 0)
            throw new InvalidOperationException(
                "no overlapping pixel formats between primary and secondary sinks (try NV12 or Bgra32-capable pipelines).");
    }

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; }

    public VideoFormat Format => _primary.Format;

    public void Configure(VideoFormat format)
    {
        _primary.Configure(format);
        _secondary.Configure(format);
    }

    public void Submit(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.DmabufNv12 is not null)
            throw new NotSupportedException(
                "--ndi twin output does not duplicate DRM dma-buf frames — omit --ndi for zero-copy GL decode.");

        var secondaryClone = DuplicateCpuBackedFrame(frame, frame.ColorTransferHint);
        _primary.Submit(frame);
        _secondary.Submit(secondaryClone);
    }

    public void Dispose()
    {
        if (_primary is IDisposable dp)
            dp.Dispose();
    }

    private static IReadOnlyList<PixelFormat> IntersectPreserveOrder(
        IReadOnlyList<PixelFormat> primary,
        IReadOnlyList<PixelFormat> secondary)
    {
        if (secondary.Count == 0)
            return ToArray(primary);
        if (primary.Count == 0)
            return ToArray(secondary);

        var sec = new HashSet<PixelFormat>(secondary);
        var list = new List<PixelFormat>();
        foreach (var p in primary)
        {
            if (sec.Contains(p))
                list.Add(p);
        }

        return list;
    }

    private static PixelFormat[] ToArray(IReadOnlyList<PixelFormat> p) =>
        p.Count == 0 ? [] : p is PixelFormat[] a ? (PixelFormat[])a.Clone() : p.ToArray();

    private static VideoFrame DuplicateCpuBackedFrame(VideoFrame source, VideoTransferHint hint)
    {
        var fmt = source.Format.PixelFormat;
        var fw = source.Format.Width;
        var fh = source.Format.Height;

        var n = PixelFormatInfo.PlaneCount(fmt);
        if (source.PlaneCount != n || source.Strides.Length != n)
            throw new ArgumentException("unexpected plane or stride mismatch", nameof(source));

        var stridesOut = new int[n];
        var planes = new ReadOnlyMemory<byte>[n];

        for (var i = 0; i < n; i++)
        {
            var stride = source.Strides[i];
            stridesOut[i] = stride;

            var totalBytes = PixelFormatInfo.PlanePitchBufferLength(fmt, fw, fh, i, stride);
            var planeSrc = source.Planes[i];
            if (planeSrc.Length < totalBytes)
                throw new ArgumentException($"plane[{i}] shorter than contiguous pitch buffer", nameof(source));

            var buf = new byte[totalBytes];
            planeSrc.Span[..totalBytes].CopyTo(buf);
            planes[i] = buf;
        }

        var vfFmt = source.Format;
        return new VideoFrame(source.PresentationTime, vfFmt, planes, stridesOut, hint);
    }
}
