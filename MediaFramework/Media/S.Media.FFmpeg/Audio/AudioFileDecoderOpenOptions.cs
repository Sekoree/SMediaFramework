namespace S.Media.FFmpeg.Audio;

/// <summary>Optional settings for <see cref="AudioFileDecoder.Open(string, AudioFileDecoderOpenOptions)"/>.</summary>
/// <remarks>
/// <para>
/// <see cref="CodecThreadCount"/> configures a single libav <c>AVCodecContext</c>. When non-zero, <c>thread_type</c> follows
/// <see cref="LibavThreadTypePreference"/> when the codec advertises both frame and slice threading; otherwise the single
/// supported kind wins (see <see cref="AudioFileDecoder.LibavCodecThreadType"/>). Policies such as multiple decoder instances
/// per stream or demuxer affinity remain host-owned.
/// </para>
/// </remarks>
public readonly record struct AudioFileDecoderOpenOptions
{
    /// <summary>
    /// Forwarded to libav as <c>AVCodecContext.thread_count</c> before <c>avcodec_open2</c>, and may set <c>thread_type</c> to frame or slice threading when the codec supports it.
    /// Zero leaves the default (typically single-threaded decode for many audio codecs).
    /// When non-zero, <see cref="AudioFileDecoder"/> clamps the value to <strong>1…64</strong> before assignment.
    /// </summary>
    public int CodecThreadCount { get; init; }

    /// <summary>
    /// When <see cref="CodecThreadCount"/> is non-zero, selects frame vs slice <c>thread_type</c> when the codec supports both.
    /// Default <see cref="AudioDecoderLibavThreadTypePreference.FrameFirst"/> matches the historical precedence.
    /// </summary>
    public AudioDecoderLibavThreadTypePreference LibavThreadTypePreference { get; init; }
}
