using S.Media.Core.Video;
using S.Media.FFmpeg.Encode.Internal;

namespace S.Media.FFmpeg.Encode;

/// <summary>
/// Encodes negotiated <see cref="VideoFrame"/> instances into a video stream inside an output file
/// (standalone or via <see cref="FFmpegMuxFileOutput"/>).
/// </summary>
public sealed class FFmpegVideoFileOutput : IVideoOutput, IDisposable
{
    private readonly FfmpegVideoEncoder _encoder;

    /// <summary>Opens a video-only output file.</summary>
    public static FFmpegVideoFileOutput Open(string path, FFmpegVideoFileOutputOptions? options = null,
        FFmpegEncodeContainer container = FFmpegEncodeContainer.Mp4)
    {
        var mux = new FfmpegMuxContext(path, container, expectVideo: true, expectAudio: false);
        return new FFmpegVideoFileOutput(mux, options ?? new FFmpegVideoFileOutputOptions(), ownsMux: true);
    }

    internal FFmpegVideoFileOutput(FfmpegMuxContext mux, FFmpegVideoFileOutputOptions options, bool ownsMux)
    {
        Mux = ownsMux ? mux : null;
        _encoder = new FfmpegVideoEncoder(mux, options);
    }

    internal FfmpegMuxContext? Mux { get; }

    public VideoFormat Format => _encoder.Format;

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _encoder.AcceptedPixelFormats;

    public void Configure(VideoFormat format) => _encoder.Configure(format);

    public void Submit(VideoFrame frame) => _encoder.Submit(frame);

    public void Dispose()
    {
        _encoder.Dispose();
        Mux?.Dispose();
    }
}
