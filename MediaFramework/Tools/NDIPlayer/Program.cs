using System.Text;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using S.Media.NDI;
using S.Media.NDI.Audio;

Console.OutputEncoding = Encoding.UTF8;

var argv = ParseArgs(args, out var videoOnly, out var audioOnly, out var noHw);
if (argv is null)
{
    Console.WriteLine("Usage: NDIPlayer [--video-only] [--audio-only] [--no-hw] <media-file> <ndi-source-name>");
    Console.WriteLine("  Decodes the file and sends video and/or audio to a new NDI source (default: both).");
    return 2;
}

var (mediaPath, ndiName) = argv.Value;
if (!File.Exists(mediaPath))
{
    Console.Error.WriteLine($"file not found: {mediaPath}");
    return 3;
}

FFmpegRuntime.EnsureInitialized();

var videoOpts = new VideoDecoderOpenOptions
{
    TryHardwareAcceleration = !noHw,
};

using var dec = MediaContainerDecoder.Open(mediaPath, videoOpts);
using var ndi = new NDIOutput(ndiName, clockVideo: false, clockAudio: true);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Task? videoTask = null;
if (!audioOnly)
{
    VideoFormatNegotiator.Connect(dec.Video, ndi.VideoSink);
    if (videoOnly)
        PumpVideo(dec.Video, ndi.VideoSink, cts.Token);
    else
        videoTask = Task.Run(() => PumpVideo(dec.Video, ndi.VideoSink, cts.Token), cts.Token);
}

if (!videoOnly)
{
    var ndAudio = ndi.EnableAudio(dec.Audio.Format);
    PumpAudio(dec.Audio, ndAudio, cts.Token);
}

if (videoTask is not null)
{
    try
    {
        videoTask.GetAwaiter().GetResult();
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C
    }
}

return 0;

static void PumpVideo(IVideoSource src, IVideoSink sink, CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        if (!src.TryReadNextFrame(out var frame))
            break;
        try
        {
            sink.Submit(frame);
        }
        finally
        {
            frame.Dispose();
        }
    }
}

static void PumpAudio(IAudioSource src, NDIAudioSink sink, CancellationToken ct)
{
    var fmt = src.Format;
    var buf = new float[8192 * fmt.Channels];
    long samples = 0;
    while (!ct.IsCancellationRequested)
    {
        var n = src.ReadInto(buf);
        if (n == 0)
        {
            if (src.IsExhausted)
                break;
            Thread.Sleep(2);
            continue;
        }

        var spc = n / fmt.Channels;
        var pts = TimeSpan.FromSeconds(samples / (double)fmt.SampleRate);
        var frame = new AudioFrame(pts, fmt, spc, buf.AsMemory(0, n));
        sink.Submit(frame);
        samples += spc;
    }
}

static (string media, string ndiName)? ParseArgs(string[] args, out bool videoOnly, out bool audioOnly, out bool noHw)
{
    videoOnly = false;
    audioOnly = false;
    noHw = false;
    var rest = new List<string>();
    foreach (var a in args)
    {
        switch (a)
        {
            case "--video-only":
                videoOnly = true;
                break;
            case "--audio-only":
                audioOnly = true;
                break;
            case "--no-hw":
                noHw = true;
                break;
            default:
                rest.Add(a);
                break;
        }
    }

    if (videoOnly && audioOnly)
        return null;
    if (rest.Count != 2)
        return null;
    return (rest[0], rest[1]);
}
