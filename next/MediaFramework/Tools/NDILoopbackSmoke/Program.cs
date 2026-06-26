using System.Diagnostics;
using S.Media.Core.Registry;
using S.Media.Decode.FFmpeg;
using S.Media.NDI;
using S.Media.Players;

// Phase 5 NDI-output smoke. Decodes a file and fans its video to an NDIOutput (NDI send), then discovers
// that sender on the network and receives the frames back (in-process loopback) — proving NDI out works
// end-to-end through the same registry/router path used for any video output. Needs FFmpeg + libndi.

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: NDILoopbackSmoke <media-file-or-uri> [seconds=8]");
    return 2;
}

var uri = args[0];
var seconds = args.Length > 1 && double.TryParse(args[1], out var s) ? s : 8.0;
const string senderName = "MFPlayer NDI Out";

var registry = MediaRegistry.Build(b => b.Use(new FFmpegModule()).Use(new NDIModule()));

// Sender: software decode (NDI send is CPU p_data) → fan the player's video to the NDI output.
using var player = MediaPlayer.Open(registry, uri, new MediaPlayerOpenOptions { TryHardwareAcceleration = false });
using var ndiOut = new NDIOutput(senderName);
player.AttachVideoOutput(ndiOut.Video, "ndi");
player.Play();
Console.WriteLine($"sending '{uri}' as NDI source '{senderName}'…");

// Receiver: discover our own sender and read the frames back.
Thread.Sleep(1000); // let the sender announce + push the first frames
var sources = NDISource.Find(TimeSpan.FromSeconds(4));
var mine = sources.FirstOrDefault(x => x.Name.Contains(senderName, StringComparison.Ordinal));
if (mine.Name is null)
{
    Console.Error.WriteLine($"FAIL: our NDI sender was not discovered. Sources: {string.Join(", ", sources.Select(x => x.Name))}");
    return 1;
}

using var recv = NDISource.Open(mine, new NDISourceOptions { ReceiveVideo = true, ReceiveAudio = false });
var video = recv.Video;

long received = 0;
var sw = Stopwatch.StartNew();
while (sw.Elapsed.TotalSeconds < seconds && !player.Video.IsSourceExhausted && video.TryReadNextFrame(out var f))
{
    f.Dispose();
    received++;
}

recv.TryGetVideoFormat(out var vf);
Console.WriteLine($"received {received} frames back at {vf.Width}x{vf.Height}; sender sees {ndiOut.GetReceiverConnectionCount()} receiver(s).");
if (received < 10 || vf.Width <= 0)
{
    Console.Error.WriteLine("FAIL: did not receive a steady stream of full frames back over NDI.");
    return 1;
}

Console.WriteLine("NDILoopbackSmoke OK — NDI out send → discover → receive round-trip verified.");
return 0;
