// Phase 3 FormatSwitchProbe — proves mid-stream video format-change reconfig through the VideoRouter.
// Submits synthetic BGRA frames at one resolution, switches resolution mid-stream, and verifies the
// downstream IVideoOutput re-Configures to the new format (the idempotent reconfigure path). No decoder.
using S.Media.Core.Video;
using S.Media.Routing;

var router = new VideoRouter();
var sink = new RecordingOutput();
var outId = router.AddOutput(sink, synchronous: true);
var input = router.AddInput(outId);

Submit(input.Output, 320, 240, count: 3, startFrame: 0);   // format A
Submit(input.Output, 640, 480, count: 3, startFrame: 3);   // format B — mid-stream switch

router.Dispose();

Console.WriteLine($"sink saw {sink.Frames} frames; Configure formats = [{string.Join(", ", sink.Configures.Select(f => $"{f.Width}x{f.Height} {f.PixelFormat}"))}]");

var sawA = sink.Configures.Any(f => f is { Width: 320, Height: 240 });
var sawB = sink.Configures.Any(f => f is { Width: 640, Height: 480 });
if (!sawA || !sawB || sink.Frames < 6)
{
    Console.Error.WriteLine($"FAIL: expected reconfigure to both formats + 6 frames (sawA={sawA}, sawB={sawB}, frames={sink.Frames}).");
    return 1;
}

Console.WriteLine("FormatSwitchProbe OK — VideoRouter reconfigured the output mid-stream on a resolution change.");
return 0;

static void Submit(IVideoOutput input, int w, int h, int count, int startFrame)
{
    var fmt = new VideoFormat(w, h, PixelFormat.Bgra32, new Rational(30, 1));
    input.Configure(fmt);
    var stride = w * 4;
    for (var i = 0; i < count; i++)
    {
        var buf = new byte[stride * h];
        var frame = new VideoFrame(TimeSpan.FromMilliseconds((startFrame + i) * 33), fmt, [buf], [stride]);
        input.Submit(frame);
    }
}

sealed class RecordingOutput : IVideoOutput
{
    public List<VideoFormat> Configures { get; } = [];
    public long Frames;
    private VideoFormat _format;

    public VideoFormat Format => _format;
    public IReadOnlyList<PixelFormat> AcceptedPixelFormats { get; } = Array.Empty<PixelFormat>();

    public void Configure(VideoFormat format)
    {
        _format = format;
        Configures.Add(format);
    }

    public void Submit(VideoFrame frame)
    {
        Frames++;
        frame.Dispose();
    }
}
