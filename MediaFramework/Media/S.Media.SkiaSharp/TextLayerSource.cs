using System.Buffers;
using S.Media.Core.Video;
using SkiaSharp;

namespace S.Media.SkiaSharp;

/// <summary>Text alignment for <see cref="TextLayerSource"/>.</summary>
public enum TextAlignment
{
    /// <summary>Text left edge anchored at <c>X = 0</c>.</summary>
    Left = 0,
    /// <summary>Text horizontally centered in the canvas.</summary>
    Center = 1,
    /// <summary>Text right edge anchored at <c>X = Width</c>.</summary>
    Right = 2,
}

/// <summary>
/// <see cref="IVideoSource"/> that rasterises a text label into a BGRA32 frame via SkiaSharp and
/// re-emits the same frame on every read. Mutable text / font / color / size — setters invalidate
/// the cached rasterisation; the next read re-rasterises.
/// </summary>
/// <remarks>
/// <para>
/// Output is BGRA32 with alpha (transparent background by default), suitable for compositing as
/// an overlay layer on a <see cref="VideoCompositorSource"/>. The frame layout matches
/// <c>SKAlphaType.Premul</c> — premultiplied — same as <see cref="ImageFileSource"/>.
/// </para>
/// <para>
/// Each rasterisation produces an immutable, refcounted pooled buffer ("generation"). Emitted frames
/// share the current generation (no per-frame copy while the text is unchanged) and hold a reference;
/// a property change rasterises a <em>new</em> generation, and an old generation returns to
/// <see cref="ArrayPool{T}.Shared"/> only once the source and every emitted frame referencing it are
/// disposed. So mutating the text or disposing the source can never change pixels or free the buffer
/// underneath frames still queued in a player/compositor/output.
/// </para>
/// <para>
/// Phase-4 first-cut: CPU rasterisation only. GPU-side SDF font rendering is a future enhancement
/// that can replace the rasterisation backend without breaking the API.
/// </para>
/// </remarks>
public sealed class TextLayerSource : IVideoSource, IDisposable
{
    private static readonly PixelFormat[] NativeFormats = [PixelFormat.Bgra32];

    private readonly VideoFormat _format;
    private readonly TimeSpan _ptsStep;
    private readonly object _gate = new();
    private FrameBuffer? _current;
    private readonly int _pixelByteCount;
    private readonly int _stride;
    private TimeSpan _nextPts;
    private bool _disposed;
    private bool _dirty = true;

    private string _text;
    private string _fontFamily;
    private float _fontSize;
    private uint _argbColor;
    private uint _backgroundArgb;
    private TextAlignment _alignment;

