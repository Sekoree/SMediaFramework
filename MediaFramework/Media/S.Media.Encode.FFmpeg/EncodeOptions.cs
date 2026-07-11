namespace S.Media.Encode.FFmpeg;

/// <summary>Output container for an encode session. Push protocols imply a container
/// (RTMP ⇒ <see cref="Flv"/>, SRT/local LAN server ⇒ <see cref="MpegTs"/>).</summary>
public enum EncodeContainer
{
    Mp4,
    Matroska,
    Mov,
    MpegTs,
    Flv,
}

public enum EncodeVideoCodec
{
    H264,
    Hevc,
    ProRes422,
    /// <summary>Always-available fallback when no libx264/libx265 is in the FFmpeg build.</summary>
    Mpeg4,
}

public enum EncodeAudioCodec
{
    Aac,
    Opus,
    Flac,
    /// <summary>Uncompressed PCM (s16), Matroska/Mov only.</summary>
    Pcm16,
}

/// <summary>Which legs an encode session carries.</summary>
public enum EncodeOutputMode
{
    VideoAndAudio,
    VideoOnly,
    AudioOnly,
}

/// <summary>
/// Video encoder settings. Rate control is either <see cref="BitrateBps"/> (CBR-ish target) or
/// <see cref="Crf"/> (quality-target, libx264/libx265 only) - setting both is a validation error;
/// setting neither uses the codec default. <see cref="ScaleWidth"/>/<see cref="ScaleHeight"/> of 0
/// keep the source dimensions (either may be 0 alone: the other is derived preserving aspect, rounded
/// to even).
/// </summary>
public sealed record VideoEncodeOptions
{
    public EncodeVideoCodec Codec { get; init; } = EncodeVideoCodec.H264;

    /// <summary>Target bitrate in bits/s (0 = unset).</summary>
    public long BitrateBps { get; init; }

    /// <summary>Constant-rate-factor quality target (null = unset). Lower is better; 18–28 is typical for x264.</summary>
    public int? Crf { get; init; }

    /// <summary>Encoder speed/efficiency preset ("ultrafast" … "veryslow", libx264/libx265). Null = default.</summary>
    public string? Preset { get; init; }

    /// <summary>GOP size in frames (0 = codec default). Live streaming wants ~2 s worth.</summary>
    public int GopSize { get; init; }

    /// <summary>Output width in pixels (0 = source width, or derived from <see cref="ScaleHeight"/> preserving aspect).</summary>
    public int ScaleWidth { get; init; }

    /// <summary>Output height in pixels (0 = source height, or derived from <see cref="ScaleWidth"/> preserving aspect).</summary>
    public int ScaleHeight { get; init; }

    /// <summary>When set, forces the encode pixel format; otherwise derived from the source format and codec.</summary>
    public PixelFormat? EncodePixelFormat { get; init; }
}

/// <summary>
/// One audio track in the output. <see cref="Channels"/>/<see cref="SampleRate"/> of 0 keep the
/// submitted format; a differing value converts (channel mixdown/upmix + resample) inside the encoder
/// leg. <see cref="Name"/>/<see cref="Language"/> become stream metadata (multi-track containers).
/// </summary>
public sealed record AudioLegOptions
{
    public EncodeAudioCodec Codec { get; init; } = EncodeAudioCodec.Aac;

    /// <summary>Target bitrate in bits/s (0 = codec default).</summary>
    public long BitrateBps { get; init; }

    /// <summary>Output channel count (0 = as submitted).</summary>
    public int Channels { get; init; }

    /// <summary>Output sample rate in Hz (0 = as submitted).</summary>
    public int SampleRate { get; init; }

    /// <summary>Track title metadata (e.g. "Commentary").</summary>
    public string? Name { get; init; }

    /// <summary>ISO 639-2 language code metadata (e.g. "eng", "jpn").</summary>
    public string? Language { get; init; }
}

