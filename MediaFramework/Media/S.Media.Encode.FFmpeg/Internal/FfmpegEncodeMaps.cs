namespace S.Media.Encode.FFmpeg.Internal;

/// <summary>Container/codec name maps + encode pixel-format policy (salvaged from the pre-rewrite
/// S.Media.FFmpeg.Encode and extended with the streaming containers/codecs).</summary>
internal static class FfmpegEncodeMaps
{
    internal static string ContainerShortName(EncodeContainer container) => container switch
    {
        EncodeContainer.Mp4 => "mp4",
        EncodeContainer.Matroska => "matroska",
        EncodeContainer.Mov => "mov",
        EncodeContainer.MpegTs => "mpegts",
        EncodeContainer.Flv => "flv",
        _ => throw new ArgumentOutOfRangeException(nameof(container)),
    };

    internal static string ContainerFileExtension(EncodeContainer container) => container switch
    {
        EncodeContainer.Mp4 => ".mp4",
        EncodeContainer.Matroska => ".mkv",
        EncodeContainer.Mov => ".mov",
        EncodeContainer.MpegTs => ".ts",
        EncodeContainer.Flv => ".flv",
        _ => throw new ArgumentOutOfRangeException(nameof(container)),
    };

    internal static AVCodecID VideoCodecId(EncodeVideoCodec codec) => codec switch
    {
        EncodeVideoCodec.H264 => AVCodecID.AV_CODEC_ID_H264,
        EncodeVideoCodec.Hevc => AVCodecID.AV_CODEC_ID_HEVC,
        EncodeVideoCodec.ProRes422 => AVCodecID.AV_CODEC_ID_PRORES,
        EncodeVideoCodec.Mpeg4 => AVCodecID.AV_CODEC_ID_MPEG4,
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    internal static string? VideoEncoderName(EncodeVideoCodec codec) => codec switch
    {
        EncodeVideoCodec.H264 => "libx264",
        EncodeVideoCodec.Hevc => "libx265",
        EncodeVideoCodec.ProRes422 => "prores_ks",
        EncodeVideoCodec.Mpeg4 => null, // built-in mpeg4 encoder
        _ => null,
    };

    internal static AVCodecID AudioCodecId(EncodeAudioCodec codec) => codec switch
    {
        EncodeAudioCodec.Aac => AVCodecID.AV_CODEC_ID_AAC,
        EncodeAudioCodec.Opus => AVCodecID.AV_CODEC_ID_OPUS,
        EncodeAudioCodec.Flac => AVCodecID.AV_CODEC_ID_FLAC,
        EncodeAudioCodec.Pcm16 => AVCodecID.AV_CODEC_ID_PCM_S16LE,
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    internal static string? AudioEncoderName(EncodeAudioCodec codec) => codec switch
    {
        EncodeAudioCodec.Aac => "aac",
        EncodeAudioCodec.Opus => "libopus",
        EncodeAudioCodec.Flac => "flac",
        EncodeAudioCodec.Pcm16 => null, // built-in pcm encoder
        _ => null,
    };

    internal static unsafe bool VideoEncoderAvailable(EncodeVideoCodec codec) =>
        FindVideoEncoder(codec) is not null;

    internal static unsafe bool AudioEncoderAvailable(EncodeAudioCodec codec) =>
        FindAudioEncoder(codec) is not null;

    internal static unsafe AVCodec* FindVideoEncoder(EncodeVideoCodec codec)
    {
        AVCodec* c = null;
        if (VideoEncoderName(codec) is { } name)
            c = avcodec_find_encoder_by_name(name);
        if (c is null)
            c = avcodec_find_encoder(VideoCodecId(codec));
        return c;
    }

    internal static unsafe AVCodec* FindAudioEncoder(EncodeAudioCodec codec)
    {
        AVCodec* c = null;
        if (AudioEncoderName(codec) is { } name)
            c = avcodec_find_encoder_by_name(name);
        if (c is null)
            c = avcodec_find_encoder(AudioCodecId(codec));
        return c;
    }

    /// <summary>Picks the pixel format frames are converted to before hitting the encoder.</summary>
    internal static PixelFormat PickVideoEncodePixel(PixelFormat input, EncodeVideoCodec codec, PixelFormat? force)
    {
        if (force is { } f)
            return f;

        if (codec == EncodeVideoCodec.ProRes422)
        {
            if (input is PixelFormat.Yuv422P10Le or PixelFormat.Yuv422P12Le or PixelFormat.Yuv422P)
                return input;
            return PixelFormat.Yuv422P10Le;
        }

        if (codec == EncodeVideoCodec.Mpeg4)
            return PixelFormat.I420; // the built-in mpeg4 encoder is yuv420p-only

        return input switch
        {
            PixelFormat.Yuv420P10Le or PixelFormat.P010 or PixelFormat.Yuv420P12Le => PixelFormat.Yuv420P10Le,
            PixelFormat.Yuv444P12Le or PixelFormat.Yuva444P12Le => PixelFormat.Yuv444P12Le,
            PixelFormat.Nv12 or PixelFormat.I420 or PixelFormat.Yv12 => PixelFormat.I420,
            PixelFormat.Bgra32 or PixelFormat.Rgba32 => PixelFormat.I420,
            _ when FfmpegVideoPixelMaps.ToAvPixelFormat(input) is not null => input,
            _ => PixelFormat.I420,
        };
    }

    internal static AVRational ToAvRational(Rational r) => new() { num = r.Numerator, den = r.Denominator };

    internal static AVRational TimeBaseFromFrameRate(Rational frameRate)
    {
        if (frameRate.Numerator <= 0 || frameRate.Denominator <= 0)
            return new AVRational { num = 1, den = 30 };
        return new AVRational { num = frameRate.Denominator, den = frameRate.Numerator };
    }
}
