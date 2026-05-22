using System.Diagnostics;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Encode;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Encode.Tests;

public sealed class FFmpegMuxRoundTripTests : IDisposable
{
    private readonly string? _sourcePath;
    private readonly List<string> _tempPaths = [];

    public FFmpegMuxRoundTripTests()
    {
        FFmpegRuntime.EnsureInitialized();
        var path = Path.Combine(Path.GetTempPath(), $"mf_enc_src_{Guid.NewGuid():N}.mp4");
        _sourcePath = TryGenerateVideo(path, 160, 120, 10, 1) ? path : null;
        if (_sourcePath is not null)
            _tempPaths.Add(_sourcePath);
    }

    public void Dispose()
    {
        foreach (var p in _tempPaths)
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(p), $"{nameof(FFmpegMuxRoundTripTests)}: delete temp");
    }

    [Fact]
    public void Mux_round_trip_writes_playable_mp4()
    {
        if (_sourcePath is null)
            return;

        var outPath = Path.Combine(Path.GetTempPath(), $"mf_enc_out_{Guid.NewGuid():N}.mp4");
        _tempPaths.Add(outPath);

        using var dec = MediaContainerDecoder.Open(_sourcePath, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
        var count = 0;
        {
            using var mux = FFmpegMuxFileOutput.Open(outPath, new FFmpegMuxFileOutputOptions
            {
                IncludeAudio = false,
                Video = new FFmpegVideoFileOutputOptions { Codec = FFmpegVideoCodec.H264, GopSize = 6 },
            });

            mux.Video!.Configure(dec.Video.Format);
            while (dec.Video.TryReadNextFrame(out var f))
            {
                mux.Video.Submit(f);
                count++;
            }
        }

        Assert.True(count > 0);
        Assert.True(File.Exists(outPath));
        Assert.True(new FileInfo(outPath).Length > 256);

        using var verify = VideoFileDecoder.Open(outPath, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
        Assert.True(verify.TryReadNextFrame(out var frame));
        frame.Dispose();
    }

    private static bool TryGenerateVideo(string path, int width, int height, int fps, int durationSec)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                ArgumentList =
                {
                    "-y", "-f", "lavfi",
                    "-i", $"testsrc=size={width}x{height}:rate={fps}:duration={durationSec}",
                    "-pix_fmt", "yuv420p", "-c:v", "libx264", "-loglevel", "error", path,
                },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(15000);
            return p.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
