using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.FFmpeg.Encode.Internal;

namespace S.Media.FFmpeg.Encode;

/// <summary>
/// Combined A+V file muxer exposing <see cref="IVideoOutput"/> and <see cref="IAudioOutput"/> legs.
/// </summary>
public sealed class FFmpegMuxFileOutput : IDisposable
{
    private readonly FfmpegMuxContext _mux;

    private FFmpegMuxFileOutput(FfmpegMuxContext mux, FFmpegMuxFileOutputOptions options)
    {
        _mux = mux;
        Video = options.IncludeVideo
            ? new FFmpegVideoFileOutput(mux, options.Video, ownsMux: false)
            : null;
        Audio = options.IncludeAudio
            ? new FFmpegAudioFileOutput(mux, options.Audio, ownsMux: false)
            : null;
    }

    /// <summary>Creates a muxed output file. Call <see cref="IVideoOutput.Configure"/> / <see cref="IAudioOutput.Configure"/> before submitting frames.</summary>
    public static FFmpegMuxFileOutput Open(string path, FFmpegMuxFileOutputOptions? options = null)
    {
        options ??= new FFmpegMuxFileOutputOptions();
        var mux = new FfmpegMuxContext(path, options.Container, options.IncludeVideo, options.IncludeAudio);
        return new FFmpegMuxFileOutput(mux, options);
    }

    public FFmpegVideoFileOutput? Video { get; }

    public FFmpegAudioFileOutput? Audio { get; }

    public IVideoOutput? VideoOutput => Video;

    public IAudioOutput? AudioOutput => Audio;

    public void Dispose()
    {
        Video?.Dispose();
        Audio?.Dispose();
        _mux.Dispose();
    }
}
