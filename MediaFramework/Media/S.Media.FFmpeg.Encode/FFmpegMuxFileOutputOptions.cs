namespace S.Media.FFmpeg.Encode;

/// <summary>Combined A+V mux settings for <see cref="FFmpegMuxFileOutput"/>.</summary>
public sealed record FFmpegMuxFileOutputOptions
{
    public FFmpegEncodeContainer Container { get; init; } = FFmpegEncodeContainer.Mp4;

    public FFmpegVideoFileOutputOptions Video { get; init; } = new();

    public FFmpegAudioFileOutputOptions Audio { get; init; } = new();

    /// <summary>When <c>false</c>, only video is muxed (audio leg omitted).</summary>
    public bool IncludeAudio { get; init; } = true;

    /// <summary>When <c>false</c>, only audio is muxed (video leg omitted).</summary>
    public bool IncludeVideo { get; init; } = true;
}
