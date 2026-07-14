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

    internal static string ContainerFileExtension(EncodeContainer container) => container.GetFileExtension();

    internal static AVCodecID VideoCodecId(EncodeVideoCodec codec) => codec switch
    {
        EncodeVideoCodec.H264 => AVCodecID.AV_CODEC_ID_H264,
        EncodeVideoCodec.Hevc => AVCodecID.AV_CODEC_ID_HEVC,
        EncodeVideoCodec.Av1 => AVCodecID.AV_CODEC_ID_AV1,
        EncodeVideoCodec.Vp9 => AVCodecID.AV_CODEC_ID_VP9,
        EncodeVideoCodec.ProRes422 or EncodeVideoCodec.ProRes4444 => AVCodecID.AV_CODEC_ID_PRORES,
        EncodeVideoCodec.DnxHr => AVCodecID.AV_CODEC_ID_DNXHD,
        EncodeVideoCodec.Ffv1 => AVCodecID.AV_CODEC_ID_FFV1,
        EncodeVideoCodec.Mpeg4 => AVCodecID.AV_CODEC_ID_MPEG4,
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    /// <summary>Ordered encoder name candidates - the first one this build ships is used. AV1 in
    /// particular has several implementations; libsvtav1 is the fast realtime-capable one.</summary>
    internal static string[] VideoEncoderNames(EncodeVideoCodec codec) => codec switch
    {
        EncodeVideoCodec.H264 => ["libx264"],
        EncodeVideoCodec.Hevc => ["libx265"],
        EncodeVideoCodec.Av1 => ["libsvtav1", "librav1e", "libaom-av1"],
        EncodeVideoCodec.Vp9 => ["libvpx-vp9"],
        EncodeVideoCodec.ProRes422 or EncodeVideoCodec.ProRes4444 => ["prores_ks", "prores"],
        EncodeVideoCodec.DnxHr => ["dnxhd"],
        EncodeVideoCodec.Ffv1 => ["ffv1"],
        EncodeVideoCodec.Mpeg4 => [], // built-in mpeg4 encoder (by codec id)
        _ => [],
    };

    /// <summary>Encoder-specific private options (av_opt_set on priv_data) applied at open. ProRes 4444
    /// selects its profile; DNxHR picks an HR profile from the encode pixel format.</summary>
    internal static IReadOnlyList<(string Key, string Value)> VideoEncoderPrivateOptions(
        EncodeVideoCodec codec, PixelFormat encodePixel) => codec switch
    {
        EncodeVideoCodec.ProRes4444 => [("profile", "4444")],
        EncodeVideoCodec.ProRes422 => [("profile", "standard")],
        // DNxHR profile is driven by bit depth / chroma: 10-bit → hqx, 8-bit 4:2:2 → hq.
        EncodeVideoCodec.DnxHr => encodePixel is PixelFormat.Yuv422P10Le
            ? [("profile", "dnxhr_hqx")]
            : [("profile", "dnxhr_hq")],
        _ => [],
    };

    internal static AVCodecID AudioCodecId(EncodeAudioCodec codec) => codec switch
    {
        EncodeAudioCodec.Aac => AVCodecID.AV_CODEC_ID_AAC,
        EncodeAudioCodec.Opus => AVCodecID.AV_CODEC_ID_OPUS,
        EncodeAudioCodec.Mp3 => AVCodecID.AV_CODEC_ID_MP3,
        EncodeAudioCodec.Ac3 => AVCodecID.AV_CODEC_ID_AC3,
        EncodeAudioCodec.Eac3 => AVCodecID.AV_CODEC_ID_EAC3,
        EncodeAudioCodec.Flac => AVCodecID.AV_CODEC_ID_FLAC,
        EncodeAudioCodec.Alac => AVCodecID.AV_CODEC_ID_ALAC,
        EncodeAudioCodec.Pcm16 => AVCodecID.AV_CODEC_ID_PCM_S16LE,
        EncodeAudioCodec.Pcm24 => AVCodecID.AV_CODEC_ID_PCM_S24LE,
        _ => throw new ArgumentOutOfRangeException(nameof(codec)),
    };

    internal static string[] AudioEncoderNames(EncodeAudioCodec codec) => codec switch
    {
        EncodeAudioCodec.Aac => ["aac"],
        EncodeAudioCodec.Opus => ["libopus", "opus"],
        EncodeAudioCodec.Mp3 => ["libmp3lame"],
        EncodeAudioCodec.Ac3 => ["ac3"],
        EncodeAudioCodec.Eac3 => ["eac3"],
        EncodeAudioCodec.Flac => ["flac"],
        EncodeAudioCodec.Alac => ["alac"],
        EncodeAudioCodec.Pcm16 or EncodeAudioCodec.Pcm24 => [], // built-in pcm encoder (by codec id)
        _ => [],
    };

    internal static unsafe bool VideoEncoderAvailable(EncodeVideoCodec codec) =>
        FindVideoEncoder(codec) is not null;

    internal static unsafe bool AudioEncoderAvailable(EncodeAudioCodec codec) =>
        FindAudioEncoder(codec) is not null;

    internal static unsafe IReadOnlyList<int> AudioEncoderSampleRates(EncodeAudioCodec codec)
    {
        var encoder = FindAudioEncoder(codec);
#pragma warning disable CS0618 // FFmpeg retains this zero-terminated capability list for AVCodec.
        if (encoder is null || encoder->supported_samplerates is null)
            return [];
        var rates = new List<int>();
        for (var p = encoder->supported_samplerates; *p != 0; p++)
            rates.Add(*p);
#pragma warning restore CS0618
        return rates;
    }

    internal static unsafe bool VideoEncoderSupportsPixelFormat(EncodeVideoCodec codec, PixelFormat pixelFormat)
    {
        var encoder = FindVideoEncoder(codec);
        var avPixel = FfmpegVideoPixelMaps.ToAvPixelFormat(pixelFormat);
        if (encoder is null || avPixel is null)
            return false;
#pragma warning disable CS0618 // FFmpeg retains this zero-terminated capability list for AVCodec.
        if (encoder->pix_fmts is null)
            return true;
        for (var p = encoder->pix_fmts; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            if (*p == avPixel.Value)
                return true;
#pragma warning restore CS0618
        return false;
    }

    internal static unsafe AVCodec* FindVideoEncoder(EncodeVideoCodec codec)
    {
        FFmpegRuntime.EnsureInitialized();
        foreach (var name in VideoEncoderNames(codec))
        {
            var byName = avcodec_find_encoder_by_name(name);
            if (byName is not null)
                return byName;
        }

        return avcodec_find_encoder(VideoCodecId(codec)); // built-in / codec-id fallback
    }

    internal static unsafe AVCodec* FindAudioEncoder(EncodeAudioCodec codec)
    {
        FFmpegRuntime.EnsureInitialized();
        foreach (var name in AudioEncoderNames(codec))
        {
            var byName = avcodec_find_encoder_by_name(name);
            if (byName is not null)
                return byName;
        }

        return avcodec_find_encoder(AudioCodecId(codec)); // built-in / codec-id fallback
    }

    /// <summary>Picks the pixel format frames are converted to before hitting the encoder.</summary>
    internal static PixelFormat PickVideoEncodePixel(PixelFormat input, EncodeVideoCodec codec, PixelFormat? force)
    {
        if (force is { } f)
            return f;

        var is10Bit = input is PixelFormat.Yuv420P10Le or PixelFormat.P010 or PixelFormat.Yuv420P12Le
            or PixelFormat.Yuv422P10Le or PixelFormat.Yuv444P12Le or PixelFormat.Yuva444P12Le;

        switch (codec)
        {
            case EncodeVideoCodec.ProRes422:
                // prores_ks accepts ONLY 10-bit (yuv422p10le / yuv444p10le / yuva444p10le); an 8- or
                // 12-bit format fails avcodec_open2 (review H8, verified against FFmpeg 8.1.2).
                return PixelFormat.Yuv422P10Le;

            case EncodeVideoCodec.ProRes4444:
                // 4444 keeps alpha when the source carries it; 10-bit is the encoder's only depth.
                return input is PixelFormat.Yuva444P12Le or PixelFormat.Yuva444P10Le
                    or PixelFormat.Bgra32 or PixelFormat.Rgba32 or PixelFormat.Abgr32
                    ? PixelFormat.Yuva444P10Le
                    : PixelFormat.Yuv444P10Le;

            case EncodeVideoCodec.DnxHr:
                // dnxhr_hqx = 10-bit 4:2:2; dnxhr_hq = 8-bit 4:2:2 (profile set in private options).
                return is10Bit ? PixelFormat.Yuv422P10Le : PixelFormat.Yuv422P;

            case EncodeVideoCodec.Ffv1:
                // Lossless: preserve the source layout when FFmpeg can map it, else I420.
                return FfmpegVideoPixelMaps.ToAvPixelFormat(input) is not null ? input : PixelFormat.I420;

            case EncodeVideoCodec.Mpeg4:
                return PixelFormat.I420; // the built-in mpeg4 encoder is yuv420p-only

            case EncodeVideoCodec.Av1:
            case EncodeVideoCodec.Vp9:
                // Both accept 8-bit and 10-bit 4:2:0; keep 10-bit sources, else I420.
                return is10Bit ? PixelFormat.Yuv420P10Le : PixelFormat.I420;
        }

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
