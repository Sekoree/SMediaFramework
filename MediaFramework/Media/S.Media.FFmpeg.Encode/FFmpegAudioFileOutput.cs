using S.Media.Core.Audio;
using S.Media.FFmpeg.Encode.Internal;

namespace S.Media.FFmpeg.Encode;

/// <summary>
/// Encodes packed-float PCM into an audio stream inside an output file
/// (standalone or via <see cref="FFmpegMuxFileOutput"/>).
/// </summary>
public sealed class FFmpegAudioFileOutput : IAudioOutput, IDisposable
{
    private readonly FfmpegAudioEncoder _encoder;

    /// <summary>Opens an audio-only output file.</summary>
    public static FFmpegAudioFileOutput Open(string path, AudioFormat format,
        FFmpegAudioFileOutputOptions? options = null,
        FFmpegEncodeContainer container = FFmpegEncodeContainer.Mp4)
    {
        var mux = new FfmpegMuxContext(path, container, expectVideo: false, expectAudio: true);
        var output = new FFmpegAudioFileOutput(mux, options ?? new FFmpegAudioFileOutputOptions(), ownsMux: true);
        output.Configure(format);
        return output;
    }

    internal FFmpegAudioFileOutput(FfmpegMuxContext mux, FFmpegAudioFileOutputOptions options, bool ownsMux)
    {
        Mux = ownsMux ? mux : null;
        _encoder = new FfmpegAudioEncoder(mux, options);
    }

    internal FfmpegMuxContext? Mux { get; }

    public AudioFormat Format => _encoder.Format;

    public void Configure(AudioFormat format) => _encoder.Configure(format);

    public void Submit(ReadOnlySpan<float> packedSamples) => _encoder.Submit(packedSamples);

    public void Dispose()
    {
        _encoder.Dispose();
        Mux?.Dispose();
    }
}
