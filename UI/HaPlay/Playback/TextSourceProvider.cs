using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HaPlay.Models;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;

namespace HaPlay.Playback;

/// <summary>
/// The <c>text:</c> registry provider (NXT-06 cutover): lets the headless <c>ShowSession</c> play a text cue the
/// same way it plays any other source. A <see cref="TextPlaylistItem"/> is encoded into a <c>text:&lt;base64-json&gt;</c>
/// URI by <see cref="HaPlayShowMapper"/>; this provider decodes it, renders the frame with <see cref="TextFrameRenderer"/>
/// (CPU/SkiaSharp — headless, off-UI-thread safe), and hands back a held video source bounded by the cue's duration.
/// Matches the <c>ndi:</c> / <c>padev:</c> pattern: a HaPlay-registered provider for a scheme the framework doesn't own.
/// </summary>
internal sealed class TextDecoderProvider : IMediaDecoderProvider
{
    /// <summary>The rendered text cue's frame rate. It is a single still frame held for the duration, so this only
    /// sets how many identical frames a bounded source emits before it exhausts (duration × this rate).</summary>
    private static readonly Rational FrameRate = new(30, 1);

    public string Name => "Text";

    public double Probe(string uri, MediaKind kind) =>
        kind == MediaKind.Video && TextSourceUri.IsTextUri(uri) ? 1.0 : 0.0;

    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options)
    {
        var spec = TextSourceUri.Decode(uri)
                   ?? throw new InvalidOperationException($"'{uri}' is not a valid text: source URI.");
        var frame = TextFrameRenderer.Render(spec.ToItem(), FrameRate)
                    ?? throw new InvalidOperationException("text cue rendering failed (SkiaSharp).");
        return new TextHeldVideoSource(frame, FrameRate, TimeSpan.FromMilliseconds(Math.Max(0, spec.DurationMs)));
    }

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) =>
        throw new NotSupportedException("text: is a video-only source.");
}

/// <summary>Encodes / decodes a <see cref="TextPlaylistItem"/> (plus the cue duration) as a <c>text:</c> URI.
/// The payload is source-generated JSON (AOT-safe, D10) base64-encoded so the whole spec round-trips through the
/// headless <c>ShowDocument</c>'s single <c>MediaPath</c> string.</summary>
internal static class TextSourceUri
{
    private const string Prefix = "text:";

    public static bool IsTextUri(string? uri) =>
        uri is not null && uri.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string Encode(TextPlaylistItem item, long durationMs)
    {
        ArgumentNullException.ThrowIfNull(item);
        var json = JsonSerializer.Serialize(TextSourceSpec.FromItem(item, durationMs), TextSourceJsonContext.Default.TextSourceSpec);
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static TextSourceSpec? Decode(string uri)
    {
        if (!IsTextUri(uri))
            return null;
        try
        {
            var payload = Convert.FromBase64String(uri[Prefix.Length..]);
            return JsonSerializer.Deserialize(Encoding.UTF8.GetString(payload), TextSourceJsonContext.Default.TextSourceSpec);
        }
        catch
        {
            return null; // malformed URI → OpenVideo surfaces a clear error via the null spec
        }
    }
}

/// <summary>The serializable form of a text cue's render parameters (a flat copy of the <see cref="TextPlaylistItem"/>
/// render fields — deliberately NOT the polymorphic <c>PlaylistItem</c>) plus the cue's display duration.</summary>
internal sealed record TextSourceSpec
{
    public string Text { get; init; } = string.Empty;
    public string FontFamily { get; init; } = "Inter";
    public double FontSizePx { get; init; } = 96;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public uint ColorArgb { get; init; } = 0xFFFFFFFF;
    public uint BackgroundArgb { get; init; }
    public uint OutlineArgb { get; init; } = 0xFF000000;
    public double OutlineWidthPx { get; init; }
    public int HAlign { get; init; }
    public int VAlign { get; init; }
    public double WrapWidthFraction { get; init; } = 0.9;
    public double PaddingPx { get; init; } = 24;
    public int CanvasWidth { get; init; } = 1920;
    public int CanvasHeight { get; init; } = 1080;
    public long DurationMs { get; init; }

    public static TextSourceSpec FromItem(TextPlaylistItem t, long durationMs) => new()
    {
        Text = t.Text,
        FontFamily = t.FontFamily,
        FontSizePx = t.FontSizePx,
        Bold = t.Bold,
        Italic = t.Italic,
        ColorArgb = t.ColorArgb,
        BackgroundArgb = t.BackgroundArgb,
        OutlineArgb = t.OutlineArgb,
        OutlineWidthPx = t.OutlineWidthPx,
        HAlign = (int)t.HAlign,
        VAlign = (int)t.VAlign,
        WrapWidthFraction = t.WrapWidthFraction,
        PaddingPx = t.PaddingPx,
        CanvasWidth = t.CanvasWidth,
        CanvasHeight = t.CanvasHeight,
        DurationMs = durationMs,
    };