/// <summary>Everything an <see cref="FFmpegEncodeSession"/> needs besides the destination.</summary>
public sealed record EncodeSessionOptions
{
    public EncodeContainer Container { get; init; } = EncodeContainer.Mp4;

    public EncodeOutputMode OutputMode { get; init; } = EncodeOutputMode.VideoAndAudio;

    public VideoEncodeOptions Video { get; init; } = new();

    /// <summary>The output's audio tracks, in stream order. Ignored when <see cref="OutputMode"/> is
    /// <see cref="EncodeOutputMode.VideoOnly"/>; must be non-empty otherwise.</summary>
    public IReadOnlyList<AudioLegOptions> AudioLegs { get; init; } = [new AudioLegOptions()];

    public bool IncludesVideo => OutputMode != EncodeOutputMode.AudioOnly;

    public bool IncludesAudio => OutputMode != EncodeOutputMode.VideoOnly;

    /// <summary>
    /// Structured validation: container/codec compatibility, rate-control conflicts, leg counts, and
    /// (when <paramref name="probeEncoders"/>) whether this FFmpeg build actually ships each selected
    /// encoder. Empty list = valid.
    /// </summary>
    public IReadOnlyList<string> Validate(bool probeEncoders = true)
    {
        var errors = new List<string>();

        if (IncludesAudio && AudioLegs.Count == 0)
            errors.Add("At least one audio track is required unless the output is video-only.");
        if (!IncludesAudio && !IncludesVideo)
            errors.Add("Output carries neither video nor audio.");

        if (Container is EncodeContainer.Flv && AudioLegs.Count > 1 && IncludesAudio)
            errors.Add("FLV (RTMP) carries a single audio track - remove the extra tracks or switch container.");

        if (IncludesVideo)
        {
            if (Video.BitrateBps > 0 && Video.Crf is not null)
                errors.Add("Video rate control: set either a bitrate or a CRF, not both.");
            if (Video.Crf is < 0 or > 51)
                errors.Add("Video CRF must be between 0 and 51.");
            if (Video.ScaleWidth < 0 || Video.ScaleHeight < 0)
                errors.Add("Video scale dimensions cannot be negative.");
            if (Container is EncodeContainer.Flv && Video.Codec is not EncodeVideoCodec.H264)
                errors.Add("FLV (RTMP) requires H.264 video.");
            if (Container is EncodeContainer.Mp4 && Video.Codec is EncodeVideoCodec.ProRes422)
                errors.Add("ProRes belongs in MOV or Matroska, not MP4.");
        }

        if (IncludesAudio)
        {
            for (var i = 0; i < AudioLegs.Count; i++)
            {
                var leg = AudioLegs[i];
                if (leg.Channels < 0 || leg.Channels > 16)
                    errors.Add($"Audio track {i + 1}: channel count {leg.Channels} is out of range.");
                if (leg.SampleRate is < 0 or > 384_000)
                    errors.Add($"Audio track {i + 1}: sample rate {leg.SampleRate} is out of range.");
                if (Container is EncodeContainer.Flv && leg.Codec is not EncodeAudioCodec.Aac)
                    errors.Add("FLV (RTMP) requires AAC audio.");
                if (Container is EncodeContainer.Mp4 && leg.Codec is EncodeAudioCodec.Pcm16)
                    errors.Add("PCM audio belongs in Matroska or MOV, not MP4.");
            }
        }

        if (probeEncoders)
        {
            FFmpegRuntime.EnsureInitialized();
            if (IncludesVideo && !Internal.FfmpegEncodeMaps.VideoEncoderAvailable(Video.Codec))
                errors.Add($"This FFmpeg build has no encoder for {Video.Codec}.");
            if (IncludesAudio)
            {
                foreach (var leg in AudioLegs.DistinctBy(l => l.Codec))
                {
                    if (!Internal.FfmpegEncodeMaps.AudioEncoderAvailable(leg.Codec))
                        errors.Add($"This FFmpeg build has no encoder for {leg.Codec}.");
                }
            }
        }

        return errors;
    }
}
