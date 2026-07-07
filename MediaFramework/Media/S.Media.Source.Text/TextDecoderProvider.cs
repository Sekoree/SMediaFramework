using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;

namespace S.Media.Source.Text;

/// <summary>
/// The <c>text:</c> registry provider (SESSION-02): lets any host's <c>ShowSession</c> play a text cue the same
/// way it plays any other source. A <see cref="TextSourceSpec"/> is encoded into a <c>text:&lt;base64-json&gt;</c>
/// URI (<see cref="TextSourceUri"/>); this decodes it, renders the frame with <see cref="TextFrameRenderer"/>
/// (CPU/SkiaSharp — headless, off-UI-thread safe), and hands back a held video source bounded by the cue duration.
/// Matches the <c>ndi:</c> / <c>padev:</c> pattern: a scheme provider registered through the media registry.
/// </summary>
public sealed class TextDecoderProvider : IMediaDecoderProvider
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
        var frame = TextFrameRenderer.Render(spec, FrameRate)
                    ?? throw new InvalidOperationException("text cue rendering failed (SkiaSharp).");
        return new TextHeldVideoSource(frame, FrameRate, TimeSpan.FromMilliseconds(Math.Max(0, spec.DurationMs)));
    }

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) =>
        throw new NotSupportedException("text: is a video-only source.");
}