    /// <summary>
    /// Constructs a <see cref="TextLayerSource"/> that produces <paramref name="width"/>×<paramref name="height"/>
    /// BGRA32 frames at <paramref name="frameRate"/>.
    /// </summary>
    /// <param name="width">Canvas width in pixels.</param>
    /// <param name="height">Canvas height in pixels.</param>
    /// <param name="frameRate">PTS spacing per emitted frame.</param>
    /// <param name="text">Initial text. Multi-line is rendered as one line (no automatic wrapping).</param>
    /// <param name="fontFamily">Family name passed to <c>SKTypeface.FromFamilyName</c>; falls back to SkiaSharp's default when not found.</param>
    /// <param name="fontSize">Font size in pixels.</param>
    /// <param name="argbColor">Text color, packed <c>0xAARRGGBB</c>.</param>
    /// <param name="alignment">Horizontal alignment within the canvas.</param>
    /// <param name="backgroundArgb">Background color (defaults to transparent, <c>0x00000000</c>).</param>
    public TextLayerSource(
        int width, int height, Rational frameRate,
        string text, string fontFamily, float fontSize,
        uint argbColor,
        TextAlignment alignment = TextAlignment.Center,
        uint backgroundArgb = 0)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "must be > 0");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "must be > 0");
        if (fontSize <= 0f) throw new ArgumentOutOfRangeException(nameof(fontSize), "must be > 0");
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fontFamily);

        _format = new VideoFormat(width, height, PixelFormat.Bgra32, frameRate);
        _stride = width * 4;
        _pixelByteCount = _stride * height;
        _ptsStep = frameRate.Numerator > 0 && frameRate.Denominator > 0
            ? TimeSpan.FromSeconds((double)frameRate.Denominator / frameRate.Numerator)
            : TimeSpan.FromMilliseconds(33);

        _text = text;
        _fontFamily = fontFamily;
        _fontSize = fontSize;
        _argbColor = argbColor;
        _backgroundArgb = backgroundArgb;
        _alignment = alignment;
    }

    public VideoFormat Format => _format;
    public IReadOnlyList<PixelFormat> NativePixelFormats => NativeFormats;
    public bool IsExhausted => _disposed;

    /// <summary>Label text. Mutating this marks the rasterisation dirty.</summary>
    public string Text
    {
        get { lock (_gate) return _text; }
        set { ArgumentNullException.ThrowIfNull(value); lock (_gate) { if (_text != value) { _text = value; _dirty = true; } } }
    }

    /// <summary>Font family name passed to SkiaSharp.</summary>
    public string FontFamily
    {
        get { lock (_gate) return _fontFamily; }
        set { ArgumentNullException.ThrowIfNull(value); lock (_gate) { if (_fontFamily != value) { _fontFamily = value; _dirty = true; } } }
    }

    /// <summary>Font size in pixels.</summary>
    public float FontSize
    {
        get { lock (_gate) return _fontSize; }
        set
        {
            if (value <= 0f) throw new ArgumentOutOfRangeException(nameof(value), "must be > 0");
            lock (_gate) { if (!_fontSize.Equals(value)) { _fontSize = value; _dirty = true; } }
        }
    }

    /// <summary>Text color, packed <c>0xAARRGGBB</c>.</summary>
    public uint ArgbColor
    {
        get { lock (_gate) return _argbColor; }
        set { lock (_gate) { if (_argbColor != value) { _argbColor = value; _dirty = true; } } }
    }

    /// <summary>Background color, packed <c>0xAARRGGBB</c>. Default 0 = transparent.</summary>
    public uint BackgroundArgb
    {
        get { lock (_gate) return _backgroundArgb; }
        set { lock (_gate) { if (_backgroundArgb != value) { _backgroundArgb = value; _dirty = true; } } }
    }

    /// <summary>Horizontal alignment within the canvas.</summary>
    public TextAlignment Alignment
    {
        get { lock (_gate) return _alignment; }
        set { lock (_gate) { if (_alignment != value) { _alignment = value; _dirty = true; } } }
    }

    public void SelectOutputFormat(PixelFormat format)
    {
        if (format != PixelFormat.Bgra32)
            throw new InvalidOperationException(
                $"TextLayerSource only delivers BGRA32; output requested {format}.");
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        if (_disposed)
        {
            frame = null!;
            return false;
        }

        FrameBuffer gen;
        lock (_gate)
        {
            if (_disposed)
            {
                frame = null!;
                return false;
            }
            if (_dirty || _current is null)
            {
                var next = Rasterise();
                _current?.Release(); // drop the source's hold on the previous generation
                _current = next;
                _dirty = false;
            }
            gen = _current;
            gen.AddRef(); // this frame's reference; released when the frame is disposed
        }

        frame = new VideoFrame(
            _nextPts,
            _format,
            new ReadOnlyMemory<byte>(gen.Buffer, 0, _pixelByteCount),
            _stride,
            release: gen,
            metadata: new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Premultiplied));
        _nextPts += _ptsStep;
        return true;
    }

    /// <summary>Rasterises the current text into a fresh refcounted generation buffer (source holds 1 ref).</summary>
    private FrameBuffer Rasterise()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_pixelByteCount);
        var info = new SKImageInfo(_format.Width, _format.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        unsafe
        {
            fixed (byte* p = buffer)
            {
                using var surface = SKSurface.Create(info, (IntPtr)p, _stride);
                var canvas = surface.Canvas;
                canvas.Clear(new SKColor(_backgroundArgb));

                using var typeface = SKTypeface.FromFamilyName(_fontFamily) ?? SKTypeface.Default;
                using var font = new SKFont(typeface, _fontSize);
                using var paint = new SKPaint
                {
                    Color = new SKColor(_argbColor),
                    IsAntialias = true,
                };

                font.MeasureText(_text, out var bounds);
                var textWidth = bounds.Width;
                var x = _alignment switch
                {
                    TextAlignment.Left => 0f - bounds.Left,
                    TextAlignment.Right => _format.Width - textWidth - bounds.Left,
                    _ => (_format.Width - textWidth) * 0.5f - bounds.Left,
                };
                var metrics = font.Metrics;
                var lineHeight = metrics.Descent - metrics.Ascent;
                var y = (_format.Height + lineHeight) * 0.5f - metrics.Descent;

                canvas.DrawText(_text, x, y, SKTextAlign.Left, font, paint);
                canvas.Flush();
            }
        }

        return new FrameBuffer(buffer, initialRefs: 1);
    }

    public void Dispose()
    {
        FrameBuffer? gen;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            gen = _current;
            _current = null;
        }
        // Drop only the source's reference; frames still in flight keep theirs and return the buffer
        // to the pool when they are disposed.
        gen?.Release();
    }

    /// <summary>
    /// Refcounted pooled BGRA buffer shared by all frames emitted from one rasterisation generation.
    /// The source holds one reference; each emitted frame adds another. The backing array returns to
    /// <see cref="ArrayPool{T}.Shared"/> only when the last reference is released, so re-rasterising a
    /// new generation or disposing the source can't pull the buffer out from under in-flight frames.
    /// </summary>
    private sealed class FrameBuffer : IDisposable
    {
        private byte[]? _buffer;
        private int _refs;

        public FrameBuffer(byte[] buffer, int initialRefs)
        {
            _buffer = buffer;
            _refs = initialRefs;
        }

        public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(FrameBuffer));

        public void AddRef() => Interlocked.Increment(ref _refs);

        /// <summary>One emitted frame's release path: <see cref="VideoFrame.Dispose"/> calls this exactly
        /// once per frame (idempotent), balancing the <see cref="AddRef"/> taken when the frame was built.</summary>
        public void Dispose() => Release();

        public void Release()
        {
            if (Interlocked.Decrement(ref _refs) != 0) return;
            var buf = Interlocked.Exchange(ref _buffer, null);
            if (buf is not null)
                ArrayPool<byte>.Shared.Return(buf, clearArray: false);
        }
    }
}
