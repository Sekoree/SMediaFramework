namespace S.Media.Core.Video;

/// <summary>
/// <see cref="IVideoOutput"/> that negotiates like a permissive display (empty <see cref="AcceptedPixelFormats"/> → first native source format)
/// and drops every frame in <see cref="Submit"/> by disposing it. Used as a hidden primary on <see cref="S.Media.Core.Video.VideoRouter"/>
/// so playback can run before any real video output is attached.
/// </summary>
public sealed class DiscardingVideoOutput : IVideoOutput
{
    private VideoFormat _format;

    public VideoFormat Format => _format;

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = Array.Empty<PixelFormat>();

    public void Configure(VideoFormat format) => _format = format;

    public void Submit(VideoFrame frame) => frame.Dispose();
}
