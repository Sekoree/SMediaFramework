using System.Diagnostics;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Audio;
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

    [Fact]
    public void Audio_flac_round_trip_preserves_signal()
    {
        // Exercises the audio encoder's swresample path for a NON-float codec (FLAC = s16). The old
        // path copied raw float bytes into the frame, which the codec interpreted as integers — the
        // decoded signal was garbage. FLAC is lossless, so the decoded RMS must track the input sine.
        const int sampleRate = 48_000;
        const int channels = 2;
        const int totalFrames = sampleRate / 2; // 0.5 s
        const float amp = 0.25f;
        var interleaved = new float[totalFrames * channels];
        for (var i = 0; i < totalFrames; i++)
        {
            var s = amp * MathF.Sin(2f * MathF.PI * 440f * i / sampleRate);
            for (var ch = 0; ch < channels; ch++)
                interleaved[i * channels + ch] = s;
        }

        var outPath = Path.Combine(Path.GetTempPath(), $"mf_enc_aud_{Guid.NewGuid():N}.mka");
        _tempPaths.Add(outPath);

        var format = new AudioFormat(sampleRate, channels);
        const int chunkFrames = 1024;
        using (var mux = FFmpegMuxFileOutput.Open(outPath, new FFmpegMuxFileOutputOptions
        {
            Container = FFmpegEncodeContainer.Matroska,
            IncludeVideo = false,
            IncludeAudio = true,
            Audio = new FFmpegAudioFileOutputOptions { Codec = FFmpegAudioCodec.Flac },
        }))
        {
            mux.Audio!.Configure(format);
            for (var off = 0; off < totalFrames; off += chunkFrames)
            {
                var n = Math.Min(chunkFrames, totalFrames - off);
                mux.Audio.Submit(interleaved.AsSpan(off * channels, n * channels));
            }
        }

        Assert.True(File.Exists(outPath));
        Assert.True(new FileInfo(outPath).Length > 256);

        // Decode back: lossless FLAC means the decoded RMS must match the input sine's RMS. A broken
        // sample-format path would yield silence or noise far from amp/√2.
        using var dec = AudioFileDecoder.Open(outPath);
        var buf = new float[chunkFrames * channels];
        double sumSq = 0;
        long count = 0;
        int read;
        while ((read = dec.ReadInto(buf)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                sumSq += buf[i] * (double)buf[i];
                count++;
            }
        }

        Assert.True(count > 20_000, $"expected a meaningful amount of decoded audio, got {count} samples");
        var rms = Math.Sqrt(sumSq / count);
        var expected = amp / Math.Sqrt(2); // ≈ 0.1768
        Assert.InRange(rms, expected * 0.6, expected * 1.4);
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
