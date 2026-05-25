using S.Media.Core.Video;
using S.Media.FFmpeg.Video.Internal;

namespace S.Media.FFmpeg.Encode.Internal;

internal static class FfmpegEncodeMaps
{
    internal static string ContainerShortName(FFmpegEncodeContainer container) => container switch
    {
        FFmpegEncodeContainer.Mp4 => "mp4",
        FFmpegEncodeContainer.Matroska => "matroska",
        FFmpegEncodeContainer.Mov => "mov",
        _ => throw new ArgumentOutOfRangeException(nameof(container)),
    };

    internal static AVCodecID VideoCodecId(FFmpegVideoCodec codec) => codec switch
    {
        FFmpegVideoCodec.H264 => AVCodecID.AV_CODEC_ID_H264,
        FFmpegVideoCodec.Hevc => AVCodecID.AV_CODEC_ID_HEVC,
        FFmpegVideoCodec.ProRes422 => AVCodecID.AV_CODEC_ID_PRORES,
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    internal static string? VideoEncoderName(FFmpegVideoCodec codec) => codec switch
    {
        FFmpegVideoCodec.H264 => "libx264",
        FFmpegVideoCodec.Hevc => "libx265",
        FFmpegVideoCodec.ProRes422 => "prores_ks",
        _ => null,
    };

    internal static AVCodecID AudioCodecId(FFmpegAudioCodec codec) => codec switch
    {
        FFmpegAudioCodec.Aac => AVCodecID.AV_CODEC_ID_AAC,
        FFmpegAudioCodec.Opus => AVCodecID.AV_CODEC_ID_OPUS,
        FFmpegAudioCodec.Flac => AVCodecID.AV_CODEC_ID_FLAC,
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    internal static string? AudioEncoderName(FFmpegAudioCodec codec) => codec switch
    {
        FFmpegAudioCodec.Aac => "aac",
        FFmpegAudioCodec.Opus => "libopus",
        FFmpegAudioCodec.Flac => "flac",
        _ => null,
    };

    internal static PixelFormat PickVideoEncodePixel(PixelFormat input, FFmpegVideoCodec codec, PixelFormat? force)
    {
        if (force is { } f)
            return f;

        if (codec == FFmpegVideoCodec.ProRes422)
        {
            if (input is PixelFormat.Yuv422P10Le or PixelFormat.Yuv422P12Le or PixelFormat.Yuv422P)
                return input;
            return PixelFormat.Yuv422P10Le;
        }

        return input switch
        {
            PixelFormat.Yuv420P10Le or PixelFormat.P010 or PixelFormat.Yuv420P12Le => PixelFormat.Yuv420P10Le,
            PixelFormat.Yuv444P12Le or PixelFormat.Yuva444P12Le => PixelFormat.Yuv444P12Le,
            PixelFormat.Nv12 or PixelFormat.I420 or PixelFormat.Yv12 => PixelFormat.Nv12,
            PixelFormat.Bgra32 or PixelFormat.Rgba32 => PixelFormat.Nv12,
            _ when FfmpegVideoPixelMaps.ToAvPixelFormat(input) is not null => input,
            _ => PixelFormat.Nv12,
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
