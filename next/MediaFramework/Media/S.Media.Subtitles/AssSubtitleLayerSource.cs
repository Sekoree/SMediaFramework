using LibAssLib;
using S.Media.Core.Video;

namespace S.Media.Subtitles;

/// <summary>
/// A full-fidelity ASS/SSA subtitle overlay aligned to the master timeline, rendered by libass (via
/// <see cref="LibAssLib"/>). Given a master time it produces the Bgra32-premultiplied overlay frame for that
/// instant — every active event composited with its styling, positioning, karaoke and animated transforms — or
/// <c>null</c> when nothing shows. This is the high-fidelity counterpart to <see cref="SubtitleLayerSource"/>
/// (which renders plain text via Skia); use it for ASS tracks where styling matters.
/// </summary>
/// <remarks>
/// The returned frame is owned by this source and <strong>re-rendered in place</strong>: it is valid only until
/// the next <see cref="RenderAt"/> call (libass content can change every frame, e.g. karaoke/transforms, so a
/// per-frame copy would churn megabytes). Composite it within the tick; do not hold a reference across calls and
/// do not dispose it. Single-threaded — call from the composition thread.
/// </remarks>
public sealed class AssSubtitleLayerSource : IVideoOverlaySource
{
    private readonly AssLibrary _library;
    private readonly AssRenderer _renderer;
    private readonly AssTrack _track;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private byte[] _buffer = [];        // set by InitOverlayBuffer in each ctor
    private VideoFrame _frame = null!;  // set by InitOverlayBuffer in each ctor
    private bool _hasContent;
    private bool _disposed;

    /// <summary>
    /// Creates a source from a complete in-memory ASS/SSA document, rendering at <paramref name="width"/>×
    /// <paramref name="height"/>. <paramref name="defaultFontFamily"/> is the fallback family libass uses when a
    /// style's font is unavailable (resolved through fontconfig / DirectWrite / CoreText).
    /// </summary>
    public AssSubtitleLayerSource(ReadOnlySpan<byte> assDocument, int width, int height, string? defaultFontFamily = "Sans", SubtitleStyleOverride? style = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "width and height must be positive.");

        _width = width;
        _height = height;
        _stride = width * 4;
        _library = new AssLibrary();
        try
        {
            _renderer = _library.CreateRenderer();
            _renderer.SetFrameSize(width, height);
            ApplyFontStyle(_renderer, defaultFontFamily, style);
            _track = _library.ReadMemory(assDocument);
        }
        catch
        {
            _renderer?.Dispose();
            _library.Dispose();
            throw;
        }

        InitOverlayBuffer();
    }

    // Applies the operator's font overrides globally to the renderer: family via the font provider (falling back
    // to the document/default family) and an optional global size multiplier.
    private static void ApplyFontStyle(AssRenderer renderer, string? defaultFamily, SubtitleStyleOverride? style)
    {
        var family = style?.FontFamily is { Length: > 0 } f ? f : defaultFamily;
        renderer.SetFonts(family);
        if (style?.FontScale is { } scale && scale > 0)
            renderer.SetFontScale(scale);
    }

    /// <summary>
    /// Creates a source from decoded ASS pieces — the header, timed events, and optional embedded fonts — the
    /// libass streaming path used for FFmpeg-decoded subtitles (any text format, sidecar or in-container). Events
    /// are fed via <c>ass_process_chunk</c>; fonts are registered before the font provider initializes so styles
    /// can resolve them.
    /// </summary>
    public AssSubtitleLayerSource(
        int width,
        int height,
        ReadOnlySpan<byte> header,
        IReadOnlyList<AssEventChunk> events,
        IReadOnlyList<AssFontAttachment>? fonts = null,
        string? defaultFontFamily = "Sans",
        SubtitleStyleOverride? style = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "width and height must be positive.");

        _width = width;
        _height = height;
        _stride = width * 4;
        _library = new AssLibrary();
        try
        {
            if (fonts is { Count: > 0 })
            {
                _library.SetExtractFonts(true);
                foreach (var font in fonts)
                    _library.AddFont(font.Name, font.Data);
            }

            _renderer = _library.CreateRenderer();
            _renderer.SetFrameSize(width, height);
            ApplyFontStyle(_renderer, defaultFontFamily, style);

            _track = _library.CreateTrack();
            _track.ProcessCodecPrivate(header);
            foreach (var chunk in events)
                _track.ProcessChunk(chunk.Body, chunk.StartMs, chunk.DurationMs);
        }
        catch
        {
            _renderer?.Dispose();
            _library.Dispose();
            throw;
        }

        InitOverlayBuffer();
    }

    private void InitOverlayBuffer()
    {
        _buffer = new byte[_stride * _height];
        var format = new VideoFormat(_width, _height, PixelFormat.Bgra32, new Rational(25, 1));
        _frame = new VideoFrame(TimeSpan.Zero, format, _buffer, _stride,
            new VideoFrameMetadata(AlphaMode: VideoAlphaMode.Premultiplied));
    }

    /// <summary>Overlay width (the composition canvas size the subtitles are rendered for).</summary>
    public int Width => _width;

    /// <summary>Overlay height.</summary>
    public int Height => _height;

    /// <summary>
    /// The overlay frame for the subtitles active at <paramref name="masterTime"/>, or <c>null</c> when nothing
    /// shows. Borrowed and re-rendered in place — valid until the next call; do not dispose.
    /// </summary>
    public VideoFrame? RenderAt(TimeSpan masterTime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var timeMs = (long)masterTime.TotalMilliseconds;

        switch (_renderer.RenderInto(_track, timeMs, _buffer, _width, _height, _stride))
        {
            case AssRenderOutcome.Empty:
                _hasContent = false;
                return null;
            case AssRenderOutcome.Rendered:
                _hasContent = true;
                return _frame;
            default: // Unchanged — buffer still holds the previous image
                return _hasContent ? _frame : null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _track.Dispose();
        _renderer.Dispose();
        _library.Dispose();
        _frame.Dispose();
    }
}

/// <summary>One timed ASS dialogue event for the streaming <see cref="AssSubtitleLayerSource"/> ctor: the
/// <c>ReadOrder,Layer,Style,…,Text</c> body (no timing) plus absolute start and duration in milliseconds.
/// Produced by an FFmpeg subtitle decode; fed to libass via <c>ass_process_chunk</c>.</summary>
public readonly record struct AssEventChunk(byte[] Body, long StartMs, long DurationMs);

/// <summary>An embedded font (name + raw bytes) registered with libass before rendering.</summary>
public readonly record struct AssFontAttachment(string Name, byte[] Data);
