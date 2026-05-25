using S.Media.FFmpeg;
using S.Media.FFmpeg.Encode;
using S.Media.FFmpeg.Video;

// EncoderSmoke — decode a short clip and re-mux to H.264/AAC (or copy through encoder pipeline).

if (args.Length < 1 || args.Contains("-h") || args.Contains("--help"))
{
    Console.WriteLine("Usage: EncoderSmoke <input> [output.mp4]");
    Console.WriteLine("  With no input file, generates a 2s lavfi test clip when ffmpeg is on PATH.");
    return args.Length < 1 ? 2 : 0;
}

var input = args[0];
var output = args.Length > 1
    ? args[1]
    : Path.Combine(Path.GetTempPath(), $"mf_encode_smoke_{Guid.NewGuid():N}.mp4");

FFmpegRuntime.EnsureInitialized();

if (!File.Exists(input))
{
    Console.Error.WriteLine($"input not found: {input}");
    return 3;
}

Console.WriteLine($"input  : {input}");
Console.WriteLine($"output : {output}");

using var decoder = MediaContainerDecoder.Open(input, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });

if (!decoder.HasVideo)
{
    Console.Error.WriteLine("input has no video stream.");
    return 4;
}

var muxOpts = new FFmpegMuxFileOutputOptions
{
    Container = FFmpegEncodeContainer.Mp4,
    IncludeVideo = true,
    IncludeAudio = decoder.HasAudio,
    Video = new FFmpegVideoFileOutputOptions { Codec = FFmpegVideoCodec.H264, GopSize = 12 },
    Audio = new FFmpegAudioFileOutputOptions { Codec = FFmpegAudioCodec.Aac },
};

var videoFrames = 0;
var audioChunks = 0;
const int audioChunk = 480;

{
    using var mux = FFmpegMuxFileOutput.Open(output, muxOpts);

    mux.Video!.Configure(decoder.Video.Format);
    if (mux.Audio is not null)
        mux.Audio.Configure(decoder.Audio.Format);

    while (!decoder.Video.IsExhausted && decoder.Video.TryReadNextFrame(out var vf))
    {
        mux.Video!.Submit(vf);
        videoFrames++;
    }

    if (mux.Audio is not null)
    {
        var scratch = new float[audioChunk * decoder.Audio.Format.Channels];
        while (!decoder.Audio.IsExhausted)
        {
            var read = decoder.Audio.ReadInto(scratch);
            if (read <= 0)
                break;
            mux.Audio.Submit(scratch.AsSpan(0, read));
            audioChunks++;
        }
    }
}

Console.WriteLine($"encoded video frames: {videoFrames}");
Console.WriteLine($"encoded audio chunks: {audioChunks}");

if (!File.Exists(output) || new FileInfo(output).Length < 64)
{
    Console.Error.WriteLine("output file missing or too small.");
    return 5;
}

Console.WriteLine($"ok — {new FileInfo(output).Length} bytes written");
return 0;
