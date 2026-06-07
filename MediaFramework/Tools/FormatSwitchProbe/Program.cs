using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.OpenGL;
using S.Media.Playback;

const string proRes = "/home/seko/Downloads/DAY1_EN3rd_Re_0826_2_Edited.mov";
const string h264 = "/home/seko/Videos/_MESMERIZER_ (German Version) _ by CALYTRIX (@Reoni @chiyonka_).mp4";
const string flac = "/home/seko/Music/EC - Still Waiting.flac";

var haPlayOpts = new MediaPlayerOpenOptions(
    TryHardwareAcceleration: true,
    IncludeAudioRouter: true,
    AudioPacketQueueDepth: 720,
    VideoPacketQueueDepth: 512,
    FileReadBufferBytes: 4 * 1024 * 1024);

static void ProbeNative(string label, string path)
{
    Console.WriteLine($"=== {label}: {path} ===");
    using var dec = MediaContainerDecoder.Open(path);
    Console.WriteLine($"  HasVideo={dec.HasVideo} HasAudio={dec.HasAudio} AttachedPic={dec.VideoIsAttachedPicture}");
    if (dec.HasVideo)
    {
        Console.WriteLine($"  Video format: {dec.Video.Format}");
        Console.WriteLine($"  Native: [{string.Join(", ", dec.Video.NativePixelFormats)}]");
    }
    Console.WriteLine();
}

static bool TryOpenWithLead(string label, string path, IVideoOutput? lead, bool disposeLead)
{
    Console.WriteLine($"--- TryOpen {label} (lead={lead?.GetType().Name ?? "discard"}) ---");
    if (!MediaPlayer.OpenFile(path).TryBuild(out var player, out var err))
    {
        Console.WriteLine($"  FAILED: {err}");
        return false;
    }

    Console.WriteLine($"  OK negotiated={player.Video.Format}");
    if (lead is not null)
    {
        var router = player.VideoRouter;
        var outId = router.AddOutput(lead, "branch", disposeOutputOnRouterDispose: disposeLead);
        if (!router.TryAddRoute(player.VideoRouterInputId, outId, out var routeErr))
        {
            Console.WriteLine($"  Route FAILED: {routeErr}");
            player.Dispose();
            return false;
        }
        Console.WriteLine($"  Routed branch OK");
    }

    player.Dispose();
    Console.WriteLine("  Disposed");
    return true;
}

ProbeNative("ProRes", proRes);
ProbeNative("H264", h264);
ProbeNative("FLAC", flac);

Console.WriteLine("=== Sequential open (discard primary, no persistent output) ===");
TryOpenWithLead("ProRes", proRes, null, false);
TryOpenWithLead("H264", h264, null, false);
TryOpenWithLead("FLAC", flac, null, false);

Console.WriteLine("=== Sequential open with persistent FakeOutput (simulates local GL) ===");
var persistent = new FakeGlLikeOutput();
TryOpenWithLead("ProRes", proRes, persistent, disposeLead: false);
TryOpenWithLead("H264", h264, persistent, disposeLead: false);
TryOpenWithLead("FLAC", flac, persistent, disposeLead: false);

Console.WriteLine("=== HaPlay path: Open(decoder) WITHOUT SeekPresentation ===");
foreach (var (label, path) in new[] { ("ProRes", proRes), ("H264", h264), ("FLAC", flac) })
{
    Console.WriteLine($"--- HaPlay-style {label} ---");
    try
    {
        using var dec = MediaContainerDecoder.Open(path, haPlayOpts.ToVideoDecoderOpenOptions());
        Console.WriteLine($"  decoder native=[{string.Join(", ", dec.Video.NativePixelFormats)}] fmt={dec.Video.Format}");
        if (!MediaPlayer.Open(dec)
                .WithOptions(haPlayOpts)
                .WithDecoderOwnership(MediaPlayerDecoderOwnership.BundleDisposesDecoder)
                .TryBuild(out var player, out var err))
        {
            Console.WriteLine($"  TryBuild FAILED: {err}");
        }
        else
        {
            Console.WriteLine($"  TryBuild OK negotiated={player!.Video.Format}");
            player.Dispose();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  EXCEPTION: {ex.Message}");
    }
}

Console.WriteLine("=== HaPlay path after ProRes session (persistent branch output) ===");
var persistent2 = new FakeGlLikeOutput();
{
    using var dec = MediaContainerDecoder.Open(proRes, haPlayOpts.ToVideoDecoderOpenOptions());
    if (MediaPlayer.Open(dec).WithOptions(haPlayOpts).TryBuild(out var player, out _))
    {
        var router = player!.VideoRouter;
        var outId = router.AddOutput(persistent2, "branch", disposeOutputOnRouterDispose: false);
        router.TryAddRoute(player.VideoRouterInputId, outId, out _);
        Console.WriteLine($"  ProRes OK {player.Video.Format}");
        player.Dispose();
    }
}
{
    using var dec = MediaContainerDecoder.Open(h264, haPlayOpts.ToVideoDecoderOpenOptions());
    Console.WriteLine($"  H264 decoder native=[{string.Join(", ", dec.Video.NativePixelFormats)}]");
    if (!MediaPlayer.Open(dec).WithOptions(haPlayOpts).TryBuild(out var player, out var err))
        Console.WriteLine($"  H264 TryBuild FAILED: {err}");
    else
    {
        var outId = player!.VideoRouter.AddOutput(persistent2, "branch", false);
        player.VideoRouter.TryAddRoute(player.VideoRouterInputId, outId, out var routeErr);
        Console.WriteLine($"  H264 OK {player.Video.Format} route={routeErr ?? "ok"}");
        player.Dispose();
    }
}

Console.WriteLine("Done.");

internal sealed class FakeGlLikeOutput : IVideoOutput
{
    private static readonly PixelFormat[] Accepted = YuvVideoRenderer.SupportedPixelFormats.ToArray();
    private VideoFormat _format;
    private bool _configured;

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => Accepted;
    public VideoFormat Format => _configured ? _format : throw new InvalidOperationException("not configured");

    public void Configure(VideoFormat format)
    {
        if (Array.IndexOf(Accepted, format.PixelFormat) < 0)
            throw new NotSupportedException($"does not accept {format.PixelFormat}");
        if (_configured && _format == format)
            return;
        _format = format;
        _configured = true;
        Console.WriteLine($"    FakeGlLikeOutput.Configure -> {format}");
    }

    public void Submit(VideoFrame frame) => frame.Dispose();
}
