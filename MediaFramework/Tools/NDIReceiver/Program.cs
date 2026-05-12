using System.Text;
using NDILib;
using NdiReceiver = NDILib.NDIReceiver;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 1 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: NDIReceiver <ndi-source-name-substring>");
    Console.WriteLine("  Discovers NDI sources, picks the first whose display name contains the substring (case-insensitive),");
    Console.WriteLine("  connects, and prints capture statistics until Ctrl+C.");
    return args.Length < 1 ? 2 : 0;
}

var needle = args[0];
if (string.IsNullOrWhiteSpace(needle))
{
    Console.Error.WriteLine("source name substring must be non-empty.");
    return 2;
}

NDIRuntime.Create(out var rt).ThrowIfNonZero("NDIRuntime.Create");
using (rt!)
{
    NDIFinder.Create(out var finder).ThrowIfNonZero("NDIFinder.Create");
    using (finder!)
    {
        NDIDiscoveredSource? picked = null;
        for (var attempt = 0; attempt < 60 && picked is null; attempt++)
        {
            _ = finder!.WaitForSources(1000);
            foreach (var s in finder.GetCurrentSources())
            {
                if (s.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    picked = s;
                    break;
                }
            }
        }

        if (picked is null)
        {
            Console.Error.WriteLine($"No NDI source matched '{needle}' within discovery timeout.");
            return 3;
        }

        Console.WriteLine($"Connecting to: {picked.Value.Name}");
        Console.WriteLine($"  URL: {picked.Value.UrlAddress}");

        NdiReceiver.Create(out var recv).ThrowIfNonZero("NDIReceiver.Create");
        using (recv!)
        {
            recv!.Connect(picked.Value);

            var videoFrames = 0L;
            var audioFrames = 0L;
            var metaFrames = 0L;
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            while (!cts.IsCancellationRequested)
            {
                using var scope = recv.CaptureScoped(500);
                switch (scope.FrameType)
                {
                    case NDIFrameType.Video:
                        videoFrames++;
                        break;
                    case NDIFrameType.Audio:
                        audioFrames++;
                        break;
                    case NDIFrameType.Metadata:
                        metaFrames++;
                        break;
                    case NDIFrameType.None:
                        continue;
                    case NDIFrameType.Error:
                        Console.Error.WriteLine("Capture returned Error — stopping.");
                        return 4;
                    default:
                        break;
                }

                if ((videoFrames + audioFrames) % 200 == 0 && videoFrames + audioFrames > 0)
                    Console.WriteLine($"video={videoFrames} audio={audioFrames} metadata={metaFrames}");
            }

            Console.WriteLine($"Done. video={videoFrames} audio={audioFrames} metadata={metaFrames}");
        }
    }
}

return 0;

internal static class NdiRcExtensions
{
    public static void ThrowIfNonZero(this int rc, string what)
    {
        if (rc != 0)
            throw new InvalidOperationException($"{what} failed (code {rc}).");
    }
}