    public TextPlaylistItem ToItem() => new()
    {
        Text = Text,
        FontFamily = FontFamily,
        FontSizePx = FontSizePx,
        Bold = Bold,
        Italic = Italic,
        ColorArgb = ColorArgb,
        BackgroundArgb = BackgroundArgb,
        OutlineArgb = OutlineArgb,
        OutlineWidthPx = OutlineWidthPx,
        HAlign = (TextAlignH)HAlign,
        VAlign = (TextAlignV)VAlign,
        WrapWidthFraction = WrapWidthFraction,
        PaddingPx = PaddingPx,
        CanvasWidth = CanvasWidth,
        CanvasHeight = CanvasHeight,
    };
}

[JsonSourceGenerationOptions]
[JsonSerializable(typeof(TextSourceSpec))]
internal partial class TextSourceJsonContext : JsonSerializerContext;

/// <summary>
/// A single rendered text frame held for the cue's duration. Wraps the proven <see cref="HeldFrameVideoSource"/>
/// (format negotiation + per-read owned clones) and adds a finite, seekable duration so <c>ShowSession</c> can bound
/// it like a normal clip: it emits <c>duration × fps</c> identical frames then exhausts. A zero/absent duration
/// leaves it unbounded (holds until the cue is stopped or the next one fires — the same as a plain held source).
/// </summary>
internal sealed class TextHeldVideoSource : IVideoSource, ISeekableSource, IReplaceableFrameSource, IDisposable
{
    private readonly object _gate = new();
    private readonly Rational _frameRate;
    private readonly TimeSpan _duration; // reported so ShowSession's end-monitor can stop the cue at its duration
    private HeldFrameVideoSource _inner; // swapped by ReplaceFrame under _gate for a live text/style edit
    private PixelFormat _selectedFormat;
    private long _next;
    private bool _disposed;

    public TextHeldVideoSource(VideoFrame template, Rational frameRate, TimeSpan duration)
    {
        _inner = new HeldFrameVideoSource(template);
        _selectedFormat = _inner.Format.PixelFormat;
        _frameRate = frameRate;
        _duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
    }

    public VideoFormat Format { get { lock (_gate) return _inner.Format; } }

    public IReadOnlyList<PixelFormat> NativePixelFormats { get { lock (_gate) return _inner.NativePixelFormats; } }

    // Deliberately UNBOUNDED — a held text frame never runs out. Ending is driven by ShowSession's time-based
    // end-monitor (against the reported Duration), NOT by read count: a resize or live-edit re-primes the pipeline
    // and re-reads a burst of frames, and a read-count bound would then hit EOF and stop the cue mid-playback.
    public bool IsExhausted => InnerExhausted();

    public TimeSpan Duration => _duration;

    public TimeSpan Position => FramesToTime(Volatile.Read(ref _next));

    public void SelectOutputFormat(PixelFormat format)
    {
        lock (_gate)
        {
            _selectedFormat = format;
            _inner.SelectOutputFormat(format);
        }
    }

    public void Seek(TimeSpan position)
    {
        var fps = _frameRate.ToDouble();
        Volatile.Write(ref _next, fps > 0 ? Math.Max(0, (long)(position.TotalSeconds * fps)) : 0);
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        var index = Interlocked.Increment(ref _next) - 1;
        VideoFrame held;
        lock (_gate)
        {
            if (!_inner.TryReadNextFrame(out held))
            {
                frame = null!;
                return false;
            }
        }

        // Re-stamp the identical held frame with an ADVANCING presentation time. Otherwise every emitted frame
        // carries the template's single timestamp, so the player sees them all as "due now", drains its queue in
        // one go, and the source exhausts almost immediately — which made the cue end early, and far more often
        // after a resize re-primes the pipeline. With a per-frame PTS the player paces the identical frames across
        // the cue's duration. Zero-copy: the emitted frame owns `held` via its release.
        frame = new VideoFrame(FramesToTime(index), held.Format, held.Planes, held.Strides, held.Metadata, release: held);
        return true;
    }

    /// <summary>Live-swap the rendered frame (a text/style edit on the playing cue). Re-selects the previously
    /// negotiated output format on the new frame so the pump keeps pulling in the same format, then disposes the
    /// old one. Never advances the playhead — the cue keeps its duration/position, only its pixels change.</summary>
    public void ReplaceFrame(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (_disposed)
            {
                frame.Dispose();
                return;
            }

            var next = new HeldFrameVideoSource(frame);
            next.SelectOutputFormat(_selectedFormat);
            var old = _inner;
            _inner = next;
            old.Dispose();
        }
    }

    private bool InnerExhausted()
    {
        lock (_gate)
            return _inner.IsExhausted;
    }

    private TimeSpan FramesToTime(long frames)
    {
        var fps = _frameRate.ToDouble();
        return fps > 0 ? TimeSpan.FromSeconds(frames / fps) : TimeSpan.Zero;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _inner.Dispose();
        }
    }
}
