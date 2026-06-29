using System.Buffers;
using S.Media.Core;
using S.Media.Core.Video;

namespace HaPlay.Playback;

/// <summary>
/// <see cref="IVideoOutput"/> shim that declares <see cref="PixelFormat.Bgra32"/> as the only
/// accepted pixel format so the upstream <c>VideoRouter</c>'s fan-out negotiator (
/// <see cref="VideoOutputFanoutFormats.PickBranchPixelFormat"/>) inserts a swscale converter
/// before Submit. Forwards converted frames to <see cref="CueCompositionRuntime"/>'s slot, which
/// feeds <c>CpuVideoCompositor</c> (BGRA32-only).
/// </summary>
/// <remarks>
/// The router handles every pixel-format-to-BGRA32 conversion FFmpeg's swscale supports
/// (NV12, YUV420P, YUV422P10LE, YUVA444P12LE — including the alpha-carrying variants the
/// operator needs for PiP). Declaring an empty <see cref="AcceptedPixelFormats"/> list reads to
/// the negotiator as "no formats accepted" and throws — the correct way to say "give me BGRA32"
/// is to list it explicitly.
/// </remarks>
internal sealed class BgraConvertingVideoOutput : IVideoOutput, IVideoOutputQueueControl, IDisposable
{
    private static readonly IReadOnlyList<PixelFormat> AcceptedFormatsArr = new[] { PixelFormat.Bgra32 };

    private readonly IVideoOutput _inner;
    private readonly bool _premultiplyAlpha;
    private VideoFormat _format;
    private bool _disposed;

    public BgraConvertingVideoOutput(IVideoOutput inner, bool premultiplyAlpha = true)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _premultiplyAlpha = premultiplyAlpha;
    }

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => AcceptedFormatsArr;

    public VideoFormat Format => _format;

    public void Configure(VideoFormat format)
    {
        // The router already negotiated BGRA32 for us; pass through.
        _format = format;
        _inner.Configure(format);
    }

    public void Submit(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (!_premultiplyAlpha || !TryPremultiplyStraightAlpha(frame, out var premultiplied))
        {
            _inner.Submit(frame);
            return;
        }

        try { _inner.Submit(premultiplied); }
        catch
        {
            premultiplied.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    public void AbandonQueuedFrames()
    {
        if (_inner is IVideoOutputQueueControl control)
            control.AbandonQueuedFrames();
    }

    public bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        _inner is IVideoOutputQueueControl control
            ? control.WaitForIdle(timeout, cancellationToken)
            : true;

    private static bool TryPremultiplyStraightAlpha(VideoFrame frame, out VideoFrame premultiplied)
    {
        premultiplied = null!;
        if (frame.Format.PixelFormat != PixelFormat.Bgra32 || frame.Planes.Length == 0)
            return false;

        var width = frame.Format.Width;
        var height = frame.Format.Height;
        var stride = frame.Strides[0];
        if (width <= 0 || height <= 0 || stride < width * 4)
            return false;

        var source = frame.Planes[0].Span;
        var required = stride * height;
        if (source.Length < required || !ContainsTransparency(source, width, height, stride))
            return false;

        var buffer = ArrayPool<byte>.Shared.Rent(required);
        try
        {
            source[..required].CopyTo(buffer);
            PremultiplyBgraInPlace(buffer.AsSpan(0, required), width, height, stride);
            premultiplied = new VideoFrame(
                frame.PresentationTime,
                frame.Format,
                [buffer.AsMemory(0, required)],
                [stride],
                frame.Metadata,
                release: DisposableRelease.Wrap(() => ArrayPool<byte>.Shared.Return(buffer)));
            frame.Dispose();
            return true;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    private static bool ContainsTransparency(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            for (var x = 0; x < row.Length; x += 4)
            {
                if (row[x + 3] < 255)
                    return true;
            }
        }
        return false;
    }

    private static void PremultiplyBgraInPlace(Span<byte> bgra, int width, int height, int stride)
    {
        for (var y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            for (var x = 0; x < row.Length; x += 4)
            {
                var a = row[x + 3];
                if (a == 255) continue;
                if (a == 0)
                {
                    row[x + 0] = 0;
                    row[x + 1] = 0;
                    row[x + 2] = 0;
                    continue;
                }

                row[x + 0] = Premultiply(row[x + 0], a);
                row[x + 1] = Premultiply(row[x + 1], a);
                row[x + 2] = Premultiply(row[x + 2], a);
            }
        }
    }

    private static byte Premultiply(byte color, byte alpha) => (byte)((color * alpha + 127) / 255);
}
