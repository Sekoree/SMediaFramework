using System.Buffers;
using S.Media.Core.Video;
using SkiaSharp;

namespace S.Media.SkiaSharp;

/// <summary>
/// Loads a still image (PNG, JPEG, WebP, BMP, GIF — any format SkiaSharp's codec stack recognises)
/// and presents it as an <see cref="IVideoSource"/> backed by a single decoded BGRA32 frame.
/// </summary>
/// <remarks>
/// <para>
/// Built for the cue-player / soundboard product surface where an image cue plays alongside live
/// video on the same <c>VideoRouter</c>. Combine with
/// <see cref="VideoPlayer.HoldLastFrameAtEnd"/> = <c>true</c> to keep the image on screen
/// indefinitely without re-decoding, or with <see cref="StaticFrameSource"/> semantics by wrapping
/// the loaded <see cref="VideoFrame"/> manually.
/// </para>
/// <para>
/// Decoding happens once at <see cref="OpenFromFile"/> / <see cref="OpenFromStream"/>. The pixel
/// buffer is rented from <see cref="ArrayPool{T}.Shared"/> and returned on <see cref="Dispose"/>;
/// emitted frames carry <c>release: null</c> because the source owns the buffer for its entire
/// lifetime (the buffer outlives every frame the source ever emits).
/// </para>
/// <para>
/// Default frame rate is 30 FPS — only used for PTS spacing of the re-emitted frames (the player
/// drops "late" frames if the PTS doesn't track the playhead).
/// </para>
/// </remarks>
public sealed class ImageFileSource : IVideoSource, IDisposable
{
    private readonly VideoFormat _format;
    private readonly byte[] _pixelBuffer;
    private readonly int _pixelBufferSize;
    private readonly ReadOnlyMemory<byte>[] _planes;
    private readonly int[] _strides;
    private readonly PixelFormat[] _native;
    private readonly TimeSpan _ptsStep;
    private TimeSpan _nextPts;
    private bool _disposed;

    private ImageFileSource(VideoFormat format, byte[] pixelBuffer, int pixelBufferSize, int stride, Rational? cadence)
    {
        _format = format;
        _pixelBuffer = pixelBuffer;
        _pixelBufferSize = pixelBufferSize;
        _planes = [new ReadOnlyMemory<byte>(pixelBuffer, 0, pixelBufferSize)];
        _strides = [stride];
        _native = [format.PixelFormat];

        var rate = cadence ?? format.FrameRate;
        _ptsStep = rate.Numerator > 0 && rate.Denominator > 0
            ? TimeSpan.FromSeconds((double)rate.Denominator / rate.Numerator)
            : TimeSpan.FromMilliseconds(33);
    }

    /// <summary>Decodes an image file (path) into a <see cref="ImageFileSource"/>.</summary>
    /// <param name="path">Path to a PNG / JPEG / WebP / BMP / GIF that SkiaSharp can decode.</param>
    /// <param name="cadence">Override PTS spacing per emitted frame. Defaults to 30 FPS.</param>
    /// <exception cref="FileNotFoundException">File does not exist.</exception>
    /// <exception cref="InvalidDataException">SkiaSharp could not decode the image.</exception>
    public static ImageFileSource OpenFromFile(string path, Rational? cadence = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("image file not found", path);
        using var stream = File.OpenRead(path);
        return OpenFromStream(stream, cadence);
    }

    /// <summary>Decodes an image from a stream (must be seekable / readable).</summary>
    public static ImageFileSource OpenFromStream(Stream stream, Rational? cadence = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var skStream = new SKManagedStream(stream, disposeManagedStream: false);
        using var codec = SKCodec.Create(skStream)
            ?? throw new InvalidDataException("SkiaSharp could not identify the image format.");

        var info = codec.Info;
        // Normalise to premul BGRA32 — matches PixelFormat.Bgra32 with stride = width * 4.
        var targetInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var stride = targetInfo.RowBytes;
        var totalBytes = checked(stride * targetInfo.Height);
        var buffer = ArrayPool<byte>.Shared.Rent(totalBytes);

        unsafe
        {
            fixed (byte* p = buffer)
            {
                var result = codec.GetPixels(targetInfo, (IntPtr)p);
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw new InvalidDataException(
                        $"SkiaSharp SKCodec.GetPixels returned {result} (file may be corrupt or unsupported).");
                }
            }
        }

        var rate = cadence ?? new Rational(30, 1);
        var format = new VideoFormat(targetInfo.Width, targetInfo.Height, PixelFormat.Bgra32, rate);
        return new ImageFileSource(format, buffer, totalBytes, stride, cadence);
    }

    public VideoFormat Format => _format;
    public IReadOnlyList<PixelFormat> NativePixelFormats => _native;
    public bool IsExhausted => _disposed;

    public void SelectOutputFormat(PixelFormat format)
    {
        if (format != _format.PixelFormat)
            throw new InvalidOperationException(
                $"ImageFileSource only delivers {_format.PixelFormat}; sink requested {format}. " +
                "Re-decode via a converter or use a different SkiaSharp pixel target.");
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        if (_disposed)
        {
            frame = null!;
            return false;
        }

        frame = new VideoFrame(
            _nextPts,
            _format,
            _planes,
            _strides,
            release: null);
        _nextPts += _ptsStep;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // pixelBufferSize is < pixelBuffer.Length because ArrayPool may give a larger array.
        _ = _pixelBufferSize;
        ArrayPool<byte>.Shared.Return(_pixelBuffer, clearArray: false);
    }
}
