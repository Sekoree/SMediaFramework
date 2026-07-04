namespace S.Media.Decode.FFmpeg.Audio;

/// <summary>
/// Chooses libav <c>AVCodecContext.thread_type</c> when <see cref="AudioFileDecoderOpenOptions.CodecThreadCount"/> is non-zero
/// and the codec advertises both <c>AV_CODEC_CAP_FRAME_THREADS</c> and <c>AV_CODEC_CAP_SLICE_THREADS</c>.
/// </summary>
public enum AudioDecoderLibavThreadTypePreference
{
    /// <summary>Prefer <c>FF_THREAD_FRAME</c> when frame threads are supported, else slice threads.</summary>
    FrameFirst = 0,

    /// <summary>Prefer <c>FF_THREAD_SLICE</c> when slice threads are supported, else frame threads.</summary>
    SliceFirst = 1,
}
